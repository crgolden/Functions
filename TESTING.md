# Testing

The standard every test here is written to: collaborators are `Mock`s created with `MockBehavior.Strict`,
so a call nobody set up fails the test instead of quietly returning a default; arguments are verified
rather than waved through as `It.IsAny<T>()` whenever the argument is part of what's being asserted; and a
test body carries no control flow of its own. ADO.NET data access is the exception — mocking the whole abstract
`DbConnection`/`DbCommand`/`DbDataReader` surface is prohibitively verbose, so those tests drive the
hand-rolled fakes in `Functions.Tests.Unit/TestSupport/` instead.

## Test Categories

| Trait | Scope | Requires |
|---|---|---|
| `Category=Unit` | Fast, no external dependencies | Nothing |
| `Category=Integration` | Repository SQL against a real PostgreSQL database | `CuratorTestDatabaseConnection` |

No E2E category exists currently.

### The integration tier

`Functions.Tests.Unit` covers the repositories through `TestSupport/FakeDb.cs`, which pins the SQL
*text* a repository sends. That catches a rewritten query; it cannot catch SQL that is well-formed but
wrong against PostgreSQL — a bad `::jsonb` cast, a mis-typed column in a `jsonb_to_recordset` list, a
`text[]` that does not bind, a `timestamptz` that does not round-trip, or a CHECK constraint the code
violates. `Functions.Tests.Integration` exists for exactly that gap and runs the same repository code
against a real database.

**This project connects; it never provisions.** The Curator API owns the schema and applies its
migrations on its own deploy, so a schema change Functions needs is made in Curator's `db/migrations/`
and reaches the database when Curator next deploys. Nothing here creates a table, and nothing here runs
a migration. The fixture probes for `entitlement_snapshots` on startup and fails with that instruction
if the table is absent.

A missing `CuratorTestDatabaseConnection` **fails** the run rather than skipping it, so a
misconfigured environment cannot report green having executed nothing. The value accepts either the
`postgresql://` URI form or Npgsql keyword form — `PostgresConnectionString.Normalize` handles both.

Tests isolate by data, not by database: each one inserts its own `app_users` row with a fresh
`identity_sub` and deletes it afterwards, and every table keyed on `identity_sub` cascades from that row.
An outer transaction would not work, because the repositories open their own connection from the
`DbDataSource` and commit internally.

**A table with no `identity_sub` does not cascade, and its rows must be deleted by id.** `games` is the
case that matters — a test that seeds one has to track the ids and remove them in `DisposeAsync`, or it
leaves rows behind on a shared server. Verify cleanup by counting rows in every table a test touches
rather than assuming it; that check is what caught a tracking list which had been declared and never
appended to.

## Church pipeline and email

| Test file | What it covers |
|---|---|
| `EmailTests.cs` | `Email` function — null payload exits cleanly |
| `SitemapGeneratorTests.cs` | Constructor throws when `ChurchesBaseUrl` is absent; succeeds when configured |
| `ScraperWorkerTests.cs` | Malformed payload dead-letters; HTTP success/failure paths; `HttpRequestException`/`TaskCanceledException` (expected fetch failures) mark the source failed and complete rather than abandon; an unexpected exception type still marks failed, abandons, and rethrows; host-shutdown cancellation (pre-cancelled token) is excluded from the expected-failure path; blob upload and extraction-request dispatch. Note on the host-cancellation test: `FakeDbConnection.OpenAsync` uses the real `DbConnection` base implementation, which honors a pre-cancelled token and faults immediately, so that test can't exercise the catch-all's own DB update/abandon calls (a fake-infra limit, not a claim about production behavior) — it only proves the expected-failure "complete" path is skipped, which a strict mock with no Complete/Abandon setup enforces (either call would itself throw) |
| `ExtractorWorkerTests.cs` | `ExtractPhone` and `ExtractFromHtmlAsync` pure logic; malformed payload dead-letters; `Run` routes high-confidence+city to `geocoding-requests`, low-confidence or missing city to `enrichment-requests` |
| `EnrichmentWorkerTests.cs` | Constructor throws when `OpenAIModel` absent; malformed payload dead-letters without calling OpenAI; `ClientResultException` under the retry ceiling abandons quietly for broker redelivery, at/above the ceiling degrades to the extractor's partial data and completes; `BuildPageContent` (null blob falls back to a placeholder, short HTML passes through unchanged, oversized HTML truncated to the prompt cap); `TryParseEnrichment` truth table (all fields, fallback paths, bool variants) |
| `GeocoderWorkerTests.cs` | `ParseCensusResponse` (match/empty); `GeocodeAsync` — no city and no street, or coordinates already on the request, both skip HTTP entirely; HTTP success, non-success and throw; an out-of-range payload coordinate falls back to Census rather than being trusted. `GeocodeCampusesAsync` fills missing campus coordinates and applies the same range fallback. `Run` — a malformed payload dead-letters without touching the database; the full geocode-then-write path completes; a full state name is normalized before the write; an unresolvable canonical name, city, state or zip completes without geocoding or writing (the guard-and-drop policy); a missing zip that backfills writes the backfilled value; a blank primary language defaults to English, an out-of-range worship style clamps, and explicitly null collections normalize to empty rather than failing the write |
| `BulkImportJobTests.cs` | `ParseIrsCsv` (field mapping, NTEE codes, pre-geocoded coords, skip-on-missing-name/state, empty/header-only); `ParseOsm` (all address fields, `addr:state` normalization, skip-on-unrecognized-state, skip-on-missing-name/state/city/postcode/tags, no elements key, multi-value `name` tag prefers the Latin/ASCII segment regardless of which side it's on, ties keep the first segment, trailing empty segment ignored); `ParseCoordinates`, `NteeToWorshipStyle`, `NteeToDenomination`, `OsmDenominationToName` (truth tables); `Run` (missing blobPath, blob not found, IRS new records published, IRS/in-file duplicates skipped, OSM source) |
| `NormalizerTests.cs` | `NormalizePhone` (parens/dashes/spaces, international prefix, already-normalized, invalid/null/short); `NormalizeZip` (9-digit, non-digit chars, 4-digit, null); `NormalizeUrl` (https, http upgrade, missing scheme, trailing slash, whitespace, null, multi-value semicolon-joined keeps only the first URL) |
| `ContributionProcessorTests.cs` | Malformed payload dead-letters without DB access |
| `DeduplicationJobTests.cs` | `JaroWinkler`/`HaversineDistance`/`ToRad` (pure, published reference values); `BucketKey` grid-cell assignment; `Run` orchestration (distance guard, similarity guard, suggestion write, close pair straddling a bucket boundary still matches via the 3x3 neighbor-cell search, query excludes `(0,0)` fallback-coordinate churches and PO Box addresses — both are non-precise geocodes that produce false-positive/OOM-inducing proximity matches, a many-churches-in-one-bucket case matches correctly without excessive cost) |
| `ReGeocodeJobTests.cs` | `LoadZeroCoordChurchesAsync` query shape; `Run` (candidate geocode success/failure counts, coordinate update dispatch) |
| `QueueDepthMonitorJobTests.cs` | `Run` handles a `RequestFailedException` from the Service Bus admin client gracefully for every queue instead of throwing (would otherwise feed the exceptions alert every 15 minutes) |
| `ChurchWriterTests.cs` | `UpsertAsync` — insert vs. update vs. identical-record skip; the closed-connection path; validation that throws before any insert (null canonical name, blank city, a state that isn't two letters); per-column truncation for every text field, including the generated slug; slug collisions get a suffix; denomination name resolved to an id, unknown ones bound as `DBNull`, absent ones not queried at all; phone/zip/website normalization; child collections (attributes, service schedules, ministries, campuses) replaced then reinserted; a confidence recalculation published on write but not on a duplicate skip; a new church with a website registers a crawl source. `UpdateCoordinatesAsync` — publishes a recalculation when a row changed, nothing when none did |
| `ConfidenceScoreCalculatorTests.cs` | `Calculate` — the empty record scores zero, core fields plus coordinates score one, the attribute-count contribution caps, a recent verification adds its bonus, and each secondary signal adds its own increment |
| `ConfidenceWorkerTests.cs` | `RecalculateAsync` — a found church has its counts read and its score written; an unknown one updates nothing |
| `CrawlSchedulerWorkerTests.cs` | `DispatchDueAsync` — due sources are published and marked pending; nothing happens when none are due |
| `ConfigurationExtensionsTests.cs` | `GetRequired` — returns a present value, and throws naming the missing key when it isn't configured |
| `SlugHelperTests.cs` | `ToSlug` kebab-case conversion |

## PlayStation library jobs

| Test file | What it covers |
|---|---|
| `LeasedJobRunnerTests.cs` | `RunAsync` — a non-JSON body or a missing required field dead-letters without claiming the run; the claim itself is a compare-and-swap on `seq` plus the lease; a superseded `seq` settles without reprocessing, except on a stale redelivery of an already-failed run, which dead-letters so the failure still surfaces in the DLQ; a rate-limit pause completes without marking the run failed; any other exception marks failed and then dead-letters; a lock lost while settling is swallowed, because the status write already committed; the lease is renewed while the work runs, on its own connection rather than one shared with the terminal write |
| `JobRunsRepositoryTests.cs` | `ReapExpiredLeasesAsync` — targets only `running` rows whose lease has lapsed or is absent, spares one still inside the redelivery window, waits a full day before calling a run abandoned, marks what it reaps failed, clears the lease so the same run isn't reaped twice, and returns the ids it reaped. `GetAsync` — missing run, and `result_summary` readable only once the run succeeded. `MarkRateLimitedAsync` bumps `seq` and returns it so the continuation can be checkpointed against it |
| `JobTimeBudgetTests.cs` | `Expired` — false until the budget is fully spent, true once it is reached, and true immediately when there is no budget left to spend; the default leaves headroom under the host's function timeout |
| `ExpiredLeaseReaperTests.cs` | `Run` — opens no database connection at all unless the reaper is enabled; reaps abandoned runs and records the abandoned-run error when it is |
| `ScheduledRefreshWorkerTests.cs` | `Run` — malformed/missing-identity payload dead-letters without DB access; no schedule, a paused schedule, and a stale `scheduled_for` (mismatched against the stored `next_run_at`) all discard (complete, no dispatch); a due schedule with no active run dispatches (`job_runs` insert + `curator-library-refresh` send) and advances the schedule + publishes the next tick; an already-active previous run skips dispatch but still advances the schedule; a generically-failed previous run increments `consecutive_failures` and dispatches again; a previous run whose error names an expired PSN link pauses immediately with no dispatch and no next tick; `consecutive_failures` crossing the configured threshold pauses the same way; a succeeded previous run resets `consecutive_failures` to zero |
| `LibraryRefreshProcessorTests.cs` | `RunAsync` — a clean run returns a succeeded summary; a RAWG rate limit marks the run rate-limited, publishes a continuation, then throws; a rejected RAWG key is persisted as rejected |
| `LibraryRefreshContinuationProcessorTests.cs` | `RunAsync` — enriches the requested games in the requested order rather than database row order; unions its titles into the existing `result_summary`, preserving order and deduping; merges a second rate limit with the summary already stored and republishes using the merged values |
| `LibraryBuildOrchestratorTests.cs` | `CanonicalizeAsync` (ingest then apply catalog rules, dropping a title an exclusion rule catches), `PersistAndLinkAsync` (upsert each game and link it to the identity's library), `EnrichDeltaAsync` (rejects mismatched game/id lists, enriches only what the repository reports as unenriched), `MatchTrophiesAsync` (delegates, and skips the stage when no trophy client was supplied) |
| `IngestionServiceTests.cs` | `IngestAsync` — returns the new pull id and the snapshots it recorded, maps every entitlement field canonicalization needs, carries PSN's verbatim entry through to the persisted raw, records an empty pull when PSN reports nothing, passes the requested limit through, and surfaces a PSN auth failure rather than swallowing it |
| `EnrichmentRunProcessorTests.cs` | `RunAsync` — the reclassification passes are skipped when the rule fingerprints are unchanged and run when they changed or have never run; the catalog pass enriches only games with no enrichment row and stops, naming the provider, when a key is rejected; the OpenCritic sweep reports not-configured / rate-limited / auth-error outcomes instead of failing the whole run; the serialized summary carries the four pass keys and the counts the admin page reads, omitting inapplicable fields rather than emitting nulls |
| `EnrichmentBatchProcessorTests.cs` | `EnrichGamesAsync` — a 429 or a rejected key disables that one provider and the batch continues against the others, including when the failure happens inside the built-in OpenCritic top-up; the stop-on-first-provider-failure mode instead leaves every game unenriched for a later run; an empty batch saves nothing |
| `EnrichmentOrchestrationServiceTests.cs` | `EnrichGameAsync` — how one game's signals merge: PS Store data wins over RAWG for publisher, ESRB rating, multiplayer, genres and release year, but an empty PS Store value falls back to RAWG rather than staying blank; the score source records which providers actually scored; publisher-then-developer tier classification sets the AAA tier. Cache behavior: a PS Store row with no fetch timestamp is re-resolved, one with a timestamp is used without calling PSN, a RAWG search that matched nothing is cached as such, and a cache hit never spends an OpenCritic request on a top-up. Failure behavior: a rejected key or a 429 propagates as its own exception (with the retry-after clamped to a 24-hour cap), three consecutive transport failures disable RAWG and record it unavailable, and a top-up that hits a network or server error is marked incomplete without persisting partial games or advancing the shared pagination cursor. Also `RateLimitBackoff.Next` doubling and its cap |
| `EnrichmentCredentialsTests.cs` | `Without` — drops the named provider's credential and leaves the other two in place, and returns a new set rather than mutating the one it was given, so a provider disabled for one batch can't leak into the next |
| `TrophyMatchServiceTests.cs` | `MatchTrophiesAsync` — skips the whole stage unless the user opted into trophy harvesting; rejects mismatched game/id lists; attempts nothing when every game already carries a persisted match; resolves a PS4 title through the exact lookup (asked for in batches, not one call per game) and records the match as exact; skips that lookup for a PS5 title and falls back to fuzzy matching when it resolves nothing; stamps an attempt even when nothing matched so it isn't retried forever; counts every game attempted; refreshes stored progress for the whole matched library |
| `TrophyTitleMatcherTests.cs` | `MatchTitles` — names that agree match, nothing below the threshold does (and a raised threshold is honoured); trophy titles with no name or no progress are ignored, since they can report no completion; a trophy title is claimed by one game only and a game claims one title only, so a near-duplicate can't take an already-claimed title; empty game or trophy lists return nothing |
| `CanonicalizationServiceTests.cs` | `NormalizeName`, `EditionRank`, and `Canonicalize` — turning a user's raw entitlement rows into one catalog game per title |
| `MergeServiceTests.cs` | `MergeByProductIdAndName` — two concept groups merge only when product id *and* name agree (case-insensitively); a group with no product id, or the sole holder of one, passes through untouched; entry order within a merged group is preserved and merged groups come back before untouched ones |
| `ExclusionRulesTests.cs` | `ShouldExclude` — the predicate a catalog title is tested against |
| `FranchiseAssignerTests.cs` | `AssignFranchise` |
| `PublisherTierRuleSetTests.cs` | `Prepare` + `ClassifyTier` — no publisher name yields null rather than Indie, an AAA rule wins over an AA rule matching the same name, no rule at all defaults to Indie, an exact-kind rule requires the whole name rather than a substring, and a rule cased differently from the name it should match still matches, since the patterns are lowered once when the set is prepared rather than per game |
| `PublisherTierClassifierTests.cs` | `FingerprintPublisherTierRules` is stable regardless of input order and changes when a rule changes |
| `CurationRuleFingerprintTests.cs` | The digest format the stored rule fingerprints use — string escaping and item separators reproduced exactly, including the empty-rule-set case, so an unchanged rule set never triggers a reclassification pass |
| `GenreServiceTests.cs` | `PickGenreSubgenre` — no tags yields null, the most specific tag ranks first, unlisted tags keep their original order below every listed one, a single tag leaves the subgenre null |
| `GenreReconciliationServiceTests.cs` | `ReconcileGenres` — PSN tags win when present, RAWG is the fallback, neither yields null |
| `ReleaseYearTests.cs` | `FromDate` and `FromText` — a PSN full timestamp, a bare RAWG date, and the null/unparseable cases |
| `TitlePlatformTests.cs` | `PlatformForTitleId`, `IsNonTitleEntitlement`, `NormalizePlatformId` |
| `PsnSessionTests.cs` | `VerifiedUrl` host checking, `CreateDefaultHandler`, `GetAsync`, `RunWithReauthAsync` re-authentication, the `RestoreAsync` entry point, and the npsso `BootstrapAsync` exchange it falls back to |
| `PsnLibraryClientTests.cs` | `EntitlementsAsync` — an empty library and a missing entitlements key both return nothing; paging stops on a short page, on the accumulated offset reaching `totalResults`, after one page when PSN omits `totalResults`, and at a caller-supplied limit; the request asks for every entitlement type and metadata block PSN exposes; mapping keeps all three artwork URLs (falling back to the game icon when the title has none), reads `packageType` from the right block, collects platform ids while skipping attributes without one, keeps PSN's verbatim entry as raw so a mapping bug can't lose a field, and covers every column ingestion persists; a rejected token throws `PsnAuthException` unless an npsso is available, in which case it re-authenticates and retries once |
| `PsnCatalogClientTests.cs` | `TitleConceptAsync` — the age/country/language query parameters PSN requires; concept parsing, including an empty result; cover art picked by role preference rather than array order, falling back to the first image with a URL; the multiplayer flag left null with no player-count notice, false for a single-player-only notice, true for an online notice above one; a rejected cached token re-authenticates and retries once when an npsso is available |
| `PsnTrophyClientTests.cs` | `TrophyTitlesAsync` and `TrophyTitlesByTitleIdAsync` |
| `PsnEntitlementPayloadTests.cs` | The entitlements wire model — a collection PSN omits or sends as null reads as empty rather than null, so no caller has to guard it; `totalResults` absent reads as zero; `activeDate` keeps whatever offset PSN sent, including a non-UTC one |
| `PsnConceptPayloadTests.cs` | The concept wire model — `id` parses whether PSN sends a number or the same value as text, as the entitlements endpoint does; `starRating.score` parses from its decimal string; a coming-soon release date carries its type with no date; a compatibility notice keeps its JSON kind per notice type, since the value is a number, a bool, or a string depending on which notice it is; omitted collections read as empty |
| `RedisPsnRateLimiterTests.cs` | `AcquireAsync` — one key shared by every account, so the budget matches PlayStation's per-client quota; the window is trimmed, the call recorded, and the TTL refreshed on each acquire; the oldest call is not inspected while the window has room; a spent budget waits exactly until the oldest call leaves the window, and does not wait when it already has or when the window holds no entries |
| `TokenCryptoTests.cs` | `Decrypt` reads a token encrypted by Curator's own Python `TokenCrypto` (a pinned known-answer vector), and throws `CryptographicException` for a wrong key, tampered ciphertext, or a token shorter than a nonce plus tag; `EncryptThenDecrypt` round-trips; the constructor rejects a key that doesn't decode to 32 bytes |
| `DbPsnTokenStoreTests.cs` | `LoadAsync` — null for no link, ciphertext encrypted under a different key, non-JSON plaintext, or a JSON root that isn't an object; an already-expired access token returned alongside the durable refresh token; `refresh_token_expires_at` parsed when present. `SaveAsync` — no-ops without an access token, encrypts and persists the durable fields, omits the expiry when absent. `ClearAsync` throws, because nothing in a job should ever unlink an account |
| `InMemoryPsnTokenStoreTests.cs` | Round-trip, `ClearAsync`, null before any save, and that two instances don't share state so the default can't silently become persistent |
| `PsnAccessTokenCacheTests.cs` | `CacheKey` scopes an entry to one identity; `LoadAsync` returns null for a miss or a payload that isn't valid JSON; `SaveAsync` writes nothing without an access token or once one has already expired, and otherwise stores only the ephemeral fields — never the durable refresh token — under a TTL that expires with the access token |
| `RawgClientTests.cs` | `SearchGamesAsync` (query parameters, candidate parsing with platform ids, `Retry-After` parsed on a 429 and left null when absent so the caller applies its own backoff); `FetchDetailAsync` (parsed detail, 404 returns null without raising); `ValidateKeyAsync` spends its one request on the genres endpoint, never search; a transport failure propagates unwrapped; a rejected key raises an error whose message leaks neither the body nor the key, and `ProviderDetail` is truncated so a long provider body can't flood the run summary |
| `RawgMatcherTests.cs` | `Normalize`, `Similarity`, and `FindBestMatch` candidate selection |
| `OpenCriticClientTests.cs` | `FetchPlatformGamesAsync` — paginates until a short or empty page, resumes from the stored cursor and resets it at the end, honours the page cap and stops as the daily request budget nears exhaustion, keeps earlier pages and reports the failed offset when a page fails or the transport does, treats a negative top-critic score as unscored, skips entries missing an id or name, carries the provider payload for persistence, and parses `Retry-After` so the run can be rescheduled. `ValidateKeyAsync` spends its one request on the catalog endpoint, never search. `ProviderDetail` carries enough body to tell an unsubscribed plan from a bad key, redacts the API key when the body echoes it back, and is truncated |
| `OpenCriticGameEntryTests.cs` | `ToGame` — OpenCritic's `-1` sentinel for an unscored game becomes null on both `topCriticScore` and `percentRecommended`, while a genuine zero is kept, because zero is a score and `-1` is absence; an entry with no id or no name yields nothing; deserialization keeps unmapped fields so the stored raw payload loses nothing |
| `OpenCriticNameIndexTests.cs` | `Normalize` (roman numerals, typographic and ASCII apostrophes alike, registered/copyright signs and their parenthesized forms, accent and compatibility folding, separator replacement and whitespace collapsing); `StripSubtitle` cuts at the first spaced colon or dash only; `Build` also indexes the year-suffix-stripped name; `FindMatch` walks its match strategies in order and, among equally good candidates, prefers the highest-scored one and otherwise the first indexed |
| `OpenCriticAdminRefreshServiceTests.cs` | `RefreshCacheAsync` — rotates to the next key on a rotating status code but not on other API or network failures; a rotated key resumes from the cursor the previous key advanced to, not the original start; partial games and the cursor are persisted even when a page fails; every key rejected or rate-limited throws with the provider detail and retry-after hint; the configured page cap is honoured; every platform is swept and its counts summed, stopping at the first platform that exhausts every key; a platform whose cursor lock another run holds is skipped without spending an API call, and each lock is released before moving on |
| `OpenCriticCacheSweepTests.cs` | `Run` — spends no quota and opens no connection when the key or endpoint is unconfigured or every indexed key is blank; rotates across the configured indexed keys when an earlier one is rejected, and does not retry a rejected key on the next platform, so one run wastes one request rather than one per platform; `MaxPagesPerRun` defaults to the admin refresh cap |
| `CatalogRepositoryTests.cs` | `ReclassifyFranchiseAsync`, the franchise-rule fingerprint accessors, and `ListAllGameIdsAndTitlesAsync` |
| `CatalogRepositoryCanonicalizationTests.cs` | The curation-rule reads (`ListExclusionRulesAsync`, `GetEditionRanksAsync`, `GetNameOverridesAsync`, `GetGloballyExcludedConceptIdsAsync`) and `UpsertGameAsync` — resolves an existing game by concept id before falling back to the normalized title, inserts when neither resolves, stores an absent franchise as null rather than an empty string, repoints every concept at the resolved game, and takes a title-scoped advisory lock in the same transaction before reading or writing |
| `LibraryRepositoryTests.cs` | `UpsertEntryAsync`, `GetUnmatchedGameIdsAsync`, `GetGamesForContinuationAsync`, `SetTrophyMatchAsync`, `RefreshTrophyProgressAsync` |
| `EnrichmentRepositoryTests.cs` | The enrichment read/write surface — RAWG and PS Store caches, the OpenCritic game cache, `GetUnenrichedGameIdsAsync`, `GetActiveGenresAsync`, `SaveGameEnrichmentAsync`, the publisher-tier rules and their fingerprint, and `ReclassifyTierAsync` |
| `EnrichmentKeysRepositoryTests.cs` | `GetDecryptedKeyMaterialAsync` and the per-provider key-rejection writes |
| `EntitlementPullRepositoryTests.cs` | `RecordPullAsync` — the pull row is stamped with its source and entry count and written even when the user owns nothing, and Postgres returning no pull id throws; snapshots upsert on `(identity_sub, entitlement_id)` rather than inserting a row per pull, keeping stored artwork when a later pull omits it and the stored raw payload when the incoming one is an empty object, setting `first_seen_at` on insert only and `last_seen_at` on both paths; every extracted column is sent alongside the raw payload, columns PSN left out are sent as null rather than an empty string, parameters Postgres can't infer from their text form are cast, and the pull row plus every snapshot commit in one transaction |
| `PsnLinkRepositoryTests.cs` | `GetLinkAsync` and `UpdateTokenAsync` |
| `OpenCriticCacheRepositoryTests.cs` | `GetCursorAsync`/`SetCursorAsync` and `SaveGamesAsync` |
| `PostgresConnectionStringTests.cs` | `Normalize` — a `postgresql://` URI becomes something Npgsql can parse, a missing port defaults, a percent-encoded password is decoded, an already-keyword-form string is returned unchanged, and a blank value or a URI naming no database throws |

---

## Running Tests Locally

No `ASPNETCORE_ENVIRONMENT` override needed — Functions reads local config from `local.settings.json`, not from `Program.cs` startup branches.

```powershell
dotnet build Functions.Tests.Unit --configuration Debug
.\Functions.Tests.Unit\bin\Debug\net10.0\Functions.Tests.Unit.exe -trait "Category=Unit" -showLiveOutput
```

The integration tier additionally needs a database to point at. Any PostgreSQL instance carrying
Curator's schema will do; build one locally by running **Curator's own** migration runner against an
empty database, which is the tool that owns that job.

Two details are load-bearing, and getting either wrong produces a confusing failure rather than a clear
one. `curator_app` has `rolcreatedb = f`, so **`postgres` must create the database** — but it must create
it `OWNER curator_app`, because everything afterwards connects as `curator_app`, exactly as CI's
`CURATOR_TEST_DATABASE_URL` does. A database created by `postgres` without that owner leaves `curator_app`
unable to touch the `public` schema, and the migration runner fails with `permission denied for schema
public` on its very first statement.

```powershell
psql -U postgres -h localhost -c 'CREATE DATABASE curator_test OWNER curator_app'
$testDb = "postgresql://curator_app:$env:CURATOR_PG_PASSWORD@localhost:5432/curator_test"
python ..\Curator\db\run_migrations.py $testDb
$env:CuratorTestDatabaseConnection = $testDb
dotnet build Functions.Tests.Integration --configuration Debug
.\Functions.Tests.Integration\bin\Debug\net10.0\Functions.Tests.Integration.exe -trait "Category=Integration" -showLiveOutput
```

The runner is idempotent, so re-running it against an existing `curator_test` only applies what is new.
If the database was created wrongly at some point, drop and recreate it rather than granting piecemeal —
it holds nothing durable, since every test deletes the rows it created.

**Functions never creates or migrates a table**, here or in CI. Curator owns the schema; if Functions
needs a schema change, it goes into `Curator/db/migrations/` and Curator applies it on its next deploy.

### Who migrates the CI test database

**Curator's** workflow, not this one. `main_crgolden-curator.yml` runs `db/run_migrations.py` twice on
every deploy — against `CURATOR_TEST_DATABASE_URL` first, then `CURATOR_DATABASE_URL` — the same
test-database-before-production ordering Identity uses when it publishes its DACPAC to `DB_NAME_E2E`
before `DB_NAME`. Migrating the test database first means a migration that is going to fail does so
before production has been touched.

This is what keeps the schema the integration tier runs against in step with the schema Curator ships.
Functions' own workflow does not migrate anything; it connects to a database Curator has already
migrated, and `CuratorDatabase.InitializeAsync` fails fast with a pointed message if that schema is
missing rather than creating tables of its own.

### Cleanup: two layers, because the first one has a hole

Each test class deletes what it created in its own `DisposeAsync`, tracking ids as it goes. That covers
the normal path, including a failing assertion — xUnit still runs `DisposeAsync`.

It does **not** cover a row that was inserted before the test got as far as tracking its id, or a run the
process never finishes. Those rows survive into the next run, and the failure they cause lands somewhere
unrelated. So `CuratorDatabase.DisposeAsync` runs a final sweep once the whole collection is done —
the same shape as `Identity.Tests.E2E`'s `PlaywrightFixture.CleanupDatabaseAsync`.

Three things about that sweep are deliberate:

- **It is guarded on the database name containing `test`.** The fixture will happily connect to whatever
  `CuratorTestDatabaseConnection` names, and a mistyped connection string pointed at `curator` would
  otherwise be catastrophic. If the guard fails the sweep is skipped silently rather than throwing,
  because throwing from `DisposeAsync` masks the real test failure.
- **`app_users` is deleted first**, so migration `0009_fix_delete_cascades.sql`'s cascades take every
  user-scoped row (`library_entries`, `entitlement_snapshots`, `job_runs`, `psn_links`, and the rest)
  with it. What the sweep lists explicitly afterwards is the game graph and the per-key caches, which no
  cascade reaches.
- **`publisher_tiers` is swept by pattern prefix, not truncated**, because migration
  `0007_seed_curation_rules.sql` seeds it — as it also seeds `genres`, `franchise_rules`,
  `size_estimates` and `platforms`. Every tier a test inserts is named with the
  `CuratorDatabase.TestPublisherTierPattern` prefix, and the sweep deletes exactly the rows matching
  it, so the seeded rows are never in range. Deleting seed data would leave the database subtly wrong
  in a way migrations will not repair, since the runner records `0007` as already applied and never
  re-runs it.

---

## CI Pipeline

The GitHub Actions workflow (`.github/workflows/main_crgolden-functions.yml`) runs on push to `main`, on pull requests, and on `workflow_dispatch`:

1. Begin Sonar analysis
2. Build (`dotnet build --no-incremental --configuration Release /p:RestoreLockedMode=true`)
3. Run unit tests with coverage (`dotnet dotnet-coverage collect "dotnet test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit ..."`)
4. Run integration tests with coverage, against the database named by the `CURATOR_TEST_DATABASE_URL` secret
5. `dotnet publish` + upload the deploy artifact
6. End Sonar analysis
7. Deploy to Azure Function App `crgolden-functions` via `Azure/functions-action` (`main` only)

Both coverage reports are handed to Sonar — `sonar.cs.vscoveragexml.reportsPaths` lists `coverage.xml`
and `coverage-integration.xml`, so repository code exercised only by the integration tier still counts.

Step 4 follows the same pattern as Identity's E2E step: it targets an existing database on the shared
server rather than standing one up, which is what lets it run on the `windows-latest` runner this
workflow already uses (GitHub service containers require a Linux runner). It is skipped for
`dependabot[bot]`, which is not given repository secrets. The workflow deploys **no** schema — see
"The integration tier" above.

### Why the workflow is serialized

The workflow declares a top-level `concurrency` group of `${{ github.workflow }}` with
`cancel-in-progress: false`, so only one run executes at a time across the whole repository.

That is a correctness requirement, not tidiness. `CURATOR_TEST_DATABASE_URL` names **one** shared
database, and the fixture sweep described under "Cleanup" truncates the game graph and per-key caches
once the collection finishes. Two runs overlapping — the usual case is a pull-request synchronize
landing while a push to `main` is still building — means one run's teardown deletes rows the other run
is mid-assertion on. The result is a red build on a test that has nothing wrong with it, and it will
not reproduce.

The group is deliberately **not** ref-scoped. The conventional
`${{ github.workflow }}-${{ github.ref }}` would place a PR run and a `main` run in different groups
and let exactly the collision above through; it is the wrong shape when what is being protected is one
shared resource rather than one branch's build. The cost is that builds queue rather than run in
parallel, and that a pending run can be superseded while it waits.

The alternative — giving CI its own database per run — is what removes the constraint rather than
working around it, and would want a Linux runner and service containers.

Step 3 runs the full `Category=Unit` suite on every push and PR. A bare `dotnet test` won't reproduce it locally — VSTest-compatible mode is gone under this repo's xUnit v3 Microsoft.Testing.Platform tooling (see "Running Tests Locally" above); CI's invocation works only because `dotnet-coverage collect` drives it.

---

## Local SonarCloud Analysis

Generate coverage first, then run from `Functions/`. **The only tool `dotnet-tools.json` pins is
`dotnet-coverage`** — `dotnet coverlet` fails with "dotnet-coverlet does not exist". Use the same
invocation CI uses, which also produces the format the scanner property below expects; the reason
`dotnet-coverage` is used rather than Coverlet is recorded inline in the workflow.

Both tiers are collected, because the repositories are exercised mostly by the integration tier and a
unit-only report reads as a large coverage regression.

**Use `dotnet-sonarscanner`, never the `sonar-scanner` CLI.** The standalone CLI has no C# analyzer: it
indexes the files, reports **no issues and no `ncloc`**, and still exits successfully — so a scan that
looks like it worked silently replaces the project's real analysis with an empty one. C# is analysed by
the MSBuild integration, which needs `begin` **before** the build and `end` after, with the build in
between so the analyzer sees the compilation. This is why CI wraps its build in the two steps.

`end` runs the analysis engine on Java. CI's runner has a JDK; this fleet's workstations do not have
`java` on PATH, and `sonar.scanner.skipJreProvisioning=true` tells the scanner not to download one — so
`end` fails with *"Could not find Java … 'JAVA_HOME' environment variable not set"*. Point `JAVA_HOME` at
the JRE the standalone CLI already ships (process scope only — do not set it machine-wide).

```powershell
dotnet tool restore
$env:JAVA_HOME = "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\jre"

dotnet-sonarscanner begin `
  /k:"crgolden_Functions" `
  /o:"crgolden" `
  /d:sonar.token="$env:SONAR_TOKEN" `
  /d:sonar.host.url="https://sonarcloud.io" `
  /d:sonar.cs.vscoveragexml.reportsPaths="coverage.xml,coverage-integration.xml" `
  /d:sonar.exclusions="**/bin/**,**/obj/**" `
  /d:sonar.coverage.exclusions="**/Program.cs" `
  /d:sonar.scanner.skipJreProvisioning=true

dotnet build Functions.slnx --no-incremental --configuration Release

dotnet dotnet-coverage collect `
  "dotnet test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit" `
  -f xml -o "coverage.xml" -s "coverage.settings.xml"

$env:CuratorTestDatabaseConnection = "postgresql://curator_app:$env:CURATOR_PG_PASSWORD@localhost:5432/curator_test"
dotnet dotnet-coverage collect `
  "dotnet test --project Functions.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration" `
  -f xml -o "coverage-integration.xml" -s "coverage.settings.xml"

dotnet-sonarscanner end /d:sonar.token="$env:SONAR_TOKEN"
```

Curator is the opposite case — Python is analysed by the standalone CLI, so `sonar-scanner` is correct
there. See `Curator/TESTING.md`.

Required coverage files: `coverage.xml` (unit) and `coverage-integration.xml` (integration), both in
`dotnet-coverage`'s xml format — hence `sonar.cs.vscoveragexml.reportsPaths`, not the OpenCover property.

`SONAR_TOKEN` is already in the environment and `sonar-scanner` is already on PATH, so neither needs
setting or a full path. `sonar.host.url` and `sonar.scanner.skipJreProvisioning` live in the scanner's
global `conf/sonar-scanner.properties`.

**A local scan republishes the project's `main` analysis.** With no `-Dsonar.branch.name` the run
replaces whatever CI last published until the next push re-analyses `main`, and the scanner warns that
uncommitted files carry no SCM blame — which degrades new-code-period detection, so new-code metrics from
a local scan of a dirty tree are not trustworthy. Scan locally to preview; treat CI's post-push analysis
as the authority, and say which one a reported number came from.

### When to build a truth table

The coverage **score is read from SonarCloud, never hand-maintained** here. Build a per-method table in [COVERAGE-TRUTH-TABLES.md](COVERAGE-TRUTH-TABLES.md) only when SonarCloud flags a method with **cognitive complexity > 15 AND uncovered conditions > 0**: the table is escalation for the gnarly few, not a per-class deliverable.
