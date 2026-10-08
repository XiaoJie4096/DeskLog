param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $projectRoot 'browser-extension'
$xpiPath = Join-Path $sourceRoot 'signed/riji-firefox.xpi'
$hashFile = Get-Content -LiteralPath ($xpiPath + '.sha256') -Raw -Encoding utf8
if ($hashFile -notmatch '^([0-9A-Fa-f]{64})  riji-firefox\.xpi\s*$') { throw 'Invalid Firefox XPI hash file.' }
$expectedHash = $Matches[1]
$actualHash = (Get-FileHash -LiteralPath $xpiPath -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw 'Firefox XPI hash mismatch. Keep the Mozilla-signed file unchanged.' }

# Check artifact integrity and source parity; Firefox performs signature trust validation on install.
$zip = [IO.Compression.ZipFile]::OpenRead($xpiPath)
try {
    $expectedFiles = @('manifest.json','worker.js','options.js','options.html','options.css','privacy.html','LICENSE.txt')
    $signatures = @('META-INF/cose.manifest','META-INF/cose.sig','META-INF/manifest.mf','META-INF/mozilla.sf','META-INF/mozilla.rsa')
    $files = @{}
    foreach ($entry in $zip.Entries) {
        if ($files.ContainsKey($entry.FullName)) { throw ('Duplicate XPI entry: ' + $entry.FullName) }
        $files[$entry.FullName] = $entry
    }
    foreach ($name in $expectedFiles + $signatures) {
        if (-not $files.ContainsKey($name) -or $files[$name].Length -eq 0) { throw ('Missing XPI entry: ' + $name) }
    }
    if ($files.Count -ne ($expectedFiles.Count + $signatures.Count)) { throw 'Unexpected XPI entries.' }
    foreach ($name in $expectedFiles) {
        $stream = $files[$name].Open()
        $buffer = [IO.MemoryStream]::new()
        try { $stream.CopyTo($buffer); $bytes = $buffer.ToArray() }
        finally { $stream.Dispose(); $buffer.Dispose() }
        $sourceName = if ($name -eq 'manifest.json') { 'manifest.firefox.json' } else { $name }
        $sourceBytes = [IO.File]::ReadAllBytes((Join-Path $sourceRoot $sourceName))
        if ($name -eq 'manifest.json') {
            $actual = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json | ConvertTo-Json -Depth 20 -Compress
            $expected = [Text.Encoding]::UTF8.GetString($sourceBytes) | ConvertFrom-Json | ConvertTo-Json -Depth 20 -Compress
            if ($actual -cne $expected) { throw 'Firefox manifest changed; sign the updated extension before release.' }
        } elseif ([Convert]::ToBase64String($bytes) -cne [Convert]::ToBase64String($sourceBytes)) {
            throw ('Firefox source changed; sign the updated extension before release: ' + $name)
        }
    }
} finally { $zip.Dispose() }
Write-Output ('Firefox XPI integrity and source parity verified: ' + $actualHash)
