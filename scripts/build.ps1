param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet restore HashLynx.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build HashLynx.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test HashLynx.sln -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet run --project tests/HashLynx.UI.Smoke -c Release --no-build -- artifacts/ui-smoke
    if ($LASTEXITCODE -ne 0) { throw 'WPF smoke checks failed.' }
    if ($Publish) {
        dotnet publish src/HashLynx.UI -c Release -r win-x64 --self-contained false -o artifacts/publish/bitlocker-drive-test
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    }
}
finally { Pop-Location }
