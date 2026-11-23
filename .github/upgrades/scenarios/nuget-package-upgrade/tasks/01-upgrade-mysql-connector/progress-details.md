# MySql.Data upgrade progress

## Changes

- Updated `src/Cya2.Infrastructure/Cya2.Infrastructure.csproj` and `tools/Cya2.DatabaseMigrator/Cya2.DatabaseMigrator.csproj` from MySql.Data 9.7.0 to 26.10.0; `dotnet list package` confirms both resolve 26.10.0.
- No production source, schema, external database, server version, or PR was modified. The changes remain uncommitted on `nuget-package-upgrade`.

## Validation

- Visual Studio solution build succeeded. Clean `dotnet build Cya2.sln --no-incremental` succeeded with 15 warnings and zero errors. An isolated archive of the pre-upgrade commit produced the identical set of 15 warnings, including two CS8321 warnings in DatabaseMigrator, nine nullable warnings in Application, one nullable warning in Database.Tests, one unused-field warning in Web, and two nullable-context warnings in BenchmarkSuite1.
- Application tests: 292 passed, zero failed/skipped. Web tests: 160 passed, zero failed/skipped.
- Opt-in Release database tests with `CYA2_RUN_MYSQL_TESTS=1`: 120 passed, zero failed/skipped, exit 0. The fixture used disposable MySQL 8.0.40, generated credentials, loopback and SSL disabled. The environment was restored and no `cya2-tests-` containers remained.
- Launched the actual app using synthetic loopback-only configuration: startup while its database port was closed returned liveness 200 and `/api/login` redirected to `/database-error`; enabling a synthetic TCP listener caused recovery and `/api/login` redirected to `/auth-config-required` with placeholder OAuth; closing it restored the `/database-error` redirect. This verifies TCP monitor transitions, not a MySQL handshake or usable database. An initial high-frequency probe hit HTTP 429; the corrected run avoided rate limiting and passed.
- Oracle release-notes and Connector/NET compatibility pages returned HTTP 403, so vendor release and server-compatibility claims were not independently verified. Production MySQL/Azure server, TLS, credentials, actual database reconnect, and browser UI verification remain untested.

## Blocker

The upgrade workflow requires a warning-free final build, but all 15 warnings predate this change. Fixing them would extend the agreed connector-only scope into unrelated application cache/account handling, web UI, tests, benchmarks, and unused migrator functions. User direction is required before that expansion; no warnings were suppressed and no unrelated source was changed.
