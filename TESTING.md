# Testing

The standard every test here is written to: collaborators are `Mock`s created with `MockBehavior.Strict`,
so a call nobody set up fails the test instead of quietly returning a default; arguments are verified
rather than waved through as `It.IsAny<T>()` whenever the argument is part of what's being asserted; and a
test body carries no control flow of its own. ADO.NET data access is the exception — mocking the whole abstract
`DbConnection`/`DbCommand`/`DbDataReader` surface is prohibitively verbose, so those tests drive the
hand-rolled fakes in `Functions.Tests.Unit/TestSupport/` instead.

Azure Blob access is mocked as a Strict chain, `Mock<BlobServiceClient>.GetBlobContainerClient` →
`Mock<BlobContainerClient>.GetBlobClient` → `Mock<BlobClient>`, with `DownloadContentAsync` returning
`Response.FromValue(BlobsModelFactory.BlobDownloadResult(content: ...), response)` (`BulkImportJobTests`,
`ExtractorWorkerTests` are the pattern). HTTP goes through `TestSupport/StubHttpMessageHandler`, never a Strict
`Mock<HttpMessageHandler>`, which throws when the `HttpClient` disposes it.

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
| `ScraperWorkerTests.cs` | Malformed payload dead-letters; HTTP success/failure paths; `HttpRequestException`/`TaskCanceledException` (expected fetch failures) mark the source failed and complete rather than abandon; an unexpected exception type still marks failed, abandons, and rethrows; host-shutdown cancellation (pre-cancelled token) is excluded from the expected-failure path; blob upload (the response bytes stored unchanged, even under a non-IANA charset) and extraction-request dispatch. Note on the host-cancellation test: `FakeDbConnection.OpenAsync` uses the real `DbConnection` base implementation, which honors a pre-cancelled token and faults immediately, so that test can't exercise the catch-all's own DB update/abandon calls (a fake-infra limit, not a claim about production behavior) — it only proves the expected-failure "complete" path is skipped, which a strict mock with no Complete/Abandon setup enforces (either call would itself throw) |
| `ExtractorWorkerTests.cs` | `ExtractPhone` and `ExtractFromHtmlAsync` pure logic; malformed payload dead-letters; `Run` routes high-confidence+city to `geocoding-requests`, low-confidence or missing city to `enrichment-requests` with the page text in `EnrichmentRequest.PageText` and the extracted street in the partial data; a blob whose download answers 404 completes without extracting |
| `EnrichmentWorkerTests.cs` | Constructor throws when `OpenAIModel` absent; malformed payload dead-letters without calling OpenAI; a full rate gate schedules the same body past the gate's wait and completes without calling OpenAI, and at `MaxGateDeferrals` degrades to the partial data instead; an HTTP 429 schedules a retry past `Retry-After` and completes even at the delivery ceiling, and at `MaxThrottledAttempts` degrades to the partial data instead; every call carries the output-token cap and minimal reasoning effort; any other `ClientResultException` under the retry ceiling abandons for broker redelivery, at/above it degrades to the extractor's partial data and completes; `RetryAfterSeconds` prefers Azure's `retry-after-ms` over `Retry-After`, reads `Retry-After` alone, and falls back without a response; `DeferralDelaySeconds` never schedules before the earliest moment and stops doubling at the cap; `BuildPageContentAsync` (scripts, styles and markup are dropped and whitespace collapsed, oversized text truncated to the prompt cap); `BuildPrompt` carries the page text the extractor forwarded, and a placeholder when a message carries none; `TryParseEnrichment` truth table (all fields, fallback paths, bool variants), including the model's street and the extractor's street kept when the model gives none or the response is unparseable; an unreachable rate gate abandons for immediate redelivery without calling OpenAI or scheduling a deferral, counts the abandon against the inner transport fault rather than the wrapper, and still abandons when the host has already cancelled the invocation token (the settlement must not inherit that cancellation, because a settlement that throws is one that did not happen; whether the SDK's own automatic abandon would cover it during teardown is unverified); a settlement that fails with `MessageLockLost` is swallowed so teardown does not resurface as an unhandled invocation, while any other `ServiceBusFailureReason` still escapes — the pair is what pins the split, since swallowing everything would hide a real broker fault; past `MaxGateUnavailableDeliveries` the same fault degrades to the partial data and completes instead of abandoning again, so the path cannot ride the delivery budget to the dead-letter queue |
| `GeocoderWorkerTests.cs` | `ParseCensusResponse` (match/empty); `GeocodeAsync` — no street (with or without a city), or coordinates already on the request, skip HTTP entirely; HTTP success, non-success and throw; an out-of-range payload coordinate falls back to Census rather than being trusted. `GeocodeCampusesAsync` fills missing campus coordinates and applies the same range fallback. `Run` — a malformed payload dead-letters without touching the database; the full geocode-then-write path completes; a full state name is normalized before the write; an unresolvable canonical name, city, state or zip completes without geocoding or writing (the guard-and-drop policy); a missing zip that backfills writes the backfilled value; a blank primary language defaults to English, an out-of-range worship style clamps, and explicitly null collections normalize to empty rather than failing the write |
| `BulkImportJobTests.cs` | `ParseIrsCsv` (field mapping, NTEE codes, pre-geocoded coords, skip-on-missing-name/state, empty/header-only); `ParseOsm` (all address fields, `addr:state` normalization, skip-on-unrecognized-state, skip-on-missing-name/state/city/postcode/tags, no elements key, multi-value `name` tag prefers the Latin/ASCII segment regardless of which side it's on, ties keep the first segment, trailing empty segment ignored); `ParseCoordinates`, `NteeToWorshipStyle`, `NteeToDenomination`, `OsmDenominationToName` (truth tables); `Run` (missing blobPath, blob not found, IRS new records published, IRS/in-file duplicates skipped, OSM source) |
| `NormalizerTests.cs` | `NormalizePhone` (parens/dashes/spaces, international prefix, already-normalized, invalid/null/short); `NormalizeZip` (9-digit, non-digit chars, 4-digit, null); `NormalizeUrl` (https, http upgrade, missing scheme, trailing slash, whitespace, null, multi-value semicolon-joined keeps only the first URL) |
| `ContributionProcessorTests.cs` | Malformed payload dead-letters without DB access |
| `DeduplicationJobTests.cs` | `JaroWinkler` against Winkler's two published examples (`TestSupport/JaroWinklerPublishedExampleConstants.cs`) and against generated pairs whose expected score is computed from the match count, transposed pairs and shared prefix, including a prefix longer than the four-character window; `HaversineDistance`; `BucketKey` grid-cell assignment; `Run` orchestration (distance guard, similarity guard, suggestion write, close pair straddling a bucket boundary still matches via the 3x3 neighbor-cell search, query excludes `(0,0)` fallback-coordinate churches and PO Box addresses — both are non-precise geocodes that produce false-positive/OOM-inducing proximity matches, a bucket holding many identically named churches yields one suggestion fewer than the cluster's size rather than one per pair, and a chain whose ends are further apart than the match radius yields only the two pairs that matched, never the unmatched end-to-end pair a star from one representative would add) |
| `DuplicateClustersTests.cs` | `TryJoin` — two indexes already connected through a third refuse the redundant link, while indexes in separate clusters still join |
| `ReGeocodeJobTests.cs` | `LoadZeroCoordChurchesAsync` and `LoadZeroCoordCampusesAsync` — row mapping including a null street, and the query shape each depends on: the `(0,0)` predicate, the four PO-Box exclusions, and on the campus side the join to `Churches` for `IsActive`, since `Campuses` has no such column and a campus of a deactivated church must not be geocoded. `Run` itself is not covered |
| `QueueDepthMonitorJobTests.cs` | `Run` handles a `RequestFailedException` from the Service Bus admin client gracefully for every queue instead of throwing (would otherwise feed the exceptions alert every 15 minutes) |
| `ChurchWriterTests.cs` | `UpsertAsync` — insert vs. update vs. identical-record skip; the closed-connection path; validation that throws before any insert (null canonical name, blank city, a state that isn't two letters); per-column truncation for every text field, including the generated slug; slug collisions get a suffix; denomination name resolved to an id, unknown ones bound as `DBNull`, absent ones not queried at all; phone/zip/website normalization; child collections (attributes, service schedules, ministries, campuses) replaced by one delete-and-insert command per table whose `@Rows` JSON carries only the rows that validate, with attribute deletes scoped to the `@Sources` JSON; a denomination resolved by an earlier write is not queried again; a confidence recalculation published on write but not on a duplicate skip; a new church with a website looks its URL up through the `UrlHash` column and registers a crawl source. `UpdateCoordinatesAsync` — publishes a recalculation when a row changed, nothing when none did. `UpdateCampusCoordinatesAsync` — updates `Campuses` and stamps `UpdatedAt`, and publishes nothing even when a row changed; the empty-outbox arm is the opposite-sign control against the church method's single publish |
| `ConfidenceScoreCalculatorTests.cs` | `Calculate` — the empty record scores zero, core fields plus coordinates score one, the attribute-count contribution caps, a recent verification adds its bonus, and each secondary signal adds its own increment |
| `ConfidenceWorkerTests.cs` | `RecalculateAsync` — a found church and its attribute count are read in one query and its score written, the score using the count that query returned; an unknown one updates nothing |
| `CrawlSchedulerWorkerTests.cs` | `DispatchDueAsync` — due sources are claimed (marked pending) and returned by one `UPDATE ... OUTPUT` statement, then published; nothing is published when none are due |
| `TelemetryMetricsTests.cs` | The church instruments' wire names and tag keys are pinned as literals, because dashboards and alert rules query them by string and a rename would otherwise pass every test; `ReGeocoded`, `ReGeocodedCampuses` and `BulkImportRows` emit on the documented instrument carrying every tag, and each re-geocode counter keeps its three outcomes on separate `result` values. A campus emission is watched by a recorder on the church instrument at the same time and must leave it empty, since each counter's three buckets sum to its own candidate total. Measurements are captured through `TestSupport/MeterRecorder.cs`, a `MeterListener` wrapper shared with `EnrichmentWorkerTests.cs` |
| `ConfigurationExtensionsTests.cs` | `GetRequired`: returns a present value, and throws naming the missing key when it isn't configured. `GetRequiredValues`: returns an indexed set in order, and throws naming the key when the set is absent or every entry is blank |
| `SlugHelperTests.cs` | `ToSlug` kebab-case conversion |

## PlayStation library jobs

| Test file | What it covers |
|---|---|
| `LeasedJobRunnerTests.cs` | `RunAsync` — a non-JSON body or a missing required field dead-letters without claiming the run; the claim itself is a compare-and-swap on `seq` plus the lease; a superseded `seq` settles without reprocessing, except on a stale redelivery of an already-failed run, which dead-letters so the failure still surfaces in the DLQ; a rate-limit pause completes without marking the run failed; any other exception marks failed and then dead-letters; a lock lost while settling is swallowed, because the status write already committed; the lease is renewed while the work runs, on its own connection rather than one shared with the terminal write. `ClassifyJobError` reports a rejection stamped `AppNpsso` as `psn_credential_rejected` (the remedy is rotating the secret) and keeps a user-link or unattributed rejection on `psn_link_expired` |
| `JobRunsRepositoryTests.cs` | `ReapExpiredLeasesAsync` — targets only `running` rows whose lease has lapsed or is absent, spares one still inside the redelivery window, waits a full day before calling a run abandoned, marks what it reaps failed, writes the error code beside the prose, clears the lease so the same run isn't reaped twice, and returns the ids it reaped. Only the **integration** tier proves the code is a value `job_runs_error_code_check` admits — a fake connection carries no constraints, so a stale value passes here and fails with Postgres `23514` in production. `GetAsync` — missing run, and `result_summary` readable only once the run succeeded. `MarkRateLimitedAsync` bumps `seq` and returns it so the continuation can be checkpointed against it. `TryBeginDeliveryAsync` excludes `cancelled` from the claim alongside `succeeded`/`failed`, so a redelivered message cannot resurrect a run an operator cancelled. **The integration-tier half of that one is owed and cannot be written yet** — seeding a `cancelled` row needs Curator's migration widening `job_runs_status_check`, and without it the insert fails `23514` |
| `JobTimeBudgetTests.cs` | `Expired` — false until the budget is fully spent, true once it is reached, and true immediately when there is no budget left to spend; the default leaves headroom under the host's function timeout |
| `ExpiredLeaseReaperTests.cs` | `Run` — opens no database connection at all unless the reaper is enabled; reaps abandoned runs and records both the abandoned-run error prose and the `abandoned` error code when it is, so a reaped run is distinguishable from a failure nothing classified. The prose string stays the operator query signature it has always been; the code is what a query should key on once this ships |
| `ScheduledRefreshWorkerTests.cs` | `Run` — malformed/missing-identity payload dead-letters without DB access; no schedule, a paused schedule, and a stale `scheduled_for` (mismatched against the stored `next_run_at`) all discard (complete, no dispatch); a due schedule with no active run dispatches (`job_runs` insert + `curator-library-refresh` send) and advances the schedule + publishes the next tick; an already-active previous run skips dispatch but still advances the schedule; a generically-failed previous run increments `consecutive_failures` and dispatches again; a previous run whose error names an expired PSN link pauses immediately with no dispatch and no next tick; `consecutive_failures` crossing the configured threshold pauses the same way; a succeeded previous run resets `consecutive_failures` to zero; a **cancelled** previous run is terminal too, so it resets the count and dispatches rather than stalling the chain behind a run that will never finish |
| `LibraryRefreshProcessorTests.cs` | `RunAsync` — a clean run returns a succeeded summary; a RAWG rate limit marks the run rate-limited, publishes a continuation, then throws; a rejected RAWG key is persisted as rejected |
| `LibraryRefreshContinuationProcessorTests.cs` | `RunAsync` — enriches the requested games in the requested order rather than database row order; unions its titles into the existing `result_summary`, preserving order and deduping; merges a second rate limit with the summary already stored and republishes using the merged values |
| `LibraryBuildOrchestratorTests.cs` | `CanonicalizeAsync` (ingest then apply catalog rules), `PersistAndLinkAsync` (upsert each game and link it to the identity's library), `RecordDownloadSizesAsync` (writes the web store's package sizes through the library repository, and records the failure and writes nothing when that host cannot be reached, so a refresh never fails on an enhancement), `EnrichDeltaAsync` (rejects mismatched game/id lists, enriches only what the repository reports as unenriched), `MatchTrophiesAsync` (delegates, and skips the stage when no trophy client was supplied) |
| `IngestionServiceTests.cs` | `IngestAsync` — returns the new pull id and the snapshots it recorded, maps every entitlement field canonicalization needs, carries PSN's verbatim entry through to the persisted raw, records an empty pull when PSN reports nothing, passes the requested limit through, and surfaces a PSN auth failure rather than swallowing it |
| `EnrichmentRunProcessorTests.cs` | `RunAsync` — the reclassification passes are skipped when the rule fingerprints are unchanged and run when they changed or have never run; the catalog pass builds its worklist from the **same** candidate query a library refresh uses — one query, selecting on the three provider success flags and never on an attempt timestamp — asks only the providers a title still needs (a game outstanding on OpenCritic alone must not reach the RAWG or PS Store clients, both of which throw on use in that test), and stops, naming the provider, when a key is rejected; the OpenCritic sweep reports not-configured / rate-limited / auth-error outcomes instead of failing the whole run; the serialized summary carries the four pass keys and the counts the admin page reads, omitting inapplicable fields rather than emitting nulls |
| `EnrichmentBatchProcessorTests.cs` | `EnrichGamesAsync` — a 429 or a rejected key disables that one provider and the batch continues against the others, including when the failure happens inside the built-in OpenCritic top-up; the stop-on-first-provider-failure mode instead leaves every game unenriched for a later run; an empty batch saves nothing |
| `EnrichmentOrchestrationServiceTests.cs` | `EnrichGameAsync` — how one game's signals merge: PS Store data wins over RAWG for publisher, ESRB rating, multiplayer, genres and release year, but an empty PS Store value falls back to RAWG rather than staying blank; the score source records which providers actually scored; publisher-then-developer tier classification sets the AAA tier. Cache behavior: a PS Store row with no fetch timestamp is re-resolved, one with a timestamp is used without calling PSN, a RAWG search that matched nothing is cached as such, and a cache hit never spends an OpenCritic request on a top-up. Failure behavior: a rejected key or a 429 propagates as its own exception (with the retry-after clamped to a 24-hour cap), three consecutive transport failures disable RAWG and record it unavailable, and a top-up that hits a network or server error is marked incomplete without persisting partial games or advancing the shared pagination cursor. Also `RateLimitBackoff.Next` doubling and its cap |
| `EnrichmentCredentialsTests.cs` | `Without` — drops the named provider's credential and leaves the other two in place, and returns a new set rather than mutating the one it was given, so a provider disabled for one batch can't leak into the next |
| `TrophyMatchServiceTests.cs` | `MatchTrophiesAsync` — skips the whole stage unless the user opted into trophy harvesting; rejects mismatched game/id lists; attempts nothing when every game already carries a persisted match; resolves a PS4 title through the exact lookup (asked for in batches, not one call per game) and records the match as exact; skips that lookup for a PS5 title and falls back to fuzzy matching when it resolves nothing; stamps an attempt even when nothing matched so it isn't retried forever; counts every game attempted; refreshes stored progress for the whole matched library |
| `TrophyTitleMatcherTests.cs` | `MatchTitles` — names that agree match, nothing below the threshold does (and a raised threshold is honoured); trophy titles with no name or no progress are ignored, since they can report no completion; a trophy title is claimed by one game only and a game claims one title only, so a near-duplicate can't take an already-claimed title; empty game or trophy lists return nothing |
| `CanonicalizationServiceTests.cs` | `NormalizeName`, `EditionRank`, and `Canonicalize` — turning a user's raw entitlement rows into one catalog game per title; a `PSMEDIA` entitlement is kept as a `media_app` candidate rather than dropped (even when PSN's `isGame` says false), a game download classifies as `game`, an entry with no package type leaves the kind unknown, and a media-app sibling no longer makes an unclassified title read as non-game; a name override keyed by concept and product renames that product and no other under the concept |
| `MergeServiceTests.cs` | `MergeByProductIdAndName` — two concept groups merge only when product id *and* name agree (case-insensitively); a group with no product id, or the sole holder of one, passes through untouched; entry order within a merged group is preserved and merged groups come back before untouched ones |
| `FranchiseAssignerTests.cs` | `AssignFranchise` |
| `PublisherTierRuleSetTests.cs` | `Prepare` + `ClassifyTier` — no publisher name yields null rather than Indie, an AAA rule wins over an AA rule matching the same name, no rule at all defaults to Indie, an exact-kind rule requires the whole name rather than a substring, and a rule cased differently from the name it should match still matches, since the patterns are lowered once when the set is prepared rather than per game |
| `PublisherTierClassifierTests.cs` | `FingerprintPublisherTierRules` is stable regardless of input order and changes when a rule changes |
| `CurationRuleFingerprintTests.cs` | The digest format the stored rule fingerprints use — string escaping and item separators reproduced exactly, including the empty-rule-set case, so an unchanged rule set never triggers a reclassification pass |
| `GenreServiceTests.cs` | `PickGenreSubgenre` — no tags yields null, the most specific tag ranks first, unlisted tags keep their original order below every listed one, a single tag leaves the subgenre null |
| `GenreReconciliationServiceTests.cs` | `ReconcileGenres` — PSN tags win when present, RAWG is the fallback, neither yields null |
| `ReleaseYearTests.cs` | `FromDate` and `FromText` — a PSN full timestamp, a bare RAWG date, and the null/unparseable cases |
| `TitlePlatformTests.cs` | `PlatformForTitleId`, `IsNonTitleEntitlement`, `NormalizePlatformId` |
| `PsnSessionTests.cs` | `VerifiedUrl` host checking, `CreateDefaultHandler`, `GetAsync`, `RunWithReauthAsync` re-authentication, the `RestoreAsync` entry point, and the npsso `BootstrapAsync` exchange it falls back to; `CredentialKind` is `UserLink` for a session restored from a token store and `AppNpsso` for one built from a bare npsso, and every rejection the session throws is stamped with it |
| `PsnLibraryClientTests.cs` | `EntitlementsAsync` — an empty library and a missing entitlements key both return nothing; paging stops on a short page, on the accumulated offset reaching `totalResults`, and at a caller-supplied limit, but an absent `totalResults` means "unknown" and keeps paging to the short page rather than truncating the library at one page; an entitlement a shifting page window serves twice comes back once, while entitlements with no id are all kept so ingestion counts and skips each one; the request asks for every entitlement type and metadata block PSN exposes; mapping keeps all three artwork URLs (falling back to the game icon when the title has none), reads `packageType` from the right block, collects platform ids while skipping attributes without one, keeps PSN's verbatim entry as raw so a mapping bug can't lose a field, and covers every column ingestion persists; a rejected token throws `PsnAuthException` unless an npsso is available, in which case it re-authenticates and retries once. `DownloadSizesAsync` asks the web store's `internal_entitlements` for `drm_def` and pages by `start`/`size`; `MapDownloadSize` sums every package of a `GAME` entitlement, names the platform from the title id inside the entitlement id, and ignores video content, a non-title entitlement, one with no DRM definition and one reporting no bytes |
| `PsnCatalogClientTests.cs` | `TitleConceptAsync` — the age/country/language query parameters PSN requires; concept parsing, including an empty result; cover art picked by role preference rather than array order, falling back to the first image with a URL; the multiplayer flag left null with no player-count notice, false for a single-player-only notice, true for an online notice above one; a rejected cached token re-authenticates and retries once when an npsso is available |
| `PsnTrophyClientTests.cs` | `TrophyTitlesAsync` and `TrophyTitlesByTitleIdAsync` |
| `PsnEntitlementPayloadTests.cs` | The entitlements wire model — a collection PSN omits or sends as null reads as empty rather than null, so no caller has to guard it; `totalResults` absent reads as null and a sent zero reads as zero, so "PSN said nothing" and "PSN said none" stay distinguishable; `activeDate` keeps whatever offset PSN sent, including a non-UTC one |
| `PsnConceptPayloadTests.cs` | The concept wire model — `id` parses whether PSN sends a number or the same value as text, as the entitlements endpoint does; `starRating.score` parses from its decimal string; a coming-soon release date carries its type with no date; a compatibility notice keeps its JSON kind per notice type, since the value is a number, a bool, or a string depending on which notice it is; omitted collections read as empty |
| `RedisPsnRateLimiterTests.cs` | `AcquireAsync` — one key shared by every account, so the budget matches PlayStation's per-client quota; the window is trimmed, the call recorded, and the TTL refreshed on each acquire; the oldest call is not inspected while the window has room; a spent budget waits exactly until the oldest call leaves the window, and does not wait when it already has or when the window holds no entries |
| `StoreGatewayClientTests.cs` | The anonymous storefront client behind the nightly product pass and the catalog crawl: each persisted query goes out as a GET to the configured endpoint carrying the operation name, its configured hash and the product id, with the locale-override and Apollo preflight headers; the product node maps every field the worker reads; a GraphQL error body with no product reads as "asked and found nothing"; a `PersistedQueryNotFound` error, by code or by message, is `StoreQueryRotatedException`; an HTTP failure surfaces rather than reading as a missing product. `CategoryPageAsync` sends the full-game filter with the first configured hash and maps the grid; a rejected hash falls through to the next one and the answer that hash returns is the page; every hash rejected is `StoreQueryRotatedException`; an answer carrying data but no grid is an unusable answer rather than an empty page, which is what keeps a walk from recording a completed category it never read |
| `StoreProductSignalsTests.cs` | How a storefront product becomes enrichment: display genre labels resolve to vocabulary keys (case-insensitively) and unknown labels drop; the shared priority picker names the genre ids; the ESRB rating is taken only from the ESRB authority; a found product is enriched and attempted while a missing one is attempted only; the cache entry carries the concept type and the release date as a date and never overwrites art |
| `StoreProductEnrichmentWorkerTests.cs` | `ProcessAsync` — a found product writes the enrichment row (publisher, rating, rating count, `psn_enriched`) and the PS Store cache row (concept id and type); a missing product records the attempt without enriching and spends no star-rating call; a rotated persisted query stops the pass and reports the remainder; a contended lock asks the store nothing; a spent time budget stops before the first call |
| `StoreCategoryProductTests.cs` | The category-grid product node: the fields the crawl admits on map from a real vendor node, the cover is the preferred role rather than the first media entry, and a round trip keeps the fields no property maps (`skus`, `__typename`, the price node's upsell text), which is what the cached raw column is for. `StoreCategoryPrice.Cents` reads a display price through a thousands separator and yields null for no price at all and for a word where an amount would be |
| `StoreCoverArtTests.cs` | `Best`: the role preference decides, not array order; an entry that is not an image is passed over even under the preferred role; a preferred role carrying no address is passed over rather than answering with nothing; no preferred role and no media at all each yield null |
| `StoreCatalogCrawlWorkerTests.cs` | `ProcessAsync`: a full game is admitted and the resume offset saved, with `isLast` completing the walk; a product that is not a full game is admitted by nothing, since the gateway answers 200 for a filter it did not honor; a renamed category, a contended lock and a rotated query each stop with their own reason; a category reporting a large total and returning no products records no progress row rather than a completed walk, and the category after it is still walked, which is the difference between a stop that ends one category and a stop that ends the run; a resumed run asks for the saved offset, a completed category restarts at zero only once the re-walk interval has passed and is left alone before it, and a spent time budget stops before the storefront is asked anything while an unreachable storefront stops the run; the page budget is what stops a second page being read. `NextOffset` advances by the products actually returned when the gateway reports a smaller offset, and still advances on a page carrying none, so a walk cannot stall on one offset |
| `StoreCatalogCrawlRepositoryTests.cs` | `AdmitAsync` against the real database: a title the catalog does not hold becomes a `game` carrying the walked cover; the same product twice leaves one row and counts as created once; admission opens the `game_enrichment` row and the `psn_catalog_cache` row that puts the game on the nightly product pass's worklist; a title a library refresh already catalogued is reused rather than duplicated; a missing cover is filled and a cover already there is left alone |
| `RedisOpenAIRateLimiterTests.cs` | `TryAcquireAsync` — the default budget is the Azure OpenAI deployment's per-minute request and token limits under one key; each call is recorded with its token estimate and the TTL refreshed, inside a transaction conditioned on the window being unchanged, while there is room; a transaction that loses the race to another instance waits `LostRaceWaitSeconds`; a spent token budget waits until enough estimated tokens leave the window, a spent request budget waits for the oldest call, an estimate larger than the whole budget waits a full window, and a call too soon after the newest one waits out `MinSecondsBetweenCalls` (the minute divided by the request limit) even with budget left; none of the waiting paths records a call. Transport translation: each of `RedisConnectionException`, `RedisTimeoutException`, `IOException` and `TaskCanceledException` becomes a `RateLimiterUnavailableException` carrying the original as its inner exception, while a non-transport fault propagates untranslated so a genuine bug is not redelivered forever — the two together are what pin the split rather than either alone |
| `RedisOpenCriticRateLimiterTests.cs` | `AcquireAsync`: one budget per RapidAPI key, keyed by the key's hash so the key itself never appears in Redis, and shared by every client and instance using that key while a different key keeps its own; each second's increment and its expiry run in one Redis transaction, so no key is left without a TTL; the configured budget admits its last request and a spent second waits for the next one; a budget of zero is refused at construction; `LocalSpacing` spreads the configured budget evenly across a second, and two requests under one key in one process are spaced by it even from separate clients while a request under a different key is not; an unreachable Redis admits on the local spacing alone while any other fault propagates |
| `RedisRawgRateLimiterTests.cs` | `TryAcquireAsync` — one key per user (`curator:rawg:{sub}`) and one for the app's own key (`curator:rawg:admin`), the default budget being RAWG's monthly free-plan quota over a rolling month; the window is trimmed, the call recorded and the TTL refreshed while there is room; a spent budget reports the seconds until the oldest call leaves the window and records nothing; a full window whose oldest call has already expired acquires |
| `RateLimitedRawgClientTests.cs` | The `IRawgClient` decorator the three workers wrap RAWG in per message: every call, `ValidateKeyAsync` included, spends one budget entry before delegating, and a spent budget throws the RAWG `EnrichmentRateLimitException` carrying the wait without calling RAWG, so the run pauses through the existing continuation path |
| `TokenCryptoTests.cs` | `Decrypt` reads an unversioned token encrypted by Curator's own Python `TokenCrypto` (a pinned known-answer vector) and a scheme-`0x01` token alike, including the 1-in-256 unversioned token whose first nonce byte is itself `0x01` — the discriminator is the GCM tag, not the byte; it throws `CryptographicException` for a wrong key, tampered ciphertext (versioned or not), or a token shorter than a nonce plus tag. `Encrypt` writes the `0x01`-led versioned framing, pinned by length and leading byte. `EncryptThenDecrypt` round-trips; the constructor rejects a key that doesn't decode to 32 bytes |
| `DbPsnTokenStoreTests.cs` | `LoadAsync` — null for no link, ciphertext encrypted under a different key, non-JSON plaintext, or a JSON root that isn't an object; an already-expired access token returned alongside the durable refresh token; `refresh_token_expires_at` parsed when present. `SaveAsync` — no-ops without an access token, encrypts and persists the durable fields, omits the expiry when absent. `ClearAsync` throws, because nothing in a job should ever unlink an account |
| `InMemoryPsnTokenStoreTests.cs` | Round-trip, `ClearAsync`, null before any save, and that two instances don't share state so the default can't silently become persistent |
| `PsnAccessTokenCacheTests.cs` | `CacheKey` scopes an entry to one identity; `LoadAsync` returns null for a miss or a payload that isn't valid JSON; `SaveAsync` writes nothing without an access token or once one has already expired, and otherwise stores only the ephemeral fields — never the durable refresh token — under a TTL that expires with the access token |
| `RawgClientTests.cs` | `SearchGamesAsync` (query parameters, candidate parsing with platform ids, `Retry-After` parsed on a 429 and left null when absent so the caller applies its own backoff); `FetchDetailAsync` (parsed detail, 404 returns null without raising); `ValidateKeyAsync` spends its one request on the genres endpoint, never search; a transport failure propagates unwrapped; a rejected key raises an error whose message leaks neither the body nor the key, and `ProviderDetail` is truncated so a long provider body can't flood the run summary |
| `RawgMatcherTests.cs` | `Normalize`, `Similarity`, and `FindBestMatch` candidate selection |
| `OpenCriticClientTests.cs` | `FetchPlatformGamesAsync`: paginates until a short or empty page, resumes from the stored cursor and resets it at the end, honors the page cap and stops as the daily request budget nears exhaustion, keeps earlier pages and reports the failed offset when a page fails or the transport does, treats a negative top-critic score as unscored, skips entries missing an id or name, carries the provider payload for persistence, and parses `Retry-After` so the run can be rescheduled. No page request is sent until the shared `IOpenCriticRateLimiter` admits it, and every request, `ValidateKeyAsync`'s included, takes its slot under the credential it sends. `ValidateKeyAsync` spends its one request on the catalog endpoint, never search. `ProviderDetail` carries enough body to tell an unsubscribed plan from a bad key, redacts the API key when the body echoes it back, and is truncated |
| `OpenCriticGameEntryTests.cs` | `ToGame` — OpenCritic's `-1` sentinel for an unscored game becomes null on both `topCriticScore` and `percentRecommended`, while a genuine zero is kept, because zero is a score and `-1` is absence; an entry with no id or no name yields nothing; deserialization keeps unmapped fields so the stored raw payload loses nothing |
| `OpenCriticNameIndexTests.cs` | `Normalize` (roman numerals, typographic and ASCII apostrophes alike, registered/copyright signs and their parenthesized forms, accent and compatibility folding, separator replacement and whitespace collapsing); `StripSubtitle` cuts at the first spaced colon or dash only; `Build` also indexes the year-suffix-stripped name; `FindMatch` walks its match strategies in order and, among equally good candidates, prefers the highest-scored one and otherwise the first indexed |
| `OpenCriticAdminRefreshServiceTests.cs` | `RefreshCacheAsync` — rotates to the next key on a rotating status code but not on other API or network failures; a rotated key resumes from the cursor the previous key advanced to, not the original start; partial games and the cursor are persisted even when a page fails; every key rejected or rate-limited throws with the provider detail and retry-after hint; the configured page cap is honoured; every platform is swept and its counts summed, stopping at the first platform that exhausts every key; a platform whose cursor lock another run holds is skipped without spending an API call, and each lock is released before moving on |
| `OpenCriticCacheSweepTests.cs` | `Run` — spends no quota and opens no connection when the key or endpoint is unconfigured or every indexed key is blank; rotates across the configured indexed keys when an earlier one is rejected, and does not retry a rejected key on the next platform, so one run wastes one request rather than one per platform; `MaxPagesPerRun` defaults to the admin refresh cap |
| `CatalogRepositoryTests.cs` | `ReclassifyFranchiseAsync` (only the games whose franchise changed, sent in one `unnest` update, a lost match written as null), the franchise-rule fingerprint accessors, `ListAllGameIdsAndTitlesAsync`, and `UpsertGameAsync` keeping a stored `content_kind` when a later upsert of the same game carries none |
| `CatalogRepositoryCanonicalizationTests.cs` | The curation-rule reads (`GetEditionRanksAsync`, `GetNameOverridesAsync`, `GetGloballyExcludedConceptIdsAsync`) and `UpsertGameAsync` — resolves an existing game by concept id before falling back to the normalized title, inserts when neither resolves, writes `content_kind` on insert and only overwrites a known kind on update, stores an absent franchise as null rather than an empty string, repoints every concept at the resolved game, and takes a title-scoped advisory lock in the same transaction before reading or writing |
| `LibraryRepositoryTests.cs` | `UpsertEntriesAsync` (including how `LibraryEntryRow.ForCanonicalGame` merges the PS5/PS4 pair with the other platforms), `GetUnmatchedGameIdsAsync`, `GetGamesForContinuationAsync`, `SetTrophyMatchAsync`, `RefreshTrophyProgressAsync`, and `UpsertDownloadSizesAsync`: a size resolves to a game only through the caller's own `library_entries.title_id`, the largest of two sizes for one platform wins, and a title the caller does not hold writes nothing |
| `EnrichmentRepositoryTests.cs` | The enrichment read/write surface — RAWG and PS Store caches (the PS Store cache also carries `concept_type` on both upsert branches, and a concept PSN types `APPLICATION` reclassifies every game linked to it as `media_app` while any other type leaves `games` alone), the OpenCritic game cache, `GetActiveGenresAsync` and `GetActiveGenresWithLabelsAsync`, `SaveGameEnrichmentAsync` (now carrying `psn_rating_count`), `GetStoreProductsNeedingPsnEnrichmentAsync` (store products PSN has not enriched, never-attempted first) and `TryLockStoreProductPassAsync`, the publisher-tier rules and their fingerprint, and `ReclassifyTierAsync` (only the rows whose tier changed, sent in one `unnest` update, a tier it can no longer justify written as null). For `GetEnrichmentNeedsAsync` the contract under test is that a game stays a candidate until **its own provider flag** flips, and that the result names *which* providers are outstanding: a row with `psn_attempted_at` recorded but `psn_enriched` still false comes back as `Psn=true, Rawg=false, OpenCritic=false`, and only a row with all three success flags set is excluded |
| `EnrichmentKeysRepositoryTests.cs` | `GetDecryptedKeyMaterialAsync` and the per-provider key-rejection writes |
| `EntitlementPullRepositoryTests.cs` | `RecordPullAsync` — the pull row is stamped with its source and entry count and written even when the user owns nothing, and Postgres returning no pull id throws; snapshots upsert on `(identity_sub, entitlement_id)` rather than inserting a row per pull, keeping stored artwork when a later pull omits it and the stored raw payload when the incoming one is an empty object, setting `first_seen_at` on insert only and `last_seen_at` on both paths; every extracted column is sent alongside the raw payload, columns PSN left out are sent as null rather than an empty string, parameters Postgres can't infer from their text form are cast, and the pull row plus every snapshot commit in one transaction |
| `PsnLinkRepositoryTests.cs` | `GetLinkAsync` and `UpdateTokenAsync` |
| `OpenCriticCacheRepositoryTests.cs` | `GetCursorAsync`/`SetCursorAsync` and `SaveGamesAsync` |
| `PostgresConnectionStringTests.cs` | `Normalize` — a `postgresql://` URI becomes something Npgsql can parse, a missing port defaults, a percent-encoded password is decoded, an already-keyword-form string is returned unchanged, and a blank value or a URI naming no database throws |

---

## Running Tests Locally

Run from the repo root and name the project. The test projects never boot `Program.cs`, so no environment variable
selects a configuration branch.

```powershell
dotnet test --project Functions.Tests.Unit --configuration Debug -- --filter-trait "Category=Unit"
```

**A bare `dotnet test` fails here, and not because of the runner.** It also picks up `Functions.Tests.Integration`,
which aborts on `CuratorTestDatabaseConnection is not set` before a single test runs: a wall of red that is really a
missing database.

### The integration tier

It needs a PostgreSQL database carrying the curation schema. That schema belongs to the curation API's repository,
whose migration runner creates and migrates it; this repo never creates or migrates a table, locally or in CI. Two
details matter when you create the database:

- The application role the tests connect as (`curator_app`) cannot create databases, so a superuser creates the
  database **with that role as its owner** (`CREATE DATABASE curator_test OWNER curator_app`). A database the
  superuser owns leaves the role unable to touch the `public` schema, and the first migration fails with
  `permission denied for schema public`.
- The database name must end with `_test` (below), such as `curator_test`.

The tier reads one setting, `CuratorTestDatabaseConnection`, from the environment. Keep its value under `Values` in
`Functions/local.settings.json` (gitignored) and copy it into the environment for the run:

```powershell
$settings = Get-Content Functions\local.settings.json -Raw | ConvertFrom-Json
$env:CuratorTestDatabaseConnection = $settings.Values.CuratorTestDatabaseConnection
dotnet test --project Functions.Tests.Integration --configuration Debug -- --filter-trait "Category=Integration"
```

Two traps present as an authentication or TLS failure rather than a configuration one:

- **Never build the connection from an ambient credential** (`PGPASSWORD` or a password variable meant for another
  server): it authenticates somewhere else, and the whole tier fails `28P01: password authentication failed`. Take the
  connection whole from `local.settings.json`, and confirm the credential by connecting with `psql` first.
- **An ambient `PGSSLMODE=require` against a local server without TLS** fails with
  `server does not support SSL, but SSL was required`. Put the SSL mode in the connection string (`sslmode=disable` on a
  URI for a local server).

The fixture probes for `entitlement_snapshots`, so a database that never migrated fails every test with that message.

### Cleanup: two layers

Each test class deletes what it created in its own `DisposeAsync`, which covers the normal path including a failing
assertion. It does not cover a row inserted before its id was tracked, or a run that never finished, so
`CuratorDatabase` also sweeps the database in `InitializeAsync` (before the first test) and in `DisposeAsync` (after
the collection). The start-of-run pass is the one a killed run relies on.

- **The fixture refuses, before any test runs, a database whose name does not end with `_test` or `_triage`**
  (ignoring case, `CuratorTestDatabaseContract.IsDisposableDatabase`), because every test writes to the database it
  is given. **The sweep re-checks the same name** and is
  skipped silently otherwise, because throwing from `DisposeAsync` would mask the real test failure. A connection
  repointed at any other name therefore stops the sweep without an error: compare row counts before and after a run to
  confirm it fired.
- **`app_users` is deleted first**, so the schema's `ON DELETE CASCADE` foreign keys take every user-scoped row
  (`library_entries`, `entitlement_snapshots`, `job_runs`, `psn_links` and the rest) with it. The sweep lists the game
  graph and per-key caches, which no cascade reaches, explicitly.
- **Seeded rows are never deleted.** The migrations seed `publisher_tiers`, `genres`, `franchise_rules`,
  `size_estimates`, `platforms` and `edition_ranks`. Every publisher tier a test inserts carries the
  `CuratorDatabase.TestPublisherTierPattern` prefix, and the sweep deletes only rows matching it; a test that adds an
  edition rank deletes it by keyword. Deleted seed data would leave the database wrong in a way the migration runner
  never repairs, since it records those migrations as applied.

---

## CI Pipeline

`.github/workflows/main_crgolden-functions.yml` runs on a push to `main`, on pull requests, and on demand:

1. Begin Sonar analysis.
2. Build (`dotnet build --no-incremental --configuration Release /p:RestoreLockedMode=true -warnaserror`), then
   `dotnet jb inspectcode`, failing on any result.
3. Unit tests under `dotnet-coverage` (`coverage.xml`).
4. Integration tests under `dotnet-coverage` (`coverage-integration.xml`), against the database the
   `CURATOR_TEST_DATABASE_URL` secret names; skipped for Dependabot, which gets no secrets.
5. `dotnet publish` and upload the deploy artifact.
6. End Sonar analysis.
7. Deploy to the Azure Function App (`main` only).

Both coverage reports go to SonarCloud, so code only the integration tier exercises still counts.

- **`dotnet-coverage` (a runtime profiler), not Coverlet.** Coverlet's static instrumentation cannot resolve the ASP.NET
  Core shared-framework types the source-generated logging references, and it reports 0% rather than failing.
  `coverage.settings.xml` scopes the profiler to the `Functions` module and excludes `Program.cs` and generated code.
- **`fetch-depth: 0` on the checkout**, because SonarCloud's new-code detection reads blame.
- **`id-token: write`**, for the OIDC federated credential the deploy job signs in with.
- **The whole workflow is serialized** (a `concurrency` group of `${{ github.workflow }}`, not per ref,
  `cancel-in-progress: false`). Every run shares one test database, and one run's sweep would delete rows another run
  is asserting on.

---

## Local SonarCloud Analysis

The analysis needs the repo's SonarCloud token and project, which only maintainers have. C# is analyzed through
`dotnet-sonarscanner` with the build between `begin` and `end`; the standalone `sonar-scanner` CLI has no C# analyzer and
uploads an empty analysis that still succeeds. `end` needs Java (`JAVA_HOME`). `dotnet-tools.json` pins
`dotnet-coverage` and ReSharper's `jb`, not Coverlet, so `dotnet coverlet` is not available here.

```powershell
dotnet tool restore
dotnet-sonarscanner begin /k:"<project key>" /o:"<organization>" /d:sonar.token="<token>" `
  /d:sonar.host.url="https://sonarcloud.io" `
  /d:sonar.cs.vscoveragexml.reportsPaths="coverage.xml,coverage-integration.xml" `
  /d:sonar.exclusions="**/bin/**,**/obj/**" `
  /d:sonar.coverage.exclusions="**/Program.cs"

dotnet build Functions.slnx --no-incremental --configuration Release

dotnet dotnet-coverage collect `
  "dotnet test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit" `
  -f xml -o "coverage.xml" -s "coverage.settings.xml"

dotnet dotnet-coverage collect `
  "dotnet test --project Functions.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration" `
  -f xml -o "coverage-integration.xml" -s "coverage.settings.xml"

dotnet-sonarscanner end /d:sonar.token="<token>"
```

Both reports are in `dotnet-coverage`'s XML format, hence `sonar.cs.vscoveragexml.reportsPaths`. A local scan with no
`sonar.branch.name` replaces the `main` analysis until the next push; treat CI's analysis as the authority.
