$ErrorActionPreference = 'Stop'

function Assert-PlainPath {
    param([Parameter(Mandatory)][string]$Path)
    $cursor = [System.IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $attributes = [System.IO.File]::GetAttributes($cursor)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw '卸载目标包含符号链接或 junction，已停止。'
        }
        $parent = [System.IO.Path]::GetDirectoryName($cursor)
        if (-not $parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Resolve-BundleFile {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Relative)
    if ($Relative.Contains('\') -or $Relative.Contains(':') -or $Relative.StartsWith('/')) {
        throw '清单包含不安全的路径。'
    }
    $parts = $Relative.Split('/')
    if (@($parts | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
        $parts[0] -in @('data', 'backups')) {
        throw '清单包含用户数据或路径穿越。'
    }
    $target = [System.IO.Path]::GetFullPath((Join-Path $Root ($parts -join [System.IO.Path]::DirectorySeparatorChar)))
    if (-not $target.StartsWith($Root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw '清单目标不在便携包内。'
    }
    return $target
}

function Assert-PlainTree {
    param([Parameter(Mandatory)][string]$Root, [switch]$CheckLocks)
    try {
        $attributes = [System.IO.File]::GetAttributes($Root)
    }
    catch [System.IO.DirectoryNotFoundException] { return }
    catch [System.IO.FileNotFoundException] { return }
    if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
        throw '用户数据根不是目录，已停止卸载。'
    }
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        Assert-PlainPath $current
        if ([System.IO.Directory]::Exists($current)) {
            foreach ($child in [System.IO.Directory]::EnumerateFileSystemEntries($current)) {
                $pending.Push($child)
            }
        }
        elseif ($CheckLocks) {
            Assert-NotInUse $current
        }
    }
}

function Remove-PlainTree {
    param([Parameter(Mandatory)][string]$Root)
    try {
        $attributes = [System.IO.File]::GetAttributes($Root)
    }
    catch [System.IO.DirectoryNotFoundException] { return }
    catch [System.IO.FileNotFoundException] { return }
    if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
        throw '用户数据根不是目录，已停止卸载。'
    }
    $pending = [System.Collections.Generic.Stack[object]]::new()
    $pending.Push(@($Root, $false))
    while ($pending.Count -gt 0) {
        $entry = $pending.Pop()
        $current = [string]$entry[0]
        $visited = [bool]$entry[1]
        Assert-PlainPath $current
        if ([System.IO.Directory]::Exists($current)) {
            if ($visited) {
                [System.IO.Directory]::Delete($current, $false)
            }
            else {
                $pending.Push(@($current, $true))
                foreach ($child in [System.IO.Directory]::EnumerateFileSystemEntries($current)) {
                    $pending.Push(@($child, $false))
                }
            }
        }
        else {
            Remove-Item -LiteralPath $current -Force -ErrorAction Stop
        }
    }
}

function Assert-NotInUse {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
    $stream.Dispose()
}

try {
    $root = [System.IO.Path]::GetFullPath($env:AIGOOFISH_UNINSTALL_ROOT).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $mode = $env:AIGOOFISH_UNINSTALL_MODE
    $parentId = [int]$env:AIGOOFISH_UNINSTALL_PARENT_PID
    if ($mode -notin @('preserve', 'complete') -or $parentId -le 0) {
        throw '卸载参数无效。'
    }
    try {
        Wait-Process -Id $parentId -Timeout 90 -ErrorAction Stop
    }
    catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
        # The parent may exit before this helper reaches Wait-Process.
    }
    Assert-PlainPath $root
    $manifestPath = Join-Path $root 'bundle-manifest.json'
    $currentPath = Join-Path $root 'current.json'
    Assert-PlainPath $manifestPath
    Assert-PlainPath $currentPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $current = Get-Content -LiteralPath $currentPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.format_version -ne 1 -or $current.format_version -ne 1 -or
        $manifest.current.release_id -ne $current.release_id -or
        $manifest.current.release_status -ne $current.release_status) {
        throw '便携包清单不匹配。'
    }
    $files = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $target = Resolve-BundleFile $root ([string]$entry.path)
        if (-not $files.Add($target)) { throw '清单包含重复路径。' }
        Assert-PlainPath $target
        if (-not [System.IO.File]::Exists($target)) { throw '程序文件缺失，已停止卸载。' }
    }
    foreach ($required in @('AiGoofish.exe', 'uninstall.exe')) {
        if (-not $files.Contains((Join-Path $root $required))) { throw '根目录入口未列入清单。' }
    }
    $known = [System.Collections.Generic.HashSet[string]]::new($files, [System.StringComparer]::OrdinalIgnoreCase)
    [void]$known.Add($manifestPath)
    [void]$known.Add($currentPath)
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-PlainPath $directory
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($directory)) {
            if ($directory -eq $root -and [System.IO.Path]::GetFileName($entry) -in @('data', 'backups')) {
                continue
            }
            Assert-PlainPath $entry
            if ([System.IO.Directory]::Exists($entry)) {
                $pending.Push($entry)
            }
            elseif (-not $known.Contains($entry)) {
                throw '便携目录含未登记文件，已停止卸载。'
            }
        }
    }
    if ($mode -eq 'complete') {
        foreach ($name in @('data', 'backups')) {
            Assert-PlainTree (Join-Path $root $name) -CheckLocks
        }
    }
    $rootPrefix = $root + [System.IO.Path]::DirectorySeparatorChar
    foreach ($process in [System.Diagnostics.Process]::GetProcesses()) {
        try {
            $executable = $process.MainModule.FileName
            if ($executable -and $executable.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw '便携包内仍有运行中的进程，已停止卸载。'
            }
        }
        catch [System.ComponentModel.Win32Exception] { }
        catch [System.InvalidOperationException] { }
        finally { $process.Dispose() }
    }
    foreach ($file in $files) { Assert-NotInUse $file }
    Assert-NotInUse $manifestPath
    Assert-NotInUse $currentPath

    foreach ($file in $files) {
        Assert-PlainPath $file
        Remove-Item -LiteralPath $file -Force -ErrorAction Stop
    }
    Remove-Item -LiteralPath $manifestPath, $currentPath -Force -ErrorAction Stop
    $directorySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        $parent = [System.IO.Path]::GetDirectoryName($file)
        while ($parent -and $parent -ne $root) {
            [void]$directorySet.Add($parent)
            $parent = [System.IO.Path]::GetDirectoryName($parent)
        }
    }
    $directories = @($directorySet | Sort-Object -Property @{ Expression = { $_.Length }; Descending = $true })
    foreach ($directory in $directories) {
        if ([System.IO.Directory]::Exists($directory)) {
            Assert-PlainPath $directory
            [System.IO.Directory]::Delete($directory, $false)
        }
    }
    if ($mode -eq 'complete') {
        foreach ($name in @('data', 'backups')) {
            $userRoot = Join-Path $root $name
            Remove-PlainTree $userRoot
        }
    }
    if (-not [System.IO.Directory]::EnumerateFileSystemEntries($root).GetEnumerator().MoveNext()) {
        [System.IO.Directory]::Delete($root, $false)
    }
    if ($env:AIGOOFISH_UNINSTALL_SHOW_UI -eq '1') {
        try {
            Add-Type -AssemblyName System.Windows.Forms
            $summary = if ($mode -eq 'preserve') { '程序文件已移除；data 和 backups 已保留。' } else { '程序和数据已移除。' }
            [void][System.Windows.Forms.MessageBox]::Show($summary, '闲鱼监控便携版卸载')
        }
        catch {
            [Console]::Error.WriteLine('卸载完成，但无法显示完成提示：' + $_.Exception.Message)
        }
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine('卸载未完成：' + $_.Exception.Message + '；位置：' + $_.InvocationInfo.ScriptLineNumber)
    try {
        if ($env:AIGOOFISH_UNINSTALL_SHOW_UI -eq '1') {
            Add-Type -AssemblyName System.Windows.Forms
            [void][System.Windows.Forms.MessageBox]::Show('卸载未完成：' + $_.Exception.Message,
                '闲鱼监控便携版卸载', [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error)
        }
    }
    catch { }
    exit 1
}
