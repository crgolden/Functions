# Testing

Testing documentation for the `Functions/` repo. For xUnit runner flags and general test guidance, see the workspace-level [TESTING.md](../TESTING.md).

---

Unit test coding standards (MockBehavior.Strict, argument verification, SetupSequence, no control-flow in tests, etc.) are in the workspace-level [Unit Test Standards](../TESTING.md#unit-test-standards).

## Test Categories

| Trait | Scope | Requires |
|---|---|---|
| `Category=Unit` | Fast, no external dependencies | Nothing |

No E2E or Integration test categories exist currently.

| Test file | What it covers |
|---|---|
| `EmailTests.cs` | `Email` function — null payload exits cleanly |
| `SitemapGeneratorTests.cs` | Constructor throws when `ChurchesBaseUrl` is absent; succeeds when configured |
| `ScraperWorkerTests.cs` | Null payload, HTTP success/failure/throw paths; blob upload and extraction-request dispatch |
| `ExtractorWorkerTests.cs` | `ExtractPhone` and `ExtractFromHtmlAsync` pure logic; `Run` routes high-confidence+city to `geocoding-requests`, low-confidence or missing city to `enrichment-requests` |
| `EnrichmentWorkerTests.cs` | Constructor throws when `OpenAIModel` absent; null payload exits without OpenAI call; `BuildPageContent` (null blob falls back to a placeholder, short HTML passes through unchanged, oversized HTML truncated to the prompt cap); `TryParseEnrichment` truth table (all fields, fallback paths, bool variants) |
| `GeocoderWorkerTests.cs` | `ParseCensusResponse` (match/empty); `GeocodeAsync` (no address, HTTP success/non-success/throw); `UpsertChurchAsync` (insert+link, update, null optionals, populated optionals); `Run` (null payload, full geocode+upsert path) |
| `BulkImportJobTests.cs` | `ParseIrsCsv` (field mapping, NTEE codes, pre-geocoded coords, skip-on-missing-name/state, empty/header-only); `ParseOsm` (all address fields, `addr:state` normalization, skip-on-unrecognized-state, skip-on-missing-name/state/city/postcode/tags, no elements key); `ParseCoordinates`, `NteeToWorshipStyle`, `NteeToDenomination`, `OsmDenominationToName` (truth tables); `Run` (missing blobPath, blob not found, IRS new records published, IRS/in-file duplicates skipped, OSM source) |
| `NormalizerTests.cs` | `NormalizePhone` (parens/dashes/spaces, international prefix, already-normalized, invalid/null/short); `NormalizeZip` (9-digit, non-digit chars, 4-digit, null); `NormalizeUrl` (https, http upgrade, missing scheme, trailing slash, whitespace, null) |
| `ContributionProcessorTests.cs` | Null payload exits cleanly without DB access |
| `DeduplicationJobTests.cs` | `JaroWinkler`/`HaversineDistance`/`ToRad` (pure, published reference values); `BucketKey` grid-cell assignment; `Run` orchestration (distance guard, similarity guard, suggestion write, close pair straddling a bucket boundary still matches via the 3x3 neighbor-cell search, query excludes `(0,0)` fallback-coordinate churches and PO Box addresses — both are non-precise geocodes that produce false-positive/OOM-inducing proximity matches, a many-churches-in-one-bucket case matches correctly without excessive cost) |
| `ReGeocodeJobTests.cs` | `LoadZeroCoordChurchesAsync` query shape; `Run` (candidate geocode success/failure counts, coordinate update dispatch) |

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

Generate coverage first, then run from `Functions/`. Unit coverage is OpenCover (branch-bearing, via
`coverlet.console` pinned in `dotnet-tools.json` — restore with `dotnet tool restore`; see the workspace
`TESTING.md` for the rationale). Functions is unit-only in CI, so OpenCover is the only report.

```powershell
dotnet build Functions.Tests --configuration Release
dotnet tool restore
dotnet coverlet Functions.Tests\bin\Release\net10.0 `
  --target "dotnet" `
  --targetargs "test --project Functions.Tests --no-build --configuration Release -- --filter-trait Category=Unit" `
  --format opencover --output "coverage.opencover.xml" `
  --skipautoprops --exclude-by-attribute GeneratedCodeAttribute `
  --exclude-by-file "**/obj/**" --exclude-by-file "**/Program.cs" `
  --does-not-return-attribute DoesNotReturnAttribute --include "[Functions]*"

$env:SONAR_TOKEN = "<token>"
& "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\bin\sonar-scanner.bat" `
  "-Dsonar.projectKey=crgolden_Functions" `
  "-Dsonar.organization=crgolden" `
  "-Dsonar.sources=Functions" `
  "-Dsonar.tests=Functions.Tests" `
  "-Dsonar.exclusions=**/bin/**,**/obj/**" `
  "-Dsonar.cs.opencover.reportsPaths=coverage.opencover.xml"
```

Required coverage files: `coverage.opencover.xml` (unit, OpenCover).

### When to build a truth table

The coverage **score is read from SonarCloud, never hand-maintained** here. Build a per-method table in `COVERAGE-TRUTH-TABLES.md` only when SonarCloud flags a method with **cognitive complexity > 15 AND uncovered conditions > 0**: the table is escalation for the gnarly few, not a per-class deliverable. See `../DESIGN-LANGUAGE.md` and `../TESTING-COVERAGE.md`.
