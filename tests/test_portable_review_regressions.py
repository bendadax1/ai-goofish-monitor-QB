"""Execute production functions with isolated dependencies; no business imports/I/O."""

import ast
import asyncio
import copy
import logging
from pathlib import Path
from threading import Lock
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock


ROOT = Path(__file__).resolve().parents[1]


def load_functions(relative, names, namespace):
    """Avoid importing modules whose legacy startup loads real .env/data."""
    source = ast.parse((ROOT / relative).read_text(encoding="utf-8"))
    selected = []
    for node in source.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in names:
            node.decorator_list = []
            selected.append(node)
    assert len(selected) == len(names), names
    module = ast.Module(body=[ast.ImportFrom(module="__future__", names=[ast.alias(name="annotations")], level=0), *selected], type_ignores=[])
    ast.fix_missing_locations(module)
    namespace.setdefault("logger", logging.getLogger(__name__))
    exec(compile(module, relative, "exec"), namespace)
    return SimpleNamespace(**namespace)


class ReviewRegressions(unittest.IsolatedAsyncioTestCase):
    async def test_portable_stale_pid_never_reaches_process_termination(self):
        for portable in (False, True):
            for handle in (None, SimpleNamespace(returncode=0)):
                with self.subTest(portable=portable, handle=handle):
                    kill = Mock()
                    update = AsyncMock()
                    api = load_functions("src/web/task_manager.py", {"stop_task_process"}, {
                        "_portable_runtime_paths": object() if portable else None,
                        "is_multi_user_mode": lambda: True,
                        "get_storage": lambda: SimpleNamespace(get_task_by_name=lambda *a, **k: {"process_pid": 12345}),
                        "_make_process_key": lambda *a: "task-key",
                        "_terminate_pid": kill, "update_task_running_status": update,
                    })
                    processes = {} if handle is None else {"task-key": handle}
                    await api.stop_task_process(0, processes, owner_id="fixture", task_name="fixture")
                    self.assertEqual(kill.call_count, 0 if portable else 1)
                    update.assert_awaited_once()
                    self.assertEqual(processes, {})

    async def test_local_health_check_is_retained_by_read_only_status(self):
        config = {"backend": "local", "source": "env"}
        state = {"ready": True, "api_key_set": True, "base_url_set": True, "model_name_set": True}
        api = load_functions("src/web/ai_health.py", {
            "run_ai_health_check", "_set_cached_snapshot", "get_ai_health_snapshot",
        }, {
            "asyncio": asyncio, "copy": copy, "STORAGE_BACKEND": lambda: "local",
            "_AI_HEALTH_CACHE": {}, "_AI_HEALTH_CACHE_LOCK": Lock(), "_cache_key": lambda user: "local",
            "_resolve_effective_ai_config": lambda *a, **k: config, "_resolve_config_state": lambda c: state,
            "_default_health_snapshot": lambda *a: {"overall_level": "unknown"},
            "_cache_identity": lambda c: ("", 0), "_build_probe_result": lambda **k: k,
            "_build_vision_result": lambda **k: k, "_now_text": lambda: "fixture-time",
            "_compute_overall_level": lambda *a: {"level": "ok", "label": "正常", "message": "fixture"},
            "_run_backend_text_probe_async": AsyncMock(return_value={"success": True}),
        })
        result = await api.run_ai_health_check(None, run_web=False, check_vision=False)
        self.assertEqual(result["overall_level"], "ok")
        cached = api.get_ai_health_snapshot(None)
        self.assertEqual(cached["checked_at"], "fixture-time")
        self.assertEqual(cached["backend_test"]["success"], True)

    async def test_generic_route_passes_only_actual_allowed_portable_fields(self):
        import sys
        from unittest.mock import patch
        class HttpError(Exception):
            def __init__(self, status_code, detail):
                self.status_code = status_code
                super().__init__(detail)
        save = Mock()
        reload = Mock()
        api = load_functions("src/web/settings_manager.py", {"update_generic_settings"}, {
            "Depends": lambda f: None, "_require_generic_settings_modify_admin": Mock(),
            "portable_mode": lambda: True, "PORTABLE_APP_ENV_ALLOWED_KEYS": {"RUN_HEADLESS"},
            "HTTPException": HttpError, "save_env_settings": save,
        })
        with patch.dict(sys.modules, {"src.config": SimpleNamespace(reload_config=reload)}):
            await api.update_generic_settings(SimpleNamespace(model_dump=lambda **k: {"RUN_HEADLESS": False}))
        save.assert_called_once_with({"RUN_HEADLESS": False}, ["RUN_HEADLESS"])
        reload.assert_called_once()
        save.reset_mock()
        for key in ("WEB_PASSWORD", "SERVER_PORT", "ENABLE_THINKING"):
            with self.assertRaises(HttpError) as raised:
                await api.update_generic_settings(SimpleNamespace(model_dump=lambda **k: {key: "fixture"}))
            self.assertEqual(raised.exception.status_code, 400)
        save.assert_not_called()

    async def test_export_endpoint_has_no_implicit_deletion(self):
        import sys
        from unittest.mock import patch
        exporter = SimpleNamespace(export_logs_package=Mock(return_value="synthetic.zip"), cleanup_old_exports=Mock())
        api = load_functions("src/web/log_manager.py", {"export_logs"}, {
            "Query": lambda default, **k: default,
            "os": SimpleNamespace(path=SimpleNamespace(exists=lambda p: True, basename=lambda p: p)),
        })
        with patch.dict(sys.modules, {"src.log_exporter": exporter,
                                      "fastapi.responses": SimpleNamespace(FileResponse=lambda **k: k)}):
            result = await api.export_logs()
        self.assertEqual(result["path"], "synthetic.zip")
        exporter.cleanup_old_exports.assert_not_called()


if __name__ == "__main__":
    unittest.main()
