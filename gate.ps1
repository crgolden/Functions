param([string]$Goal, [string[]]$Steps)

$ErrorActionPreference = 'Continue'
$gateCommon = Join-Path $PSScriptRoot '..\Tools\Gates\GateCommon.ps1'
if (-not (Test-Path -LiteralPath $gateCommon)) {
    Write-Host "GATE: FAILED (the Tools repository must be cloned beside this one: $gateCommon)"
    exit 1
}
. $gateCommon
$gateOutput = Join-Path ([IO.Path]::GetTempPath()) "crgolden-gates\$(Split-Path -Leaf $PSScriptRoot)"
New-Item -ItemType Directory -Force -Path $gateOutput | Out-Null
Register-GateSteps @('Integration test database configuration', 'Restore local tools', 'Begin Sonar analysis',
    'Build with dotnet', 'jb inspectcode', 'Run unit tests with coverage', 'Run integration tests with coverage',
    'End Sonar analysis',
    'Fail on open Sonar issues')
Register-StepInputs @{
    'Integration test database configuration' = @('*')
    'Restore local tools'                     = @('dotnet-tools.json')
    'Begin Sonar analysis'                    = @('*')
    'Build with dotnet'                       = @('*')
    'jb inspectcode'                          = @('*')
    'Run unit tests with coverage'            = @('*')
    'Run integration tests with coverage'     = @('*')
    'End Sonar analysis'                      = @('*')
    'Fail on open Sonar issues'               = @('*')
}
$repo = $PSScriptRoot
$sarif = (Join-Path $gateOutput 'functions-inspect.sarif')
$unitTrx = Join-Path $repo 'Functions.Tests.Unit\bin\Release\net10.0\TestResults\unit-tests.trx'
$integrationTrx = Join-Path $repo 'Functions.Tests.Integration\bin\Release\net10.0\TestResults\integration-tests.trx'
$sonarBranch = Get-SonarBranchName
$beginSonar = "Begin Sonar analysis (branch $sonarBranch)"
$build = 'Build with dotnet (Release, RestoreLockedMode)'
$endSonar = 'End Sonar analysis (quality gate waited)'
$sonarIssues = 'Fail on open Sonar issues'
$env:TZ = 'UTC'
if ($env:TZ -ne 'UTC') { Write-Host 'GATE: FAILED (TZ pin)'; exit 1 }
Set-Location $repo
Initialize-GateState 'Functions' $repo
Assert-RequestedSteps $Steps
Invoke-CatalogSteps

if ([string]::IsNullOrWhiteSpace($env:CuratorTestDatabaseConnection)) {
    $localSettingsPath = Join-Path $repo 'Functions\local.settings.json'
    if (-not (Test-Path -LiteralPath $localSettingsPath)) {
        Stop-Gate 'Integration test database configuration' 'CuratorTestDatabaseConnection is not in the environment and Functions/local.settings.json does not exist'
    }
    $env:CuratorTestDatabaseConnection = (Get-Content $localSettingsPath -Raw | ConvertFrom-Json).Values.CuratorTestDatabaseConnection
    if ([string]::IsNullOrWhiteSpace($env:CuratorTestDatabaseConnection)) {
        Stop-Gate 'Integration test database configuration' 'CuratorTestDatabaseConnection missing from the environment and from Functions/local.settings.json'
    }
    Write-Row 'Integration test database configuration' 'PASS' 'CuratorTestDatabaseConnection loaded from Functions/local.settings.json'
}
else {
    Write-Row 'Integration test database configuration' 'PASS' 'CuratorTestDatabaseConnection taken from the environment'
}

$global:LASTEXITCODE = $null
dotnet tool restore
$null = Test-Exit 'Restore local tools (dotnet tool restore)'

$sonarCarried = Test-StepCarried $sonarIssues
if ($sonarCarried) {
    $null = Test-StepCarried $beginSonar
    $null = Test-StepCarried $build
    $null = Test-StepCarried $endSonar
}
else {
    $sonarStartedAt = [DateTimeOffset]::UtcNow
    $env:JAVA_HOME = "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\jre"
    $global:LASTEXITCODE = $null
    dotnet-sonarscanner begin /k:"crgolden_Functions" /o:"crgolden" /d:sonar.host.url="https://sonarcloud.io" /d:sonar.cs.vscoveragexml.reportsPaths="coverage.xml,coverage-integration.xml" /d:sonar.exclusions="**/bin/**,**/obj/**" /d:sonar.coverage.exclusions="**/Program.cs,**/gate.ps1" /d:sonar.qualitygate.wait=true /d:sonar.scanner.skipJreProvisioning=true /d:sonar.branch.name="$sonarBranch"
    $null = Test-Exit $beginSonar

    $global:LASTEXITCODE = $null
    dotnet build --no-incremental --configuration Release /p:RestoreLockedMode=true -warnaserror
    $null = Test-Exit $build
}

if (-not (Test-StepCarried 'jb inspectcode')) {
    if (Test-Path $sarif) { Remove-Item $sarif -Force }
    dotnet jb inspectcode "$repo\Functions.slnx" --no-build -e=WARNING --caches-home="$(New-InspectCodeCaches $gateOutput)" --output="$sarif"
    Test-Sarif $sarif
}

if (-not (Test-StepCarried 'Run unit tests with coverage (Category=Unit)')) {
    if (Test-Path $unitTrx) { Remove-Item $unitTrx -Force }
    $global:LASTEXITCODE = $null
    dotnet dotnet-coverage collect `
        "dotnet test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit --stop-on-fail on --report-xunit-trx --report-xunit-trx-filename unit-tests.trx --results-directory=Functions.Tests.Unit/bin/Release/net10.0/TestResults" `
        -f xml -o "coverage.xml" -s "coverage.settings.xml"
    Test-Trx 'Run unit tests with coverage (Category=Unit)' $unitTrx $global:LASTEXITCODE -floor 1
}

if (-not (Test-StepCarried 'Run integration tests with coverage (Category=Integration)')) {
    if (Test-Path $integrationTrx) { Remove-Item $integrationTrx -Force }
    $global:LASTEXITCODE = $null
    dotnet dotnet-coverage collect `
        "dotnet test --project Functions.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration --stop-on-fail on --report-xunit-trx --report-xunit-trx-filename integration-tests.trx --results-directory=Functions.Tests.Integration/bin/Release/net10.0/TestResults" `
        -f xml -o "coverage-integration.xml" -s "coverage.settings.xml"
    Test-Trx 'Run integration tests with coverage (Category=Integration)' $integrationTrx $global:LASTEXITCODE -floor 1
}

if (-not $sonarCarried) {
    $global:LASTEXITCODE = $null
    dotnet-sonarscanner end
    $null = Test-Exit $endSonar
    Test-SonarIssues $sonarIssues 'crgolden_Functions' $sonarBranch $sonarStartedAt
}

Write-Row 'Upload test results / dotnet publish / Upload artifact / deploy' 'NOT RUN' 'delivery steps, not checks'
Complete-Gate
