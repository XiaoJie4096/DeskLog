param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $projectRoot 'src/Riji.Web')
try {
    & npm ci --no-fund
    if ($LASTEXITCODE -ne 0) { throw '前端依赖安装失败。' }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' }
} finally { Pop-Location }
Push-Location $projectRoot
try {
    & dotnet build Riji.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw '桌面构建失败。' }
    if ($Test) {
        & node --experimental-strip-types --test tests/record-gaps.test.mjs tests/ui-data.test.mjs
        if ($LASTEXITCODE -ne 0) { throw '记录缺口测试失败。' }
        & node --test tests/browser-extension.test.cjs
        if ($LASTEXITCODE -ne 0) { throw '扩展隐私测试失败。' }
        & dotnet test tests/Riji.Tests/Riji.Tests.csproj --no-build --nologo
        if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
    }
} finally { Pop-Location }
