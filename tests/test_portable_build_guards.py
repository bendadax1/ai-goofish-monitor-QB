"""Exercise offline preparation guards without downloads or filesystem mutations."""

import os
from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(os.name == "nt", "PowerShell build scripts")
class PortableBuildGuardTests(unittest.TestCase):
    def test_runtime_acceptance_reuses_bundle_and_never_publishes(self):
        source = (ROOT / "scripts/portable/build-launcher-prototype.ps1").read_text(encoding="utf-8")
        start = source.index("if ($RuntimeAcceptance) {")
        end = source.index("if ($Incremental", start)
        self.assertIn("$NoPublish = $true", source[start:end])
        self.assertIn("$BuildOnly -or $CoreOnly", source[start:end])
        self.assertIn("$PostgresIntegration = $true", source[start:end])
        self.assertIn("($RealE2E -or $RuntimeAcceptance) -and [string]::IsNullOrWhiteSpace($BundleRoot)", source)
        self.assertIn("@('--automatic-port-real-ui', $resolvedBundleRoot)", source)
        self.assertIn("@('--host-recovery-acceptance', $resolvedBundleRoot)", source)
        self.assertIn("@('--web-port-ui-acceptance')", source)

    def test_launcher_development_and_publish_are_separate(self):
        source = (ROOT / "scripts/portable/build-launcher-prototype.ps1").read_text(encoding="utf-8")
        self.assertIn("if (-not $CoreOnly -and -not $NoPublish)", source)
        self.assertIn("if ($BuildOnly) { $NoPublish = $true }", source)
        self.assertIn("[System.IO.FileShare]::None", source)
        self.assertIn("if (-not $BuildOnly)", source)
        project = (ROOT / "launcher/src/AiGoofish.Launcher.App/AiGoofish.Launcher.App.csproj").read_text(encoding="utf-8")
        self.assertIn('Name="RejectDevelopmentPublish"', project)
        program = (ROOT / "launcher/src/AiGoofish.Launcher.App/Program.cs").read_text(encoding="utf-8")
        run_start = program.index("private static int Run(")
        start = program.index("#if LAUNCHER_DEVELOPMENT", run_start)
        end = program.index("#endif", start)
        self.assertIn('args[0] == "--development"', program[start:end])

    def test_missing_archive_in_offline_mode_fails_before_curl(self):
        script = r'''
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$tokens = $null
$tree = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path (Get-Location) 'scripts/portable/prepare-python.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'parse failed' }
$guard = $tree.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf))')
}, $true)
if ($null -eq $guard) { throw 'guard not found' }
function Test-Path { param($LiteralPath, $PathType) return $false }
function curl.exe { throw 'NETWORK_MUST_NOT_RUN' }
$Offline = $true
$archivePath = 'synthetic-missing-cache.zip'
try {
    & ([scriptblock]::Create($guard.Extent.Text))
    throw 'EXPECTED_OFFLINE_FAILURE'
} catch {
    if ($_.Exception.Message -match 'NETWORK_MUST_NOT_RUN|EXPECTED_OFFLINE_FAILURE') { throw }
    if ($_.Exception.Message -notmatch 'Python ZIP') { throw }
    Write-Output 'OFFLINE_CACHE_GUARD_PASS'
}
'''
        completed = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            cwd=ROOT, capture_output=True, timeout=30,
        )
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", errors="replace"))
        self.assertIn(b"OFFLINE_CACHE_GUARD_PASS", completed.stdout)

    def test_disk_guards_precede_heavy_build_operations(self):
        source = (ROOT / "scripts/portable/build-launcher-prototype.ps1").read_text(encoding="utf-8")
        for operation in ("& $DotnetPath @restoreArguments", "& $DotnetPath build $buildTarget", "& $DotnetPath publish $appProjectPath"):
            position = source.index(operation)
            preceding = source[:position].rstrip()
            if "publish" in operation:
                preceding = preceding[:preceding.rfind("New-Item -ItemType Directory -Path $publishRoot")].rstrip()
            self.assertTrue(preceding.endswith("Assert-PortableBuildDiskFloor -Path $repositoryRoot"), operation)


if __name__ == "__main__":
    unittest.main()
