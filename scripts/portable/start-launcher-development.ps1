[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundleRoot,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,47}$')][string]$SessionId = ('dev-' + [Guid]::NewGuid().ToString('N')),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
try {
    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
    $bundlePath = (Resolve-Path -LiteralPath $BundleRoot).Path
    if (-not $NoBuild) {
        # Run in a child shell because the existing build entry owns its exit code/environment.
        $shellPath = (Get-Process -Id $PID).Path
        & $shellPath -NoProfile -File (Join-Path $PSScriptRoot 'build-launcher-prototype.ps1') -BuildOnly -Incremental -DevelopmentBuild
        if ($LASTEXITCODE -ne 0) { throw '开发编译失败；未启动窗口。' }
    }
    $app = Join-Path $repositoryRoot '.tmp\build\launcher-refactor\development\artifacts\bin\AiGoofish.Launcher.App\release_win-x64\AiGoofish.Launcher.App.dll'
    if (-not (Test-Path -LiteralPath $app -PathType Leaf)) { throw '缺少开发入口；请先执行开发编译。' }
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $sdkRoot = Join-Path $repositoryRoot '.tmp\dependencies\p0-b2\dotnet-sdk-10.0.401'
    $info.FileName = Join-Path $sdkRoot 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $info.FileName -PathType Leaf)) { throw '缺少锁定的隔离 SDK；未使用系统 dotnet 替代。' }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $repositoryRoot
    $info.EnvironmentVariables['DOTNET_ROOT'] = $sdkRoot
    $info.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $info.EnvironmentVariables['DOTNET_CLI_HOME'] = Join-Path $repositoryRoot '.tmp\dependencies\p0-b2\dotnet-home'
    $info.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    # ProcessStartInfo.ArgumentList is unavailable on Windows PowerShell 5.1.
    # Windows arguments cannot contain a quote; validate and quote every path explicitly.
    foreach ($value in @($repositoryRoot, $bundlePath)) {
        if ($value.Contains('"')) { throw '开发路径不能包含双引号。' }
    }
    $info.Arguments = '"{0}" --development "{1}" "{2}" {3}' -f $app, $repositoryRoot.TrimEnd('\'), $bundlePath.TrimEnd('\'), $SessionId
    $process = [System.Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw '无法创建开发 Launcher 进程。' }
    Write-Host "DEVELOPMENT_PID=$($process.Id)"
    Write-Host "DEVELOPMENT_SESSION=$SessionId"
    Write-Host '开发模式使用隔离合成数据；组件来自已验证候选；不会写候选包或替代发行验收。'
    $process.Dispose()
}
catch {
    Write-Error "开发启动失败：$($_.Exception.Message)"
    exit 1
}
