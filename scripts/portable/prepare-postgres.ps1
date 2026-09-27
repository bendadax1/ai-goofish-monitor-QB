[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptExitCode = 0

function Assert-DiskFloor {
    param([string]$RepositoryRoot)

    $driveRoot = [System.IO.Path]::GetPathRoot($RepositoryRoot)
    $drive = [System.IO.DriveInfo]::new($driveRoot)
    $freeRatio = $drive.AvailableFreeSpace / $drive.TotalSize
    Write-Host "磁盘检查：$($drive.Name) 可用 $($drive.AvailableFreeSpace) / $($drive.TotalSize) B（$([math]::Round($freeRatio * 100, 2))%）。"
    if ($drive.AvailableFreeSpace -lt 10GB -or $freeRatio -lt 0.05) {
        throw '磁盘可用空间已低于 10 GiB 或 5%，按仓库规则暂停 PostgreSQL 下载/解压。'
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
        throw "PostgreSQL ZIP 大小不符：$($archive.Length) B，预期 $ExpectedLength B。"
    }
    $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "PostgreSQL ZIP 本地 SHA-256 锁不符：$actualHash。"
    }
    return $actualHash
}

function Test-SelectedEntry {
    param(
        [string]$EntryName,
        [object]$Lock
    )

    foreach ($prefix in $Lock.selected_archive_prefixes) {
        if ($EntryName.StartsWith([string]$prefix, [StringComparison]::Ordinal)) {
            return $true
        }
    }
    foreach ($file in $Lock.selected_archive_files) {
        if ($EntryName.Equals([string]$file, [StringComparison]::Ordinal)) {
            return $true
        }
    }
    return $false
}

function Get-SafeRelativePath {
    param(
        [string]$EntryName,
        [string]$DestinationRoot
    )

    if (-not $EntryName.StartsWith('pgsql/', [StringComparison]::Ordinal)) {
        throw '选中的 PostgreSQL ZIP 条目缺少 pgsql/ 根前缀。'
    }
    $relative = $EntryName.Substring(6).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    if ([string]::IsNullOrWhiteSpace($relative) -or [System.IO.Path]::IsPathRooted($relative)) {
        throw 'PostgreSQL ZIP 包含非法路径条目。'
    }
    $segments = $relative.Split([System.IO.Path]::DirectorySeparatorChar)
    if ($segments -contains '..' -or $segments -contains '.' -or $relative.Contains(':')) {
        throw 'PostgreSQL ZIP 包含路径穿越或绝对路径条目。'
    }

    $destinationRootFull = [System.IO.Path]::GetFullPath($DestinationRoot)
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $destinationRootFull $relative))
    $boundary = $destinationRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PostgreSQL ZIP 解压目标越出隔离目录。'
    }
    return $candidate
}

try {
    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
    $lockPath = Join-Path $PSScriptRoot 'postgresql-win-x64.lock.json'
    $lock = Get-Content -Raw -Encoding UTF8 -LiteralPath $lockPath | ConvertFrom-Json
    if ($lock.schema_version -ne 1 -or $lock.verification -ne 'official_https_local_sha256') {
        throw 'PostgreSQL 组件锁格式或验证类型不受支持。'
    }
    if ($lock.signature_verified -ne $false) {
        throw '本工具不接受未有实际证据的 PostgreSQL 签名验证声明。'
    }

    $dependencyRoot = Join-Path $repositoryRoot '.tmp\dependencies\portable-pg'
    $downloadRoot = Join-Path $dependencyRoot 'downloads'
    $archivePath = Join-Path $downloadRoot ([string]$lock.file_name)
    $partialArchivePath = "$archivePath.part"
    $runtimeRoot = Join-Path $dependencyRoot "postgresql-$($lock.version)-$($lock.package_revision)-windows-x64"
    $extractingRoot = "$runtimeRoot.extracting"
    $requiredRelativePaths = @(
        'bin\postgres.exe',
        'bin\pg_ctl.exe',
        'bin\initdb.exe',
        'bin\psql.exe',
        'share\postgresql.conf.sample',
        'server_license.txt',
        'commandlinetools_3rd_party_licenses.txt'
    )
    New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null

    Assert-DiskFloor -RepositoryRoot $repositoryRoot
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        $actualHash = Assert-Archive -ArchivePath $archivePath -ExpectedLength $lock.archive_bytes -ExpectedSha256 $lock.sha256
        Write-Host "复用已校验 PostgreSQL ZIP：$archivePath"
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
                throw '现有 PostgreSQL .part 文件超过锁定大小，不自动覆盖或删除。'
            }
        }

        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            Write-Host "正在从 PostgreSQL 官方页面指向的 EDB HTTPS 源下载 $($lock.file_name)；最多 3 次传输尝试。"
            & curl.exe `
                --fail `
                --location `
                --silent `
                --show-error `
                --retry 2 `
                --retry-all-errors `
                --retry-delay 2 `
                --connect-timeout 20 `
                --max-time 1800 `
                --continue-at - `
                --output $partialArchivePath `
                ([string]$lock.url)
            if ($LASTEXITCODE -ne 0) {
                throw "PostgreSQL 下载失败，curl 退出码 $LASTEXITCODE；保留 .part 供有界续传。"
            }
            $actualHash = Assert-Archive -ArchivePath $partialArchivePath -ExpectedLength $lock.archive_bytes -ExpectedSha256 $lock.sha256
            Move-Item -LiteralPath $partialArchivePath -Destination $archivePath
            Write-Host "PostgreSQL ZIP 本地 SHA-256 锁校验通过：$actualHash"
        }
    }

    $postgresExecutable = Join-Path $runtimeRoot 'bin\postgres.exe'
    if (-not (Test-Path -LiteralPath $postgresExecutable -PathType Leaf)) {
        Assert-DiskFloor -RepositoryRoot $repositoryRoot
        if ((Test-Path -LiteralPath $runtimeRoot) -or (Test-Path -LiteralPath $extractingRoot)) {
            throw 'PostgreSQL runtime 或解压暂存目录存在但不完整；为保护诊断证据，不自动删除或覆盖。'
        }
        New-Item -ItemType Directory -Path $extractingRoot | Out-Null

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $selectedCount = 0L
            $selectedBytes = 0L
            foreach ($entry in $zip.Entries) {
                if (-not (Test-SelectedEntry -EntryName $entry.FullName -Lock $lock)) {
                    continue
                }
                $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
                if ($unixType -eq 0xA000) {
                    throw 'PostgreSQL ZIP 选中内容包含符号链接，已拒绝解压。'
                }
                $targetPath = Get-SafeRelativePath -EntryName $entry.FullName -DestinationRoot $extractingRoot
                $selectedCount++
                $selectedBytes += $entry.Length
                if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) {
                    New-Item -ItemType Directory -Force -Path $targetPath | Out-Null
                    continue
                }
                $targetParent = Split-Path -Parent $targetPath
                New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
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

        if ($selectedCount -ne $lock.selected_entries -or $selectedBytes -ne $lock.selected_uncompressed_bytes) {
            throw "PostgreSQL ZIP 选择内容与锁不符：$selectedCount 项/$selectedBytes B。"
        }
        foreach ($requiredRelativePath in $requiredRelativePaths) {
            if (-not (Test-Path -LiteralPath (Join-Path $extractingRoot $requiredRelativePath) -PathType Leaf)) {
                throw "PostgreSQL 选择性解压缺少必需文件：$requiredRelativePath。"
            }
        }
        Move-Item -LiteralPath $extractingRoot -Destination $runtimeRoot
        Write-Host "PostgreSQL 选择性 runtime 解压完成：$runtimeRoot"
    }

    foreach ($requiredRelativePath in $requiredRelativePaths) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot $requiredRelativePath) -PathType Leaf)) {
            throw "已有 PostgreSQL runtime 缺少必需文件：$requiredRelativePath。"
        }
    }

    $versionOutput = (& $postgresExecutable --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $versionOutput -ne "postgres (PostgreSQL) $($lock.version)") {
        throw "PostgreSQL runtime 版本验证失败：'$versionOutput'。"
    }
    foreach ($toolName in @('initdb.exe', 'pg_ctl.exe', 'psql.exe')) {
        $toolPath = Join-Path $runtimeRoot "bin\$toolName"
        $toolVersion = (& $toolPath --version).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $toolVersion.EndsWith(" $($lock.version)", [StringComparison]::Ordinal)) {
            throw "PostgreSQL 工具版本验证失败：$toolName。"
        }
    }

    $archiveBytes = (Get-Item -LiteralPath $archivePath).Length
    $runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeRoot -File -Recurse -Force)
    $runtimeBytes = ($runtimeFiles | Measure-Object -Property Length -Sum).Sum
    if ($runtimeFiles.Count -ne $lock.runtime_files -or $runtimeBytes -ne $lock.runtime_bytes) {
        throw "PostgreSQL runtime 文件集与锁不符：$($runtimeFiles.Count) 个文件/$runtimeBytes B。"
    }
    Write-Host "POSTGRES_VERSION=$($lock.version)"
    Write-Host "POSTGRES_ARCHIVE=$archivePath"
    Write-Host "POSTGRES_ARCHIVE_BYTES=$archiveBytes"
    Write-Host "POSTGRES_ARCHIVE_SHA256=$actualHash"
    Write-Host "POSTGRES_VERIFICATION=$($lock.verification)"
    Write-Host "POSTGRES_SIGNATURE_VERIFIED=$($lock.signature_verified)"
    Write-Host "POSTGRES_RUNTIME=$runtimeRoot"
    Write-Host "POSTGRES_RUNTIME_FILES=$($runtimeFiles.Count)"
    Write-Host "POSTGRES_RUNTIME_BYTES=$runtimeBytes"
    Assert-DiskFloor -RepositoryRoot $repositoryRoot
}
catch {
    Write-Warning "隔离 PostgreSQL 准备失败：$($_.Exception.Message)"
    $scriptExitCode = 1
}

if ($scriptExitCode -ne 0) {
    exit $scriptExitCode
}
