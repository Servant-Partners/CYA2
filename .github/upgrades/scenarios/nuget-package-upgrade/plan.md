# MySql.Data Package Upgrade Plan

## Overview

**Target**: Upgrade MySql.Data 9.7.0 to 26.10.0.
**Scope**: Two directly referencing projects and solution-wide validation.

## Tasks

### 01-upgrade-mysql-connector: Upgrade and validate the MySQL connector

Update the per-project MySql.Data references in `src/Cya2.Infrastructure/Cya2.Infrastructure.csproj` and `tools/Cya2.DatabaseMigrator/Cya2.DatabaseMigrator.csproj` to the unified 26.10.0 version. The quick [API diff](apidiff/MySql.Data.apidiff.md) detected no source-breaking API changes; address any actual compiler or test failures without changing unrelated behavior.

Preserve the startup probe and limited-mode recovery. Validate the solution and tests, including opt-in disposable MySQL integration tests and safe startup outage/recovery checks. Never apply schema or data changes outside disposable tests; document production TLS/server and browser verification limitations.

The user approved expanding the connector-only scope to resolve the 15 pre-existing solution warnings across the migrator, application, web, database tests, and benchmarks. Preserve supported behavior and update cache architecture documentation if caching behavior changes.

**Done when**: Both references use 26.10.0, applicable builds and tests pass without warnings, outage and recovery behavior is verified safely, and any remaining external compatibility or verification limitations are reported.
