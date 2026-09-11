param(
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [switch]$Resume
)
$ErrorActionPreference = 'Stop'
$backup = [IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\','/')
$data = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\','/')
$installation = Split-Path -Parent $backup
$metadata = Get-Content -LiteralPath (Join-Path $backup 'backup.json') -Raw | ConvertFrom-Json
if ($metadata.Product -ne 'Riji' -or $metadata.Format -ne 1 -or $metadata.Complete -ne $true -or $metadata.DataDirectory -ne $data) { throw '快照与明确指定的数据目录不匹配。' }
if ((Get-Content -LiteralPath (Join-Path $installation '.riji-installation') -Raw).Trim() -ne 'Riji managed installation 1') { throw '不是日迹管理的安装。' }
function Assert-NoLinks([string]$Path) {
    $part = Get-Item -LiteralPath $Path -Force
    while ($part) {
        if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '恢复路径不能包含目录链接。' }
        $parent = Split-Path -Parent $part.FullName
        if (-not $parent -or $parent -eq $part.FullName) { break }
        $part = Get-Item -LiteralPath $parent -Force
    }
}
Assert-NoLinks $backup
Assert-NoLinks (Split-Path -Parent $data)
if (Test-Path -LiteralPath $data) { Assert-NoLinks $data }
$shell = New-Object -ComObject WScript.Shell
try { $oldExe = $shell.CreateShortcut((Join-Path $backup 'previous.lnk')).TargetPath }
finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
if (-not $oldExe.StartsWith($installation + '\version-', [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $oldExe) -ne 'Riji.Desktop.exe' -or -not (Test-Path -LiteralPath $oldExe)) { throw '快照没有有效的旧版本入口。' }
Assert-NoLinks $oldExe
$sha = [Security.Cryptography.SHA256]::Create()
try { $identity = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($data.ToUpperInvariant())))).Replace('-','') } finally { $sha.Dispose() }
$first = $false
$mutex = New-Object Threading.Mutex($true, ('Local\Riji-' + $identity), [ref]$first)
if (-not $first) { $mutex.Dispose(); throw '日迹仍在使用此数据目录，请先退出。' }
try {
    $installLock = [IO.File]::Open((Join-Path $installation '.install.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $parent = Split-Path -Parent $data
        $marker = Join-Path $parent ('.Riji-Recovery-' + $identity + '.json')
        $launcher = Join-Path $installation '日迹.lnk'
        $moved = $false; $published = $false
        if (Test-Path -LiteralPath $marker) {
            if (-not $Resume) { throw '存在中断的回滚标记。确认使用原快照后加 -Resume 继续；未再次修改。' }
            Assert-NoLinks $marker
            $plan = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
            if ($plan.Product -ne 'Riji' -or $plan.Data -ne $data -or $plan.Backup -ne $backup -or $plan.Launcher -ne $launcher -or
                (Split-Path -Leaf $plan.Staged) -notmatch '^Riji-Restore-([a-f0-9]{32})$') { throw '恢复标记不匹配，未修改。' }
            $token = $Matches[1]
            $staged = Join-Path $parent ('Riji-Restore-' + $token)
            $retained = Join-Path $parent ('Riji-Before-Rollback-' + $token)
            if ($plan.Staged -ne $staged -or $plan.Retained -ne $retained) { throw '恢复目录不匹配。' }
            $moved = Test-Path -LiteralPath $retained
            $hasStage = Test-Path -LiteralPath $staged
            $hasData = Test-Path -LiteralPath $data
            if (($hasStage -and $hasData -and $moved) -or (-not $moved -and -not $hasData) -or (-not $hasStage -and (-not $hasData -or -not $moved))) { throw '恢复目录状态不明确，未修改。' }
            $published = -not $hasStage
            $copy = if ($published) { $data } else { $staged }
            Assert-NoLinks $copy
            if ($moved) { Assert-NoLinks $retained }
            foreach ($line in Get-Content -LiteralPath (Join-Path $backup 'data.sha256')) {
                if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw '备份清单无效。' }
                $hash = $Matches[1]; $relative = $Matches[2]
                if ([IO.Path]::IsPathRooted($relative) -or ($relative -split '[\\/]') -contains '..' -or $relative.Contains(':')) { throw '备份路径无效。' }
                $file = Join-Path $copy $relative
                Assert-NoLinks $file
                if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $hash) { throw '中断恢复副本已改变，未继续。' }
            }
        } else {
            if ($Resume) { throw '没有待继续的回滚，不执行新的回滚。' }
            $token = [guid]::NewGuid().ToString('N')
            $staged = Join-Path $parent ('Riji-Restore-' + $token)
            $retained = Join-Path $parent ('Riji-Before-Rollback-' + $token)
            & (Join-Path $PSScriptRoot 'restore-upgrade-backup.ps1') -BackupDirectory $backup -DestinationDirectory $staged | Out-Null
        }
        $temporary = Join-Path $installation ('rollback-' + $token + '.lnk')
        Copy-Item -LiteralPath (Join-Path $backup 'previous.lnk') -Destination $temporary
        $savedLauncher = Join-Path $installation ('before-rollback-' + $token + '.lnk')
        [pscustomobject]@{ Product='Riji'; Data=$data; Staged=$staged; Retained=$retained; Backup=$backup; Launcher=$launcher } |
            ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
        # The three directory paths are explicit siblings. Retain current data instead of deleting it.
        if ((Split-Path -Parent $staged) -ne $parent -or (Split-Path -Parent $retained) -ne $parent) { throw '恢复路径检查失败。' }
        try {
            if (-not $moved -and (Test-Path -LiteralPath $data)) { Move-Item -LiteralPath $data -Destination $retained; $moved = $true }
            if (-not $published) { Move-Item -LiteralPath $staged -Destination $data; $published = $true }
            [IO.File]::Replace($temporary, $launcher, $savedLauncher)
        } catch {
            if ($published) { Move-Item -LiteralPath $data -Destination $staged }
            if ($moved) { Move-Item -LiteralPath $retained -Destination $data }
            Remove-Item -LiteralPath $marker
            throw
        }
        Remove-Item -LiteralPath $marker
        [pscustomobject]@{ RolledBack=$true; DataDirectory=$data; RetainedData=$retained; OldProgram=$oldExe; Launcher=$launcher }
    } finally { $installLock.Dispose() }
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
