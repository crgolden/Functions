[string[]]$Command = @($args)
if (-not $Command.Count) {
    Write-Host 'Use-TestDatabase.ps1 was given no command to run'
    exit 1
}
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($env:CuratorTestDatabaseConnection)) {
    $localSettingsPath = Join-Path $repo 'Functions\local.settings.json'
    if (-not (Test-Path -LiteralPath $localSettingsPath)) {
        Write-Host 'CuratorTestDatabaseConnection is not in the environment and Functions/local.settings.json does not exist'
        exit 1
    }
    $env:CuratorTestDatabaseConnection = (Get-Content -LiteralPath $localSettingsPath -Raw | ConvertFrom-Json).Values.CuratorTestDatabaseConnection
    if ([string]::IsNullOrWhiteSpace($env:CuratorTestDatabaseConnection)) {
        Write-Host 'CuratorTestDatabaseConnection is missing from the environment and from Functions/local.settings.json'
        exit 1
    }
}
Set-Location -LiteralPath $repo
$global:LASTEXITCODE = $null
& $Command[0] @($Command | Select-Object -Skip 1)
if ($null -eq $global:LASTEXITCODE) { exit 1 }
exit $global:LASTEXITCODE
