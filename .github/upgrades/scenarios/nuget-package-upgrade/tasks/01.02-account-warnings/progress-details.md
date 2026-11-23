# Account and cache warning cleanup

- Propagated the legitimately nullable accounting class through account projections, accounting repository interfaces, matcher inputs and cache keys. SQL and matching logic retain existing behavior; bulk lookup still excludes missing classes and associates rows by AccountId. No intern-source normalization or monetary data was changed.
- Preserved existing null-to-empty accounting cache key normalization and added a focused test for shared/overflow keys, balance hits and AccountId isolation. Updated `src/Cya2.Web/docs/CACHE_ARCHITECTURE.md`.
- Fixed remaining nullable display fallback warnings and aligned repository test doubles. Added a disposable MySQL test exercising null-class single/batch reads and separate AccountId results, populated through the import process.
- Clean `dotnet build Cya2.sln --no-incremental`: succeeded, 0 warnings, 0 errors. Application tests: 293 passed; Web tests: 160 passed. Focused opt-in Release disposable MySQL test: 1 passed. Full disposable database suite before the new regression test: 120 passed; full rerun is assigned to final validation.
- No real database, real monetary data, schema or server changes were made. Production TLS/server, actual MySQL recovery and browser verification remain final-validation limitations.
