# MySql.Data Package Upgrade

## Preferences
- **Flow Mode**: Automatic
- **Scope**: `src/Cya2.Infrastructure/Cya2.Infrastructure.csproj` and `tools/Cya2.DatabaseMigrator/Cya2.DatabaseMigrator.csproj`.
- **Package**: MySql.Data 26.10.0.
- **Version reconciliation**: Unified 26.10.0; no project exclusions.
- **Package management**: Per-project references; no central package management found.

## Source Control
- **Source Branch**: main
- **Working Branch**: nuget-package-upgrade
- **Commit Strategy**: After Each Task
- **Branch Sync**: Auto (Merge)
- **Last Sync Commit**: 18cfc97

## User Preferences
### Technical Preferences
- Do not merge PR #11 or upgrade the MySQL server.
- Do not touch real monetary data or apply schema or data changes outside disposable tests.
- Preserve the database startup probe and limited-mode behavior; validate startup outage and recovery.
- Run the opt-in disposable Docker MySQL integration tests according to `docs/DATABASE_TESTS.md`, cleaning the environment and only leftover `cya2-tests-` containers if needed.
- Document limitations for production TLS/server and browser verification, and report unverifiable Oracle compatibility information.
- Preserve unrelated project changes. Do not resume the unrelated .NET version scenario.
- The user approved expanding the connector-only plan to fix all 15 pre-existing solution build warnings before completing the upgrade; preserve behavior and document caching changes.

## Build Tool Decisions
- Both affected projects: SDK-style net10.0; use the IDE build or dotnet build with restore.
