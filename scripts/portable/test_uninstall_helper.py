"""Exercise destructive uninstall behavior only against synthetic task-owned trees."""

import base64
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "launcher/src/AiGoofish.Launcher.App/UninstallHelper.ps1"


@unittest.skipUnless(os.name == "nt", "Windows portable uninstaller")
class UninstallHelperTests(unittest.TestCase):
    def setUp(self):
        parent = ROOT / ".tmp/tests/portable-uninstall"
        parent.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=parent)
        self.bundle = Path(self.temporary.name) / "中文 包"
        self.bundle.mkdir()
        files = {
            "AiGoofish.exe": b"launch",
            "uninstall.exe": b"uninstall",
            "app/nested/main.py": b"app",
            "runtime/python.exe": b"runtime",
            "browsers/chrome-win64/chrome.exe": b"browser",
            "postgres/bin/postgres.exe": b"postgres",
            "launcher/AiGoofish.Launcher.App.exe": b"launcher",
            "third-party-notices/inventory.json": b"{}",
        }
        for relative, content in files.items():
            target = self.bundle / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        current = {"format_version": 1, "release_id": "synthetic", "release_status": "preview-integration"}
        (self.bundle / "current.json").write_text(json.dumps(current), encoding="utf-8")
        (self.bundle / "bundle-manifest.json").write_text(json.dumps({
            "format_version": 1,
            "current": current,
            "files": [{"path": relative} for relative in files],
        }), encoding="utf-8")
        (self.bundle / "data/postgres").mkdir(parents=True)
        (self.bundle / "data/postgres/cluster.dat").write_bytes(b"user database")
        (self.bundle / "backups").mkdir()
        (self.bundle / "backups/backup.zip").write_bytes(b"user backup")

    def tearDown(self):
        self.temporary.cleanup()

    def run_helper(self, mode):
        environment = {
            **os.environ,
            "AIGOOFISH_UNINSTALL_ROOT": str(self.bundle),
            "AIGOOFISH_UNINSTALL_MODE": mode,
            "AIGOOFISH_UNINSTALL_PARENT_PID": "99999999",
        }
        encoded = base64.b64encode(HELPER.read_text(encoding="utf-8").encode("utf-16-le")).decode("ascii")
        return subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", encoded],
            env=environment, capture_output=True, timeout=120, creationflags=subprocess.CREATE_NO_WINDOW,
        )

    def test_default_removes_program_but_preserves_database_and_backup(self):
        result = self.run_helper("preserve")
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        self.assertFalse((self.bundle / "AiGoofish.exe").exists())
        self.assertFalse((self.bundle / "app").exists())
        self.assertEqual((self.bundle / "data/postgres/cluster.dat").read_bytes(), b"user database")
        self.assertEqual((self.bundle / "backups/backup.zip").read_bytes(), b"user backup")

    def test_separate_complete_mode_removes_entire_synthetic_bundle(self):
        result = self.run_helper("complete")
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        self.assertFalse(self.bundle.exists())

    def test_unknown_file_blocks_uninstall_without_removing_anything(self):
        (self.bundle / "app/unknown.txt").write_text("do not delete", encoding="utf-8")
        result = self.run_helper("complete")
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue((self.bundle / "AiGoofish.exe").exists())
        self.assertTrue((self.bundle / "data/postgres/cluster.dat").exists())

    def test_manifest_path_traversal_blocks_uninstall(self):
        manifest = self.bundle / "bundle-manifest.json"
        content = json.loads(manifest.read_text(encoding="utf-8"))
        content["files"].append({"path": "../outside.txt"})
        manifest.write_text(json.dumps(content), encoding="utf-8")
        result = self.run_helper("preserve")
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue((self.bundle / "AiGoofish.exe").exists())


if __name__ == "__main__":
    unittest.main()
