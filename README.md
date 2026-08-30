# Functions

[![Build and deploy dotnet core project to Azure Function App - crgolden-functions](https://github.com/crgolden/Functions/actions/workflows/main_crgolden-functions.yml/badge.svg)](https://github.com/crgolden/Functions/actions/workflows/main_crgolden-functions.yml)

[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=crgolden_Functions&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=crgolden_Functions)

Azure Functions isolated worker (v4, .NET 10). It hosts three unrelated workloads in one app: the church-platform data pipeline, transactional email delivery, and the PlayStation library curation jobs behind the Curator API. Two databases are written from here — the Directory SQL Server database, and Curator's PostgreSQL database through a connection registered under the `Curator` service key, so a class has to name it to get it.

The church pipeline's end-to-end architecture — queue cascade, `ChurchWriter` single-writer invariant, corrections lifecycle, failure handling, and Azure hosting/RBAC — is documented in [Churches/ARCHITECTURE.md](https://github.com/crgolden/Churches/blob/main/ARCHITECTURE.md).

## What runs here

**Queue-triggered workers** (Azure Service Bus): the church data cascade `ScraperWorker` → `ExtractorWorker` → `EnrichmentWorker` (Azure OpenAI, only for low-confidence extractions) → `GeocoderWorker` → `ChurchWriter` (single transactional write path to the Directory SQL database) → `CalculateConfidenceScore`; plus `ContributionProcessor` (user corrections → pending moderation rows) and `Email` (Resend delivery for Identity and Infrastructure).

**Timers**: `CrawlSchedulerWorker` (enqueue recrawls every 6h), `DeduplicationJob` (nightly geo+name-similarity merge suggestions for moderator review), `SitemapGenerator` (nightly sitemap to `$web` blob static hosting), `QueueDepthMonitorJob` (queue/DLQ depth gauges every 15 min).

**HTTP admin jobs**: `BulkImportJob` (seed from IRS 990 CSV / OSM JSON blobs) and `ReGeocodeJob` (sweep rows stranded at `(0,0)`).

**PlayStation library jobs** (Azure Service Bus, against Curator's PostgreSQL database): `LibraryRefreshWorker` (`curator-library-refresh`) pulls one user's PSN entitlements, canonicalizes them into the shared game catalog, enriches the titles it hasn't seen before from RAWG/OpenCritic/the PS Store, and matches their trophy progress. `LibraryRefreshContinuationWorker` (`curator-library-refresh-continuation`) resumes a refresh that a provider rate limit paused, starting from the game ids that pause recorded. `EnrichmentRunWorker` (`curator-enrichment`) runs the same enrichment catalog-wide from admin-held provider keys rather than one user's. `ScheduledRefreshWorker` (`curator-scheduled-refresh`) turns a user's standing weekly/monthly schedule into a queued refresh, advances the schedule to its next tick, and pauses it when the user's PSN link has expired or their runs keep failing.

Each of these workers takes a lease on the `job_runs` row its message names, heartbeats that lease while it works, and writes the run's terminal status and `result_summary` back to the same row — which is how the Curator API reports progress to a user who is polling. Curator publishes the messages and reads `job_runs`; none of the work above happens on its request path.

Two timers keep that machinery honest: `ExpiredLeaseReaper` (every 15 min, fails runs whose lease lapsed with nothing renewing it, so a run killed mid-flight can't sit at `running` forever) and `OpenCriticCacheSweep` (nightly, extends the shared OpenCritic score cache from where the last sweep or enrichment run left the pagination cursor).

```powershell
func start   # requires local.settings.json (not User Secrets)
```

Tests: `Functions.Tests.Unit/` — unit only; see [TESTING.md](TESTING.md).

## Telemetry

Traces and metrics leave the app over OTLP in production only — `Program.cs` registers the OpenTelemetry pipeline inside its `IsProduction()` branch and nowhere else.

### Why the stable database semantic conventions are selected in code rather than by an app setting

`Program.cs` calls `Telemetry.SemanticConventions.OptInToStableDatabaseConventionsUnlessAlreadyChosen()` as its **first statement, above `FunctionsApplication.CreateBuilder(args)`** — not next to the OpenTelemetry registration, where it would read more naturally. `CreateBuilder` seeds a `ConfigurationManager`, which builds its providers eagerly, so the environment-variable provider has already taken its snapshot by the time that call returns. Setting the variable afterwards would still be seen by anything that reads the environment directly, but would be invisible to anything resolving the value through this app's `IConfiguration`. Above `CreateBuilder` is the one position that is correct for both, so do not move it down. Without the call at all the Redis instrumentation keeps emitting the pre-1.0 `db.system`/`db.statement` attributes, which nothing queries any more: the Npgsql and SQL Server legs of this app already emit the stable `db.system.name`/`db.namespace`/`db.query.text` set, so Redis was the last producer of the old shape here.

**The value has to reach the instrumentation as a process environment variable, and that is the whole reason it is written in code rather than declared in configuration.** `StackExchangeRedisInstrumentationOptions` exposes a public parameterless constructor that builds its own `new ConfigurationBuilder().AddEnvironmentVariables().Build()`, and the instrumentation registers no delegating options factory — so the default options object never sees this app's `IConfiguration`. A key added to a JSON configuration file would therefore be read by nothing, the legacy attributes would keep flowing, and no build or startup check would notice. Confirm that against the package before "simplifying" this into a settings file.

The helper writes the variable **only when it is unset**, so an Application Setting on the Function App still wins. That is what keeps `database/dup` — emit both conventions during a migration window — available without a redeploy.

## Data at rest — the `TokenCrypto` blob format

`TokenCrypto` reads and writes the encrypted columns Curator's PostgreSQL database shares between this app and Curator's own Python runtime (`psn_links.token_response_enc`, the `user_enrichment_keys` key columns). **Both runtimes must agree on this format byte for byte**, and both derive their key from the same `CuratorTokenKey` / `CURATOR_TOKEN_KEY` value — a base64url-encoded 32-byte AES-256 key, padding optional.

Two framings exist. Both runtimes now write the versioned one, and every blob written before 2026-08-28
is the unversioned one:

| Scheme | Layout | Written by |
|---|---|---|
| unversioned (legacy) | `nonce(12) ‖ ciphertext(n) ‖ tag(16)` | nobody, since 2026-08-28 |
| `0x01` | `0x01 ‖ nonce(12) ‖ ciphertext(n) ‖ tag(16)` | both runtimes |

Both are AES-256-GCM with a 12-byte random nonce, a 16-byte tag, and **no additional authenticated data** — the scheme byte is framing, not AAD, so a v1 blob's tag is exactly the tag its unversioned body would have carried. That is what lets the byte be prepended to an existing encoder's output.

**`Decrypt` accepts both, and the discriminator is the GCM tag rather than the leading byte.** A legacy blob's first byte is a random nonce byte, so it is `0x01` once in every 256 blobs and a leading-byte test alone would misread it. The rule is: when the blob is at least 29 bytes and its first byte is `0x01`, attempt the v1 framing first; if that raises an authentication-tag mismatch, fall back to the unversioned framing. Forging a blob that authenticates under the wrong framing means forging a 128-bit GCM tag, so the two cases cannot collide in practice. Any other cryptographic failure — a wrong key, a truncated blob, tampering — still propagates as a `CryptographicException`, which is what callers such as `DbPsnTokenStore.LoadAsync` catch.

**`Encrypt` emits `0x01`, in both runtimes, as of 2026-08-28.** The dual-read shipped first and is deployed, which is what makes this safe: a runtime that *emits* `0x01` before the other runtime can *read* it makes every affected account look unlinked rather than raising an error, which is the exact failure the scheme byte exists to prevent. `TokenCryptoTests` pins the written length and the leading byte, so a silent regression to the legacy framing fails the build.

**Two consequences.** Curator and Functions must **deploy together** for this change, and neither may be rolled back alone — an old reader in front of these columns reads a versioned blob as an unlinked account. And unversioned blobs are never migrated: nothing rewrites one except a token refresh, a re-link, or a re-submitted BYOK key, so the legacy reading is permanent rather than transitional. `db/reencrypt_tokens.py::classify` is a third reader of these columns and gates every deploy; it classifies a versioned blob as already-migrated, which is pinned by `test_scheme_byte_led_blob_is_left_alone_rather_than_failing_the_deploy`.

## Build notes

### `<NoWarn>SA1516</NoWarn>` in Functions.csproj

`Microsoft.Azure.Functions.Worker.Sdk` runs source generators during compilation (`FunctionExecutorGenerator`, `FunctionMetadataProviderGenerator`, `ExtensionStartupRunnerGenerator`). The generated files place adjacent class declarations without a blank line between them, which violates StyleCop rule SA1516 (Elements should be separated by blank line).

StyleCop.Analyzers 1.1.118 does not skip these files because the generators emit `// <auto-generated/>` (self-closing XML), which StyleCop does not recognize as a generated-code marker (it checks for the opening-tag form `// <auto-generated>`). The diagnostic is reported with no source location (`Location.None`), so it cannot be suppressed via `#pragma warning disable` or per-file editorconfig patterns. The project-level `<NoWarn>SA1516</NoWarn>` is the only available suppression mechanism.
