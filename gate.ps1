param([string]$Goal, [string]$Only)

$ErrorActionPreference = 'Continue'
$gateCommon = $env:GATE_LIBRARY
if (-not $gateCommon -or -not (Test-Path -LiteralPath $gateCommon -PathType Leaf)) {
    Write-Host 'GATE: FAILED (GATE_LIBRARY names no gate library script; README.md, section Gate, says how to plug one in or run the steps without one)'
    exit 1
}
. $gateCommon
$gateOutput = if (Get-Command Get-GateOutputDirectory -ErrorAction SilentlyContinue) {
    Get-GateOutputDirectory (Split-Path -Leaf $PSScriptRoot)
} else {
    (New-Item -ItemType Directory -Force -Path (Join-Path ([IO.Path]::GetTempPath()) "claude\crgolden-gates\$(Split-Path -Leaf $PSScriptRoot)")).FullName
}
Register-GateStep @('Integration test database configuration', 'Restore local tools', 'Begin Sonar analysis',
    'Build with dotnet', 'jb inspectcode', 'Run unit tests with coverage', 'Run integration tests with coverage',
    'End Sonar analysis',
    'Fail on open Sonar issues')
Register-StepInput 'Integration test database configuration' @('*')
Register-StepInput 'Restore local tools' @('dotnet-tools.json')
Register-StepInput 'Begin Sonar analysis' @('*')
Register-StepInput 'Build with dotnet' @('*')
Register-StepInput 'jb inspectcode' @('*')
Register-StepInput 'Run unit tests with coverage' @('*', '!Functions.Tests.Integration/*')
Register-StepInput 'Run integration tests with coverage' @('*', '!Functions.Tests.Unit/*')
Register-StepInput 'End Sonar analysis' @('*')
Register-StepInput 'Fail on open Sonar issues' @('*')
$repo = $PSScriptRoot
$sarif = (Join-Path $gateOutput 'functions-inspect.sarif')
$unitTrx = Join-Path $repo 'Functions.Tests.Unit\bin\Release\net11.0\TestResults\unit-tests.trx'
$integrationTrx = Join-Path $repo 'Functions.Tests.Integration\bin\Release\net11.0\TestResults\integration-tests.trx'
$sonarBranch = Get-SonarBranchName
$beginSonar = "Begin Sonar analysis (branch $sonarBranch)"
$build = 'Build with dotnet (Release, RestoreLockedMode)'
$endSonar = 'End Sonar analysis (quality gate waited)'
$sonarIssues = 'Fail on open Sonar issues'
$env:TZ = 'UTC'
if ($env:TZ -ne 'UTC') { Write-Host 'GATE: FAILED (TZ pin)'; exit 1 }
Set-Location $repo
$probeProjects = @('Functions.Tests.Unit', 'Functions.Tests.Integration')
if ($Only) {
    $probeFile, $probeTest = $Only -split '::', 2
    $probeFile = "$probeFile".Trim().Replace('\', '/')
    $probeTest = "$probeTest".Trim()
    $probeProject = $probeFile.Split('/')[0]
    if (-not $probeTest -or $probeTest.Contains(',') -or $probeProject -notin $probeProjects -or
            -not (Test-Path -LiteralPath (Join-Path $repo $probeFile) -PathType Leaf)) {
        Write-Host "PROBE: FAILED (a probe runs exactly one test, named by the file it lives in: -Only '<a test file under $($probeProjects -join ' | ')>::<test method>')"
        exit 1
    }
    $probeClass = [IO.Path]::GetFileNameWithoutExtension($probeFile)
    if ($probeProject -eq $probeProjects[1]) {
        if ([string]::IsNullOrWhiteSpace($env:CuratorTestDatabaseConnection)) {
            $localSettingsPath = Join-Path $repo 'Functions\local.settings.json'
            if (Test-Path -LiteralPath $localSettingsPath) {
                $env:CuratorTestDatabaseConnection = (Get-Content $localSettingsPath -Raw | ConvertFrom-Json).Values.CuratorTestDatabaseConnection
            }
            if ([string]::IsNullOrWhiteSpace($env:CuratorTestDatabaseConnection)) {
                Write-Host 'PROBE: FAILED (CuratorTestDatabaseConnection is in neither the environment nor Functions/local.settings.json)'
                exit 1
            }
        }
    }
    $probeResults = Join-Path $gateOutput 'probe-results'
    $probeTrx = Join-Path $probeResults 'probe.trx'
    if (Test-Path -LiteralPath $probeTrx) { Remove-Item -LiteralPath $probeTrx -Force }
    Write-Host "PROBE: running one test, '$probeTest' of $probeClass in $probeProject; no gate record or verdict is written"
    $global:LASTEXITCODE = $null
    dotnet test --project $probeProject --configuration Release -- --filter-query "/*/*/$probeClass/$probeTest" --report-xunit-trx --report-xunit-trx-filename probe.trx --results-directory=$probeResults
    $probeExit = $global:LASTEXITCODE
    if (-not (Test-Path -LiteralPath $probeTrx)) { Write-Host "PROBE: FAILED (no TRX, exit $probeExit)"; exit 1 }
    $counters = ([xml](Get-Content -LiteralPath $probeTrx -Raw)).TestRun.ResultSummary.Counters
    if ($probeExit -ne 0 -or [int]$counters.executed -ne 1 -or [int]$counters.failed -ne 0) {
        Write-Host "PROBE: FAILED (a probe runs one test, and '$Only' executed $($counters.executed), failed $($counters.failed), exit $probeExit)"
        exit 1
    }
    Write-Host "PROBE: PASSED for the one test named, against the last build's binaries; no verdict was saved"
    exit 0
}
exit (Invoke-Gate {
Initialize-GateState 'Functions' $repo
Get-OwedStep | Register-OwedStep
Invoke-GateLibraryStep

$dbConfig = 'Integration test database configuration'
Invoke-GateStep -Name $dbConfig -Always -Body {
    if ([string]::IsNullOrWhiteSpace($env:CuratorTestDatabaseConnection)) {
        $localSettingsPath = Join-Path $repo 'Functions\local.settings.json'
        if (-not (Test-Path -LiteralPath $localSettingsPath)) {
            Stop-Gate $dbConfig 'CuratorTestDatabaseConnection is not in the environment and Functions/local.settings.json does not exist'
        }
        $env:CuratorTestDatabaseConnection = (Get-Content $localSettingsPath -Raw | ConvertFrom-Json).Values.CuratorTestDatabaseConnection
        if ([string]::IsNullOrWhiteSpace($env:CuratorTestDatabaseConnection)) {
            Stop-Gate $dbConfig 'CuratorTestDatabaseConnection missing from the environment and from Functions/local.settings.json'
        }
        Write-Row $dbConfig 'PASS' 'CuratorTestDatabaseConnection loaded from Functions/local.settings.json'
    }
    else {
        Write-Row $dbConfig 'PASS' 'CuratorTestDatabaseConnection taken from the environment'
    }
}

$restore = 'Restore local tools (dotnet tool restore)'
Invoke-GateStep -Name $restore -Body {
    $global:LASTEXITCODE = $null
    dotnet tool restore
    Assert-StepExit $restore
}

$sonarCarried = Test-StepCarried $sonarIssues
if ($sonarCarried) {
    $null = Test-StepCarried $beginSonar
    $null = Test-StepCarried $build
    $null = Test-StepCarried $endSonar
}
else {
    $sonarStartedAt = [DateTimeOffset]::UtcNow
    Invoke-GateStep -Name $beginSonar -Needs @($restore) -Always -Body {
        $env:JAVA_HOME = Get-SonarScannerJavaHome $beginSonar
        $global:LASTEXITCODE = $null
        dotnet-sonarscanner begin /k:"crgolden_Functions" /o:"crgolden" /d:sonar.host.url="https://sonarcloud.io" /d:sonar.cs.vscoveragexml.reportsPaths="coverage.xml,coverage-integration.xml" /d:sonar.exclusions="**/bin/**,**/obj/**" /d:sonar.coverage.exclusions="**/Program.cs,**/gate.ps1" /d:sonar.qualitygate.wait=true /d:sonar.scanner.skipJreProvisioning=true /d:sonar.scm.use.blame.algorithm=GIT_NATIVE_BLAME /d:sonar.branch.name="$sonarBranch"
        Assert-StepExit $beginSonar
    }

    Invoke-GateStep -Name $build -Needs @($restore) -Always -Body {
        $global:LASTEXITCODE = $null
        dotnet clean --configuration Release
        Assert-StepExit $build

        $global:LASTEXITCODE = $null
        dotnet restore "$repo\Functions\Functions.csproj" /p:RestoreLockedMode=true
        Assert-StepExit $build

        $global:LASTEXITCODE = $null
        dotnet build --configuration Release /p:RestoreLockedMode=true -warnaserror
        Assert-StepExit $build
    }
}

Invoke-GateStep -Name 'jb inspectcode' -Needs @($build) -Body {
    if (Test-Path $sarif) { Remove-Item $sarif -Force }
    dotnet jb inspectcode "$repo\Functions.slnx" --no-build -e=WARNING --caches-home="$(New-InspectCodeCache $gateOutput)" --output="$sarif"
    Test-Sarif $sarif
}

$unit = 'Run unit tests with coverage (Category=Unit)'
Invoke-GateStep -Name $unit -Needs @($build) -Body {
    if (Test-Path $unitTrx) { Remove-Item $unitTrx -Force }
    $global:LASTEXITCODE = $null
    dotnet dotnet-coverage collect `
        "dotnet test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit --report-xunit-trx --report-xunit-trx-filename unit-tests.trx --results-directory=Functions.Tests.Unit/bin/Release/net11.0/TestResults" `
        -f xml -o "coverage.xml" -s "coverage.settings.xml"
    Test-Trx $unit $unitTrx $global:LASTEXITCODE -floor 1
}

$integration = 'Run integration tests with coverage (Category=Integration)'
Invoke-GateStep -Name $integration -Needs @($build, $dbConfig) -Body {
    if (Test-Path $integrationTrx) { Remove-Item $integrationTrx -Force }
    $global:LASTEXITCODE = $null
    dotnet dotnet-coverage collect `
        "dotnet test --project Functions.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration --report-xunit-trx --report-xunit-trx-filename integration-tests.trx --results-directory=Functions.Tests.Integration/bin/Release/net11.0/TestResults" `
        -f xml -o "coverage-integration.xml" -s "coverage.settings.xml"
    Test-Trx $integration $integrationTrx $global:LASTEXITCODE -floor 1
}

if (-not $sonarCarried) {
    Invoke-GateStep -Name $endSonar -Needs @($beginSonar, $build) -Always -Body {
        $global:LASTEXITCODE = $null
        dotnet-sonarscanner end
        Assert-StepExit $endSonar
    }
    Invoke-GateStep -Name $sonarIssues -Needs @($endSonar) -Always -Body {
        Assert-NoOpenSonarIssue $sonarIssues 'crgolden_Functions' $sonarBranch $sonarStartedAt
    }
}

Write-Row 'Upload test results / dotnet publish / Upload artifact / deploy' 'NOT RUN' 'delivery steps, not checks'
Complete-Gate
})
