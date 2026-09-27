[CmdletBinding()]
param(
    [switch]$PythonIntegration,
    [switch]$RefreshLock,
    [switch]$CoreOnly,
    [switch]$NoPublish,
    [switch]$BuildOnly,
    [switch]$Incremental,
    [switch]$DevelopmentBuild,
    [switch]$PostgresIntegration,
    [switch]$RealE2E,
    [switch]$RuntimeAcceptance,
    [string]$BundleRoot,
    [string]$PostgresRoot,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$windowsNoWerErrorMode = [ordered]@{
    Changed = $false
    Ready = $false
    Previous = [uint32]0
}
$isWindowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function Assert-PortableBuildDiskFloor {
    param([string]$Path)
    $drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($Path))
    if ($drive.AvailableFreeSpace -lt 10GB -or $drive.AvailableFreeSpace / $drive.TotalSize -lt 0.05) {
        throw '磁盘可用空间低于 10 GiB 或 5%，暂停依赖还原、构建和发布。'
    }
}

function Enable-PortableBuildNoWerErrorMode {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$State,
        [Parameter(Mandatory = $true)][scriptblock]$GetErrorMode,
        [Parameter(Mandatory = $true)][scriptblock]$SetErrorMode
    )

    $currentMode = [uint32](& $GetErrorMode)
    $requestedMode = $currentMode -bor [uint32]0x2
    $State.Previous = [uint32](& $SetErrorMode $requestedMode)
    $State.Changed = $true

    $verifiedMode = [uint32](& $GetErrorMode)
    if (($verifiedMode -band [uint32]0x2) -eq 0) {
        throw '无法启用 SEM_NOGPFAULTERRORBOX；拒绝继续运行 dotnet。'
    }

    $State.Ready = $true
}

function Restore-PortableBuildNoWerErrorMode {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$State,
        [Parameter(Mandatory = $true)][scriptblock]$GetErrorMode,
        [Parameter(Mandatory = $true)][scriptblock]$SetErrorMode
    )

    if (-not $State.Changed) {
        return
    }

    [void](& $SetErrorMode ([uint32]$State.Previous))
    $restoredMode = [uint32](& $GetErrorMode)
    if ($restoredMode -ne [uint32]$State.Previous) {
        throw ('无法恢复 PowerShell 进程原始错误模式：预期 0x{0:X8}，实际 0x{1:X8}。' -f [uint32]$State.Previous, $restoredMode)
    }
}

$environmentKeys = @(
    'AIGOOFISH_REPOSITORY_ROOT',
    'DOTNET_CLI_HOME',
    'NUGET_PACKAGES',
    'NUGET_HTTP_CACHE_PATH',
    'TEMP',
    'TMP',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
    'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DOTNET_ROOT',
    'DOTNET_MULTILEVEL_LOOKUP',
    'AVALONIA_TELEMETRY_OPTOUT',
    'DOTNET_CLI_USE_MSBUILD_SERVER',
    'MSBUILDDISABLENODEREUSE',
    'AIGOOFISH_PROCESS_TEST_ROOT',
    'AIGOOFISH_PYTHON_TEST_ROOT',
    'AIGOOFISH_PG_TEST_ROOT',
    'AIGOOFISH_PG_INSTALLATION_ROOT'
)
$previousEnvironment = @{}
foreach ($key in $environmentKeys) {
    $environmentPath = "Env:$key"
    $previousEnvironment[$key] = @{
        Exists = Test-Path -LiteralPath $environmentPath
        Value = [Environment]::GetEnvironmentVariable($key, 'Process')
    }
}
$scriptExitCode = 0
$incrementalBuildLease = $null

try {
    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
    $solutionPath = Join-Path $repositoryRoot 'launcher\AiGoofish.Launcher.sln'
    $launcherRoot = Join-Path $repositoryRoot 'launcher'
    $nugetConfigPath = Join-Path $repositoryRoot 'launcher\NuGet.Config'
    $testProjectPath = Join-Path $repositoryRoot 'launcher\tests\AiGoofish.Launcher.Core.Tests\AiGoofish.Launcher.Core.Tests.csproj'
    $windowsTestProjectName = 'AiGoofish.Launcher.Platform.Windows.Tests'
    $postgresTestProjectName = 'AiGoofish.Launcher.Postgres.Tests'
    $smokeProjectPath = Join-Path $repositoryRoot 'launcher\tests\AiGoofish.Launcher.Ui.Smoke\AiGoofish.Launcher.Ui.Smoke.csproj'
    $appProjectPath = Join-Path $repositoryRoot 'launcher\src\AiGoofish.Launcher.App\AiGoofish.Launcher.App.csproj'
    $acceptanceId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 12))
    $buildRoot = Join-Path $repositoryRoot ".tmp\build\portable-acceptance\$acceptanceId"
    if ($BuildOnly) { $NoPublish = $true }
    if ($RuntimeAcceptance) {
        if ($BuildOnly -or $CoreOnly) { throw '-RuntimeAcceptance 要求完整编译并运行测试。' }
        $NoPublish = $true
        $PostgresIntegration = $true
    }
    if ($Incremental -and -not $NoPublish) { throw '-Incremental 只允许与 -NoPublish 或 -BuildOnly 一起使用。' }
    if ($DevelopmentBuild -and (-not $NoPublish -or $CoreOnly)) { throw '-DevelopmentBuild 需要完整工程且禁止发布；请指定 -NoPublish 或 -BuildOnly。' }
    if ($BuildOnly -and ($PythonIntegration -or $PostgresIntegration -or $RealE2E)) { throw '-BuildOnly 不执行测试，不可与集成验收开关组合。' }
    if ($Incremental) {
        $buildFlavor = if ($CoreOnly) { 'core' } elseif ($DevelopmentBuild) { 'development' } else { 'full' }
        $buildRoot = Join-Path $repositoryRoot ".tmp\build\launcher-refactor\$buildFlavor"
    }
    $dependencyRoot = Join-Path $repositoryRoot '.tmp\dependencies\p0-b2'
    $nugetDependencyRoot = Join-Path $repositoryRoot '.tmp\dependencies\p0-b1'
    $nugetPackageRoot = Join-Path $dependencyRoot 'nuget-packages'
    $artifactRoot = Join-Path $buildRoot 'artifacts'
    if ($Incremental) {
        Assert-PortableBuildDiskFloor -Path $repositoryRoot
        New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
        $incrementalBuildLease = [System.IO.File]::Open((Join-Path $buildRoot '.build.lock'), [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    }
    $publishRoot = Join-Path $launcherRoot "dist\launcher-p1-acceptance-$acceptanceId"
    $publishInventoryPath = Join-Path $buildRoot 'launcher-publish-inventory.json'
    $publishInventoryScript = Join-Path $repositoryRoot 'scripts\portable\launcher_publish_inventory.py'
    $verificationRoot = Join-Path $repositoryRoot ".tmp\tests\portable-acceptance\$acceptanceId"
    $dotnetRunnerPath = Join-Path $repositoryRoot 'scripts\portable\windows_dotnet_runner.py'
    $dotnetTestCaptureRoot = Join-Path $repositoryRoot ".tmp\tests\portable-dotnet-runner\$acceptanceId"
    if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
        $DotnetPath = Join-Path $dependencyRoot 'dotnet-sdk-10.0.401\dotnet.exe'
    }
    $DotnetPath = [System.IO.Path]::GetFullPath($DotnetPath)
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        throw "未找到隔离 .NET 10 SDK：$DotnetPath。请先运行 scripts/portable/install-dotnet-sdk.ps1。"
    }
    if (-not (Test-Path -LiteralPath $dotnetRunnerPath -PathType Leaf)) {
        throw "未找到 Windows dotnet 安全运行器：$dotnetRunnerPath"
    }
    $sdkRoot = Split-Path -Parent $DotnetPath

    function Invoke-SafeDotnetTest {
        param(
            [Parameter(Mandatory = $true)][string]$TestName,
            [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$TestId,
            [Parameter(Mandatory = $true)][string]$DllPath,
            [Parameter(Mandatory = $true)][ValidateRange(1, 3600)][int]$TimeoutSeconds,
            [Parameter(Mandatory = $true)][string]$FailureMessage,
            [ValidatePattern('^[A-Z0-9_:=.-]{1,128}$')][string]$ExpectedMarker,
            [string[]]$DotnetArguments = @()
        )

        $captureName = '{0}-{1}' -f $TestName, ([Guid]::NewGuid().ToString('N'))
        $captureDirectory = Join-Path $dotnetTestCaptureRoot $captureName
        $runnerArguments = @(
            '--dotnet-exe', $DotnetPath,
            '--dll', $DllPath,
            '--timeout-seconds', [string]$TimeoutSeconds,
            '--log-dir', $captureDirectory,
            '--test-id', $TestId,
            '--repository-root', $repositoryRoot
        )
        if ($ExpectedMarker) {
            $runnerArguments += @('--expected-marker', $ExpectedMarker)
        }
        & python -B $dotnetRunnerPath @runnerArguments -- @DotnetArguments
        $testExitCode = $LASTEXITCODE
        if ($testExitCode -ne 0) {
            throw "$FailureMessage，退出码 $testExitCode。"
        }
    }

    $env:DOTNET_CLI_HOME = Join-Path $dependencyRoot 'dotnet-home'
    $env:AIGOOFISH_REPOSITORY_ROOT = $repositoryRoot
    $env:NUGET_PACKAGES = $nugetPackageRoot
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $dependencyRoot 'nuget-http-cache'
    $env:TEMP = Join-Path $buildRoot 'temp'
    $env:TMP = $env:TEMP
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ROOT = $sdkRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $env:MSBUILDDISABLENODEREUSE = '1'

    New-Item -ItemType Directory -Force -Path @(
        $env:DOTNET_CLI_HOME,
        $env:NUGET_PACKAGES,
        $env:NUGET_HTTP_CACHE_PATH,
        $env:TEMP
    ) | Out-Null

    if ($isWindowsHost) {
        $nativeErrorModeType = 'AiGoofish.Portable.NativeErrorMode' -as [type]
        if (-not $nativeErrorModeType) {
            Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;

namespace AiGoofish.Portable
{
    public static class NativeErrorMode
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetErrorMode();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SetErrorMode(uint mode);
    }
}
'@
        }

        $getNativeErrorMode = { [AiGoofish.Portable.NativeErrorMode]::GetErrorMode() }
        $setNativeErrorMode = {
            param([uint32]$Mode)
            [AiGoofish.Portable.NativeErrorMode]::SetErrorMode($Mode)
        }
        Enable-PortableBuildNoWerErrorMode `
            -State $windowsNoWerErrorMode `
            -GetErrorMode $getNativeErrorMode `
            -SetErrorMode $setNativeErrorMode
    }

    $sdkVersion = (& $DotnetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.401') {
        throw "隔离 SDK 版本不符：'$sdkVersion'，预期 '10.0.401'。"
    }
    if ($RealE2E -and $CoreOnly) {
        throw '-RealE2E 需要完整 Launcher 构建，不能与 -CoreOnly 同时使用。'
    }
    if (($RealE2E -or $RuntimeAcceptance) -and [string]::IsNullOrWhiteSpace($BundleRoot)) {
        throw '真实 Host 验收需要通过 -BundleRoot 指定一个已经生成的完整便携目录。'
    }
    $resolvedBundleRoot = $null
    if ($RealE2E -or $RuntimeAcceptance) {
        $bundleCandidate = if ([System.IO.Path]::IsPathRooted($BundleRoot)) { $BundleRoot } else { Join-Path $repositoryRoot $BundleRoot }
        $resolvedBundleRoot = (Resolve-Path -LiteralPath $bundleCandidate).Path
        if (-not (Test-Path -LiteralPath (Join-Path $resolvedBundleRoot 'bundle-manifest.json') -PathType Leaf)) {
            throw "E2E 目录缺少 bundle-manifest.json：$resolvedBundleRoot"
        }
    }

    $restoreTarget = if ($CoreOnly) { $testProjectPath } else { $solutionPath }
    $buildTarget = $restoreTarget
    $restoreArguments = @(
        'restore',
        $restoreTarget,
        '--configfile',
        $nugetConfigPath,
        '--artifacts-path',
        $artifactRoot,
        '--property:NuGetAudit=false'
    )
    if ($RefreshLock) {
        $restoreArguments += '--force-evaluate'
    }
    else {
        $restoreArguments += '--locked-mode'
    }
    if (-not $CoreOnly) {
        $restoreArguments += @(
            '--runtime',
            'win-x64',
            '--property:SelfContained=true',
            "--property:RestoreAdditionalProjectFallbackFolders=$(Join-Path $nugetDependencyRoot 'nuget-packages')"
        )
    }

    Push-Location -LiteralPath $launcherRoot
    try {
        Assert-PortableBuildDiskFloor -Path $repositoryRoot
        & $DotnetPath @restoreArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Launcher 依赖还原失败，退出码 $LASTEXITCODE。"
        }

        Assert-PortableBuildDiskFloor -Path $repositoryRoot
        & $DotnetPath build $buildTarget --configuration Release --no-restore --artifacts-path $artifactRoot --property:UseSharedCompilation=false "--property:LauncherDevelopmentEnabled=$($DevelopmentBuild.IsPresent.ToString().ToLowerInvariant())"
        if ($LASTEXITCODE -ne 0) {
            throw "Launcher 构建失败，退出码 $LASTEXITCODE。"
        }

        if (-not $BuildOnly) {
        $testDlls = @(Get-ChildItem -LiteralPath (Join-Path $artifactRoot 'bin\AiGoofish.Launcher.Core.Tests') `
            -Filter 'AiGoofish.Launcher.Core.Tests.dll' -File -Recurse |
            Where-Object { $_.FullName -match '[\\/]release[\\/]' })
        if ($testDlls.Count -ne 1) {
            throw "预期找到一个 Core 测试入口，实际找到 $($testDlls.Count) 个。"
        }

        Invoke-SafeDotnetTest `
            -TestName 'core' `
            -TestId 'core' `
            -DllPath $testDlls[0].FullName `
            -TimeoutSeconds 180 `
            -FailureMessage 'Launcher Core 测试失败'

        if (-not $CoreOnly) {
            $httpContractDll = Join-Path $artifactRoot 'bin\AiGoofish.Launcher.Python.Tests\release\AiGoofish.Launcher.Python.Tests.dll'
            Invoke-SafeDotnetTest `
                -TestName 'http-contracts' `
                -TestId 'http-contracts' `
                -DllPath $httpContractDll `
                -TimeoutSeconds 90 `
                -FailureMessage 'Launcher loopback HTTP 契约测试失败' `
                -DotnetArguments @('--http-contracts')
            $pythonTestDll = Join-Path $artifactRoot 'bin\AiGoofish.Launcher.Python.Tests\release\AiGoofish.Launcher.Python.Tests.dll'
            $env:AIGOOFISH_PYTHON_TEST_ROOT = Join-Path $verificationRoot 'python-lifecycle'
            New-Item -ItemType Directory -Force -Path $env:AIGOOFISH_PYTHON_TEST_ROOT | Out-Null
            Invoke-SafeDotnetTest `
                -TestName 'python-lifecycle' `
                -TestId 'python-lifecycle' `
                -DllPath $pythonTestDll `
                -TimeoutSeconds 240 `
                -FailureMessage 'Launcher Python 生命周期测试失败'
            $windowsTestDlls = @(
                Get-ChildItem -LiteralPath (Join-Path $artifactRoot "bin\$windowsTestProjectName") `
                    -Filter "$windowsTestProjectName.dll" -File -Recurse |
                    Where-Object { $_.FullName -match '[\\/]release[\\/]' }
            )
            if ($windowsTestDlls.Count -ne 1) {
                throw "预期找到一个 Windows 进程测试入口，实际找到 $($windowsTestDlls.Count) 个。"
            }

            $env:AIGOOFISH_PROCESS_TEST_ROOT = Join-Path $verificationRoot 'windows-process'
            New-Item -ItemType Directory -Force -Path $env:AIGOOFISH_PROCESS_TEST_ROOT | Out-Null
            Invoke-SafeDotnetTest `
                -TestName 'windows-process' `
                -TestId 'windows-process' `
                -DllPath $windowsTestDlls[0].FullName `
                -TimeoutSeconds 240 `
                -FailureMessage 'Launcher Windows 进程测试失败'

            if ($PythonIntegration) {
                $env:AIGOOFISH_REPOSITORY_ROOT = $repositoryRoot
                Invoke-SafeDotnetTest `
                    -TestName 'python-real-stack' `
                    -TestId 'python-real-stack' `
                    -DllPath $pythonTestDll `
                    -TimeoutSeconds 600 `
                    -FailureMessage 'Launcher Python 真实栈集成失败' `
                    -ExpectedMarker 'PORTABLE_REAL_STACK=PASS' `
                    -DotnetArguments @('--real-stack')
            }

            if ($PostgresIntegration) {
                if ([string]::IsNullOrWhiteSpace($PostgresRoot)) {
                    $PostgresRoot = Join-Path $repositoryRoot '.tmp\dependencies\portable-pg\postgresql-17.11-3-windows-x64'
                }
                $PostgresRoot = [System.IO.Path]::GetFullPath($PostgresRoot)
                $postgresExecutable = Join-Path $PostgresRoot 'bin\postgres.exe'
                if (-not (Test-Path -LiteralPath $postgresExecutable -PathType Leaf)) {
                    throw "未找到锁定的 PostgreSQL 17.11 测试组件：$postgresExecutable"
                }

                $postgresTestDlls = @(
                    Get-ChildItem -LiteralPath (Join-Path $artifactRoot "bin\$postgresTestProjectName") `
                        -Filter "$postgresTestProjectName.dll" -File -Recurse |
                        Where-Object { $_.FullName -match '[\\/]release[\\/]' }
                )
                if ($postgresTestDlls.Count -ne 1) {
                    throw "预期找到一个 PostgreSQL 集成测试入口，实际找到 $($postgresTestDlls.Count) 个。"
                }

                $env:AIGOOFISH_PG_TEST_ROOT = Join-Path $verificationRoot 'postgres'
                $env:AIGOOFISH_PG_INSTALLATION_ROOT = $PostgresRoot
                New-Item -ItemType Directory -Force -Path $env:AIGOOFISH_PG_TEST_ROOT | Out-Null
                Invoke-SafeDotnetTest `
                    -TestName 'postgres-integration' `
                    -TestId 'postgres-integration' `
                    -DllPath $postgresTestDlls[0].FullName `
                    -TimeoutSeconds 900 `
                    -FailureMessage 'Launcher PostgreSQL 集成测试失败'
            }

            if ($RealE2E -or $RuntimeAcceptance) {
                Invoke-SafeDotnetTest `
                    -TestName 'bundle-host-pg-e2e' `
                    -TestId 'bundle-host-pg-e2e' `
                    -DllPath $pythonTestDll `
                    -TimeoutSeconds 1800 `
                    -FailureMessage '便携包真实 Host→PG E2E 失败或被运行环境阻断' `
                    -ExpectedMarker 'BUNDLE_ACCEPTANCE=PASS' `
                    -DotnetArguments @('--bundle-acceptance', $resolvedBundleRoot)
            }
        }

        if (-not $CoreOnly) {
            New-Item -ItemType Directory -Force -Path $verificationRoot | Out-Null
            $smokeDlls = @(
                Get-ChildItem -LiteralPath (Join-Path $artifactRoot 'bin\AiGoofish.Launcher.Ui.Smoke') `
                    -Filter 'AiGoofish.Launcher.Ui.Smoke.dll' -File -Recurse |
                    Where-Object { $_.Directory.Name -eq 'release' }
            )
            if ($smokeDlls.Count -ne 1) {
                throw "预期找到一个 UI Smoke 入口，实际找到 $($smokeDlls.Count) 个。"
            }

            Invoke-SafeDotnetTest `
                -TestName 'ui-smoke' `
                -TestId 'ui-smoke' `
                -DllPath $smokeDlls[0].FullName `
                -TimeoutSeconds 300 `
                -FailureMessage 'Launcher UI 离屏冒烟失败' `
                -ExpectedMarker 'UI_SMOKE_PASS' `
                -DotnetArguments @($verificationRoot)

            Invoke-SafeDotnetTest `
                -TestName 'startup-diagnostic' `
                -TestId 'startup-diagnostic' `
                -DllPath $smokeDlls[0].FullName `
                -TimeoutSeconds 60 `
                -FailureMessage 'Launcher 启动早期诊断验收失败' `
                -ExpectedMarker 'STARTUP_DIAGNOSTIC_UI_PASS' `
                -DotnetArguments @('--startup-diagnostic-acceptance')

            Invoke-SafeDotnetTest `
                -TestName 'web-port-ui' `
                -TestId 'web-port-ui' `
                -DllPath $smokeDlls[0].FullName `
                -TimeoutSeconds 120 `
                -FailureMessage 'Launcher Runtime/端口 UI 验收失败' `
                -ExpectedMarker 'WEB_PORT_UI_ACCEPTANCE=PASS' `
                -DotnetArguments @('--web-port-ui-acceptance')

            if ($RuntimeAcceptance) {
                # Real stacks are deliberately serial and reuse verified read-only components.
                Invoke-SafeDotnetTest `
                    -TestName 'runtime-automatic-port' -TestId 'runtime-automatic-port' `
                    -DllPath $smokeDlls[0].FullName -TimeoutSeconds 480 `
                    -FailureMessage '真实 Runtime 自动端口/取消/重启验收失败' `
                    -ExpectedMarker 'AUTO_PORT_REAL_UI=PASS' `
                    -DotnetArguments @('--automatic-port-real-ui', $resolvedBundleRoot)
                Invoke-SafeDotnetTest `
                    -TestName 'runtime-recovery' -TestId 'runtime-recovery' `
                    -DllPath $smokeDlls[0].FullName -TimeoutSeconds 480 `
                    -FailureMessage '真实 Runtime 进程恢复验收失败' `
                    -ExpectedMarker 'HOST_UI_RECOVERY=PASS' `
                    -DotnetArguments @('--host-recovery-acceptance', $resolvedBundleRoot)
            }

        }
        }

        if (-not $CoreOnly -and -not $NoPublish) {

            if (Test-Path -LiteralPath $publishRoot) {
                throw "本次唯一发布目录已存在，拒绝复用旧文件：$publishRoot"
            }
            Assert-PortableBuildDiskFloor -Path $repositoryRoot
            New-Item -ItemType Directory -Path $publishRoot | Out-Null
            & $DotnetPath publish $appProjectPath `
                --configuration Release `
                --runtime win-x64 `
                --self-contained true `
                --no-restore `
                --artifacts-path $artifactRoot `
                --property:UseSharedCompilation=false `
                --output $publishRoot
            if ($LASTEXITCODE -ne 0) {
                throw "Launcher win-x64 自包含发布失败，退出码 $LASTEXITCODE。"
            }

            $publishFiles = Get-ChildItem -LiteralPath $publishRoot -File -Recurse
            $publishBytes = ($publishFiles | Measure-Object -Property Length -Sum).Sum
            $publishedExe = Join-Path $publishRoot 'AiGoofish.Launcher.App.exe'
            foreach ($requiredFile in @(
                $publishedExe,
                (Join-Path $publishRoot 'AiGoofish.Launcher.App.runtimeconfig.json'),
                (Join-Path $publishRoot 'coreclr.dll'),
                (Join-Path $publishRoot 'hostfxr.dll')
            )) {
                if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
                    throw "自包含发布缺少必需文件：$requiredFile"
                }
            }

            $verificationStdout = Join-Path $verificationRoot 'package-verify.stdout.txt'
            $verificationStderr = Join-Path $verificationRoot 'package-verify.stderr.txt'
            $verificationProcess = Start-Process `
                -FilePath $publishedExe `
                -ArgumentList '--verify-package' `
                -WindowStyle Hidden `
                -RedirectStandardOutput $verificationStdout `
                -RedirectStandardError $verificationStderr `
                -Wait `
                -PassThru
            $packageVerification = @(Get-Content -LiteralPath $verificationStdout -Encoding UTF8)
            if ($verificationProcess.ExitCode -ne 0 -or $packageVerification -notcontains 'PACKAGE_VERIFICATION=PASS') {
                throw "自包含入口验证失败，退出码 $($verificationProcess.ExitCode)；输出见 $verificationRoot。"
            }

            & python -B $publishInventoryScript `
                --repository-root $repositoryRoot `
                --launcher-root $publishRoot `
                --acceptance-id $acceptanceId
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $publishInventoryPath -PathType Leaf)) {
                throw "Launcher 发布目录独立清单生成失败，退出码 $LASTEXITCODE。"
            }

            $packageVerification | ForEach-Object { Write-Host "PUBLISHED_$_" }
            Write-Host "PUBLISH_PATH=$publishRoot"
            Write-Host "PUBLISH_INVENTORY=$publishInventoryPath"
            Write-Host "PUBLISH_FILES=$($publishFiles.Count)"
            Write-Host "PUBLISH_BYTES=$publishBytes"
            Write-Host "BUILD_ARTIFACT_ROOT=$artifactRoot"
            Write-Host "UI_SMOKE_PATH=$verificationRoot"
        }

        $scopeLabel = if ($CoreOnly) { 'Core' } else { '完整原型' }
        Write-Host "BUILD_ARTIFACT_ROOT=$artifactRoot"
        if ($BuildOnly) { Write-Host "Launcher $scopeLabel 仅编译通过；未运行测试，未发布。" }
        elseif ($NoPublish) { Write-Host "Launcher $scopeLabel 构建和所选验收通过；未发布。" }
        else { Write-Host "Launcher $scopeLabel 构建和验收通过。" }
    }
    finally {
        Pop-Location
    }
}
catch {
    Write-Warning "Launcher 验收失败：$($_.Exception.Message)"
    $scriptExitCode = 1
}
finally {
    if ($null -ne $incrementalBuildLease) { $incrementalBuildLease.Dispose() }
    if ($windowsNoWerErrorMode.Ready -and $DotnetPath -and (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        try {
            & $DotnetPath build-server shutdown
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "隔离 SDK 构建服务器关闭失败，退出码 $LASTEXITCODE。"
                $scriptExitCode = 1
            }
        }
        catch {
            Write-Warning "隔离 SDK 构建服务器关闭失败：$($_.Exception.Message)"
            $scriptExitCode = 1
        }
    }

    if ($windowsNoWerErrorMode.Changed) {
        try {
            Restore-PortableBuildNoWerErrorMode `
                -State $windowsNoWerErrorMode `
                -GetErrorMode $getNativeErrorMode `
                -SetErrorMode $setNativeErrorMode
        }
        catch {
            Write-Warning "无法恢复 PowerShell 进程错误模式：$($_.Exception.Message)"
            $scriptExitCode = 1
        }
    }

    foreach ($key in $environmentKeys) {
        $environmentPath = "Env:$key"
        $previous = $previousEnvironment[$key]
        if ($previous.Exists) {
            [Environment]::SetEnvironmentVariable($key, $previous.Value, 'Process')
        }
        elseif (Test-Path -LiteralPath $environmentPath) {
            try {
                Remove-Item -LiteralPath $environmentPath -ErrorAction Stop
            }
            catch {
                Write-Warning "无法恢复不存在的环境变量 $key：$($_.Exception.Message)"
                $scriptExitCode = 1
            }
        }
    }

    if ($dotnetTestCaptureRoot -and (Test-Path -LiteralPath $dotnetTestCaptureRoot -PathType Container)) {
        try {
            $remainingCaptures = @(Get-ChildItem -LiteralPath $dotnetTestCaptureRoot -Force -ErrorAction Stop)
            if ($remainingCaptures.Count -eq 0) {
                Remove-Item -LiteralPath $dotnetTestCaptureRoot -ErrorAction Stop
            }
            else {
                Write-Warning "dotnet 运行器捕获目录仍有内容，保留以供诊断：$dotnetTestCaptureRoot"
                $scriptExitCode = 1
            }
        }
        catch {
            Write-Warning "无法检查或移除本次空的 dotnet 捕获目录：$dotnetTestCaptureRoot；$($_.Exception.Message)"
            $scriptExitCode = 1
        }
    }
}

if ($scriptExitCode -ne 0) {
    exit $scriptExitCode
}
