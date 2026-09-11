param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
# Verify archive contents without extracting or launching the application.
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $files = @{}
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.EndsWith('/')) { continue }
        if ($name.StartsWith('/') -or $name.Contains(':') -or $name.Split('/') -contains '..') { throw "Unsafe archive path: $name" }
        if ($files.ContainsKey($name)) { throw "Duplicate archive path: $name" }
        $files[$name] = $entry
    }
    $manifests = @($files.Keys | Where-Object { $_ -match '(^|/)files\.sha256$' })
    if ($manifests.Count -ne 1) { throw 'Expected exactly one hash manifest.' }
    $manifest = $manifests[0]
    $prefix = $manifest.Substring(0, $manifest.Length - 'files.sha256'.Length)
    foreach ($required in @('Riji.Desktop.exe','install-desktop.ps1','restore-upgrade-backup.ps1','rollback-desktop.ps1','browser-extension/manifest.json','README.md')) {
        if (-not $files.ContainsKey($prefix + $required)) { throw "Missing required file: $required" }
    }
    $reader = [IO.StreamReader]::new($files[$manifest].Open())
    try { $lines = $reader.ReadToEnd() -split '\r?\n' } finally { $reader.Dispose() }
    $checked = @{}
    foreach ($line in $lines) {
        if (-not $line.Trim()) { continue }
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw 'Malformed hash manifest.' }
        $expected = $Matches[1]
        $name = $prefix + $Matches[2].Replace('\', '/')
        if ($checked.ContainsKey($name) -or -not $files.ContainsKey($name)) { throw "Duplicate or missing manifest file: $name" }
        $stream = $files[$name].Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($actual -ne $expected) { throw "Hash mismatch: $name" }
        $checked[$name] = $true
    }
    foreach ($name in $files.Keys) {
        if ($name -ne $manifest -and -not $checked.ContainsKey($name)) { throw "Unlisted file: $name" }
    }
    [pscustomobject]@{ Package = $package; SHA256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash; FileCount = $files.Count; VerifiedFiles = $checked.Count; Passed = $true } | ConvertTo-Json
} finally { $zip.Dispose() }
