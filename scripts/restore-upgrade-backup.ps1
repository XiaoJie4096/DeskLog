param(
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [Parameter(Mandatory=$true)][string]$DestinationDirectory
)
$ErrorActionPreference = 'Stop'
$backup = [IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\','/')
$destination = [IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\','/')
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
if (Test-Path -LiteralPath $destination) { throw '恢复目标必须是不存在的新目录，未覆盖现有数据。' }
if ($destination.StartsWith($backup + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '恢复目标不能位于备份内。' }
$metadata = Get-Content -LiteralPath (Join-Path $backup 'backup.json') -Raw | ConvertFrom-Json
if ($metadata.Product -ne 'Riji' -or $metadata.Format -ne 1 -or $metadata.Complete -ne $true) { throw '不是完整的日迹升级备份。' }
$data = Join-Path $backup 'Data'
$entries = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $backup 'data.sha256')) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw '备份清单格式错误。' }
    $hash = $Matches[1]; $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or ($relative -split '[\\/]') -contains '..' -or $relative.Contains(':') -or $entries.ContainsKey($relative)) { throw '备份路径无效或重复。' }
    $source = [IO.Path]::GetFullPath((Join-Path $data $relative))
    if (-not $source.StartsWith($data + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '备份路径越界。' }
    Assert-NoLinks $source
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash) { throw ('备份校验失败：' + $relative) }
    $entries[$relative] = $hash
}
if (-not $entries.ContainsKey('riji.db')) { throw '备份缺少数据库。' }
$ancestor = Split-Path -Parent $destination
while (-not (Test-Path -LiteralPath $ancestor)) { $ancestor = Split-Path -Parent $ancestor }
Assert-NoLinks $ancestor
$parent = Split-Path -Parent $destination
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staging = Join-Path $parent ('Riji-Recovery-Staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
# Publish only a complete restored copy. Failed staging stays available for diagnosis.
foreach ($relative in $entries.Keys) {
    $target = Join-Path $staging $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $data $relative) -Destination $target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entries[$relative]) { throw '恢复副本校验失败，未发布目标目录。' }
}
if (Test-Path -LiteralPath $destination) { throw '目标目录已被占用，恢复副本仍在暂存目录。' }
# Both paths are verified siblings beneath the explicit destination parent; no source data is moved.
Move-Item -LiteralPath $staging -Destination $destination
[pscustomobject]@{ Restored = $true; Directory = $destination; VerifiedFiles = $entries.Count; CurrentDataModified = $false; BackupModified = $false }
