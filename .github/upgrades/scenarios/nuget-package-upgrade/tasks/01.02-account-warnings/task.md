# 01.02-account-warnings: Resolve account and cache nullability warnings

## Objective
Fix the nine existing application nullable warnings in account projections and cache flows while preserving intern identity, AccountId authority, caching behavior, and financial semantics.

## Scope
Inspect contracts, callers, and tests for AdminPreloadService, AdminRecentAccountSnapshotService, CacheRebuildService, AccountSnapshotWarmupService, FundReferenceService and UserAccountContextService. Update cache documentation if cache behavior or contracts change and add focused tests where needed.

## Validation
Build Application warning-free and run application tests and any focused cache tests. Never modify real data.

## Done when
All application warnings are resolved without suppression and the relevant tests pass.
