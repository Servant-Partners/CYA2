# Support warning cleanup

- Removed unused pre-production migration-history local functions and their exclusive constants/record; active empty-database initial-schema execution, lock and statement splitter are unchanged.
- Preserved the Razor user-import busy state as an auto-property, matching the neighboring account-import state.
- Removed the nullable-context warning from the benchmark's synthetic non-null actor implementation and used a null-checked single-entry assertion in the database tests.
- Clean solution build passed with 9 warnings, all in Application and assigned to the next task; the six warnings in touched support projects are gone.
- Web tests: 160 passed. Opt-in Release disposable MySQL/database tests: 120 passed, zero skipped; environment restored and no matching disposable containers remained.
- MySql.Data 26.10.0 project changes from the parent task are present; no external database or real data was used.
