param(
    [string]$PackageDirectory = $PSScriptRoot,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs/Riji'),
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'Riji/Production')
)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\','/')
$installation = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\','/')
$dataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\','/')
if ($dataDirectory -eq [IO.Path]::GetPathRoot($dataDirectory).TrimEnd('\','/') -or
    $installation -eq $dataDirectory -or $installation.StartsWith($dataDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $dataDirectory.StartsWith($installation + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装目录与数据目录必须分离。' }
if ($installation -eq [IO.Path]::GetPathRoot($installation).TrimEnd('\','/') -or
    $package -eq $installation -or $installation.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '安装目录必须是独立的日迹文件夹。'
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'production-default'))) { throw '请选择日迹发布包目录。' }
$manifest = Join-Path $package 'files.sha256'
if (-not (Test-Path -LiteralPath $manifest)) { throw '发布包缺少校验清单。' }
$entries = @{}
function Assert-NoLinks([string]$Path) {
    $part = Get-Item -LiteralPath $Path -Force
    while ($part) {
        if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '包或安装路径不能包含目录链接。' }
        $parent = Split-Path -Parent $part.FullName
        if (-not $parent -or $parent -eq $part.FullName) { break }
        $part = Get-Item -LiteralPath $parent -Force
    }
}
Assert-NoLinks $package
# Validate the entire input before creating or switching an installation.
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw '校验清单格式错误。' }
    $expected = $Matches[1]; $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or ($relative -split '[\\/]') -contains '..' -or $relative.Contains(':')) { throw '清单含越界路径。' }
    $candidate = [IO.Path]::GetFullPath((Join-Path $package $relative))
    if (-not $candidate.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase) -or $entries.ContainsKey($relative)) { throw '清单含重复或越界路径。' }
    $item = Get-Item -LiteralPath $candidate
    Assert-NoLinks $candidate
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '包内文件类型无效。' }
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $expected) { throw ('发布包校验失败：' + $relative) }
    $entries[$relative] = $expected
}
foreach ($required in @('Riji.Desktop.exe','Riji.Desktop.dll','Web/index.html','production-default')) {
    if (-not ($entries.ContainsKey($required) -or $entries.ContainsKey($required.Replace('/','\')))) { throw ('发布包缺少：' + $required) }
}
if (Test-Path -LiteralPath $installation) {
    if (-not (Test-Path -LiteralPath (Join-Path $installation '.riji-installation'))) { throw '目标目录已存在且不是日迹管理的安装，请选择空闲目录。' }
    if ((Get-Item -LiteralPath $installation).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '安装目录不能是链接。' }
    if ((Get-Content -LiteralPath (Join-Path $installation '.riji-installation') -Raw).Trim() -ne 'Riji managed installation 1') { throw '安装标记无效。' }
}
$existingAncestor = $installation
while (-not (Test-Path -LiteralPath $existingAncestor)) { $existingAncestor = Split-Path -Parent $existingAncestor }
Assert-NoLinks $existingAncestor
New-Item -ItemType Directory -Path $installation -Force | Out-Null
Assert-NoLinks $installation
$installLock = $null
try {
    $installLock = [IO.File]::Open((Join-Path $installation '.install.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch { throw '另一个安装任务正在使用此目录，或无法写入安装锁。原入口未改变。' }
try {
Set-Content -LiteralPath (Join-Path $installation '.riji-installation') -Value 'Riji managed installation 1' -Encoding UTF8
$identityBytes = [Text.Encoding]::UTF8.GetBytes($dataDirectory.ToUpperInvariant())
$sha = [Security.Cryptography.SHA256]::Create()
try { $identity = ([BitConverter]::ToString($sha.ComputeHash($identityBytes))).Replace('-','') } finally { $sha.Dispose() }
$first = $false
$dataMutex = New-Object Threading.Mutex($true, ('Local\Riji-' + $identity), [ref]$first)
if (-not $first) { $dataMutex.Dispose(); throw '此数据目录的日迹仍在运行，请从设置或托盘退出后再安装。' }
try {
$backupDirectory = $null
if (Test-Path -LiteralPath $dataDirectory) {
    Assert-NoLinks $dataDirectory
    if (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'riji.db') -PathType Leaf)) { throw '数据目录存在但没有日迹数据库，请核对路径。' }
    $backupDirectory = Join-Path $installation ('before-upgrade-' + [guid]::NewGuid().ToString('N'))
    $backupData = Join-Path $backupDirectory 'Data'
    New-Item -ItemType Directory -Path $backupData | Out-Null
    $backupHashes = @()
    # The application mutex keeps SQLite and screenshot writers stopped during the snapshot.
    foreach ($file in Get-ChildItem -LiteralPath $dataDirectory -File -Recurse -Force) {
        Assert-NoLinks $file.FullName
        $relative = $file.FullName.Substring($dataDirectory.Length + 1)
        if (($relative -split '[\\/]')[0] -eq 'WebView') { continue }
        $target = Join-Path $backupData $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        $beforeHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $file.FullName -Destination $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $beforeHash -or
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $beforeHash) { throw '备份期间数据发生变化，未切换版本。' }
        $backupHashes += $beforeHash + '  ' + $relative
    }
    Set-Content -LiteralPath (Join-Path $backupDirectory 'data.sha256') -Value $backupHashes -Encoding UTF8
    $oldLauncher = Join-Path $installation '日迹.lnk'
    if (Test-Path -LiteralPath $oldLauncher) { Copy-Item -LiteralPath $oldLauncher -Destination (Join-Path $backupDirectory 'previous.lnk') }
    [pscustomobject]@{ Format = 1; Product = 'Riji'; DataDirectory = $dataDirectory; CreatedUtc = [DateTime]::UtcNow.ToString('o'); Complete = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupDirectory 'backup.json') -Encoding UTF8
}
$version = 'version-' + [guid]::NewGuid().ToString('N')
$destination = Join-Path $installation $version
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($relative in $entries.Keys) {
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $package $relative) -Destination $target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entries[$relative]) { throw '复制后校验失败，原入口未改变。' }
}
Copy-Item -LiteralPath $manifest -Destination $destination
# Publish the launcher only after every copied file has been verified; retain earlier versions.
$shell = New-Object -ComObject WScript.Shell
$temporary = Join-Path $installation ('launch-' + [guid]::NewGuid().ToString('N') + '.lnk')
$shortcut = $shell.CreateShortcut($temporary)
$shortcut.TargetPath = Join-Path $destination 'Riji.Desktop.exe'
$shortcut.WorkingDirectory = $destination
$shortcut.Description = '日迹'
$shortcut.Save()
$launcher = Join-Path $installation '日迹.lnk'
if (Test-Path -LiteralPath $launcher) {
    [IO.File]::Replace($temporary, $launcher, (Join-Path $installation ('previous-launch-' + [guid]::NewGuid().ToString('N') + '.lnk')))
} else { [IO.File]::Move($temporary, $launcher) }
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
[pscustomobject]@{ Installed = $true; Launcher = $launcher; VersionDirectory = $destination; VerifiedFiles = $entries.Count; DataModified = $false; UpgradeBackup = $backupDirectory }
} finally { $dataMutex.ReleaseMutex(); $dataMutex.Dispose() }
} finally { $installLock.Dispose() }
