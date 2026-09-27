[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sdkVersion = '10.0.401'
$sdkFileName = "dotnet-sdk-$sdkVersion-win-x64.zip"
$sdkUrl = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/$sdkFileName"
$expectedLength = 300608304L
$expectedSha512 = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
$environmentKeys = @(
    'DOTNET_CLI_HOME',
    'NUGET_PACKAGES',
    'NUGET_HTTP_CACHE_PATH',
    'TEMP',
    'TMP',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
    'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DOTNET_ROOT',
    'DOTNET_MULTILEVEL_LOOKUP'
)
$previousEnvironment = @{}
$scriptExitCode = 0

function Assert-DiskFloor {
    param([string]$RepositoryRoot)

    $driveRoot = [System.IO.Path]::GetPathRoot($RepositoryRoot)
    $drive = [System.IO.DriveInfo]::new($driveRoot)
    $freeRatio = $drive.AvailableFreeSpace / $drive.TotalSize
    Write-Host "磁盘检查：$($drive.Name) 可用 $($drive.AvailableFreeSpace) / $($drive.TotalSize) B（$([math]::Round($freeRatio * 100, 2))%）。"
    if ($drive.AvailableFreeSpace -lt 10GB -or $freeRatio -lt 0.05) {
        throw '磁盘可用空间已低于 10 GiB 或 5%，按仓库规则暂停 SDK 下载/解压。'
    }
}

foreach ($key in $environmentKeys) {
    $environmentPath = "Env:$key"
    $previousEnvironment[$key] = @{
        Exists = Test-Path -LiteralPath $environmentPath
        Value = [Environment]::GetEnvironmentVariable($key, 'Process')
    }
}

try {
    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
    $dependencyRoot = Join-Path $repositoryRoot '.tmp\dependencies\p0-b2'
    $downloadRoot = Join-Path $dependencyRoot 'downloads'
    $tempRoot = Join-Path $dependencyRoot 'temp'
    $archivePath = Join-Path $downloadRoot $sdkFileName
    $partialArchivePath = "$archivePath.part"
    $sdkRoot = Join-Path $dependencyRoot "dotnet-sdk-$sdkVersion"
    $extractingRoot = "$sdkRoot.extracting"
    $dotnetExecutable = Join-Path $sdkRoot 'dotnet.exe'

    $env:DOTNET_CLI_HOME = Join-Path $dependencyRoot 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $dependencyRoot 'nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $dependencyRoot 'nuget-http-cache'
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ROOT = $sdkRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    New-Item -ItemType Directory -Force -Path @(
        $downloadRoot,
        $tempRoot,
        $env:DOTNET_CLI_HOME,
        $env:NUGET_PACKAGES,
        $env:NUGET_HTTP_CACHE_PATH
    ) | Out-Null

    Assert-DiskFloor -RepositoryRoot $repositoryRoot

    $archiveReady = $false
    if (Test-Path -LiteralPath $archivePath) {
        $existingArchive = Get-Item -LiteralPath $archivePath
        if ($existingArchive.Length -ne $expectedLength) {
            throw "已有 SDK ZIP 大小不符：$($existingArchive.Length) B，预期 $expectedLength B。请先核验准确文件再处理。"
        }

        $existingHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash.ToLowerInvariant()
        if ($existingHash -ne $expectedSha512) {
            throw "已有 SDK ZIP SHA-512 不符：$existingHash。请先核验准确文件再处理。"
        }

        $archiveReady = $true
        Write-Host "复用已校验 SDK ZIP：$archivePath"
    }

    if (-not $archiveReady) {
        Write-Host "正在从 Microsoft 官方源下载 $sdkFileName；最多 3 次传输尝试。"
        & curl.exe `
            --fail `
            --location `
            --silent `
            --show-error `
            --retry 2 `
            --retry-all-errors `
            --retry-delay 2 `
            --retry-max-time 1800 `
            --connect-timeout 20 `
            --max-time 900 `
            --continue-at - `
            --output $partialArchivePath `
            $sdkUrl
        if ($LASTEXITCODE -ne 0) {
            throw "SDK 下载失败，curl 退出码 $LASTEXITCODE；可保留 .part 文件供有界续传。"
        }

        $downloadedArchive = Get-Item -LiteralPath $partialArchivePath
        if ($downloadedArchive.Length -ne $expectedLength) {
            throw "SDK 下载大小不符：$($downloadedArchive.Length) B，预期 $expectedLength B。"
        }

        $downloadedHash = (Get-FileHash -LiteralPath $partialArchivePath -Algorithm SHA512).Hash.ToLowerInvariant()
        if ($downloadedHash -ne $expectedSha512) {
            throw "SDK 下载 SHA-512 不符：$downloadedHash。"
        }

        Move-Item -LiteralPath $partialArchivePath -Destination $archivePath
        Write-Host "SDK ZIP 校验通过：SHA-512 $downloadedHash"
    }

    if (Test-Path -LiteralPath $dotnetExecutable) {
        $installedVersion = (& $dotnetExecutable --version).Trim()
        if ($LASTEXITCODE -ne 0 -or $installedVersion -ne $sdkVersion) {
            throw "已有隔离 SDK 验证失败：版本 '$installedVersion'，预期 '$sdkVersion'。"
        }

        Write-Host "复用已校验隔离 SDK：$dotnetExecutable"
    }
    else {
        Assert-DiskFloor -RepositoryRoot $repositoryRoot
        if ((Test-Path -LiteralPath $sdkRoot) -or (Test-Path -LiteralPath $extractingRoot)) {
            throw 'SDK 目标目录或解压暂存目录已存在但不完整；为避免误删，本脚本不自动覆盖。'
        }

        New-Item -ItemType Directory -Path $extractingRoot | Out-Null
        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractingRoot
        $stagedDotnet = Join-Path $extractingRoot 'dotnet.exe'
        if (-not (Test-Path -LiteralPath $stagedDotnet -PathType Leaf)) {
            throw 'SDK 解压后未找到 dotnet.exe，保留暂存目录供诊断。'
        }

        $stagedVersion = (& $stagedDotnet --version).Trim()
        if ($LASTEXITCODE -ne 0 -or $stagedVersion -ne $sdkVersion) {
            throw "SDK 解压验证失败：版本 '$stagedVersion'，预期 '$sdkVersion'。"
        }

        Move-Item -LiteralPath $extractingRoot -Destination $sdkRoot
        Write-Host "隔离 SDK 解压完成：$dotnetExecutable"
    }

    $archiveBytes = (Get-Item -LiteralPath $archivePath).Length
    $sdkFiles = Get-ChildItem -LiteralPath $sdkRoot -File -Recurse -Force
    $sdkBytes = ($sdkFiles | Measure-Object -Property Length -Sum).Sum
    Write-Host "SDK_ARCHIVE_BYTES=$archiveBytes"
    Write-Host "SDK_EXTRACTED_FILES=$($sdkFiles.Count)"
    Write-Host "SDK_EXTRACTED_BYTES=$sdkBytes"
    Assert-DiskFloor -RepositoryRoot $repositoryRoot
}
catch {
    Write-Warning "隔离 .NET SDK 准备失败：$($_.Exception.Message)"
    $scriptExitCode = 1
}
finally {
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
}

if ($scriptExitCode -ne 0) {
    exit $scriptExitCode
}
