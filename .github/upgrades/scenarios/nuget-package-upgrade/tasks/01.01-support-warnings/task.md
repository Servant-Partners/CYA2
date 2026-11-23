# 01.01-support-warnings: Resolve migrator, web, test and benchmark warnings

## Objective
Resolve the pre-existing migrator, web, database-test and benchmark warnings without changing supported behavior.

## Scope
- DatabaseMigrator unused legacy migration-history functions; preserve initial-schema-only behavior.
- Admin Razor busy state used by markup; preserve UI behavior.
- Database test nullable assertion and benchmark nullable annotation context.

## Validation
Build touched projects warning-free, run their relevant tests including disposable MySQL where appropriate, and record results. No real data or schema changes.

## Done when
The listed warnings are resolved without suppression and relevant tests pass.

## Research and Scope Inventory

- `tools/Cya2.DatabaseMigrator/Program.cs`: `LoadAppliedAsync`, `ValidateAppliedChecksums`, `AppliedMigration`, `historyTable`, and `baselineId` occur only in unused pre-production migration-history code. The running path initializes an empty database from `001_InitialSchema.sql` and uses an advisory lock; retain that path and the SQL splitter.
- `src/Cya2.Web/Components/Pages/Admin.razor`: the user-import markup referencing `userImportBusy` is currently Razor-commented out, while methods still assign the field. Use the same auto-property pattern already used for `accountImportBusy`; preserve all code and behavior.
- `tests/Cya2.Database.Tests/UserImportTests.cs`: assert entries are non-null, then use the value returned by `Assert.Single` to verify its email; keep the exact supported test behavior.
- `BenchmarkSuite1/DonationPreviewValidationBenchmarks.cs`: nullable context is disabled; the synthetic authorization method always returns a non-null actor. Express its return in the existing nullable-oblivious context without enabling a whole-project migration or suppressing warnings.
- Current 15-warning baseline includes only these six support warnings: two CS8321, one CS0414, one CS8602 and two CS8632. The remaining nine Application warnings are assigned to the next subtask.
