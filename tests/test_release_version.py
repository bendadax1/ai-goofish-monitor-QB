"""Keep backend and Windows Launcher release metadata aligned without packaging."""

from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

from src.version import VERSION, VERSION_HISTORY


ROOT = Path(__file__).resolve().parents[1]


class ReleaseVersionTests(unittest.TestCase):
    def test_backend_history_describes_current_release(self):
        self.assertEqual(VERSION_HISTORY[0]["version"], VERSION)
        self.assertTrue(VERSION_HISTORY[0]["changes"])

    def test_launcher_and_backend_versions_match(self):
        project = ET.parse(ROOT / "launcher/src/AiGoofish.Launcher.App/AiGoofish.Launcher.App.csproj")
        version = VERSION.removeprefix("V")
        self.assertEqual(project.findtext(".//Version"), version)
        self.assertEqual(project.findtext(".//InformationalVersion"), version)
        for property_name in ("AssemblyVersion", "FileVersion"):
            self.assertEqual(project.findtext(f".//{property_name}"), version.split("-", 1)[0])


if __name__ == "__main__":
    unittest.main()
