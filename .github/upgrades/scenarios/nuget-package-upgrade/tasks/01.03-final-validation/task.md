# 01.03-final-validation: Validate connector upgrade and outage recovery

## Objective
Revalidate the MySql.Data 26.10.0 upgrade and all approved warning fixes end to end.

## Scope
Build the solution without warnings and run application, web, and opt-in Release disposable Docker MySQL tests. Verify startup outage and TCP recovery safely with synthetic loopback configuration. Document Oracle page, production server/TLS, actual reconnect and browser limitations. Never use real monetary data.

## Done when
The solution builds warning-free, all applicable tests pass, outage/recovery checks pass, and limitations are recorded.

## Research and validation approach
- SDK-style net10.0 solution: `dotnet build Cya2.sln --no-incremental` exercises all projects with restore. The prior task's clean build passed with zero warnings and errors; Application (293) and Web (160) suites passed after the contract changes.
- `docs/DATABASE_TESTS.md` specifies opt-in discovery with `CYA2_RUN_MYSQL_TESTS=1`, Release configuration and Docker MySQL 8.0.40 on loopback with isolated, generated credentials. Restore the process environment and inspect only `cya2-tests-` containers afterwards. Rerun the whole suite including the new null-class regression.
- `DatabaseStartupInitializer` calls `MySqlDatabaseStartupProbe`, then `DatabaseMonitorService` polls the configured TCP host/port every five seconds. `/health/live` and `/api/login` expose the outage and recovery transition. Override both MySQL connection-string environment variables with a synthetic loopback port and placeholder OAuth values; a bound local TCP socket is not a MySQL server and cannot prove handshake/reconnect or TLS.
- Confirm both scoped project references and resolved versions. Oracle vendor documentation previously returned HTTP 403. Record that Oracle compatibility, production server/TLS, actual database reconnect and browser verification remain unverified. No real financial data or deployment actions are authorized.
