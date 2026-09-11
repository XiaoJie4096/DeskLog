$ErrorActionPreference = 'Stop'
# Build isolated archive fixtures; never alter the actual release package.
$root = Join-Path ([IO.Path]::GetTempPath()) ('Riji-Integrity-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$verifier = Join-Path $PSScriptRoot 'verify-package.ps1'
$required = @('Riji.Desktop.exe','install-desktop.ps1','restore-upgrade-backup.ps1','rollback-desktop.ps1','browser-extension/manifest.json','README.md')
foreach ($scenario in @('valid','tampered','unlisted','missing')) {
    $path = Join-Path $root ($scenario + '.zip')
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifest = @()
        foreach ($name in $required) {
            $bytes = [Text.Encoding]::UTF8.GetBytes('synthetic fixture')
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') } finally { $sha.Dispose() }
            $manifest += "$hash  $name"
            if ($scenario -eq 'missing' -and $name -eq 'README.md') { continue }
            if ($scenario -eq 'tampered' -and $name -eq 'README.md') { $bytes = [Text.Encoding]::UTF8.GetBytes('changed fixture') }
            $stream = $zip.CreateEntry('package/' + $name).Open()
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
        if ($scenario -eq 'unlisted') { $zip.CreateEntry('package/extra.txt') | Out-Null }
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('package/files.sha256').Open())
        try { $writer.Write($manifest -join "`n") } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    $rejected = $false
    try { & $verifier -PackagePath $path | Out-Null } catch { $rejected = $true }
    if ($rejected -ne ($scenario -ne 'valid')) { throw "Unexpected verification result: $scenario" }
    Write-Output "$scenario passed"
}
Write-Output "Fixtures: $root"
