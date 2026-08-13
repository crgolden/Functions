# Testing

Testing documentation for the `Functions/` repo. For xUnit runner flags and general test guidance, see the workspace-level [TESTING.md](../AGENTS/TESTING.md).

---

Unit test coding standards (MockBehavior.Strict, argument verification, SetupSequence, no control-flow in tests, etc.) are in the workspace-level [Unit Test Standards](../AGENTS/TESTING.md#unit-test-standards).

## Test Categories

| Trait | Scope | Requires |
|---|---|---|
| `Category=Unit` | Fast, no external dependencies | Nothing |

No E2E or Integration test categories exist currently.

| Test file | What it covers |
|---|---|
| `EmailTests.cs` | `Email` function — null payload exits cleanly |
| `SitemapGeneratorTests.cs` | Constructor throws when `ChurchesBaseUrl` is absent; succeeds when configured |
| `ScraperWorkerTests.cs` | Malformed payload dead-letters; HTTP success/failure paths; `HttpRequestException`/`TaskCanceledException` (expected fetch failures) mark the source failed and complete rather than abandon; an unexpected exception type still marks failed, abandons, and rethrows; host-shutdown cancellation (pre-cancelled token) is excluded from the expected-failure path; blob upload and extraction-request dispatch. Note on the host-cancellation test: `FakeDbConnection.OpenAsync` uses the real `DbConnection` base implementation, which honors a pre-cancelled token and faults immediately, so that test can't exercise the catch-all's own DB update/abandon calls (a fake-infra limit, not a claim about production behavior) — it only proves the expected-failure "complete" path is skipped, which a strict mock with no Complete/Abandon setup enforces (either call would itself throw) |
| `ExtractorWorkerTests.cs` | `ExtractPhone` and `ExtractFromHtmlAsync` pure logic; malformed payload dead-letters; `Run` routes high-confidence+city to `geocoding-requests`, low-confidence or missing city to `enrichment-requests` |
| `EnrichmentWorkerTests.cs` | Constructor throws when `OpenAIModel` absent; malformed payload dead-letters without calling OpenAI; `ClientResultException` under the retry ceiling abandons quietly for broker redelivery, at/above the ceiling degrades to the extractor's partial data and completes; `BuildPageContent` (null blob falls back to a placeholder, short HTML passes through unchanged, oversized HTML truncated to the prompt cap); `TryParseEnrichment` truth table (all fields, fallback paths, bool variants) |
| `GeocoderWorkerTests.cs` | `ParseCensusResponse` (match/empty); `GeocodeAsync` (no address, HTTP success/non-success/throw); `UpsertChurchAsync` (insert+link, update, null optionals, populated optionals); `Run` (malformed payload dead-letters, full geocode+upsert path) |
| `BulkImportJobTests.cs` | `ParseIrsCsv` (field mapping, NTEE codes, pre-geocoded coords, skip-on-missing-name/state, empty/header-only); `ParseOsm` (all address fields, `addr:state` normalization, skip-on-unrecognized-state, skip-on-missing-name/state/city/postcode/tags, no elements key, multi-value `name` tag prefers the Latin/ASCII segment regardless of which side it's on, ties keep the first segment, trailing empty segment ignored); `ParseCoordinates`, `NteeToWorshipStyle`, `NteeToDenomination`, `OsmDenominationToName` (truth tables); `Run` (missing blobPath, blob not found, IRS new records published, IRS/in-file duplicates skipped, OSM source) |
| `NormalizerTests.cs` | `NormalizePhone` (parens/dashes/spaces, international prefix, already-normalized, invalid/null/short); `NormalizeZip` (9-digit, non-digit chars, 4-digit, null); `NormalizeUrl` (https, http upgrade, missing scheme, trailing slash, whitespace, null, multi-value semicolon-joined keeps only the first URL) |
| `ContributionProcessorTests.cs` | Malformed payload dead-letters without DB access |
| `DeduplicationJobTests.cs` | `JaroWinkler`/`HaversineDistance`/`ToRad` (pure, published reference values); `BucketKey` grid-cell assignment; `Run` orchestration (distance guard, similarity guard, suggestion write, close pair straddling a bucket boundary still matches via the 3x3 neighbor-cell search, query excludes `(0,0)` fallback-coordinate churches and PO Box addresses — both are non-precise geocodes that produce false-positive/OOM-inducing proximity matches, a many-churches-in-one-bucket case matches correctly without excessive cost) |
| `ReGeocodeJobTests.cs` | `LoadZeroCoordChurchesAsync` query shape; `Run` (candidate geocode success/failure counts, coordinate update dispatch) |
| `QueueDepthMonitorJobTests.cs` | `Run` handles a `RequestFailedException` from the Service Bus admin client gracefully for every queue instead of throwing (would otherwise feed the exceptions alert every 15 minutes) |
| `ScheduledRefreshWorkerTests.cs` | `Run` — malformed/missing-identity payload dead-letters without DB access; no schedule, a paused schedule, and a stale `scheduled_for` (mismatched against the stored `next_run_at`) all discard (complete, no dispatch); a due schedule with no active run dispatches (`job_runs` insert + `curator-library-refresh` send) and advances the schedule + publishes the next tick; an already-active previous run skips dispatch but still advances the schedule; a generically-failed previous run increments `consecutive_failures` and dispatches again; a previous run whose error names an expired PSN link pauses immediately with no dispatch and no next tick; `consecutive_failures` crossing the configured threshold pauses the same way; a succeeded previous run resets `consecutive_failures` to zero |

---

## Running Tests Locally

No `ASPNETCORE_ENVIRONMENT` override needed — Functions reads local config from `local.settings.json`, not from `Program.cs` startup branches.

```powershell
dotnet build Functions.Tests.Unit --configuration Debug
.\Functions.Tests.Unit\bin\Debug\net10.0\Functions.Tests.Unit.exe -trait "Category=Unit" -showLiveOutput
```

---

## CI Pipeline

The GitHub Actions workflow (`.github/workflows/main_crgolden-functions.yml`) runs on push to `main`, on pull requests, and on `workflow_dispatch`:

1. Begin Sonar analysis
2. Build (`dotnet build --no-incremental --configuration Release /p:RestoreLockedMode=true`)
3. Run unit tests with coverage (`dotnet dotnet-coverage collect "dotnet test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit ..."`)
4. `dotnet publish` + upload the deploy artifact
5. End Sonar analysis
6. Deploy to Azure Function App `crgolden-functions` via `Azure/functions-action` (`main` only)

The previous line here — "No test step is present in the current workflow" — was wrong; the coverage step above runs the full `Category=Unit` suite on every push and PR. `dotnet test`'s VSTest-compatible mode is gone under this repo's xUnit v3 MTP tooling, though (see "Running Tests Locally" above) — CI's own invocation works because `dotnet-coverage collect` drives it, not a bare `dotnet test`.

---

## Local SonarCloud Analysis

Generate coverage first, then run from `Functions/`. Unit coverage is OpenCover (branch-bearing, via
`coverlet.console` pinned in `dotnet-tools.json` — restore with `dotnet tool restore`; see the workspace
`TESTING.md` for the rationale). Functions is unit-only in CI, so OpenCover is the only report.

```powershell
dotnet build Functions.Tests.Unit --configuration Release
dotnet tool restore
dotnet coverlet Functions.Tests.Unit\bin\Release\net10.0 `
  --target "dotnet" `
  --targetargs "test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit" `
  --format opencover --output "coverage.opencover.xml" `
  --skipautoprops --exclude-by-attribute GeneratedCodeAttribute `
  --exclude-by-file "**/obj/**" --exclude-by-file "**/Program.cs" `
  --does-not-return-attribute DoesNotReturnAttribute --include "[Functions]*"

$env:SONAR_TOKEN = "<token>"
& "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\bin\sonar-scanner.bat" `
  "-Dsonar.projectKey=crgolden_Functions" `
  "-Dsonar.organization=crgolden" `
  "-Dsonar.sources=Functions" `
  "-Dsonar.tests=Functions.Tests.Unit" `
  "-Dsonar.exclusions=**/bin/**,**/obj/**" `
  "-Dsonar.cs.opencover.reportsPaths=coverage.opencover.xml"
```

Required coverage files: `coverage.opencover.xml` (unit, OpenCover).

### When to build a truth table

The coverage **score is read from SonarCloud, never hand-maintained** here. Build a per-method table in `COVERAGE-TRUTH-TABLES.md` only when SonarCloud flags a method with **cognitive complexity > 15 AND uncovered conditions > 0**: the table is escalation for the gnarly few, not a per-class deliverable. See `../AGENTS/DESIGN-LANGUAGE.md` and `../AGENTS/TESTING-COVERAGE.md`.
