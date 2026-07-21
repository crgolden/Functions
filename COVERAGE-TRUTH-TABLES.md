# Functions — Coverage Truth Tables

Demand-driven MC/DC tables for the methods the coverage baseline flags (highest uncovered-branch
first). Vocabulary, the three laws, and the status/home legend live in the workspace
`DESIGN-LANGUAGE.md`; the derivation rules (MC/DC, `tests = 1 + Σ(cases − 1)`, lossless rows,
pyramid escalation, 🔧-seam vs ⬆️-escalate) live in `TESTING-COVERAGE.md`. This file does not restate
them.

Baseline (2026-06-12, unit only, generated code excluded): **8.1% line / 2.8% branch / 6.4% blended,
9 tests** (all `EmailTests`; `Email` function itself is 93.8%). The entire branch gap is the Churches
data-pipeline workers, which are `ServiceBusTrigger` background jobs, no HTTP surface, so **unit is the
correct level** (E2E cannot drive them without standing up Service Bus + Blob + SQL). Selection order:
ExtractorWorker (127 uncovered branches), EnrichmentWorker (77), DeduplicationJob (44), then
ScraperWorker/ContributionProcessor/SitemapGenerator (10/10/6).

---

## ExtractorWorker — 127 uncovered branches → ~28 tests

The 127:1 ratio is the whole point of the table. Most of those branches are **defensive
`?? DBNull.Value` coalesces** (collapse into two record-shape tests) and **accumulator guards**
(each independent, one `[Theory]` row apiece), not 127 independent paths.

**🔧 Seam (DONE, 2026-06-12):** `ExtractPhone`, `ExtractFromHtmlAsync`, `ToSlug` were `private static`.
They are the densest, purest, highest-value logic in the class (HTML parsing + confidence scoring +
slug normalization, zero external dependencies, AngleSharp runs in-memory). The only thing blocking a
unit test was the accessibility keyword, so they are now `internal static` + `InternalsVisibleTo
Functions.Tests` (added to `Functions.csproj`). This is a 🔧 add-the-seam fix on code we own, **not** a
⬆️ pyramid escalation. Escalating pure scoring math to a Service-Bus E2E test would be slower, flakier,
and could not enumerate the input permutations a `[Theory]` covers in milliseconds. There is no benefit
to keeping them private.

### `ExtractPhone(IDocument)` — pure, Home: Unit — 3 tests

| # | itemprop `telephone` | body regex `match.Success` | Result | Status |
|---|---|---|---|---|
| 1 | present (non-blank) | — (short-circuit) | returns itemprop value | ❌ |
| 2 | blank/absent | matches | returns regex value | ❌ |
| 3 | blank/absent | no match | returns `null` | ❌ |

`tests = 1 + (2−1) + (2−1) = 3`.

### `ToSlug(string)` — pure, Home: Unit — 5 tests

Decisions: `foreach` (empty vs non-empty), `char.IsLetterOrDigit(ch)`, and the AND
`!prevDash && sb.Length > 0` (MC/DC 3 cases).

| # | Input | Output | Branch exercised | Status |
|---|---|---|---|---|
| 1 | `"Grace Church"` | `"grace-church"` | alnum append + single separator (both AND operands true) | ❌ |
| 2 | `""` | `""` | `foreach` zero iterations | ❌ |
| 3 | `"  Grace"` | `"grace"` | leading non-alnum, `sb.Length == 0` (second operand false) | ❌ |
| 4 | `"Grace!!!Church"` | `"grace-church"` | consecutive non-alnum, `prevDash == true` (first operand false) | ❌ |
| 5 | `"Grace Church!"` | `"grace-church"` | trailing dash → `TrimEnd('-')` | ❌ |

`tests = 1 + (2−1)[foreach] + (2−1)[IsLetterOrDigit] + (3−1)[AND] = 5`.

### `ExtractFromHtmlAsync(string html, string url)` — pure (AngleSharp in-memory), Home: Unit — 10 tests

Decisions: the `name` resolution `itemprop ?? h1 ?? title` (4 cases), four independent confidence
guards (name/city/state/zip, 2 cases each), and the OR `phone || email` (MC/DC 3 cases). The `?.`
null-conditionals on the address `QuerySelector` results are defensive and covered incidentally by the
present/absent rows below.

| # | Microdata present | Asserts | Branch exercised | Status |
|---|---|---|---|---|
| 1 | full (`name`,`city`,`state`,`zip`,`phone`) via `itemprop` | confidence `0.9`, name from itemprop | all guards true, name source #1 | ❌ |
| 2 | no `itemprop name`, `<h1>` present | name from h1 | name source #2 (`?? h1`) | ❌ |
| 3 | no itemprop/h1, `<title>` present | name from title | name source #3 (`?? title`) | ❌ |
| 4 | none of the three | name `null`, no `+0.2` for name | name resolution all-null | ❌ |
| 5 | `city` only | `+0.2` city | city guard true, others false | ❌ |
| 6 | `state` only | `+0.2` state | state guard true | ❌ |
| 7 | `zip` only | `+0.2` zip | zip guard true | ❌ |
| 8 | `phone` only (no email) | `+0.1` | OR first operand true | ❌ |
| 9 | `email` only (no phone) | `+0.1` | OR second operand true (first false) | ❌ |
| 10 | neither phone nor email | no `+0.1` | OR both false | ❌ |

`tests = 1 + (4−1)[name] + (2−1)×4[guards] + (3−1)[OR] = 10`.

### `Run(...)` + `DownloadBlobAsync(...)` — orchestrator, Home: Unit (mock `DbConnection` / `ServiceBusClient` / `BlobServiceClient`) — 6 tests

`DownloadBlobAsync`'s two decisions (`IsNullOrWhiteSpace(blobPath)`, `!blob.ExistsAsync`) are reachable
through `Run`, so they fold in here rather than getting their own table.

| # | Condition | Expected | Branch exercised | Status |
|---|---|---|---|---|
| 1 | body not deserializable → `payload is null` | `CompleteMessageAsync`, no download | `payload is null` true | ❌ |
| 2 | valid payload, blank `BlobPath` | html `null` → warn + Complete | `IsNullOrWhiteSpace(blobPath)` true | ❌ |
| 3 | valid payload, blob does not exist | html `null` → warn + Complete | `!ExistsAsync` true | ❌ |
| 4 | confidence ≥ 0.5 AND city present | `UpsertChurchAsync` called, Complete | AND both true | ❌ |
| 5 | confidence < 0.5 | enrichment sender, Complete | AND first operand false | ❌ |
| 6 | confidence ≥ 0.5 but city blank | enrichment sender, Complete | AND second operand false | ❌ |

`_logger.IsEnabled(Information)` guard → ⏳ **parked** (log-level branch, near-zero value, exercised
incidentally when the logger is enabled; not worth a dedicated row). `tests = 1 + 1[payload] +
1[blank path] + 1[not exists] + 2[AND] = 6`.

### `UpsertChurchAsync(Guid, ExtractionResult, CancellationToken)` — Home: Unit (mock `DbConnection` → fake `DbCommand`) — 4 tests

The ~20 `(object?)result.X ?? DBNull.Value` coalesces are **not** 20 tests: a single insert with all
optional fields null exercises every `DBNull.Value` side, and a single insert with all fields populated
exercises every value side.

| # | Condition | Expected | Branch exercised | Status |
|---|---|---|---|---|
| 1 | `CrawlSources` lookup returns a `Guid`, conn `Closed` | `OpenAsync`, UPDATE executed | `State == Closed` true, `isNew` false (update path) | ❌ |
| 2 | lookup returns non-Guid, conn already open | INSERT + `CrawlSources` link UPDATE, no `OpenAsync` | `State == Closed` false, `isNew` true (insert path) | ❌ |
| 3 | insert path, all optional fields `null` | every param bound `DBNull.Value` | all `?? DBNull.Value` → right side | ❌ |
| 4 | insert path, all optional fields populated | every param bound to its value | all `?? DBNull.Value` → left side | ❌ |

`tests = 1 + 1[conn state] + 1[new/existing] + 1[null vs populated shape] = 4`.

### ExtractorWorker total

| Method | Home | Tests | Unlocked by |
|---|---|---|---|
| `ExtractPhone` | Unit | 3 | 🔧 seam (done) |
| `ToSlug` | Unit | 5 | 🔧 seam (done) |
| `ExtractFromHtmlAsync` | Unit | 10 | 🔧 seam (done) |
| `Run` + `DownloadBlobAsync` | Unit (mocks) | 6 | existing seams |
| `UpsertChurchAsync` | Unit (mocks) | 4 | existing seams |
| **Total** | | **28** | |

**127 uncovered branches → 28 tests.** 18 of them (`ExtractPhone` + `ToSlug` + `ExtractFromHtmlAsync`)
are pure `[Theory]` functions the seam just unlocked; the other 10 are mocked-dependency
orchestration/DB tests. No branch is escalated; every one is unit-reachable.

---

## EnrichmentWorker — 77 uncovered branches → ~24 tests

Same shape as ExtractorWorker: the branch mass is in one pure JSON-parsing method plus the DB upsert.

**🔧 Seam (DONE, 2026-06-12):** `TryParseEnrichment` and `ToSlug` were `private static`, now `internal
static` (the `Functions.Tests` seam added for ExtractorWorker covers this class too).

**🔧 Dedup finding (open):** `ToSlug` is byte-for-byte identical in `ExtractorWorker` and
`EnrichmentWorker`. Recommend extracting to a shared `internal static class SlugHelper` so it is
defined and tested **once**. Until then the 5 `ToSlug` rows below are the same 5 already authored for
ExtractorWorker, so the marginal cost here is 0 once that helper exists.

### `Run(...)` — orchestrator, Home: Unit (mock `ResponsesClient` + `DbConnection`) — 4 tests

| # | Condition | Expected | Branch exercised | Status |
|---|---|---|---|---|
| 1 | body not deserializable → `payload is null` | Complete, no OpenAI call | `payload is null` true | ❌ |
| 2 | OpenAI returns output text | `TryParseEnrichment` + `UpsertChurchAsync` + Complete | happy path | ❌ |
| 3 | `response?.Value?.GetOutputText()` is `null` | `?? throw` → caught → `LogError` + Complete | null-coalesce throw into catch | ❌ |
| 4 | `CreateResponseAsync` throws | caught → `LogError` + Complete | catch path | ❌ |

`_logger.IsEnabled` guard → ⏳ parked. `tests = 1 + 1[payload] + 1[null-output throw] + 1[exception] = 4`.

### `TryParseEnrichment(string json, EnrichmentPartialData)` — pure, Home: Unit — 11 tests

Decisions: the JSON-slice AND `start >= 0 && end > start` (3 cases); local `GetStr` (`TryGetProperty &&
ValueKind == String`, 3 cases); local `GetBool` (missing / `True` / `False` / other-kind, 4 cases);
local `GetInt` (`TryGetProperty && TryGetInt32`, 3 cases); the `?? partial.X` / `?? "English"`
fallbacks; and the outer `try/catch` (malformed → fallback `EnrichedData`).

| # | Input | Asserts | Branch exercised | Status |
|---|---|---|---|---|
| 1 | clean JSON, all fields valid | every field mapped from JSON | happy path, all locals' "present" sides | ❌ |
| 2 | JSON wrapped in prose / ```` ```json ```` fences | braces sliced, parses | `start >= 0 && end > start` both true | ❌ |
| 3 | text with no `{` | parse fails → fallback | slice AND first operand false | ❌ |
| 4 | `{` present but no closing after it | parse may fail → fallback | slice AND second operand false | ❌ |
| 5 | malformed JSON inside braces | catch → `EnrichedData` from `partial` | outer catch path | ❌ |
| 6 | `canonicalName` is a number (wrong kind) | `GetStr` → null → `?? partial.CanonicalName` | `GetStr` non-string kind + fallback | ❌ |
| 7 | `city` key absent | `GetStr` → null → `?? partial.City` | `GetStr` no-property + fallback | ❌ |
| 8 | `acceptsLGBTQ: true` | `true` | `GetBool` True | ❌ |
| 9 | `acceptsLGBTQ: false` | `false` | `GetBool` False | ❌ |
| 10 | `acceptsLGBTQ` absent / `null` literal | `null` | `GetBool` no-property + other-kind | ❌ |
| 11 | `worshipStyle` absent or non-int; `primaryLanguage` absent | `0`; `"English"` | `GetInt` fallback + `?? "English"` | ❌ |

`tests = 1 + 2[slice AND] + 2[GetStr] + 3[GetBool] + 1[GetInt] + 1[primaryLanguage] + 1[catch] = 11`
(rows merge several where one input exercises multiple "present" sides).

### `ToSlug(string)` — pure, Home: Unit — 5 tests (shared with ExtractorWorker)

Identical to the ExtractorWorker `ToSlug` table. Marginal cost 0 once the shared `SlugHelper` exists.

### `UpsertChurchAsync(...)` + `BindEnriched(...)` — Home: Unit (mock `DbConnection`) — 4 tests

`BindEnriched`'s four `HasValue ? value : DBNull.Value` ternaries (`AcceptsLGBTQ`,
`WheelchairAccessible`, `HasNursery`, `HasYouthProgram`) collapse, like the ExtractorWorker coalesces,
into the null-vs-populated record-shape pair.

| # | Condition | Expected | Branch exercised | Status |
|---|---|---|---|---|
| 1 | lookup returns `Guid`, conn `Closed` | `OpenAsync`, UPDATE | `State == Closed` true, update path | ❌ |
| 2 | lookup returns non-Guid, conn open | INSERT + link UPDATE, no `OpenAsync` | insert path | ❌ |
| 3 | insert, all nullable bools `null` | each bound `DBNull.Value` | every `HasValue ? :` → false side | ❌ |
| 4 | insert, all nullable bools set | each bound to value | every `HasValue ? :` → true side | ❌ |

`tests = 1 + 1[conn state] + 1[new/existing] + 1[null vs populated] = 4`.

### EnrichmentWorker total

| Method | Home | Tests |
|---|---|---|
| `Run` | Unit (mocks) | 4 |
| `TryParseEnrichment` | Unit | 11 |
| `ToSlug` | Unit | 5 (shared) |
| `UpsertChurchAsync` + `BindEnriched` | Unit (mocks) | 4 |
| **Total** | | **24** (19 net of shared `ToSlug`) |

**77 uncovered branches → 24 tests** (19 once `ToSlug` is deduped to a shared helper). Again no
escalation; the OpenAI client and `DbConnection` are constructor-injected and mockable.

## DeduplicationJob — 44 uncovered branches → ~15 tests

The branch mass is the pure `JaroWinkler` string-similarity algorithm; the rest is geo math and a
nested-loop scan with two `continue` guards.

**🔧 Seam (DONE, 2026-06-12):** `JaroWinkler`, `HaversineDistance`, `ToRad` were `private static`, now
`internal static`. These are deterministic numeric algorithms, the textbook case for known-value
`[Theory]` tests; pushing them to E2E would be absurd.

### `JaroWinkler(string s1, string s2)` — pure, Home: Unit — 9 tests

Decisions: `s1 == s2`; the OR `s1.Length == 0 || s2.Length == 0` (3 cases); the inner-match OR
`s2Matches[j] || s1[i] != s2[j]` (3 cases); `matches == 0`; the transposition `s1[i] != s2[k]`; and
the prefix-loop `s1[i] != s2[i]` break. Assert against published Jaro-Winkler reference values.

| # | `(s1, s2)` | Expected | Branch exercised | Status |
|---|---|---|---|---|
| 1 | `("grace", "grace")` | `1.0` | `s1 == s2` true | ❌ |
| 2 | `("", "grace")` | `0.0` | length OR first operand true | ❌ |
| 3 | `("grace", "")` | `0.0` | length OR second operand true (first false) | ❌ |
| 4 | `("abc", "xyz")` | `0.0` | `matches == 0` true | ❌ |
| 5 | `("martha", "marhta")` | `≈0.961` | transposition path (`s1[i] != s2[k]`) | ❌ |
| 6 | `("dixon", "dicksonx")` | `≈0.813` | matches, no transposition, prefix boost | ❌ |
| 7 | `("aa", "a")` | `>0` | inner OR `s2Matches[j]` already-true skip | ❌ |
| 8 | `("abc", "xbc")` | prefix `0` | prefix-loop break on first char | ❌ |
| 9 | `("abcdef", "abcdxy")` | prefix `4` (capped) | prefix-loop runs full window | ❌ |

`tests = 1 + 1[equal] + 2[length OR] + 1[matches==0] + 1[transposition] + 1[inner skip] + 2[prefix] = 9`.

### `HaversineDistance(...)` + `ToRad(...)` — pure, Home: Unit — 2 tests

Straight-line math, no decisions. Anchor to a known great-circle distance and the zero case.

| # | Input | Expected | Status |
|---|---|---|---|
| 1 | two known coordinates | reference miles (±tolerance) | ❌ |
| 2 | identical coordinates | `0.0` | ❌ |

### `Run(...)` + `WriteSuggestionAsync(...)` — orchestrator, Home: Unit (mock `DbConnection` → `DbDataReader`) — 4 tests

The two `continue` guards (`distance > MaxDistanceMiles`, `similarity < JaroWinklerThreshold`) are the
decisions; the nested `for` loops are exercised by the fixture row counts. `WriteSuggestionAsync` is
straight-line and folds into row 4.

| # | Fixture | Expected | Branch exercised | Status |
|---|---|---|---|---|
| 1 | conn `Closed`, reader yields 0 rows | `OpenAsync`, logs `0` suggestions | `State == Closed` true, empty scan | ❌ |
| 2 | 2 churches > 0.1 mi apart | skip, `0` suggestions | `distance > Max` true (continue) | ❌ |
| 3 | 2 churches close, dissimilar names | skip, `0` suggestions | `similarity < Threshold` true (continue) | ❌ |
| 4 | 2 churches close, similar names | `WriteSuggestionAsync` called, `1` suggestion | both guards false → write | ❌ |

`tests = 1 + 1[conn state] + 1[distance guard] + 1[similarity guard] = 4`.

### DeduplicationJob total

| Method | Home | Tests |
|---|---|---|
| `JaroWinkler` | Unit | 9 |
| `HaversineDistance` + `ToRad` | Unit | 2 |
| `Run` + `WriteSuggestionAsync` | Unit (mocks) | 4 |
| **Total** | | **15** |

**44 uncovered branches → 15 tests.** All unit; the densest logic is pure numeric algorithm.

---

## ScraperWorker — 10 uncovered branches → ~4 tests

Orchestrator (HTTP fetch → blob → extraction queue). Deps `IHttpClientFactory` / `BlobServiceClient` /
`ServiceBusClient` / `DbConnection` all mockable. **No seam needed** (`StoreBlobAsync` /
`UpdateCrawlStatusAsync` are reached via `Run`).

| # | Condition | Expected | Branch | Status |
|---|---|---|---|---|
| 1 | body not deserializable → `payload is null` | Complete, no HTTP | `payload is null` true | ❌ |
| 2 | `!response.IsSuccessStatusCode` (e.g. 404) | `UpdateCrawlStatus(2)` + Complete | non-success true | ❌ |
| 3 | success | `StoreBlob` + send `extraction-requests` + `UpdateCrawlStatus(1)` + Complete | happy | ❌ |
| 4 | `httpClient.GetAsync` throws | `LogError` + `UpdateCrawlStatus(2)` + **Abandon** | catch path | ❌ |

`_logger.IsEnabled` ⏳ parked; `UpdateCrawlStatusAsync` conn-state covered incidentally.
`tests = 1 + 1[payload] + 1[non-success] + 1[exception] = 4`.

## ContributionProcessor — 10 uncovered branches → ~3 tests

Orchestrator (Service Bus → SQL insert). **No seam needed.**

| # | Condition | Expected | Branch | Status |
|---|---|---|---|---|
| 1 | `payload is null` | warn + Complete, no insert | `payload is null` true | ❌ |
| 2 | valid, `OldValue` present, conn open | INSERT with value + Complete | `?? DBNull.Value` left, `State == Closed` false | ❌ |
| 3 | valid, `OldValue` null, conn `Closed` | `OpenAsync` + INSERT with `DBNull` + Complete | `?? DBNull.Value` right, `State == Closed` true | ❌ |

`_logger.IsEnabled` ⏳ parked. `tests = 1 + 1[payload] + 1[null-vs-open shape] = 3`.

## SitemapGenerator — 8 uncovered branches → 4 tests

Timer-triggered (SQL slugs → chunked, gzipped `urlset` blobs + a `sitemapindex` blob → orphan cleanup).
**No seam needed** — `DbConnection`, `IAzureClientFactory<BlobServiceClient>`, `IConfiguration` are all
already-injected abstractions.

| # | Condition | Expected | Branch | Status |
|---|---|---|---|---|
| 1 | reader yields exactly 49,999 rows (homepage + rows = 50,000) | exactly 1 chunk blob (`application/gzip`) + index blob (`application/xml`) referencing 1 chunk, all `<loc>` under `ChurchesBaseUrl` | chunk-full check never trips | ✅ |
| 2 | reader yields 50,000 rows (homepage + rows = 50,001) | chunk 1 flushed at 50,000, chunk 2 holds the 1 remaining row, index references both | chunk-full check trips exactly once | ✅ |
| 3 | reader yields 0 rows | single chunk containing only the homepage `<url>` | read loop 0 iterations | ✅ |
| 4 | previous run left more chunk blobs (`sitemaps/sitemap-{n}.xml.gz`) than the current run produced | only blobs whose `{n}` exceeds the current chunk count are deleted | `int.TryParse` + `blobChunkNumber > chunkCount` both branches | ✅ |

Row 1 and row 2 together exercise the chunk-boundary branch (never trips / trips once) and double as the
gzip-round-trip check (each test gunzips the captured upload stream and asserts well-formed `<urlset>`
XML) and the same-origin guard (every `<loc>` in the index must start with `ChurchesBaseUrl`, never the
blob service's own URI — this directly guards against reintroducing the original sitemap-origin bug in
this new code path). `tests = 1[boundary-exact] + 1[boundary+1] + 1[zero-rows] + 1[orphan-cleanup] = 4`.

---

## Functions repo roll-up

| Worker | Uncovered branches | Tests | 🔧 seams made |
|---|---|---|---|
| ExtractorWorker | 127 | 28 | `ExtractPhone`, `ExtractFromHtmlAsync`, `ToSlug` → internal |
| EnrichmentWorker | 77 | 24 (19 net) | `TryParseEnrichment`, `ToSlug` → internal |
| DeduplicationJob | 44 | 15 | `JaroWinkler`, `HaversineDistance`, `ToRad` → internal |
| ScraperWorker | 10 | 4 | none |
| ContributionProcessor | 10 | 3 | none |
| SitemapGenerator | 8 | 4 | none |
| **Total (all 6)** | **276** | **78 (73 net)** | one `InternalsVisibleTo` line |

The headline for the whole repo: **274 uncovered branches resolve to ~72 tests**, because (a) pure
parsing/scoring/similarity logic — the bulk — collapses into `[Theory]` rows once the `private static`
seam is opened, and (b) the defensive `?? DBNull.Value` / `HasValue ? :` mass collapses into
null-vs-populated record-shape pairs. None of it is escalated to E2E; the three large workers needed
the accessibility seam, the three small ones are pure orchestration reachable through `Run` with
mocked dependencies.
