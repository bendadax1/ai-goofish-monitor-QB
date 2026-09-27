"""Focused tests for the fail-closed portable release scanner."""

import hashlib
import importlib.util
import json
from pathlib import Path
from pathlib import PurePosixPath
import sys
import tempfile
import unittest
from copy import deepcopy
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("portable_release_scanner", Path(__file__).with_name("scan-portable-release.py"))
scanner = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = scanner
SPEC.loader.exec_module(scanner)


class PortableReleaseScannerTests(unittest.TestCase):
    def setUp(self):
        parent = ROOT / ".tmp" / "tests" / "portable-release-scan"
        parent.mkdir(parents=True, exist_ok=True)
        self._temporary = tempfile.TemporaryDirectory(dir=parent)
        self.root = Path(self._temporary.name)

    def tearDown(self):
        self._temporary.cleanup()
        parent = self.root.parent
        if parent.exists() and not any(parent.iterdir()):
            parent.rmdir()

    def write_bundle(self, files=None, *, inventory=None):
        files = dict(files or {"app/fixture/readme.txt": b"preview\n", "runtime/python/python.exe": b"py",
                               "browsers/chromium/chrome.exe": b"chrome", "postgres/bin/postgres.exe": b"pg"})
        notice = inventory if inventory is not None else self.valid_notice_inventory()
        for entry in notice.get("components", []):
            if not isinstance(entry, dict):
                continue
            target = entry.get("file")
            if isinstance(target, str):
                files.setdefault(target, self._notice_payload(entry))
        entries = []
        for relative, content in files.items():
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
            entries.append({"path": relative, "size": len(content), "sha256": hashlib.sha256(content).hexdigest()})
        current = {"format_version": 1, "app": {"relative_dir": "app/fixture"},
                   "runtime": {"relative_dir": "runtime/python"}, "browser": {"relative_dir": "browsers/chromium"},
                   "postgres": {"relative_dir": "postgres"}, "launcher": {"relative_dir": "."}}
        (self.root / "current.json").write_text(json.dumps(current), encoding="utf-8")
        notice_path = self.root / "third-party-notices" / "inventory.json"
        notice_path.parent.mkdir(parents=True, exist_ok=True)
        notice_path.write_text(json.dumps(notice), encoding="utf-8")
        # The inventory itself is included in real bundle manifests.
        raw = notice_path.read_bytes()
        entries.append({"path": "third-party-notices/inventory.json", "size": len(raw), "sha256": hashlib.sha256(raw).hexdigest()})
        (self.root / "bundle-manifest.json").write_text(json.dumps({"format_version": 1, "current": current, "files": entries}), encoding="utf-8")

    @staticmethod
    def _notice_payload(entry):
        source_inventory = json.loads((ROOT / "scripts/portable/third-party-notices/inventory.json").read_text(encoding="utf-8"))
        source = next((item for item in source_inventory["components"]
                       if item["component"] == entry.get("component")), None)
        if source is not None:
            return (ROOT / source["notice_source"]).read_bytes()
        return f"{entry.get('component')} notice\n".encode("utf-8")

    @staticmethod
    def valid_notice_inventory():
        source = json.loads((ROOT / "scripts/portable/third-party-notices/inventory.json").read_text(encoding="utf-8"))
        components = []
        for original in source["components"]:
            entry = {key: value for key, value in original.items()
                     if key not in {"notice_source", "evidence_sha256"}}
            target = original.get("bundle_target")
            if not target:
                target = f"third-party-notices/{original['component']}/{PurePosixPath(original['notice_source']).name}"
            entry["file"] = target
            payload = (ROOT / original["notice_source"]).read_bytes()
            entry["sha256"] = hashlib.sha256(payload).hexdigest()
            components.append(entry)
        return {"format_version": 1, "notice_complete": True, "components": components}

    def test_valid_manifest_tree_passes(self):
        self.write_bundle()
        self.assertEqual("PASS", scanner.scan(self.root)["status"])

    def test_unknown_file_is_reported(self):
        self.write_bundle()
        (self.root / "unlisted.dll").write_bytes(b"extra")
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("unknown-file", codes)

    def test_missing_and_changed_files_are_reported(self):
        self.write_bundle()
        (self.root / "app/fixture/readme.txt").write_bytes(b"changed")
        (self.root / "third-party-notices/inventory.json").unlink()
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertTrue({"manifest-mismatch", "missing-file", "license-inventory-missing"}.issubset(codes))

    def test_sensitive_data_bom_and_data_directory_are_reported(self):
        self.write_bundle()
        (self.root / "app/fixture/.env").write_text("x=y", encoding="utf-8")
        state = self.root / "state" / "snapshot.json"
        state.parent.mkdir()
        state.write_bytes(b"\xef\xbb\xbf{}")
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertTrue({"sensitive-file", "user-data-path", "bom", "unknown-file"}.issubset(codes))

    def test_license_gaps_block_preview(self):
        self.write_bundle(inventory={"format_version": 1, "notice_complete": False,
                                     "components": [{"component": "chromium", "is_gap": True}]})
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("license-gap", codes)

    def test_true_notice_complete_with_empty_inventory_is_rejected(self):
        self.write_bundle(inventory={"format_version": 1, "notice_complete": True, "components": []})
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("license-inventory-invalid", codes)

    def test_deleted_notice_entry_is_rejected_even_when_complete_flag_stays_true(self):
        inventory = self.valid_notice_inventory()
        inventory["components"].pop()
        self.write_bundle(inventory=inventory)
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("license-inventory-invalid", codes)

    def test_deleted_chromium_onnx_notice_is_rejected(self):
        inventory = self.valid_notice_inventory()
        inventory["components"] = [item for item in inventory["components"]
                                   if item["component"] != "chromium-onnxruntime-headers"]
        self.write_bundle(inventory=inventory)
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("license-inventory-invalid", codes)

    def test_duplicate_notice_entry_is_rejected(self):
        inventory = self.valid_notice_inventory()
        inventory["components"].append(deepcopy(inventory["components"][-1]))
        self.write_bundle(inventory=inventory)
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("license-inventory-invalid", codes)

    def test_forged_wheel_version_is_rejected(self):
        inventory = self.valid_notice_inventory()
        wheel = next(item for item in inventory["components"] if item["component"].startswith("python-wheel-"))
        wheel["version"] = "999.0.0"
        self.write_bundle(inventory=inventory)
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("license-inventory-invalid", codes)

    def test_chromium_notice_cannot_be_redirected_to_an_application_file(self):
        inventory = self.valid_notice_inventory()
        chromium = next(item for item in inventory["components"] if item["component"] == "chromium")
        chromium["file"] = "app/fixture/readme.txt"
        chromium["sha256"] = hashlib.sha256(b"preview\n").hexdigest()
        self.write_bundle(inventory=inventory)
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("license-inventory-invalid", codes)

    def test_notice_hash_must_match_packaged_file_manifest(self):
        inventory = self.valid_notice_inventory()
        inventory["components"][0]["sha256"] = "0" * 64
        self.write_bundle(inventory=inventory)
        codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("license-inventory-invalid", codes)

    def test_unsafe_manifest_path_is_rejected(self):
        self.write_bundle()
        manifest = json.loads((self.root / "bundle-manifest.json").read_text(encoding="utf-8"))
        manifest["files"][0]["path"] = "../escape"
        (self.root / "bundle-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(scanner.ScanError):
            scanner.scan(self.root)

    def test_current_descriptor_must_match_manifest_snapshot(self):
        self.write_bundle()
        (self.root / "current.json").write_text('{"format_version":1,"changed":true}', encoding="utf-8")
        with self.assertRaises(scanner.ScanError):
            scanner.scan(self.root)

    def test_malformed_manifest_shape_fails_closed(self):
        self.write_bundle()
        (self.root / "bundle-manifest.json").write_text('{"format_version":1,"current":{},"files":{}}', encoding="utf-8")
        with self.assertRaises(scanner.ScanError):
            scanner.scan(self.root)

    def test_only_exact_hash_locked_third_party_notice_can_use_legacy_encoding(self):
        relative = "postgres/postgresql-17.11-3-windows-x64/commandlinetools_3rd_party_licenses.txt"
        legacy_notice = b"copyright \x81 legacy"
        digest = hashlib.sha256(legacy_notice).hexdigest()
        self.write_bundle(files={"app/fixture/readme.txt": b"preview\n", "runtime/python/python.exe": b"py",
                                 "browsers/chromium/chrome.exe": b"chrome", "postgres/bin/postgres.exe": b"pg",
                                 relative: legacy_notice})
        with patch.dict(scanner._PINNED_LEGACY_NOTICE_HASHES, {relative: digest}):
            self.assertEqual("PASS", scanner.scan(self.root)["status"])
            (self.root / relative).write_bytes(b"tampered \x81")
            codes = {item["code"] for item in scanner.scan(self.root)["findings"]}
        self.assertIn("third-party-encoding-exception-hash-mismatch", codes)

    def test_other_invalid_utf8_notice_is_not_exempt(self):
        self.write_bundle()
        relative = "third-party-notices/other.txt"
        target = self.root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(b"\x81")
        findings = scanner.scan(self.root)["findings"]
        self.assertIn({"code": "invalid-utf8", "path": relative}, findings)


if __name__ == "__main__":
    unittest.main()
