# 01.02-account-warnings: Resolve account and cache nullability warnings

## Objective
Fix the nine existing application nullable warnings in account projections and cache flows while preserving intern identity, AccountId authority, caching behavior, and financial semantics.

## Scope
Inspect contracts, callers, and tests for AdminPreloadService, AdminRecentAccountSnapshotService, CacheRebuildService, AccountSnapshotWarmupService, FundReferenceService and UserAccountContextService. Update cache documentation if cache behavior or contracts change and add focused tests where needed.

## Validation
Build Application warning-free and run application tests and any focused cache tests. Never modify real data.

## Done when
All application warnings are resolved without suppression and the relevant tests pass.

## Scope Inventory and Research
- A clean Application rebuild confirms nine warnings in the six listed services; the package version changes were already committed in 72492fa. The current branch includes source-sync merge f4e8e99 and has existing workflow-only modifications to preserve.
- `Account.AccountingClass` is legitimately nullable. Fund references, context and donor projections currently declare it non-null despite passing null; the bulk accounting repository explicitly filters null and empty classes and associates results by AccountId. Preserve the existing values and behavior when correcting contracts.
- Both accounting fact keys and balance-cache keys already normalize a null class to an empty key; warm and hit paths must agree. Interns have no accounting and donation lookups use opaque case-sensitive InternSourceId, not display name or fund.
- `Account.Fund` is declared non-null; conditional null checks in the admin overview and account-warmup paths can narrow compiler flow unexpectedly. Check exact data flow rather than masking absent values.
- Existing `CacheRebuildInternIsolationTests` cover central/fallback intern isolation and historical accounting; `FundReferenceTableTests` cover intern overview and cached reference loading. Extend focused coverage for null accounting and cache normalization, then update the Web cache architecture notes if contracts change.
- SDK-style net10.0 projects use dotnet/IDE build with restore. Validate the Application project and its tests; full solution and disposable Docker integration plus safe startup tests are assigned to final validation.
- Recovery clean solution build after the prior contract edits succeeded with 17 warnings: eight Application, two Infrastructure, and seven test warnings. Follow nullable accounting class through the single/batch repository, matchers, overflow keys, test fakes and record construction without changing SQL or matching semantics. Batch grouping must exclude absent classes before constructing non-null dictionary keys.
- `AccountBalanceCache` and both accounting fact-key types already normalize null to empty while retaining AccountId and generation; cover those invariants in focused tests. The matching routine uses `string.Equals` and accepts null without changing matching behavior. Intern-source keys are not normalized by these changes.
