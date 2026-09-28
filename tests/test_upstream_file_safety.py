"""合成文件与真实 FastAPI 路由接线测试；不启动业务配置或数据库。"""

import ast
import json
from functools import wraps
import logging
import os
from pathlib import Path
import stat
import subprocess
import sys
import textwrap
from types import SimpleNamespace
from typing import Optional
import unittest
from unittest import mock
from uuid import uuid4

import aiofiles
from fastapi import APIRouter, Depends, FastAPI, HTTPException
from fastapi.testclient import TestClient

from src.file_safety import UnsafeFilePathError, check_scoped_path, validate_filename
from src.runtime_paths import RuntimePaths
from src import user_file_store as store
from src.web.models import BayesUpdate, NewPromptRequest, PromptUpdate


ROOT = Path(__file__).resolve().parents[1]
TEMP_ROOT = ROOT / ".tmp/upstream-upgrade/b1b-20260928"
BAD_NAMES = (
    "", ".", "..", "../x", "a/../x", "a\\x", "/x", "C:x", "C:\\x",
    "\\\\server\\share\\x", "x.txt:stream", "CON", "con.txt", "NUL.json",
    "COM1.txt", "LPT9.txt", "COM¹.txt", "CON .txt", "CONIN$.txt",
    "x.", "a..txt", "a?b.txt", "a\x00b", "a\nb", "%2fsecret", "%255csecret",
    "%2e%2e", "x%3astream", "x%2500",
)


def load_settings(storage_backend, storage, current_user):
    """保留真实路由装饰器/依赖，仅排除模块初始化及真实配置导入。"""
    async def require_auth():
        return current_user[0]

    names = {
        "_require_ai_access", "_require_ai_or_tasks_access", "_resolve_current_user_id",
        "_resolve_owner_for_scoped_files", "_validate_safe_filename", "_file_operation_error",
        "_file_api_errors", "list_prompts", "create_new_prompt", "get_prompt_content",
        "update_prompt_content", "delete_prompt",
        "list_criteria_files", "get_criteria_content", "update_criteria_content",
        "list_bayes_profiles", "create_bayes_profile", "get_bayes_profile",
        "update_bayes_profile", "delete_bayes_profile", "_normalize_bayes_version",
    }
    path = ROOT / "src/web/settings_manager.py"
    tree = ast.parse(path.read_text(encoding="utf-8"))
    nodes = [node for node in tree.body if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in names]
    assert {node.name for node in nodes} == names
    namespace = {
        "router": APIRouter(), "Depends": Depends, "HTTPException": HTTPException,
        "require_auth": require_auth, "check_permission": lambda user, name: False,
        "has_category": lambda user, name: name in user.get("categories", []),
        "Optional": Optional, "wraps": wraps, "os": os, "aiofiles": aiofiles,
        "logger": logging.getLogger(__name__), "validate_filename": validate_filename,
        "UnsafeFilePathError": UnsafeFilePathError, "NewPromptRequest": NewPromptRequest,
        "PromptUpdate": PromptUpdate, "resolve_scoped_path": store.resolve_scoped_path,
        "list_scoped_files": store.list_scoped_files, "STORAGE_BACKEND": lambda: storage_backend[0],
        "get_storage": lambda: storage,
        "json": json, "BayesUpdate": BayesUpdate, "get_shared_path": store.get_shared_path,
    }
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(path), "exec"), namespace)
    app = FastAPI()
    app.include_router(namespace["router"])
    return namespace, TestClient(app)


class FilenameTests(unittest.TestCase):
    def test_rejects_unsafe_names_on_every_os(self):
        for name in BAD_NAMES:
            with self.subTest(name=name), self.assertRaises(UnsafeFilePathError):
                validate_filename(name)

    def test_accepts_normal_chinese_and_percent_names(self):
        for name in ("标准模板.txt", "base_prompt.txt", "型号 v2.json", "折扣50%.txt", "COM10.txt"):
            self.assertEqual(validate_filename(name), name)

    def test_reparse_point_rejected_without_following(self):
        root = Path.cwd()
        target = root / "synthetic-reparse.txt"
        original = Path.lstat

        def fake_lstat(path):
            if path == target:
                return SimpleNamespace(st_mode=stat.S_IFREG, st_file_attributes=stat.FILE_ATTRIBUTE_REPARSE_POINT)
            return original(path)

        with mock.patch.object(Path, "lstat", fake_lstat), self.assertRaises(UnsafeFilePathError):
            check_scoped_path(target, root)


class FileFixture(unittest.TestCase):
    def setUp(self):
        TEMP_ROOT.mkdir(parents=True, exist_ok=True)
        self.root = TEMP_ROOT / uuid4().hex
        self.root.mkdir()  # 普通 mkdir 继承权限，不使用 Windows 私有临时目录 ACL。
        self.paths = RuntimePaths.create(program_root=self.root / "program", data_root=self.root / "data", cache_root=self.root / "cache")
        self.patcher = mock.patch.object(store, "get_portable_runtime_paths", return_value=self.paths)
        self.patcher.start()
        self.addCleanup(self.patcher.stop)
        self.addCleanup(self.cleanup_fixture)

    def cleanup_fixture(self):
        # 只处理本用例创建的精确根；不跟随符号链接，不递归删除历史目录。
        def clean(folder):
            for child in folder.iterdir():
                info = child.lstat()
                if child.is_symlink() or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    # 只移除本用例的链接本身，不递归进入目标。
                    if child.is_symlink():
                        child.unlink()
                    else:
                        child.rmdir()
                elif child.is_dir():
                    clean(child)
                else:
                    child.unlink()
            folder.rmdir()
        self.assertEqual(self.root.parent, TEMP_ROOT)
        clean(self.root)
        try:
            TEMP_ROOT.rmdir()
        except OSError:
            pass

    def write(self, path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")


class ScopedFileTests(FileFixture):
    def test_legacy_compatibility_without_config_import(self):
        result = subprocess.run(
            [sys.executable, "-B", "-c", textwrap.dedent('''
                import importlib.abc
                import sys
                from pathlib import Path
                class NoConfig(importlib.abc.MetaPathFinder):
                    def find_spec(self, fullname, path=None, target=None):
                        if fullname in {"src.config", "dotenv"}:
                            raise AssertionError("不允许加载业务配置")
                sys.meta_path.insert(0, NoConfig())
                from src import user_file_store as store
                assert store.get_shared_path("prompts", "nested/sample.txt") == Path("prompts/sample.txt")
                assert store.get_user_scoped_path("bayes", "model.json", "owner") == Path("state/user_files/owner/bayes/model.json")
                old_path = Path.cwd() / "old-task.txt"
                assert store.resolve_virtual_task_file(str(old_path), "owner") == old_path
                assert not list(Path.cwd().iterdir())
            ''')],
            cwd=self.root,
            env={key: value for key, value in {**os.environ, "PYTHONPATH": str(ROOT), "PYTHONDONTWRITEBYTECODE": "1"}.items() if not key.startswith("GOOFISH_")},
            capture_output=True, text=True, timeout=20,
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_chinese_file_and_owner_isolation(self):
        for owner in ("owner-a", "owner-b"):
            path = store.resolve_scoped_path("prompts", "中文.txt", owner, for_write=True)
            path.write_text(owner, encoding="utf-8")
        for owner in ("owner-a", "owner-b"):
            self.assertEqual(store.resolve_scoped_path("prompts", "中文.txt", owner).read_text(encoding="utf-8"), owner)
            self.assertEqual(store.list_scoped_files("prompts", owner), ["中文.txt"])

    def test_bad_names_do_not_create_directories(self):
        for name in BAD_NAMES:
            with self.subTest(name=name), self.assertRaises(ValueError):
                store.resolve_scoped_path("prompts", name, "owner", for_write=True)
        self.assertFalse(self.paths.data_root.exists())

    def test_directory_target_is_rejected_for_read_and_write(self):
        target = self.paths.data_path("assets/prompts/folder.txt")
        target.mkdir(parents=True)
        for writing in (False, True):
            with self.assertRaises(UnsafeFilePathError):
                store.resolve_scoped_path("prompts", "folder.txt", None, for_write=writing)
        self.assertEqual(store.list_scoped_files("prompts", None), [])

    def test_shared_and_seed_priority_and_private_writes(self):
        seed = self.paths.program_path("defaults/prompts/base.txt")
        shared = self.paths.data_path("assets/prompts/base.txt")
        self.write(seed, "seed")
        self.assertEqual(store.resolve_scoped_path("prompts", "base.txt", "a"), seed)
        self.write(shared, "shared")
        self.assertEqual(store.resolve_scoped_path("prompts", "base.txt", "a"), shared)
        private = store.resolve_scoped_path("prompts", "base.txt", "a", for_write=True)
        private.write_text("private", encoding="utf-8")
        self.assertEqual(store.resolve_scoped_path("prompts", "base.txt", "a"), private)
        self.assertEqual(seed.read_text(encoding="utf-8"), "seed")
        self.assertEqual(shared.read_text(encoding="utf-8"), "shared")

    def test_scope_escape_and_parent_file_are_rejected(self):
        with self.assertRaises(UnsafeFilePathError):
            check_scoped_path(self.root / "outside.txt", self.paths.data_root)
        parent = self.paths.data_path("assets")
        self.write(parent, "not a directory")
        with self.assertRaises(UnsafeFilePathError):
            store.resolve_scoped_path("prompts", "new.txt", None, for_write=True)
        self.assertEqual(parent.read_text(encoding="utf-8"), "not a directory")

    def test_real_symlink_blocks_read_write_and_listing(self):
        outside = self.root / "outside.txt"
        self.write(outside, "unchanged")
        link = self.paths.data_path("assets/prompts/link.txt")
        link.parent.mkdir(parents=True)
        try:
            link.symlink_to(outside)
        except OSError as error:
            self.skipTest(f"当前环境不能创建符号链接: {type(error).__name__}")
        for writing in (False, True):
            with self.assertRaises(UnsafeFilePathError):
                store.resolve_scoped_path("prompts", "link.txt", None, for_write=writing)
        self.assertEqual(store.list_scoped_files("prompts", None), [])
        self.assertEqual(outside.read_text(encoding="utf-8"), "unchanged")

    def test_mocked_parent_link_refuses_mkdir(self):
        target_parent = self.paths.data_path("state/user_files/owner")
        original = Path.lstat

        def fake_lstat(path):
            if path == target_parent:
                return SimpleNamespace(st_mode=stat.S_IFDIR, st_file_attributes=stat.FILE_ATTRIBUTE_REPARSE_POINT)
            return original(path)

        with mock.patch.object(Path, "lstat", fake_lstat), mock.patch.object(Path, "mkdir") as mkdir:
            with self.assertRaises(UnsafeFilePathError):
                store.resolve_scoped_path("prompts", "new.txt", "owner", for_write=True)
            mkdir.assert_not_called()

    @unittest.skipUnless(os.name == "nt", "仅 Windows 存在 junction")
    def test_real_windows_junction_blocks_read_write_and_listing(self):
        outside = self.root / "outside"
        self.write(outside / "keep.txt", "unchanged")
        link = self.paths.data_path("assets/prompts")
        link.parent.mkdir(parents=True)
        # 合成目标均在本用例下，参数通过环境传递，不拼接可执行文本。
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
             "New-Item -ItemType Junction -Path $env:TEST_LINK_PATH -Target $env:TEST_TARGET_PATH -ErrorAction Stop | Out-Null"],
            env={**os.environ, "TEST_LINK_PATH": str(link), "TEST_TARGET_PATH": str(outside)},
            capture_output=True, timeout=20,
        )
        self.assertEqual(result.returncode, 0, "无法建立合成 junction 夹具")
        for writing in (False, True):
            with self.assertRaises(UnsafeFilePathError):
                store.resolve_scoped_path("prompts", "keep.txt", None, for_write=writing)
        with self.assertRaises(UnsafeFilePathError):
            store.list_scoped_files("prompts", None)
        self.assertEqual((outside / "keep.txt").read_text(encoding="utf-8"), "unchanged")
        self.assertEqual(list(outside.iterdir()), [outside / "keep.txt"])


class FileApiTests(FileFixture):
    def setUp(self):
        super().setUp()
        self.backend = ["file"]
        self.user = [{"id": "owner-a", "categories": ["ai"]}]
        self.storage = mock.Mock()
        self.namespace, self.client = load_settings(self.backend, self.storage, self.user)
        self.addCleanup(self.client.close)

    def test_roundtrip_file_api_and_extension_compatibility(self):
        created = self.client.post("/api/prompts", json={"filename": "中文", "content": "初版"})
        self.assertEqual(created.status_code, 200, created.text)
        self.assertEqual(self.client.get("/api/prompts/中文.txt").json()["content"], "初版")
        self.assertEqual(self.client.put("/api/prompts/中文.txt", json={"content": "新版"}).status_code, 200)
        self.assertEqual(self.client.get("/api/prompts").json(), ["中文.txt"])
        self.assertEqual(self.client.delete("/api/prompts/中文.txt").status_code, 200)
        self.assertEqual(self.client.get("/api/prompts/中文.txt").status_code, 404)

    def test_bad_api_inputs_have_no_file_or_storage_side_effect(self):
        for backend in ("file", "postgres"):
            self.backend[0] = backend
            for name in BAD_NAMES:
                with self.subTest(name=name, backend=backend):
                    response = self.client.post("/api/prompts", json={"filename": name, "content": "never"})
                    self.assertEqual(response.status_code, 400, response.text)
        self.assertFalse(self.paths.data_root.exists())
        self.assertEqual(self.storage.mock_calls, [])

    def test_unauthenticated_and_unprivileged_block_before_storage(self):
        for user, expected in ((None, 401), ({"id": "a", "categories": []}, 403)):
            self.user[0] = user
            self.assertEqual(self.client.get("/api/prompts").status_code, expected)
            self.assertEqual(self.client.post("/api/prompts", json={"filename": "x", "content": "x"}).status_code, expected)
        self.assertEqual(self.storage.mock_calls, [])
        self.assertFalse(self.paths.data_root.exists())

    def test_directory_and_io_error_responses_are_sanitized(self):
        directory = self.paths.data_path("assets/prompts/folder.txt")
        directory.mkdir(parents=True)
        response = self.client.get("/api/prompts/folder.txt")
        self.assertEqual(response.status_code, 400)
        self.assertNotIn(str(self.root), response.text)
        self.write(self.paths.data_path("assets/prompts/valid.txt"), "unchanged")
        with mock.patch.object(aiofiles, "open", side_effect=OSError(f"private path: {self.root}")):
            response = self.client.get("/api/prompts/valid.txt")
        self.assertEqual(response.status_code, 500)
        self.assertNotIn(str(self.root), response.text)

    def test_postgres_update_delete_keep_authenticated_owner(self):
        self.backend[0] = "postgres"
        self.storage.get_prompt_template.return_value = {"content": "system", "is_default": True}
        self.assertEqual(self.client.put("/api/prompts/shared.txt", json={"content": "private"}).status_code, 200)
        self.assertEqual(self.storage.save_prompt_template.call_args.kwargs, {"owner_id": "owner-a"})
        self.storage.delete_prompt_template.return_value = False
        response = self.client.delete("/api/prompts/shared.txt")
        self.assertEqual(response.status_code, 400)
        self.storage.delete_prompt_template.assert_called_once_with("shared.txt", owner_id="owner-a")
        self.storage.get_prompt_template.return_value = None
        self.assertEqual(self.client.put("/api/prompts/other.txt", json={"content": "overwrite"}).status_code, 404)
        self.assertEqual(self.storage.save_prompt_template.call_count, 1)

    def test_postgres_routes_use_authenticated_owner_not_input_owner(self):
        self.backend[0] = "postgres"
        self.storage.get_prompt_template.return_value = None
        response = self.client.post("/api/prompts", json={"filename": "中文", "content": "测试", "owner_id": "owner-b"})
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(self.storage.save_prompt_template.call_args.kwargs["owner_id"], "owner-a")
        self.storage.get_prompt_template.return_value = {"content": "owner-a"}
        self.assertEqual(self.client.get("/api/prompts/中文.txt").json()["content"], "owner-a")
        self.storage.get_prompt_template.assert_called_with("中文.txt", owner_id="owner-a")
        self.user[0] = {"id": "owner-b", "categories": ["ai"]}
        self.storage.get_prompt_template.return_value = None
        self.assertEqual(self.client.get("/api/prompts/中文.txt").status_code, 404)
        self.storage.get_prompt_template.assert_called_with("中文.txt", owner_id="owner-b")
        self.assertFalse(self.paths.data_root.exists())


class ExtendedFileApiTests(FileFixture):
    def setUp(self):
        super().setUp()
        self.backend = ["file"]
        self.user = [{"id": "owner-a", "categories": ["ai"]}]
        self.storage = mock.Mock()
        self.namespace, self.client = load_settings(self.backend, self.storage, self.user)
        self.addCleanup(self.client.close)

    def test_seed_updates_write_override_and_never_modify_program_defaults(self):
        for route, kind, name in (("prompts", "prompts", "seed.txt"), ("criteria", "criteria", "seed.txt"), ("criteria", "requirement", "req.txt"), ("bayes", "prompts/bayes", "seed.json")):
            with self.subTest(kind=kind):
                seed = self.paths.program_path(f"defaults/{kind}/{name}")
                self.write(seed, "seed-original")
                response = self.client.put(f"/api/{route}/{name}", json={"content": "override"})
                self.assertEqual(response.status_code, 200, response.text)
                self.assertEqual(seed.read_text(encoding="utf-8"), "seed-original")
                self.assertEqual(self.paths.data_path(f"assets/{kind}/{name}").read_text(encoding="utf-8"), "override")
                self.assertEqual(self.client.get(f"/api/{route}/{name}").json()["content"], "override")

    def test_seed_delete_rejected_and_override_delete_restores_seed(self):
        for route, kind, name in (("prompts", "prompts", "seed.txt"), ("bayes", "prompts/bayes", "seed.json")):
            with self.subTest(route=route):
                seed = self.paths.program_path(f"defaults/{kind}/{name}")
                self.write(seed, "seed-original")
                response = self.client.delete(f"/api/{route}/{name}")
                self.assertEqual(response.status_code, 400, response.text)
                self.assertEqual(seed.read_text(encoding="utf-8"), "seed-original")
                override = self.paths.data_path(f"assets/{kind}/{name}")
                self.write(override, "override")
                self.assertEqual(self.client.delete(f"/api/{route}/{name}").status_code, 200)
                self.assertFalse(override.exists())
                self.assertEqual(self.client.get(f"/api/{route}/{name}").json()["content"], "seed-original")

    def test_criteria_requirement_priority_and_private_copy_isolation(self):
        self.backend[0] = "postgres"
        for kind in ("criteria", "requirement"):
            self.write(self.paths.data_path(f"assets/{kind}/same.txt"), kind)
        self.assertEqual(self.client.get("/api/criteria/same.txt").json()["content"], "requirement")
        response = self.client.put("/api/criteria/same.txt?owner_id=owner-b", json={"content": "private-a"})
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(self.client.get("/api/criteria/same.txt").json()["content"], "private-a")
        self.user[0] = {"id": "owner-b", "categories": ["tasks"]}
        self.assertEqual(self.client.get("/api/criteria/same.txt").json()["content"], "requirement")
        self.assertEqual(self.paths.data_path("assets/requirement/same.txt").read_text(encoding="utf-8"), "requirement")
        self.assertEqual(self.storage.mock_calls, [])

    def test_criteria_without_requirement_and_missing_update(self):
        self.backend[0] = "postgres"
        self.write(self.paths.data_path("assets/criteria/criteria.txt"), "shared")
        self.assertEqual(self.client.put("/api/criteria/criteria.txt", json={"content": "private"}).status_code, 200)
        self.assertEqual(self.client.get("/api/criteria/criteria.txt").json()["content"], "private")
        self.assertEqual(self.client.get("/api/criteria").json(), ["criteria.txt"])
        self.assertEqual(self.client.put("/api/criteria/missing.txt", json={"content": "no"}).status_code, 404)

    def test_bayes_file_roundtrip_and_extension(self):
        self.assertEqual(self.client.post("/api/bayes", json={"filename": "中文", "content": "{}"}).status_code, 200)
        self.assertEqual(self.client.post("/api/bayes", json={"filename": "中文", "content": "duplicate"}).status_code, 400)
        self.assertEqual(self.client.get("/api/bayes").json(), ["中文.json"])
        self.assertEqual(self.client.put("/api/bayes/中文", json={"content": '{"version":"中文"}'}).status_code, 200)
        self.assertEqual(json.loads(self.client.get("/api/bayes/中文").json()["content"]), {"version": "中文"})
        self.assertEqual(self.client.delete("/api/bayes/中文").status_code, 200)
        self.assertEqual(self.client.delete("/api/bayes/中文").status_code, 404)

    def test_bayes_pg_payload_version_owner_and_shared_delete_protection(self):
        self.backend[0] = "postgres"
        self.storage.get_bayes_profile.return_value = None
        result = self.client.post("/api/bayes", json={"filename": "中文", "content": '{"version":"other","weight":0.3}', "owner_id": "other"})
        self.assertEqual(result.status_code, 200, result.text)
        self.storage.save_bayes_profile.assert_called_with({"version": "中文", "weight": 0.3}, owner_id="owner-a")
        self.storage.get_bayes_profile.return_value = {"version": "中文"}
        self.assertEqual(self.client.put("/api/bayes/中文.json", json={"content": '{"weight":0.4}'}).status_code, 200)
        self.storage.save_bayes_profile.assert_called_with({"version": "中文", "weight": 0.4}, owner_id="owner-a")
        self.storage.delete_bayes_profile.return_value = False
        self.assertEqual(self.client.delete("/api/bayes/中文.json").status_code, 400)
        self.storage.delete_bayes_profile.assert_called_with("中文", owner_id="owner-a")
        self.storage.delete_bayes_profile.return_value = True
        self.assertIn("共享模板仍保留", self.client.delete("/api/bayes/中文.json").json()["message"])
        self.storage.list_bayes_profiles.return_value = [{"version": "中文"}]
        self.assertEqual(self.client.get("/api/bayes").json(), ["中文.json"])
        self.assertFalse(self.paths.data_root.exists())

    def test_bayes_invalid_pg_json_never_saved(self):
        self.backend[0] = "postgres"
        for content in ("{", "[]", '"text"', "null", "1"):
            self.storage.get_bayes_profile.return_value = None
            self.assertEqual(self.client.post("/api/bayes", json={"filename": "x", "content": content}).status_code, 400)
            self.storage.get_bayes_profile.return_value = {"version": "x"}
            self.assertEqual(self.client.put("/api/bayes/x", json={"content": content}).status_code, 400)
        self.storage.save_bayes_profile.assert_not_called()

    def test_bayes_pg_fallback_reads_only_current_private_and_shared(self):
        self.backend[0] = "postgres"
        private = store.resolve_scoped_path("bayes", "a.json", "owner-a", for_write=True)
        private.write_text("private-a", encoding="utf-8")
        self.write(self.paths.data_path("assets/prompts/bayes/shared.json"), "shared")
        self.storage.get_bayes_profile.side_effect = RuntimeError("synthetic failure")
        self.storage.list_bayes_profiles.side_effect = RuntimeError("synthetic failure")
        with mock.patch.object(self.namespace["logger"], "warning"):
            self.assertEqual(self.client.get("/api/bayes/a.json").json()["content"], "private-a")
            self.user[0] = {"id": "owner-b", "categories": ["ai"]}
            self.assertEqual(self.client.get("/api/bayes/a.json").status_code, 404)
            self.assertEqual(self.client.get("/api/bayes").json(), ["shared.json"])
        self.storage.get_bayes_profile.assert_called_with("a", owner_id="owner-b")

    def test_remaining_file_routes_permission_matrix(self):
        operations = (("get", "/api/criteria", None), ("get", "/api/criteria/a.txt", None), ("put", "/api/criteria/a.txt", {"content": "x"}), ("get", "/api/bayes", None), ("get", "/api/bayes/a", None), ("post", "/api/bayes", {"filename": "a", "content": "{}"}), ("put", "/api/bayes/a", {"content": "{}"}), ("delete", "/api/bayes/a", None))
        for user, status in ((None, 401), ({"id": "a", "categories": []}, 403)):
            self.user[0] = user
            for method, url, payload in operations:
                response = self.client.request(method, url, **({"json": payload} if payload else {}))
                self.assertEqual(response.status_code, status, (method, url, response.text))
        self.user[0] = {"id": "a", "categories": ["tasks"]}
        for method, url, payload in operations[5:]:
            self.assertEqual(self.client.request(method, url, **({"json": payload} if payload else {})).status_code, 403)
        self.assertEqual(self.storage.mock_calls, [])
        self.assertFalse(self.paths.data_root.exists())

    def test_invalid_names_and_file_io_errors_are_sanitized(self):
        for backend in ("file", "postgres"):
            self.backend[0] = backend
            for name in BAD_NAMES:
                self.assertEqual(self.client.post("/api/bayes", json={"filename": name, "content": "{}"}).status_code, 400)
            for route in ("criteria", "bayes"):
                for method in ("get", "put"):
                    response = self.client.request(method, f"/api/{route}/CON", **({"json": {"content": "x"}} if method == "put" else {}))
                    self.assertEqual(response.status_code, 400)
        self.assertEqual(self.storage.mock_calls, [])
        self.backend[0] = "file"
        for route, kind, name in (("criteria", "criteria", "x.txt"), ("bayes", "prompts/bayes", "x.json")):
            self.write(self.paths.data_path(f"assets/{kind}/{name}"), "keep")
            with mock.patch.object(aiofiles, "open", side_effect=OSError(f"secret path {self.root}")):
                response = self.client.get(f"/api/{route}/{name}")
            self.assertEqual(response.status_code, 500)
            self.assertNotIn(str(self.root), response.text)


if __name__ == "__main__":
    unittest.main()
