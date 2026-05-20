# Testing

Testing documentation for the `Functions/` repo. For xUnit runner flags and general test guidance, see the workspace-level [TESTING.md](../TESTING.md).

---

## Test Categories

| Trait | Scope | Requires |
|---|---|---|
| `Category=Unit` | Fast, no external dependencies | Nothing |

No E2E or Integration test categories exist currently. `EmailTests.cs` is a placeholder.

---

## Running Tests Locally

No `ASPNETCORE_ENVIRONMENT` override needed — Functions reads local config from `local.settings.json`, not from `Program.cs` startup branches.

```powershell
dotnet build Functions.Tests --configuration Debug
.\Functions.Tests\bin\Debug\net10.0\Functions.Tests.exe -trait "Category=Unit" -showLiveOutput
```

---

## CI Pipeline

The GitHub Actions workflow (`.github/workflows/main_crgolden-functions.yml`) runs on push to `main` and `workflow_dispatch`:

1. Build solution (`dotnet build --configuration Release --output ./output`)
2. Deploy to Azure Function App `crgolden-functions` via `Azure/functions-action`

No test step is present in the current workflow. Tests run locally only.

---

## Local SonarCloud Analysis

Generate coverage first (unit tests only), then run from `Functions/`:

```powershell
dotnet-coverage collect `
  "dotnet test --project Functions.Tests --no-build --configuration Release -- --filter-trait Category=Unit --report-xunit-trx --report-xunit-trx-filename unit-tests.trx" `
  -f xml -o "coverage.xml" -s "coverage.settings.xml"

$env:SONAR_TOKEN = "<token>"
& "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\bin\sonar-scanner.bat" `
  "-Dsonar.projectKey=crgolden_Functions" `
  "-Dsonar.organization=crgolden" `
  "-Dsonar.sources=Functions" `
  "-Dsonar.tests=Functions.Tests" `
  "-Dsonar.exclusions=**/bin/**,**/obj/**" `
  "-Dsonar.cs.vscoveragexml.reportsPaths=coverage.xml"
```

Required coverage files: `coverage.xml`.
