param(
    [string]$Version = '0.1.0.9001'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = 'artifacts/private-releases'
$outputBaseName = 'DeskLog-Setup-Private-v' + $Version

Write-Output '正在构建私有正式数据测试版。该安装器不会上传到 GitHub Release。'
& (Join-Path $PSScriptRoot 'build-stable-release.ps1') `
    -Version $Version `
    -OutputDirectory $outputDirectory `
    -OutputBaseName $outputBaseName
if ($LASTEXITCODE -ne 0) { throw '私有测试版构建失败。' }

$installer = Join-Path (Join-Path $projectRoot $outputDirectory) ($outputBaseName + '.exe')
$hashFile = Join-Path (Join-Path $projectRoot $outputDirectory) 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $installer) -or -not (Test-Path -LiteralPath $hashFile)) { throw '私有测试版输出不完整。' }
Write-Output $installer
Write-Output ('SHA256 清单：' + $hashFile)
