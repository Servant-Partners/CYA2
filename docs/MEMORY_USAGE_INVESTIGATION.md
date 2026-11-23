# Memory Usage Investigation

## Purpose and scope

Identify potentially unnecessary memory use during uploads and normal idle operation. This document records investigation targets, not confirmed defects or an implementation plan.

No upload limits, cache budgets, application behavior, or production GC settings are changed by this document. The previously discussed 4,000 KB file limit and 300 MiB processing allowance are not implemented or validated.

Preserve existing uncommitted changes. Use synthetic data only. Do not start the application against its configured database, initialize a database, run Docker, or perform rollback/provider simulations without separate user direction. Those simulations remain reserved for the user.

## Donation-frequency implementation and synthetic CPU comparison

The agreed chronological gift-pattern and targeted publication completion is implemented; see `DONATION_FREQUENCY.md`. The implementation removes standard publication's all-affected history/change lists and repeated per-gift classification. It probes affected donor/fund prefixes for a proven Monthly checkpoint with full twelve-month context, or replays only the unresolved affected group from its beginning. Group/history/change buffers are bounded at 64/500/500 rows; the active group's rolling twelve-month gift queue remains density-dependent. Contact evidence projection and the incoming serialized evidence/write-model array were not redesigned.

Saved `BenchmarkSuite3` artifacts were inspected first. Fresh before/after runs then used the same unchanged `DonationFrequencyBenchmarks.CurrentFrequency` and `LatestGift`, the same 60/240 monthly synthetic gifts, ShortRun configuration, and CPU profiler. Neither run accessed a database or started the configured application. The earlier saved artifact's different timings are not substituted for the freshly collected baseline.

Each timing cell below is **mean / reported error / standard deviation**, in microseconds. Delta refers to mean time only; these short profiled runs have broad error estimates and are not production upload speedup claims.

| Workload | Gifts | Before (mean / error / SD), us | After (mean / error / SD), us | Mean delta |
| --- | ---: | ---: | ---: | ---: |
| CurrentFrequency | 60 | 42.36 / 25.42 / 1.394 | 14.89 / 4.506 / 0.247 | -64.8% |
| LatestGift | 60 | 42.83 / 41.20 / 2.258 | 18.40 / 18.661 / 1.023 | -57.0% |
| CurrentFrequency | 240 | 193.53 / 116.55 / 6.388 | 68.81 / 14.338 / 0.786 | -64.4% |
| LatestGift | 240 | 190.88 / 105.66 / 5.792 | 75.82 / 20.725 / 1.136 | -60.3% |

Baseline CPU session: `9c848f08-ceb0-43b1-9acb-c6f924d72c5b`. Post-change CPU session: `7a4da543-67d5-45d3-8693-7afc6b9fda2d`. In the sampled process, `IsMonthly` had 60.42% self / 74.20% total CPU samples before and 21.81% self / 57.29% total afterward. These are sample shares within each trace, not independently measured elapsed method durations. Remaining samples include monthly-total enumeration and decimal arithmetic. No further speculative optimization was applied.

This benchmark measures the real core classification entry points, not MySQL checkpoint query execution, publication phase latency, retained heap, live/peak RAM, working set, or provider/server buffers. No measured memory-retention saving is claimed. Seeded synthetic checkpoint/full-replay equivalence tests support the narrowed context design; provider query plans, cancellation/atomicity, backups/rollback, summary refresh, and normal UI/precaching acceptance remain user-run.

## Evidence available

### Operational observations supplied by the user

- Azure B1 plan with 1.75 GB RAM, hosting only this application.
- Approximately 75% plan-level baseline usage reported.
- Application working set approximately 100 MB idle.
- Accounting upload/rollback/reupload peak approximately 266 MB, settling around 220–230 MB.

These observations are not synchronized heap measurements. Plan-level usage and application working set have different scopes and must not be added together or treated as equivalent counters.

### Recovered synthetic benchmark evidence

The existing `BenchmarkSuite1/DonationPreviewValidationBenchmarks.cs` uses a 2,000-row synthetic donation workbook and calls the real `ImportOrchestrationService.PreviewAsync`. It uses synthetic administrator authorization, the actual validator and donation workbook reader, and immediate in-memory preview cleanup. Publication processors throw if invoked. The workload is database-free.

The source declares `CPUUsageDiagnoser`, not BenchmarkDotNet `MemoryDiagnoser`. Saved run logs nevertheless show `DotNetObjectAllocDiagnoser` collection. Both saved runs completed; the later run could not automatically open its diagnostic session in Visual Studio.

Recovered diagnostic samples from the later run:

| Method | Managed allocation per preview | GC diagnostic sample |
| --- | ---: | --- |
| `DonationPreview` | 39,254,040 bytes, approximately 37.44 MiB | Gen0/Gen1/Gen2: 5/2/1 over one operation |
| `AccountingPreview` | 10,186,720 bytes, approximately 9.71 MiB | Gen0/Gen1/Gen2: 12/6/6 over six operations |

These values were derived from the saved BenchmarkDotNet `// GC:` records. They are allocation volume, not simultaneously live memory, working set, or peak memory. Diagnostic GC counts may be affected by benchmark/profiler behavior.

Both methods use the same synthetic donation workbook. Only the donation preview branch performs donation semantic parsing. `AccountingPreview` therefore measures generic preview validation/storage using that workbook, not a representative full accounting import.

The recovered trace reports approximately 991.41 MB cumulatively allocated across repeated executions and both benchmark processes. Strings, EPPlus address/range objects, and byte buffers dominate reported allocations. This total includes benchmark lifecycle activity and is not per upload. The recovered raw ETL did not expose a usable requested string-allocation backtrace or counter series.

Artifacts are under:

`BenchmarkSuite1/bin/Release/net10.0/BenchmarkDotNet.Artifacts/`

Relevant saved logs:

- `DonationPreviewValidationBenchmarks-20261116-162957.log`
- `DonationPreviewValidationBenchmarks-20261116-221748.log`

The later run overwrote the shared summary reports; the earlier log preserves its own results. The recovered later ETL is approximately 4.96 GiB on disk. Trace size is not application RAM use.

## Upload investigation targets

### 1. Canonical models and serialized source evidence

**Source:** `src/Cya2.Infrastructure/Services/DonationImportProcessor.cs`, especially the private processing method.

The processor reads canonical validation results, builds a complete `CanonicalDonationWriteModel` array, creates donor write models, and serializes each canonical row into `SourceEvidenceJson` before persistence.

**Hypothesis:** Parsed rows, write models, evidence strings, and repository buffers may overlap and contribute substantially to publication peak memory. Source inspection identifies possible overlap, not exact GC lifetimes or measured peak contribution.

**Investigate:**

- Live sizes and reference paths for validation results, write models, and evidence strings during persistence.
- Whether repository staging introduces additional materialized collections or parameter buffers.
- Whether bounded batches or shorter object lifetimes could reduce overlap while preserving validation-before-publication, atomicity, and source evidence.
- Duplicate/error report size for unusually repetitive or invalid synthetic inputs.

Do not remove required evidence or weaken publication safety to reduce RAM. Publication is not covered by the existing preview benchmark.

### 2. Expanded workbook and parsing allocations

**Sources:** `src/Cya2.Infrastructure/Services/DonationWorkbookReader.cs` and `src/Cya2.Application/Services/ImportUploadValidator.cs`.

**Evidence:** EPPlus objects, strings, and buffers account for substantial recovered allocation volume.

**Investigate:**

- Allocation call sites and live workbook size during parsing.
- Repeated cell/address access, formatting conversions, and temporary string creation.
- Worksheet dimensions and expanded ZIP/XML size, not only compressed file bytes.
- Disposal and release of workbook-related references after parsing.

Donation preview and publication parse separately. This increases cumulative work but does not establish simultaneous retention. Keeping parsed previews to avoid reparsing could increase idle RAM instead.

### 3. Cache refresh overlapping publication completion

**Sources:** Import completion/invalidation flow and the data, summary, and presentation cache loaders.

**Hypothesis:** Old snapshots, new query results, and rebuilt DTOs may coexist around publication completion.

**Investigate:**

- Phase-aligned memory before publication, after commit, during rebuild, and after completion.
- Which caches rebuild immediately and which remain demand-loaded.
- Whether obsolete generations remain referenced by pages or in-flight loads.
- Whether multiple rebuilds materialize equivalent data concurrently.

Preserve automatic financial-summary regeneration, accounts-overview behavior, domain-aware invalidation, and authorization checks. If caching behavior is changed later, update `src/Cya2.Web/docs/CACHE_ARCHITECTURE.md`.

### 4. Independent admission budgets and operation overlap

**Sources:** `ImportUploadValidator`, `ImportPreviewStore`, `ImportOrchestrationService`, and `src/Cya2.Web/Services/Imports/ImportWorkQueue.cs`.

Current base configuration permits two simultaneous validations, two retained previews totaling 10,240,000 compressed bytes, and two admitted background imports totaling 10,240,000 compressed bytes. The queue worker processes one item at a time.

**Hypothesis:** A running publication plus new preview validations and retained previews can create a higher peak than any isolated operation.

**Investigate:**

- Combined peak under explicitly controlled overlap.
- Memory held by waiting, cancelled, and rejected operations.
- Whether independent count/file-byte guards correspond to measured processing costs.

The HTTP confirmation path invokes `ImportFromPreviewAsync` directly rather than using background queue admission. Queue serialization is not a universal application-wide guarantee. The donation processor also uses `FinancialWriteCoordinator`; trace downstream gate coverage before concluding that publications execute concurrently.

The inspected admission checks do not reserve expanded-workbook or process-memory capacity. Do not change limits based solely on preview allocation totals.

### 5. Temporary compressed-file buffers

**Source:** `src/Cya2.Application/Services/ImportUploadValidator.cs`.

The validator grows a `MemoryStream`, then calls `ToArray()`. The stream backing buffer and returned byte array temporarily coexist, along with a read buffer.

**Investigate:** Quantify the peak contribution before considering alternate buffering. Preserve actual streamed-byte enforcement, cancellation, validation, and ownership safety. This is likely smaller than workbook/model costs.

## Idle and post-upload investigation targets

### 1. Cache ownership and duplicate retention — highest priority

**Sources:**

- `src/Cya2.Application/Extensions/ServiceCollectionExtensions.cs`
- `src/Cya2.Application/Services/AuthoritativeDataCaches.cs`
- `src/Cya2.Application/Services/UserOverflowCoverageCaches.cs`
- `src/Cya2.Application/Models/AuthoritativeCacheModels.cs`
- Summary and presentation cache implementations.

Multiple cache families are singleton services. `DonationDataCache` has an estimated 64 MiB budget and 64-entry cap; this is a ceiling, not a reservation or current usage. The inspected user donation overflow cache has a 256-key cap, but no byte budget. Entry count alone does not bound retained row volume.

**Investigate:**

- Retained bytes, entry counts, row counts, generations, and coverage per cache.
- Whether equivalent account/date-range data is copied into several cache layers.
- Whether narrower ranges reuse broad snapshots rather than producing redundant entries.
- Whether overflow merges and DTO construction allocate duplicate collections.
- Whether cancelled loads or old generations keep data reachable.
- How estimated cache bytes compare with actual retained sizes.

Distinguish copied collection containers from copied records and strings; multiple collections can share the same underlying objects. Caches may reduce total allocations and database load, so blanket removal is not justified.

#### Central missing-gift contact warmup: implementation status

Source inspection confirmed that central warmup hydrated contacts for all donors represented in its six-month donation window before filtering missing-gift warnings. `DonorService.WarmMissingGiftDonorsFromDataAsync` now classifies the complete deduplicated date scope first, preserving its latest-donation freshness anchor, and hydrates only rows belonging to actual warning donors with positive IDs. Empty warning results are still cached and require no contact load.

This change applies to central startup, database-recovery, and post-update rebuilding only. Existing default/selected-account and non-admin 4–5-account precaching remains unchanged, as do user-facing donor/donation hydration and contacts already loaded by those paths. No cache budgets, expiration policies, financial-summary/overview behavior, authorization rules, or database-unavailable startup behavior were changed.

A separate database-free synthetic workload, `BenchmarkSuite2/MissingGiftContactWarmupBenchmarks.cs`, exercises the real warmup with 1,000 donors and either zero or 100 warning donors. Its pre-change allocation trace identifies `DonorContactHydrationService.JoinContactValues` among major allocation sites. The trace's approximately 489.78 MB total covers repeated benchmark execution across both cases, not per-operation allocation or retained memory. This workload is separate from the frozen upload-preview benchmark.

Seven targeted regression tests were added in `tests/Cya2.Application.Tests/MissingGiftContactWarmupTests.cs`. Two Visual Studio test-run attempts found no matching tests; none executed. Post-change measurement and successful regression validation remain pending. No retained-heap or working-set reduction has been established, and no application/database or rollback simulations were run for this change. Contact-generation cleanup remains an investigation target below; no stale-generation fencing or eviction redesign was implemented.

### 2. Donor-contact cache generation cleanup

**Source:** `src/Cya2.Application/Services/DonorContactCache.cs`.

This cache stores arrays by donor ID and generation. Its local implementation has no eviction or expiration policy, only `InvalidateAll()`.

**Investigate:** Whether every relevant generation transition clears obsolete entries, how many donors accumulate during normal navigation, and whether old-generation entries remain reachable. This is a concrete retention question, not a confirmed leak.

### 3. Blazor circuit and page state

**Sources:** Admin/report components, dialog state, scoped services, event subscriptions, timers, and circuit configuration.

**Investigate:**

- Large result collections and duplicate/error reports remaining referenced after dialogs close.
- Grid data retained alongside cached copies.
- Subscriptions and timers that outlive their owning components.
- Multiple tabs and disconnected circuits retaining page state.
- Difference between no connected users and an authenticated browser left open.

These are framework-appropriate hypotheses; their contribution has not been measured. Preserve normal circuit reconnection behavior until its cost is established.

### 4. Import progress and error retention

**Source:** `src/Cya2.Web/Services/Imports/ImportProgressService.cs` and retention cleanup wiring.

Progress records retain errors and step details. `RemoveExpired` removes completed records only.

**Investigate:** Whether every terminal failure marks progress complete, whether periodic cleanup runs as expected, and how large invalid-input error collections become. Prioritize below workbook and cache investigations unless repeated failures show accumulation.

### 5. Runtime, native memory, and reusable heap capacity

Approximately 100 MB idle working set is not inherently abnormal for a .NET/Blazor application with its loaded libraries. Working set includes more than live managed objects.

A post-upload plateau can reflect reusable GC heap capacity, fragmentation, native allocations, loaded code, warmed caches, or genuinely retained application data. A plateau alone does not identify which.

**Distinguish:**

- Live managed objects increase: inspect retained roots and ownership.
- Live objects decrease while process memory remains high: inspect GC committed capacity, fragmentation, and native/process memory.
- Plan memory differs from process memory: reconcile counter scope and units before drawing conclusions.

Do not add forced production GC as a remedy.

## Measurement approach

1. Inspect and reuse saved artifacts before collecting again.
2. Reuse the existing frozen synthetic preview benchmark for comparable preview allocation measurements. Do not modify its workload/setup or create a duplicate project.
3. For idle retention, compare controlled states: cold idle, warmed navigation, connected idle, and idle after circuit cleanup. Record elapsed time and active circuits.
4. For upload peaks, correlate memory with validation, parsing, publication, cache rebuild, and post-completion phases. Existing preview measurements do not cover the latter phases.
5. Capture both allocation and retention/process metrics: allocated bytes, live managed heap, committed heap, LOH/fragmentation, GC counts/pauses, working set, and private bytes where available. Record unavailable metrics explicitly.
6. Use heap snapshots/reference roots for retention questions; allocation totals alone cannot identify leaks.
7. Introduce any broader synthetic/database-free measurement harness only as a separate agreed step. Do not run full application/database or rollback simulations under this investigation document's authority.
8. Record a measured baseline before production optimization. Re-measure the same workload afterward and report improvements and regressions without comparing incompatible profiler modes or environments.

Use non-personal diagnostics only: counts, byte totals, ranges, kinds, timings, and generations. Do not record donor details, intern names, or Fund/SubFund values.

## Recommended order and completion criteria

1. Cache retention and duplicated ownership.
2. Donation publication model/evidence overlap.
3. Cache-refresh overlap at import completion.
4. Blazor circuit/page retention.
5. Contact/progress lifecycle and smaller temporary buffers.

For each candidate, record the hypothesis, measured evidence, ownership/root path, approximate live/peak contribution, correctness constraints, and whether a change is justified. Preserve required financial behavior, source evidence, startup database-unavailable limited mode, and authorization. A confirmed allocation site is not automatically an unnecessary allocation, and a high working set is not automatically a leak.
