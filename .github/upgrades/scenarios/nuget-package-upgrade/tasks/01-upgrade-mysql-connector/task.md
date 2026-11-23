# 01-upgrade-mysql-connector: Upgrade and validate the MySQL connector

Update the per-project MySql.Data references in `src/Cya2.Infrastructure/Cya2.Infrastructure.csproj` and `tools/Cya2.DatabaseMigrator/Cya2.DatabaseMigrator.csproj` to the unified 26.10.0 version. The quick [API diff](apidiff/MySql.Data.apidiff.md) detected no source-breaking API changes; address any actual compiler or test failures without changing unrelated behavior.

Preserve the startup probe and limited-mode recovery. Validate the solution and tests, including opt-in disposable MySQL integration tests and safe startup outage/recovery checks. Never apply schema or data changes outside disposable tests; document production TLS/server and browser verification limitations.

**Done when**: Both references use 26.10.0, applicable builds and tests pass without warnings, outage and recovery behavior is verified safely, and any remaining external compatibility or verification limitations are reported.

## Scope Inventory and Research

- Both SDK-style net10.0 projects define MySql.Data 9.7.0 locally; dependency inspection found no central package management. Their references must move together to 26.10.0. Infrastructure also references Application and Core; the database test project builds and copies the migrator.
- The quick API diff reports no detected source-breaking changes, but does not scan call sites or guarantee a conclusive or runtime-compatible result.
- Startup `DatabaseStartupInitializer` calls `MySqlDatabaseStartupProbe` (a TCP probe), enters limited mode on failure, and resumes `DatabaseMonitorService`, whose five-second TCP polling can recover. Existing web health tests use fake monitor/probe implementations, so they cannot alone prove real startup/monitor behavior.
- The database fixture isolates MySQL 8.0.40 in a loopback-only disposable container with generated credentials and SSL disabled; it never uses application configuration or external connection strings. `docs/DATABASE_TESTS.md` specifies opt-in discovery and teardown.
- Use a safely isolated local endpoint for real startup/outage and recovery checks; do not connect to a real database. Validate production TLS/server and browser behavior separately.
- Package-reference and build skills require dependency discovery, restoring builds, warning review, and solution validation. Both projects are SDK-style; use IDE build or dotnet build.

## Approved Warning-Cleanup Expansion

- Clean builds of the original commit and the upgraded tree both have the same 15 warning locations; approval was obtained to fix them rather than suppress them.
- DatabaseMigrator has two unused local functions for obsolete pre-production migration history. Confirm no callers before removal and preserve the initial-schema-only path.
- Application has nullable mismatches involving optional account accounting metadata, selector display names, cached projections and accounting cache keys; intern accounts have no accounting data and `AccountId` remains authoritative. Inspect null-handling contracts and preserve behavior, with focused tests and cache documentation if changed.
- Web's Razor markup reads `userImportBusy`, so the field must not be deleted; adjust its representation if it resolves the compiler warning without affecting UI behavior. Database.Tests has one nullable dereference in an assertion, and BenchmarkSuite1 has two nullable-annotation-context warnings.
- Split the remaining work into non-application warning cleanup, application/account-cache warning cleanup, and final connector validation because they touch independent concerns and have separate validation boundaries.
