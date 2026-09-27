[CmdletBinding()]
param(
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$scriptExitCode = 0
$environmentKeys = @(
    'UV_CACHE_DIR',
    'UV_INDEX_URL',
    'UV_NO_CONFIG',
    'UV_LINK_MODE',
    'VIRTUAL_ENV',
    'PYTHONHOME',
    'PYTHONPATH',
    'PIP_CACHE_DIR',
    'PIP_CONFIG_FILE',
    'PIP_REQUIRE_VIRTUALENV',
    'TEMP',
    'TMP'
)
$previousEnvironment = @{}

function Assert-DiskFloor {
    param([string]$RepositoryRoot)

    $driveRoot = [System.IO.Path]::GetPathRoot($RepositoryRoot)
    $drive = [System.IO.DriveInfo]::new($driveRoot)
    $freeRatio = $drive.AvailableFreeSpace / $drive.TotalSize
    Write-Host "磁盘检查：$($drive.Name) 可用 $($drive.AvailableFreeSpace) / $($drive.TotalSize) B（$([math]::Round($freeRatio * 100, 2))%）。"
    if ($drive.AvailableFreeSpace -lt 10GB -or $freeRatio -lt 0.05) {
        throw '磁盘可用空间已低于 10 GiB 或 5%，按仓库规则暂停 Python 下载/解压/依赖安装。'
    }
}

function Assert-Archive {
    param(
        [string]$ArchivePath,
        [long]$ExpectedLength,
        [string]$ExpectedSha256
    )

    $archive = Get-Item -LiteralPath $ArchivePath -ErrorAction Stop
    if ($archive.Length -ne $ExpectedLength) {
        throw "Python ZIP 大小不符：$($archive.Length) B，预期 $ExpectedLength B。"
    }
    $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "Python ZIP SHA-256 不符：$actualHash。"
    }
    return $actualHash
}

function Get-SafeRelativePath {
    param(
        [string]$EntryName,
        [string]$DestinationRoot
    )

    if ([string]::IsNullOrWhiteSpace($EntryName) -or [System.IO.Path]::IsPathRooted($EntryName)) {
        throw 'Python ZIP 包含非法路径条目。'
    }
    $relative = $EntryName.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $segments = $relative.Split([System.IO.Path]::DirectorySeparatorChar)
    if ($segments -contains '..' -or $segments -contains '.' -or $relative.Contains(':')) {
        throw 'Python ZIP 包含路径穿越或绝对路径条目。'
    }
    $destinationRootFull = [System.IO.Path]::GetFullPath($DestinationRoot)
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $destinationRootFull $relative))
    $boundary = $destinationRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Python ZIP 解压目标越出隔离目录。'
    }
    return $candidate
}

function Get-IdentityHash {
    param(
        [string]$ArchiveSha256,
        [string]$RequirementsSha256
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes("$ArchiveSha256`n$RequirementsSha256`n")
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $hasher.Dispose()
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
    $lockPath = Join-Path $PSScriptRoot 'python-runtime.lock.json'
    $requirementsPath = Join-Path $PSScriptRoot 'requirements-python.lock.txt'
    $runtimeVerifier = Join-Path $PSScriptRoot 'verify-python-runtime.py'
    $lock = Get-Content -Raw -Encoding UTF8 -LiteralPath $lockPath | ConvertFrom-Json
    if ($lock.schema_version -ne 1 -or $lock.component -ne 'python-runtime' -or $lock.platform -ne 'windows-x64' -or $lock.verification -ne 'python_org_published_sha256') {
        throw 'Python 组件锁格式或验证类型不受支持。'
    }
    if ($lock.signature_verified -ne $false) {
        throw '本工具不接受没有实际证据的 Python 签名验证声明。'
    }
    if (-not (Test-Path -LiteralPath $requirementsPath -PathType Leaf)) {
        throw 'Python requirements 锁文件不存在。'
    }
    if (-not (Test-Path -LiteralPath $runtimeVerifier -PathType Leaf)) {
        throw 'Python runtime 身份验证工具不存在。'
    }

    $dependencyRoot = Join-Path $repositoryRoot '.tmp\dependencies\portable-python'
    $downloadRoot = Join-Path $dependencyRoot 'downloads'
    $cacheRoot = Join-Path $dependencyRoot 'uv-cache'
    $tempRoot = Join-Path $dependencyRoot 'temp'
    $archivePath = Join-Path $downloadRoot ([string]$lock.file_name)
    $partialArchivePath = "$archivePath.part"
    $uvCommand = Get-Command uv.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $uvCommand) {
        $uvCommand = Get-Command uv -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if ($null -eq $uvCommand) {
        throw '未找到已批准的 uv 工具，未尝试安装或修改系统工具。'
    }
    $uvVersion = (& $uvCommand.Source --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $uvVersion -notmatch '^uv 0\.10\.8(?:\s|$)') {
        throw "uv 版本验证失败：'$uvVersion'，预期 0.10.8。"
    }

    $env:UV_CACHE_DIR = $cacheRoot
    $env:UV_INDEX_URL = 'https://pypi.org/simple'
    $env:UV_NO_CONFIG = '1'
    $env:UV_LINK_MODE = 'copy'
    $env:VIRTUAL_ENV = ''
    $env:PYTHONHOME = ''
    $env:PYTHONPATH = ''
    $env:PIP_CACHE_DIR = ''
    $env:PIP_CONFIG_FILE = ''
    $env:PIP_REQUIRE_VIRTUALENV = ''
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    New-Item -ItemType Directory -Force -Path @($downloadRoot, $cacheRoot, $tempRoot) | Out-Null

    Assert-DiskFloor -RepositoryRoot $repositoryRoot
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        $actualHash = Assert-Archive -ArchivePath $archivePath -ExpectedLength $lock.archive_bytes -ExpectedSha256 $lock.sha256
        Write-Host "复用已校验 Python ZIP：$archivePath"
    }
    else {
        if (Test-Path -LiteralPath $partialArchivePath -PathType Leaf) {
            $partialLength = (Get-Item -LiteralPath $partialArchivePath).Length
            if ($partialLength -eq $lock.archive_bytes) {
                $actualHash = Assert-Archive -ArchivePath $partialArchivePath -ExpectedLength $lock.archive_bytes -ExpectedSha256 $lock.sha256
                Move-Item -LiteralPath $partialArchivePath -Destination $archivePath
                Write-Host '复用已完成的 .part 文件并校验通过。'
            }
            elseif ($partialLength -gt $lock.archive_bytes) {
                throw '现有 Python .part 文件超过锁定大小，不自动覆盖或删除。'
            }
        }
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            if ($Offline) {
                throw '离线模式缺少完整且已校验的 Python ZIP 缓存；不会联网下载。'
            }
            Write-Host "正在从 Python.org 官方 HTTPS 源下载 $($lock.file_name)；最多 3 次传输尝试。"
            & curl.exe `
                --fail `
                --location `
                --silent `
                --show-error `
                --retry 2 `
                --retry-all-errors `
                --retry-delay 2 `
                --connect-timeout 20 `
                --max-time 900 `
                --continue-at - `
                --output $partialArchivePath `
                ([string]$lock.url)
            if ($LASTEXITCODE -ne 0) {
                throw "Python 下载失败，curl 退出码 $LASTEXITCODE；保留 .part 供有界续传。"
            }
            $actualHash = Assert-Archive -ArchivePath $partialArchivePath -ExpectedLength $lock.archive_bytes -ExpectedSha256 $lock.sha256
            Move-Item -LiteralPath $partialArchivePath -Destination $archivePath
            Write-Host "Python ZIP SHA-256 校验通过：$actualHash"
        }
    }

    $requirementsHash = (Get-FileHash -LiteralPath $requirementsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $runtimeIdentity = (Get-IdentityHash -ArchiveSha256 $actualHash -RequirementsSha256 $requirementsHash).Substring(0, 12)
    $runtimeRoot = Join-Path $dependencyRoot "python-$($lock.version)-$runtimeIdentity-windows-x64"
    $extractingRoot = "$runtimeRoot.extracting"
    $pythonExecutable = Join-Path $runtimeRoot 'python.exe'
    $pythonDll = Join-Path $runtimeRoot ([string]$lock.python_dll)
    $pthPath = Join-Path $runtimeRoot ([string]$lock.pth_file)
    $sitePackages = Join-Path $runtimeRoot 'site-packages'

    if (-not (Test-Path -LiteralPath $pythonExecutable -PathType Leaf)) {
        Assert-DiskFloor -RepositoryRoot $repositoryRoot
        if ((Test-Path -LiteralPath $runtimeRoot) -or (Test-Path -LiteralPath $extractingRoot)) {
            throw 'Python runtime 或解压暂存目录存在但不完整；为保护诊断证据，不自动删除或覆盖。'
        }
        New-Item -ItemType Directory -Path $extractingRoot | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($entry in $zip.Entries) {
                $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
                if ($unixType -eq 0xA000) {
                    throw 'Python ZIP 包含符号链接，已拒绝解压。'
                }
                $targetPath = Get-SafeRelativePath -EntryName $entry.FullName -DestinationRoot $extractingRoot
                if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) {
                    New-Item -ItemType Directory -Force -Path $targetPath | Out-Null
                    continue
                }
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetPath) | Out-Null
                $inputStream = $entry.Open()
                try {
                    $outputStream = [System.IO.File]::Create($targetPath)
                    try {
                        $inputStream.CopyTo($outputStream)
                    }
                    finally {
                        $outputStream.Dispose()
                    }
                }
                finally {
                    $inputStream.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
        $stagedPython = Join-Path $extractingRoot 'python.exe'
        $stagedPth = Join-Path $extractingRoot ([string]$lock.pth_file)
        $stagedDll = Join-Path $extractingRoot ([string]$lock.python_dll)
        if (-not (Test-Path -LiteralPath $stagedPython -PathType Leaf) -or -not (Test-Path -LiteralPath $stagedDll -PathType Leaf) -or -not (Test-Path -LiteralPath $stagedPth -PathType Leaf)) {
            throw 'Python 解压后缺少解释器、运行时 DLL 或 ._pth 文件。'
        }
        [System.IO.File]::WriteAllText($stagedPth, "python313.zip`n.`nsite-packages`nimport site`n", [System.Text.UTF8Encoding]::new($false))
        New-Item -ItemType Directory -Path (Join-Path $extractingRoot 'site-packages') | Out-Null
        $stagedVersion = (& $stagedPython -I -B -c 'import sys; print(sys.version.split()[0])').Trim()
        if ($LASTEXITCODE -ne 0 -or $stagedVersion -ne $lock.version) {
            throw "Python 解压验证失败：版本 '$stagedVersion'，预期 '$($lock.version)'。"
        }
        Assert-DiskFloor -RepositoryRoot $repositoryRoot
        $uvInstallArguments = @(
            'pip', 'install',
            '--python', $stagedPython,
            '--target', (Join-Path $extractingRoot 'site-packages'),
            '--require-hashes',
            '--only-binary', ':all:',
            '-r', $requirementsPath
        )
        if ($Offline) {
            $uvInstallArguments += '--offline'
            Write-Host 'Python 依赖安装模式：离线，仅使用隔离 uv 缓存。'
        }
        else {
            Write-Host 'Python 依赖安装模式：联网，仅使用固定 PyPI 索引和 requirements hash 校验。'
        }
        & $uvCommand.Source @uvInstallArguments
        if ($LASTEXITCODE -ne 0) {
            throw "uv 依赖安装失败，退出码 $LASTEXITCODE；保留隔离暂存目录供诊断。"
        }
        & $stagedPython -B $runtimeVerifier `
            --runtime-root $extractingRoot `
            --archive $archivePath `
            --lock $lockPath `
            --requirements-lock $requirementsPath `
            --write-manifest
        if ($LASTEXITCODE -ne 0) {
            throw "Python runtime 身份清单写入失败；保留隔离暂存目录供诊断。"
        }
        Move-Item -LiteralPath $extractingRoot -Destination $runtimeRoot
        Write-Host "Python runtime 解压及锁定依赖安装完成：$pythonExecutable"
    }

    foreach ($requiredPath in @($pythonExecutable, $pythonDll, $pthPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "已有 Python runtime 缺少必需文件：$requiredPath。"
        }
    }
    $expectedPth = "python313.zip`n.`nsite-packages`nimport site`n"
    $actualPth = [System.IO.File]::ReadAllText($pthPath, [System.Text.UTF8Encoding]::new($false))
    if ($actualPth -ne $expectedPth) {
        throw '已有 Python runtime 的 python313._pth 不符合隔离路径契约。'
    }
    $installedVersion = (& $pythonExecutable -I -B -c 'import sys; print(sys.version.split()[0])').Trim()
    if ($LASTEXITCODE -ne 0 -or $installedVersion -ne $lock.version) {
        throw "已有 Python runtime 版本验证失败：'$installedVersion'。"
    }
    & $pythonExecutable -B $runtimeVerifier `
        --runtime-root $runtimeRoot `
        --archive $archivePath `
        --lock $lockPath `
        --requirements-lock $requirementsPath `
        --verify-manifest
    if ($LASTEXITCODE -ne 0) {
        throw '已有 Python runtime 身份或依赖完整性验证失败；为保护未知或在用文件，不自动覆盖。'
    }
    $runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeRoot -File -Recurse -Force)
    $runtimeBytes = ($runtimeFiles | Measure-Object -Property Length -Sum).Sum
    $siteFiles = @(Get-ChildItem -LiteralPath $sitePackages -File -Recurse -Force)
    $siteBytes = ($siteFiles | Measure-Object -Property Length -Sum).Sum
    Write-Host "PYTHON_VERSION=$($lock.version)"
    Write-Host "PYTHON_RUNTIME_ID=$runtimeIdentity"
    Write-Host "PYTHON_REQUIREMENTS_LOCK_SHA256=$requirementsHash"
    Write-Host "PYTHON_UV_VERSION=$uvVersion"
    Write-Host "PYTHON_ARCHIVE=$archivePath"
    Write-Host "PYTHON_ARCHIVE_BYTES=$((Get-Item -LiteralPath $archivePath).Length)"
    Write-Host "PYTHON_ARCHIVE_SHA256=$actualHash"
    Write-Host "PYTHON_VERIFICATION=$($lock.verification)"
    Write-Host "PYTHON_SIGNATURE_VERIFIED=$($lock.signature_verified)"
    Write-Host "PYTHON_RUNTIME=$runtimeRoot"
    Write-Host "PYTHON_RUNTIME_FILES=$($runtimeFiles.Count)"
    Write-Host "PYTHON_RUNTIME_BYTES=$runtimeBytes"
    Write-Host "PYTHON_SITE_PACKAGES_FILES=$($siteFiles.Count)"
    Write-Host "PYTHON_SITE_PACKAGES_BYTES=$siteBytes"
    Assert-DiskFloor -RepositoryRoot $repositoryRoot
}
catch {
    Write-Warning "隔离 Python 准备失败：$($_.Exception.Message)"
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
