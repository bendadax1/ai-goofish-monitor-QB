"""Schema model imports must not discover application configuration."""

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class StorageImportIsolationTests(unittest.TestCase):
    def test_models_import_without_config_or_file_writes(self):
        repository = Path(__file__).resolve().parents[1]
        parent = repository / ".tmp" / "tests" / "storage-import"
        parent.mkdir(parents=True, exist_ok=True)
        try:
            with tempfile.TemporaryDirectory(dir=parent) as directory:
                environment = dict(os.environ)
                environment["PYTHONPATH"] = str(repository)
                environment["PYTHONDONTWRITEBYTECODE"] = "1"
                result = subprocess.run(
                    [sys.executable, "-B", "-c", """
import importlib.abc
import sys
from pathlib import Path
class NoConfig(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname in ('src.config', 'dotenv'):
            raise AssertionError('model import reached business configuration')
sys.meta_path.insert(0, NoConfig())
from src.storage.models import Base
assert 'users' in Base.metadata.tables
assert 'src.config' not in sys.modules
assert not list(Path.cwd().iterdir())
"""],
                    cwd=directory, env=environment, capture_output=True,
                    text=True, timeout=30,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
        finally:
            parent.rmdir()
