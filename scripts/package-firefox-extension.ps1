param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& node (Join-Path $PSScriptRoot 'prepare-browser-extension.cjs') firefox
if ($LASTEXITCODE -ne 0) { throw 'Firefox 扩展准备失败。' }
$extensionRoot = Join-Path $projectRoot 'artifacts/extensions/firefox'
$manifest = Get-Content -LiteralPath (Join-Path $extensionRoot 'manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
$packagePath = Join-Path $projectRoot ('artifacts/extensions/riji-firefox-' + $manifest.version + '.zip')
# Package only reviewed extension sources, with manifest.json at the archive root.
$files = @('manifest.json','worker.js','options.js','options.html','options.css','privacy.html','LICENSE.txt') |
    ForEach-Object { Join-Path $extensionRoot $_ }
Compress-Archive -LiteralPath $files -DestinationPath $packagePath -Force
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
Set-Content -LiteralPath ($packagePath + '.sha256') -Value ($hash + '  ' + (Split-Path -Leaf $packagePath)) -Encoding utf8
Write-Output $packagePath
Write-Output ('SHA256: ' + $hash)
Write-Output '此 ZIP 仅供提交签名，尚不是已签名的 XPI。'
