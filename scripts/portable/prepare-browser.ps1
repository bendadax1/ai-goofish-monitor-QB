[CmdletBinding()]
param(
    [switch]$Offline,
    [switch]$Smoke,
    [switch]$Headed
)

$ErrorActionPreference = 'Stop'
$scriptExitCode = 0
$environmentKeys = @('TEMP', 'TMP', 'PLAYWRIGHT_BROWSERS_PATH')
$previousEnvironment = @{}

function Assert-DiskFloor {
    param([string]$RepositoryRoot)

    $drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($RepositoryRoot))
    $freeRatio = $drive.AvailableFreeSpace / $drive.TotalSize
    Write-Host "磁盘检查：$($drive.Name) 可用 $($drive.AvailableFreeSpace) / $($drive.TotalSize) B（$([math]::Round($freeRatio * 100, 2))%）。"
    if ($drive.AvailableFreeSpace -lt 10GB -or $freeRatio -lt 0.05) {
        throw '磁盘可用空间已低于 10 GiB 或 5%，按仓库规则暂停 Chromium 下载/解压。'
    }
}

function Assert-Archive {
    param([string]$ArchivePath, [long]$ExpectedLength, [string]$ExpectedSha256)

    $archive = Get-Item -LiteralPath $ArchivePath -ErrorAction Stop
    if ($archive.Length -ne $ExpectedLength) {
        throw "Chromium ZIP 大小不符：$($archive.Length) B，预期 $ExpectedLength B。"
    }
    $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "Chromium ZIP SHA-256 不符：$actualHash。"
    }
    return $actualHash
}

function Get-SafeRelativePath {
    param([string]$EntryName, [string]$DestinationRoot, [string]$ExpectedRoot)

    if ([string]::IsNullOrWhiteSpace($EntryName) -or [System.IO.Path]::IsPathRooted($EntryName)) {
        throw 'Chromium ZIP 包含非法路径条目。'
    }
    $relative = $EntryName.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $segments = $relative.Split([System.IO.Path]::DirectorySeparatorChar)
    if ($segments -contains '..' -or $segments -contains '.' -or $relative.Contains(':') -or $segments[0] -ne $ExpectedRoot) {
        throw 'Chromium ZIP 包含路径穿越、ADS、绝对路径或意外根目录条目。'
    }
    $rootFull = [System.IO.Path]::GetFullPath($DestinationRoot)
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $rootFull $relative))
    $boundary = $rootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Chromium ZIP 解压目标越出隔离目录。'
    }
    return $candidate
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
    $lockPath = Join-Path $PSScriptRoot 'browser-runtime.lock.json'
    $verifierPath = Join-Path $PSScriptRoot 'verify-browser-runtime.py'
    $pythonRoot = Join-Path $repositoryRoot '.tmp\dependencies\portable-python\python-3.13.15-e67c6b779c81-windows-x64'
    $pythonExecutable = Join-Path $pythonRoot 'python.exe'
    $lock = Get-Content -Raw -Encoding UTF8 -LiteralPath $lockPath | ConvertFrom-Json
    if ($lock.schema_version -ne 1 -or $lock.component -ne 'browser-runtime' -or $lock.platform -ne 'win64' -or $lock.verification -ne 'playwright_cdn_https_local_sha256') {
        throw 'Chromium 组件锁格式或验证类型不受支持。'
    }
    if ($lock.signature_verified -ne $false) {
        throw '本工具不接受没有实际证据的 Chromium 签名验证声明。'
    }
    if (-not (Test-Path -LiteralPath $pythonExecutable -PathType Leaf) -or -not (Test-Path -LiteralPath $verifierPath -PathType Leaf)) {
        throw '内置 Python runtime 或浏览器验证器不存在。'
    }

    $dependencyRoot = Join-Path $repositoryRoot '.tmp\dependencies\portable-browser'
    $downloadRoot = Join-Path $dependencyRoot 'downloads'
    $tempRoot = Join-Path $dependencyRoot 'temp'
    $archivePath = Join-Path $downloadRoot ([string]$lock.file_name)
    $partialArchivePath = "$archivePath.part"
    $runtimeIdentity = "$($lock.playwright_version)-r$($lock.chromium_revision)-$($lock.sha256.Substring(0, 12))"
    $runtimeRoot = Join-Path $dependencyRoot "chromium-$runtimeIdentity-win64"
    $extractingRoot = "$runtimeRoot.extracting"

    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    $env:PLAYWRIGHT_BROWSERS_PATH = $dependencyRoot
    New-Item -ItemType Directory -Force -Path @($downloadRoot, $tempRoot) | Out-Null
    Assert-DiskFloor -RepositoryRoot $repositoryRoot

    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        $actualHash = Assert-Archive -ArchivePath $archivePath -ExpectedLength $lock.archive_bytes -ExpectedSha256 $lock.sha256
        Write-Host "复用已校验 Chromium ZIP：$archivePath"
    }
    else {
        if (Test-Path -LiteralPath $partialArchivePath -PathType Leaf) {
            $partialLength = (Get-Item -LiteralPath $partialArchivePath).Length
            if ($partialLength -eq $lock.archive_bytes) {
                $actualHash = Assert-Archive -ArchivePath $partialArchivePath -ExpectedLength $lock.archive_bytes -ExpectedSha256 $lock.sha256
                Move-Item -LiteralPath $partialArchivePath -Destination $archivePath
                Write-Host '复用已完成的 Chromium .part 文件并校验通过。'
            }
            elseif ($partialLength -gt $lock.archive_bytes) {
                throw '现有 Chromium .part 文件超过锁定大小，不自动覆盖或删除。'
            }
        }
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            if ($Offline) {
                throw '离线模式下没有已校验 Chromium ZIP；不发起网络下载。'
            }
            Write-Host "正在从 Playwright 官方 CDN 下载 $($lock.file_name)；最多 3 次传输尝试。"
            & curl.exe --fail --location --silent --show-error --retry 2 --retry-all-errors --retry-delay 2 --connect-timeout 20 --max-time 900 --continue-at - --output $partialArchivePath ([string]$lock.url)
            if ($LASTEXITCODE -ne 0) {
                throw "Chromium 下载失败，curl 退出码 $LASTEXITCODE；保留 .part 供有界续传。"
            }
            $actualHash = Assert-Archive -ArchivePath $partialArchivePath -ExpectedLength $lock.archive_bytes -ExpectedSha256 $lock.sha256
            Move-Item -LiteralPath $partialArchivePath -Destination $archivePath
            Write-Host "Chromium ZIP SHA-256 校验通过：$actualHash"
        }
    }

    $executablePath = Join-Path $runtimeRoot ([string]$lock.executable_relative_path)
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        Assert-DiskFloor -RepositoryRoot $repositoryRoot
        if ((Test-Path -LiteralPath $runtimeRoot) -or (Test-Path -LiteralPath $extractingRoot)) {
            throw 'Chromium runtime 或解压暂存目录存在但不完整；为保护未知或诊断文件，不自动删除或覆盖。'
        }
        New-Item -ItemType Directory -Path $extractingRoot | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($entry in $zip.Entries) {
                $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
                if ($unixType -eq 0xA000) {
                    throw 'Chromium ZIP 包含符号链接，已拒绝解压。'
                }
                $targetPath = Get-SafeRelativePath -EntryName $entry.FullName -DestinationRoot $extractingRoot -ExpectedRoot ([string]$lock.archive_root)
                if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) {
                    New-Item -ItemType Directory -Force -Path $targetPath | Out-Null
                    continue
                }
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetPath) | Out-Null
                $inputStream = $entry.Open()
                try {
                    $outputStream = [System.IO.File]::Create($targetPath)
                    try { $inputStream.CopyTo($outputStream) }
                    finally { $outputStream.Dispose() }
                }
                finally { $inputStream.Dispose() }
            }
        }
        finally { $zip.Dispose() }
        & $pythonExecutable -B $verifierPath --runtime-root $extractingRoot --archive $archivePath --lock $lockPath --write-manifest
        if ($LASTEXITCODE -ne 0) { throw 'Chromium runtime 身份清单写入失败；保留隔离暂存目录供诊断。' }
        Move-Item -LiteralPath $extractingRoot -Destination $runtimeRoot
        Write-Host "Chromium runtime 解压完成：$executablePath"
    }

    & $pythonExecutable -B $verifierPath --runtime-root $runtimeRoot --archive $archivePath --lock $lockPath --verify-manifest
    if ($LASTEXITCODE -ne 0) { throw '已有 Chromium runtime 身份或文件完整性验证失败；不自动覆盖。' }
    if ($Smoke) {
        $smokeArguments = @('-B', $verifierPath, '--runtime-root', $runtimeRoot, '--archive', $archivePath, '--lock', $lockPath, '--smoke')
        if ($Headed) { $smokeArguments += '--headed' }
        & $pythonExecutable @smokeArguments
        if ($LASTEXITCODE -ne 0) { throw 'Chromium 本地 smoke 失败。' }
    }
    $files = @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File -Force)
    $bytes = ($files | Measure-Object Length -Sum).Sum
    Write-Host "BROWSER_PLAYWRIGHT_VERSION=$($lock.playwright_version)"
    Write-Host "BROWSER_CHROMIUM_REVISION=$($lock.chromium_revision)"
    Write-Host "BROWSER_VERSION=$($lock.browser_version)"
    Write-Host "BROWSER_ARCHIVE_SHA256=$actualHash"
    Write-Host "BROWSER_RUNTIME=$runtimeRoot"
    Write-Host "BROWSER_EXECUTABLE_RELATIVE_PATH=$($lock.executable_relative_path)"
    Write-Host "BROWSER_RUNTIME_FILES=$($files.Count)"
    Write-Host "BROWSER_RUNTIME_BYTES=$bytes"
    Assert-DiskFloor -RepositoryRoot $repositoryRoot
}
catch {
    Write-Warning "隔离 Chromium 准备失败：$($_.Exception.Message)"
    $scriptExitCode = 1
}
finally {
    foreach ($key in $environmentKeys) {
        $environmentPath = "Env:$key"
        $previous = $previousEnvironment[$key]
        if ($previous.Exists) { [Environment]::SetEnvironmentVariable($key, $previous.Value, 'Process') }
        elseif (Test-Path -LiteralPath $environmentPath) {
            try { Remove-Item -LiteralPath $environmentPath -ErrorAction Stop }
            catch { Write-Warning "无法恢复不存在的环境变量 $key：$($_.Exception.Message)"; $scriptExitCode = 1 }
        }
    }
}

if ($scriptExitCode -ne 0) { exit $scriptExitCode }
