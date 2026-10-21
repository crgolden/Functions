# Functions

[![Build and deploy dotnet core project to Azure Function App - crgolden-functions](https://github.com/crgolden/Functions/actions/workflows/main_crgolden-functions.yml/badge.svg)](https://github.com/crgolden/Functions/actions/workflows/main_crgolden-functions.yml)

[![Quality gate](https://sonarcloud.io/api/project_badges/quality_gate?project=crgolden_Functions)](https://sonarcloud.io/summary/new_code?id=crgolden_Functions)

Azure Functions isolated worker (v4, .NET 10). It hosts three unrelated workloads in one app: a church-directory data
pipeline, transactional email delivery, and the background jobs of a PlayStation library-curation service. It writes
two databases: the church directory's SQL Server database, and the curation service's PostgreSQL database through a
connection registered under the `Curator` service key, so a class has to name it to get it.

How the parts fit together and the rules each keeps: [ARCHITECTURE.md](ARCHITECTURE.md). How to run every test tier:
[TESTING.md](TESTING.md).

## What runs here

**Queue-triggered workers** (Azure Service Bus): the church data cascade `ScraperWorker` → `ExtractorWorker` → `EnrichmentWorker` (Azure OpenAI, only for low-confidence extractions) → `GeocoderWorker` → `ChurchWriter` (the single transactional write path to the church SQL database) → `CalculateConfidenceScore`; plus `ContributionProcessor` (user corrections → pending moderation rows) and `Email` (delivery through Resend for whatever service enqueues to the `email` queue).

**Timers**: `CrawlSchedulerWorker` (enqueue recrawls every 6h), `DeduplicationJob` (nightly geo+name-similarity merge suggestions for moderator review), `SitemapGenerator` (nightly sitemap to `$web` blob static hosting), `QueueDepthMonitorJob` (queue/DLQ depth gauges every 15 min).

**HTTP admin jobs**: `BulkImportJob` (seed from IRS 990 CSV / OSM JSON blobs) and `ReGeocodeJob` (sweep rows stranded at `(0,0)`).

**PlayStation library jobs** (Azure Service Bus, against the curation PostgreSQL database): `LibraryRefreshWorker` (`curator-library-refresh`) pulls one user's PSN entitlements, canonicalizes them into the shared game catalog, enriches the titles it hasn't seen before from RAWG/OpenCritic/the PS Store, and matches their trophy progress. `LibraryRefreshContinuationWorker` (`curator-library-refresh-continuation`) resumes a refresh that a provider rate limit paused, starting from the game ids that pause recorded. `EnrichmentRunWorker` (`curator-enrichment`) runs the same enrichment catalog-wide from admin-held provider keys rather than one user's. `ScheduledRefreshWorker` (`curator-scheduled-refresh`) turns a user's standing weekly/monthly schedule into a queued refresh, advances the schedule to its next tick, and pauses it when the user's PSN link has expired or their runs keep failing. Two storefront timers, `StoreProductEnrichmentWorker` and `StoreCatalogCrawlWorker`, fill in what the PS Store's web storefront publishes.

Each queue worker takes a lease on the `job_runs` row its message names, heartbeats that lease while it works, and writes the run's terminal status and `result_summary` back to the same row, which is how the curation API reports progress to a user who is polling. That API publishes the messages and reads `job_runs`; none of the work above happens on its request path.

Two timers keep that machinery honest: `ExpiredLeaseReaper` (hourly, fails runs whose lease lapsed with nothing renewing it, so a run killed mid-flight can't sit at `running` forever) and `OpenCriticCacheSweep` (nightly, extends the shared OpenCritic score cache from where the last sweep or enrichment run left the pagination cursor).

## Technologies

- .NET 10, Azure Functions v4 isolated worker (`FunctionsApplication`), on a Linux Flex Consumption plan in production.
- Azure Service Bus, Blob Storage and Azure OpenAI; SQL Server through ADO.NET; PostgreSQL through Npgsql; Redis
  (rate limits and token cache); Resend for email.
- OpenTelemetry (OTLP) and Serilog to Elasticsearch in production.
- xUnit v3 on Microsoft.Testing.Platform, `dotnet-coverage`, ReSharper `jb inspectcode`, SonarCloud.

## Prerequisites

- The .NET 10 SDK (`global.json` pins `10.0.201` and rolls forward to the latest feature band) and Azure Functions Core
  Tools v4 (`func`).
- A GitHub personal access token with the `read:packages` scope. The `Shared` package restores from the `crgolden`
  GitHub Packages feed (`NuGet.Config`), which asks for a token even for a public package. Store it as the `GitHub`
  source's credentials in your user NuGet configuration, never in this repo:

  ```powershell
  dotnet nuget update source GitHub --username <your GitHub user> --password <token> --store-password-in-clear-text --configfile "$env:APPDATA\NuGet\NuGet.Config"
  ```

- To run the host (not to run the unit tests): an Azure Service Bus namespace and storage account the signed-in
  `az login` identity can use (both refuse shared keys), SQL Server carrying the church-directory schema, PostgreSQL
  carrying the curation schema, Redis, and an Azure OpenAI deployment.

## Clone and build

```powershell
git clone https://github.com/crgolden/Functions.git
cd Functions
dotnet tool restore
dotnet build Functions.slnx
```

## Configuration

Local values come from `Functions/local.settings.json` (gitignored; there are no User Secrets), under `Values`.
Production values are Function App settings, secrets as Key Vault references. A missing startup-required setting stops
the host before any function is indexed.

| Setting | Needed by |
|---|---|
| `OpenAIEndpoint`; `OpenAIApiKey` outside production | the host |
| `SqlConnectionStringBuilder` section | the church pipeline (SQL Server) |
| `CuratorDatabaseConnection` (a `postgresql://` URI or Npgsql keyword form) | the library jobs (PostgreSQL) |
| `StorageUri`, `ServiceBusConnection__fullyQualifiedNamespace` | Blob Storage and Service Bus, reached with `DefaultAzureCredential` |
| `RedisHost`, `RedisPort`, `RedisSsl`, `RedisPassword` | rate limiters and the PSN token cache |
| `CuratorTokenKey` | decrypting stored PSN tokens and provider keys (a base64url 32-byte key) |
| `RawgEndpoint` (with its trailing slash), `OpenCriticEndpoint`, `OpenCriticRequestsPerSecond` | enrichment |
| `ResendApiToken` | `Email` |
| `StoreGraphqlEndpoint`, `StoreProductQueryHash`, `StoreStarRatingQueryHash`, `StoreQueryHash__N` | the storefront passes |
| `TelemetryOptions__<Instrument>Description`, one per property of `TelemetryOptions` | the metrics |
| `ElasticsearchNode`, `ElasticsearchUsername`, `ElasticsearchPassword`, `AlloyEndpoint`, `BlobUri`, `DataProtectionKeyIdentifier`, `WEBSITE_SITE_NAME` | production only |

Some settings are required only by one function, which fails alone when its setting is missing: `ChurchesBaseUrl`
(`SitemapGenerator`), `CensusGeocoderUrl` (`GeocoderWorker`, `ReGeocodeJob`), `OpenAIModel` (`EnrichmentWorker`),
`CrawlRefreshDays` and `CrawlSchedulerBatchSize` (`CrawlSchedulerWorker`), `OpenCriticSweepMaxPages`
(`OpenCriticCacheSweep`), `ScheduledRefreshMaxConsecutiveFailures` (`ScheduledRefreshWorker`),
`CuratorJobReaperEnabled` (`ExpiredLeaseReaper`), and `StoreCrawlPagesPerRun`, `StoreCrawlPaceMilliseconds`,
`StoreCrawlRewalkDays` (`StoreCatalogCrawlWorker`). Provider keys are indexed sets (`RawgApiKey__N`,
`OpenCriticRapidApiKey__N`, `PsnNpsso__N`); an empty set leaves that provider unconfigured for a run.

## Run locally

```powershell
Set-Location Functions
func start
```

Set `AzureWebJobs.<FunctionName>.Disabled` to `true` in `local.settings.json` for every function you are not exercising,
and give the local host its own `AzureFunctionsWebHost__hostid`: an enabled consumer competes for whatever queue it is
pointed at. A timer can be fired by hand through the host's admin endpoint,
`POST http://localhost:7071/admin/functions/<Name>` with body `{"input":""}`.

## Tests

```powershell
dotnet test --project Functions.Tests.Unit --configuration Debug -- --filter-trait "Category=Unit"
```

The unit tier needs no network, database or secrets. The integration tier needs a PostgreSQL database carrying the
curation schema, named with a `_test` suffix; [TESTING.md](TESTING.md) has the setup. Always name the project: a bare
`dotnet test` also starts the integration tier.

## Gate

`gate.ps1` is the maintainers' gate. It loads a private toolkit from a folder beside this repo, so in a plain clone it
stops at once with `GATE: FAILED`. To gate your own change, run the checks CI runs, in this order:

```powershell
dotnet tool restore
dotnet build --no-incremental --configuration Release /p:RestoreLockedMode=true -warnaserror
dotnet jb inspectcode Functions.slnx --no-build -e=WARNING --output=inspectcode.sarif
dotnet test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait "Category=Unit"
dotnet test --project Functions.Tests.Integration --no-build --configuration Release -- --filter-trait "Category=Integration"
```

`jb inspectcode` passes only when `inspectcode.sarif` holds no results.

## Telemetry

Traces and metrics leave the app over OTLP in production only: `Program.cs` registers the OpenTelemetry pipeline inside its `IsProduction()` branch and nowhere else.

### Why the stable database semantic conventions are selected in code rather than by an app setting

`Program.cs` calls `Telemetry.SemanticConventions.OptInToStableDatabaseConventionsUnlessAlreadyChosen()` as its **first statement, above `FunctionsApplication.CreateBuilder(args)`**, not next to the OpenTelemetry registration, where it would read more naturally. `CreateBuilder` seeds a `ConfigurationManager`, which builds its providers eagerly, so the environment-variable provider has already taken its snapshot by the time that call returns. Setting the variable afterwards would still be seen by anything that reads the environment directly, but would be invisible to anything resolving the value through this app's `IConfiguration`. Above `CreateBuilder` is the one position that is correct for both, so do not move it down. Without the call the Redis instrumentation emits the pre-1.0 `db.system`/`db.statement` attributes, while the Npgsql and SQL Server legs of this app emit the stable `db.system.name`/`db.namespace`/`db.query.text` set.

**The value has to reach the instrumentation as a process environment variable, and that is the whole reason it is written in code rather than declared in configuration.** `StackExchangeRedisInstrumentationOptions` exposes a public parameterless constructor that builds its own `new ConfigurationBuilder().AddEnvironmentVariables().Build()`, and the instrumentation registers no delegating options factory, so the default options object never sees this app's `IConfiguration`. A key added to a JSON configuration file would be read by nothing, the legacy attributes would keep flowing, and no build or startup check would notice.

The helper writes the variable **only when it is unset**, so an Application Setting on the Function App still wins. That keeps `database/dup` (emit both conventions during a migration window) available without a redeploy.

## Data at rest: the `TokenCrypto` blob format

`TokenCrypto` reads and writes encrypted columns in the curation PostgreSQL database that another runtime (the curation API) also reads and writes (`psn_links.token_response_enc`, the `user_enrichment_keys` key columns). **Both runtimes must agree on this format byte for byte**, and both derive their key from the same `CuratorTokenKey` value: a base64url-encoded 32-byte AES-256 key, padding optional.

Two framings exist:

| Scheme | Layout | Written |
|---|---|---|
| unversioned (legacy) | `nonce(12) ‖ ciphertext(n) ‖ tag(16)` | never; still read |
| `0x01` | `0x01 ‖ nonce(12) ‖ ciphertext(n) ‖ tag(16)` | by `Encrypt` |

Both are AES-256-GCM with a 12-byte random nonce, a 16-byte tag, and **no additional authenticated data**: the scheme byte is framing, not AAD, so a v1 blob's tag is exactly the tag its unversioned body would have carried.

**`Decrypt` accepts both, and the discriminator is the GCM tag rather than the leading byte.** A legacy blob's first byte is a random nonce byte, so it is `0x01` once in every 256 blobs and a leading-byte test alone would misread it. The rule: when the blob is at least 29 bytes and its first byte is `0x01`, attempt the v1 framing first; if that raises an authentication-tag mismatch, fall back to the unversioned framing. Forging a blob that authenticates under the wrong framing means forging a 128-bit GCM tag, so the two cases cannot collide in practice. Any other cryptographic failure (a wrong key, a truncated blob, tampering) still propagates as a `CryptographicException`, which callers such as `DbPsnTokenStore.LoadAsync` catch.

**`Encrypt` emits `0x01`.** `TokenCryptoTests` pins the written length and the leading byte, so a regression to the legacy framing fails the build. Unversioned blobs are never migrated: nothing rewrites one except a token refresh, a re-link, or a re-submitted provider key, so the legacy reading is permanent. Any change to the format has to ship in every runtime that reads these columns before any of them writes it, or every affected account reads as unlinked.

## Build notes

### Source-generated files and SA1516

`Microsoft.Azure.Functions.Worker.Sdk` runs source generators during compilation (`FunctionExecutorGenerator`, `FunctionMetadataProviderGenerator`, `ExtensionStartupRunnerGenerator`). The generated files place adjacent class declarations without a blank line between them, the shape StyleCop rule SA1516 rejects, and they mark themselves `// <auto-generated/>` (self-closing XML) rather than with the opening-tag form.

StyleCop.Analyzers 1.2.0-beta.556 skips them, so this project carries no `<NoWarn>SA1516</NoWarn>`. Re-adding one is a last resort: the diagnostic arrives with no source location (`Location.None`), so a `#pragma warning disable` or a per-file editorconfig pattern cannot reach it, and the project-level `<NoWarn>` would be the only mechanism left.
