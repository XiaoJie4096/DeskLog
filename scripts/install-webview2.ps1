param(
    [string]$ApplicationDirectory = $PSScriptRoot,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$applicationPath = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$corePath = Join-Path $applicationPath 'Microsoft.Web.WebView2.Core.dll'
if (-not (Test-Path -LiteralPath $corePath)) { throw '请从完整日迹程序目录运行此工具，或指定 -ApplicationDirectory。' }
Add-Type -Path $corePath
function Get-RijiWebViewVersion {
    try { return [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::GetAvailableBrowserVersionString() }
    catch {
        if ($_.Exception.ToString() -match 'WebView2RuntimeNotFoundException') { return $null }
        throw
    }
}
$version = Get-RijiWebViewVersion
if ($version) { Write-Output ('WebView2 已安装：' + $version); exit 0 }
if ($CheckOnly) { Write-Output '未检测到 WebView2 Runtime。'; exit 2 }

# Verify the publisher before executing the official evergreen bootstrapper.
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('Riji-WebView2-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$installer = Join-Path $temporary 'MicrosoftEdgeWebview2Setup.exe'
try {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $installer
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw '安装程序签名不是有效的微软签名，已停止安装。'
    }
    $process = Start-Process -FilePath $installer -ArgumentList @('/silent','/install') -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -notin @(0, 3010)) { throw ('WebView2 安装失败，退出码：' + $process.ExitCode) }
    $version = Get-RijiWebViewVersion
    if (-not $version) { throw '安装后仍未检测到 WebView2。请重启电脑后复查，或从微软官网手动安装。' }
    Write-Output ('WebView2 安装已验证：' + $version)
} finally {
    # Delete only the one downloaded file; leave unexpected contents untouched.
    if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer }
    if ((Get-ChildItem -LiteralPath $temporary -Force | Measure-Object).Count -eq 0) { Remove-Item -LiteralPath $temporary }
}
