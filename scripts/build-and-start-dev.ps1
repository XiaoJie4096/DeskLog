$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $projectRoot 'src/Riji.Desktop/bin/Debug/net10.0-windows/Riji.Desktop.exe'
# Release the development instance before rebuilding locked assemblies.
if (Test-Path -LiteralPath $executable) {
    $quit = Start-Process -FilePath $executable -ArgumentList '--quit' -PassThru -WindowStyle Hidden
    if (-not $quit.WaitForExit(10000)) { throw 'Development exit request timed out.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (Get-Process -Name 'Riji.Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Please finish saving and exit the development application.' }
        Start-Sleep -Milliseconds 200
    }
}
Push-Location (Join-Path $projectRoot 'src/Riji.Web')
try {
    if (-not (Test-Path -LiteralPath 'node_modules')) {
        & npm ci --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
    }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
} finally { Pop-Location }
Push-Location $projectRoot
try {
    & dotnet build Riji.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    & dotnet restore src/Riji.Desktop/Riji.Desktop.csproj -r win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Runtime restore failed.' }
} finally { Pop-Location }
Start-Process -FilePath $executable -WorkingDirectory $projectRoot
