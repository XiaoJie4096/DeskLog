param([switch]$Production)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $projectRoot 'src/Riji.Desktop/bin/Debug/net10.0-windows/Riji.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw '请先运行 scripts/build-desktop.ps1。' }
$startOptions = @{ FilePath = $executable; WorkingDirectory = $projectRoot }
if ($Production) { $startOptions.ArgumentList = @('--production') }
Start-Process @startOptions
