param([Parameter(Mandatory=$true)][string]$BackupDirectory,[Parameter(Mandatory=$true)][string]$DataDirectory)
$ErrorActionPreference='Stop'
$data=[IO.Path]::GetFullPath($DataDirectory).TrimEnd('\','/')
$temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
if (-not $data.StartsWith($temporary+'\Riji-CrossVersion-Data-', [StringComparison]::OrdinalIgnoreCase)) { throw '仅允许已命名的临时跨版本测试数据。' }
$backup=[IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\','/')
$root=Split-Path -Parent $backup
$sha=[Security.Cryptography.SHA256]::Create()
try { $identity=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($data.ToUpperInvariant())))).Replace('-','') }finally{$sha.Dispose()}
$parent=Split-Path -Parent $data
$marker=Join-Path $parent ('.Riji-Recovery-'+$identity+'.json')
$results=@()
foreach ($phase in @('prepared','data-moved','data-published')) {
    if(Test-Path -LiteralPath $marker){throw '测试不能覆盖现有恢复标记。'}
    $token=[guid]::NewGuid().ToString('N')
    $stage=Join-Path $parent ('Riji-Restore-'+$token)
    $retained=Join-Path $parent ('Riji-Before-Rollback-'+$token)
    & (Join-Path $PSScriptRoot 'restore-upgrade-backup.ps1') -BackupDirectory $backup -DestinationDirectory $stage | Out-Null
    [pscustomobject]@{Product='Riji';Data=$data;Staged=$stage;Retained=$retained;Backup=$backup;Launcher=(Join-Path $root '日迹.lnk')} |
        ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
    if((Split-Path -Parent $stage) -ne $parent -or (Split-Path -Parent $retained) -ne $parent){throw '测试路径越界。'}
    if($phase -ne 'prepared'){Move-Item -LiteralPath $data -Destination $retained}
    if($phase -eq 'data-published'){Move-Item -LiteralPath $stage -Destination $data}
    & (Join-Path $PSScriptRoot 'rollback-desktop.ps1') -BackupDirectory $backup -DataDirectory $data -Resume | Out-Null
    $match=(Get-FileHash (Join-Path $data 'riji.db')).Hash -eq (Get-FileHash (Join-Path $backup 'Data/riji.db')).Hash
    $passed=$match -and (Test-Path $retained) -and -not(Test-Path $marker)
    $results += [pscustomobject]@{Phase=$phase;Passed=$passed;Retained=$retained}
    if(-not $passed){throw '中断恢复测试失败。'}
}
# Reject a changed staged database without touching either the current data or launcher.
$token=[guid]::NewGuid().ToString('N')
$stage=Join-Path $parent ('Riji-Restore-'+$token)
$retained=Join-Path $parent ('Riji-Before-Rollback-'+$token)
& (Join-Path $PSScriptRoot 'restore-upgrade-backup.ps1') -BackupDirectory $backup -DestinationDirectory $stage | Out-Null
$launcher=Join-Path $root '日迹.lnk'
[pscustomobject]@{Product='Riji';Data=$data;Staged=$stage;Retained=$retained;Backup=$backup;Launcher=$launcher} |
    ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
$currentHash=(Get-FileHash (Join-Path $data 'riji.db')).Hash
$launcherHash=(Get-FileHash $launcher).Hash
$markerHash=(Get-FileHash $marker).Hash
$stream=[IO.File]::Open((Join-Path $stage 'riji.db'),[IO.FileMode]::Append,[IO.FileAccess]::Write)
try{$stream.WriteByte(1)}finally{$stream.Dispose()}
$rejected=$false
try { & (Join-Path $PSScriptRoot 'rollback-desktop.ps1') -BackupDirectory $backup -DataDirectory $data -Resume | Out-Null }
catch { $rejected=$_.Exception.Message -like '*副本已改变*' }
$passed=$rejected -and (Get-FileHash (Join-Path $data 'riji.db')).Hash -eq $currentHash -and
    (Get-FileHash $launcher).Hash -eq $launcherHash -and (Get-FileHash $marker).Hash -eq $markerHash -and
    (Test-Path $stage) -and -not(Test-Path $retained)
$results += [pscustomobject]@{Phase='changed-copy-rejected';Passed=$passed;Retained=$stage}
if(-not $passed){throw '已改动副本未安全拒绝。'}
# Repair only the explicitly corrupted fixture from its original snapshot, then finish normally.
Copy-Item -LiteralPath (Join-Path $backup 'Data/riji.db') -Destination (Join-Path $stage 'riji.db')
& (Join-Path $PSScriptRoot 'rollback-desktop.ps1') -BackupDirectory $backup -DataDirectory $data -Resume | Out-Null
if(Test-Path $marker){throw '测试修复后仍有恢复标记。'}
$results | ConvertTo-Json
