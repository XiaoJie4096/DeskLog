param(
    [string]$Version = '0.1.0',
    [string]$OutputDirectory = 'artifacts/releases',
    [string]$OutputBaseName = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = $Version
$outputRoot = Join-Path $projectRoot $OutputDirectory
$outputBase = if ($OutputBaseName) { $OutputBaseName } else { 'DeskLog-Setup-v' + $version }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$payload = Join-Path $projectRoot ('artifacts/release-payload-' + $stamp)
$release = $outputRoot
$publish = Join-Path $payload 'publish'
$extensions = Join-Path $payload 'extensions'
$thirdParty = Join-Path $payload 'ThirdParty'
New-Item -ItemType Directory -Path $publish,$extensions,$thirdParty,$release -Force | Out-Null

Push-Location (Join-Path $projectRoot 'src/Riji.Web')
try {
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' }
} finally { Pop-Location }

& dotnet publish (Join-Path $projectRoot 'src/Riji.Desktop/Riji.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw '稳定版发布构建失败。' }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'Riji.Desktop.exe'))) { throw '发布构建缺少桌面程序。' }
Move-Item -LiteralPath (Join-Path $publish 'Riji.Desktop.exe') -Destination (Join-Path $payload 'DeskLog.exe')
Copy-Item -LiteralPath (Join-Path $publish 'Web') -Destination $payload -Recurse
Set-Content -LiteralPath (Join-Path $payload 'production-default') -Value 'Production' -Encoding utf8
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets/icons/riji.ico') -Destination $payload
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/package-readme.md') -Destination (Join-Path $payload 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $payload
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload

$chromeEdge = Join-Path $extensions 'chrome-edge'
$firefox = Join-Path $extensions 'firefox'
New-Item -ItemType Directory -Path $chromeEdge,$firefox -Force | Out-Null
foreach ($name in @('worker.js','options.js','options.html','options.css','manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('browser-extension/' + $name)) -Destination $chromeEdge
}
foreach ($name in @('worker.js','options.js','options.html','options.css')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('browser-extension/' + $name)) -Destination $firefox
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'browser-extension/manifest.firefox.json') -Destination (Join-Path $firefox 'manifest.json')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/licenses/Apache-2.0.txt') -Destination $thirdParty

$inno = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $inno) { throw '未找到 Inno Setup 6，请先安装 Inno Setup。' }
$env:DESKLOG_PAYLOAD = $payload
$env:DESKLOG_APP_VERSION = $version
$env:DESKLOG_OUTPUT_BASE = $outputBase
try {
    & $inno (Join-Path $projectRoot 'installer/DeskLog.iss') (('/O' + $release))
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 编译失败。' }
} finally {
    Remove-Item Env:DESKLOG_PAYLOAD -ErrorAction SilentlyContinue
    Remove-Item Env:DESKLOG_APP_VERSION -ErrorAction SilentlyContinue
    Remove-Item Env:DESKLOG_OUTPUT_BASE -ErrorAction SilentlyContinue
}
$installer = Join-Path $release ($outputBase + '.exe')
if (-not (Test-Path -LiteralPath $installer)) { throw '未生成稳定版安装器。' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Value ($hash + '  ' + (Split-Path -Leaf $installer)) -Encoding utf8
Write-Output $installer
Write-Output ('SHA256: ' + $hash)
