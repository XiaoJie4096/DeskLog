param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceCommit = & git -C $projectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw '无法确定源码提交。' }
$sourceChanges = @(& git -C $projectRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw '无法确定工作区状态。' }
if ($sourceChanges.Count -gt 0) { throw '交付打包要求工作区干净，请先提交当前改动。' }
$packageRoot = Join-Path $projectRoot ('artifacts/packages/riji-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $packageRoot | Out-Null
Push-Location (Join-Path $projectRoot 'src/Riji.Web')
try {
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' }
} finally { Pop-Location }
& dotnet publish (Join-Path $projectRoot 'src/Riji.Desktop/Riji.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $packageRoot --nologo
if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
Set-Content -LiteralPath (Join-Path $packageRoot 'production-default') -Value 'Production'
[ordered]@{ Product = 'Riji'; Channel = 'preview'; SourceCommit = $sourceCommit; WorkingTreeDirty = $false; BuiltAtUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'build-info.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/package-readme.md') -Destination (Join-Path $packageRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts/install-webview2.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts/install-desktop.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts/restore-upgrade-backup.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts/rollback-desktop.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'browser-extension') -Destination $packageRoot -Recurse
$firefoxRoot = Join-Path $packageRoot 'browser-extension-firefox'
New-Item -ItemType Directory -Path $firefoxRoot | Out-Null
foreach ($name in @('worker.js','options.js','options.html','options.css')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('browser-extension/' + $name)) -Destination $firefoxRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'browser-extension/manifest.firefox.json') -Destination (Join-Path $firefoxRoot 'manifest.json')
$legalRoot = Join-Path $packageRoot 'ThirdParty'
New-Item -ItemType Directory -Path $legalRoot | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/Riji.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
$notice = [System.Collections.Generic.List[string]]::new()
$notice.Add('Third-party package metadata and original license notices. See ThirdParty for full texts.')
foreach ($library in $assets.libraries.psobject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $folder = $null
    foreach ($cache in $assets.packageFolders.psobject.Properties.Name) {
        $candidate = Join-Path $cache $library.Value.path
        if (Test-Path -LiteralPath $candidate) { $folder = $candidate; break }
    }
    if (-not $folder) { throw ('缺少依赖包：' + $library.Name) }
    $target = Join-Path $legalRoot ($library.Name.Replace('/', '-'))
    New-Item -ItemType Directory -Path $target | Out-Null
    $spec = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' | Select-Object -First 1
    [xml]$metadata = Get-Content -LiteralPath $spec.FullName -Raw
    Copy-Item -LiteralPath $spec.FullName -Destination $target
    $notice.Add($library.Name + ' | ' + $metadata.package.metadata.license.InnerText + ' | ' + $metadata.package.metadata.copyright)
    Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match '^(LICENSE|NOTICE|THIRD.PARTY|COPYING)' | Copy-Item -Destination $target
}
# Runtime packs are redistributed too, even though they are not ordinary package libraries.
foreach ($runtime in @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64','Microsoft.AspNetCore.App.Runtime.win-x64')) {
    $entry = $assets.project.frameworks.psobject.Properties.Value.downloadDependencies | Where-Object name -EQ $runtime | Select-Object -First 1
    if (-not $entry) { throw ('未找到运行时版本：' + $runtime) }
    $version = $entry.version.Trim('[',']').Split(',')[0]
    $folder = Join-Path @($assets.packageFolders.psobject.Properties.Name)[0] ($runtime.ToLowerInvariant() + '/' + $version)
    $target = Join-Path $legalRoot ($runtime + '-' + $version)
    New-Item -ItemType Directory -Path $target | Out-Null
    $files = Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match '^(LICENSE|NOTICE|THIRD.PARTY)'
    if (-not $files) { throw ('缺少运行时许可：' + $runtime) }
    $files | Copy-Item -Destination $target
    $notice.Add($runtime + '/' + $version)
}
foreach ($name in @('react','react-dom','scheduler')) {
    $folder = Join-Path $projectRoot ('src/Riji.Web/node_modules/' + $name)
    $npmMetadata = Get-Content -LiteralPath (Join-Path $folder 'package.json') -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath (Join-Path $folder 'LICENSE') -Destination (Join-Path $legalRoot ($name + '-LICENSE.txt'))
    $notice.Add($name + '/' + $npmMetadata.version + ' | ' + $npmMetadata.license)
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/licenses/Apache-2.0.txt') -Destination $legalRoot
Set-Content -LiteralPath (Join-Path $packageRoot 'THIRD-PARTY.txt') -Value $notice -Encoding utf8
$hashes = Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Sort-Object FullName | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetRelativePath($packageRoot, $_.FullName) }
Set-Content -LiteralPath (Join-Path $packageRoot 'files.sha256') -Value $hashes -Encoding utf8
$finalChanges = @(& git -C $projectRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw '无法核对构建后的工作区。' }
$finalCommit = & git -C $projectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $finalCommit -ne $sourceCommit -or $finalChanges.Count -gt 0) { throw '构建过程中源码状态变化，停止生成交付 ZIP。' }
Compress-Archive -LiteralPath $packageRoot -DestinationPath ($packageRoot + '.zip')
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath ($packageRoot + '.zip')
Write-Output $packageRoot
