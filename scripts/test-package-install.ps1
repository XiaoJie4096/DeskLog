param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
# Exercise packaged deployment with synthetic bytes, not a live user database.
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath $PackagePath | Out-Null
$root = Join-Path ([IO.Path]::GetTempPath()) ('Riji-Install-Test-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $PackagePath -DestinationPath $root
$package = @(Get-ChildItem -LiteralPath $root -Directory)[0].FullName
$installation = Join-Path $root 'Installed'
$data = Join-Path $root 'TestData'
$first = & (Join-Path $package 'install-desktop.ps1') -PackageDirectory $package -InstallDirectory $installation -DataDirectory $data
[IO.Directory]::CreateDirectory($data) | Out-Null
$fixture = Join-Path $data 'riji.db'
[IO.File]::WriteAllText($fixture, 'Synthetic installer byte-copy fixture; not a SQLite database.')
$before = (Get-FileHash -LiteralPath $fixture).Hash
$second = & (Join-Path $package 'install-desktop.ps1') -PackageDirectory $package -InstallDirectory $installation -DataDirectory $data
$restored = Join-Path $root 'Restored'
& (Join-Path $package 'restore-upgrade-backup.ps1') -BackupDirectory $second.UpgradeBackup -DestinationDirectory $restored | Out-Null
$shell = New-Object -ComObject WScript.Shell
try { $target = $shell.CreateShortcut($second.Launcher).TargetPath }
finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
$passed = $first.Installed -and $second.Installed -and
    ($first.VersionDirectory -ne $second.VersionDirectory) -and
    (Test-Path -LiteralPath $first.VersionDirectory) -and
    ($target -eq (Join-Path $second.VersionDirectory 'Riji.Desktop.exe')) -and
    ((Get-FileHash -LiteralPath $fixture).Hash -eq $before) -and
    ((Get-FileHash -LiteralPath (Join-Path $restored 'riji.db')).Hash -eq $before)
$result = [ordered]@{ Passed = [bool]$passed; Root = $root; FirstVersion = $first.VersionDirectory; SecondVersion = $second.VersionDirectory; VerifiedFiles = $second.VerifiedFiles; Backup = $second.UpgradeBackup; Restored = $restored; DatabaseSemanticsTested = $false; ApplicationStarted = $false }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'result.json') -Encoding utf8
$result | ConvertTo-Json
if (-not $passed) { throw 'Package install/upgrade/restore verification failed.' }
