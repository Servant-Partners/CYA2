# Donation frequency: chronological patterns and publication

## Completion plan and scope

The completion sequence is: preserve the existing changes and synthetic benchmark; use the collected CPU baseline; finish chronological classification and occurrence identity; integrate affected-group targeted replay; add database-free regressions; build, run safe tests, and remeasure the same benchmark. The implementation does not start the configured application or database, initialize schema, run Docker, or perform rollback/provider simulations. Provider/UI acceptance remains operator-run.

This supersedes the older import-plan language about retaining the previous whole-history classifier unchanged. It does not authorize unrelated contact/evidence, cache, account, startup, authorization, or schema redesign.

## Classification contract

- Call the classifier separately for each donor identity and normalized fund. Sort gifts by date, with donation ID as the publication tie-breaker. Separate gifts, including identical date/amount occurrences, remain separate.
- No gifts: `None`. One gift: `OneTime`. Two gifts in the same calendar month or years apart: both `Sporadic`.
- A consecutive calendar-month pair establishes `Monthly`; a 2–4-month gap establishes `Quarterly`; an 11–13-month gap establishes `Yearly`. Other gaps are `Sporadic` unless an established Monthly pattern persists.
- A newly established regular interval can revise the immediately preceding boundary gift. It does not relabel an entire donor's history with the latest status. Four annual gifts followed by consecutive monthly giving leave the first three annual gifts Yearly; the fourth can become Monthly.
- Monthly persistence uses the existing date-relative trailing twelve-month window, inclusive at its start, and permits **at most 25%** missed months between the first and last occupied months in that window. Calendar-month amounts, not individual split-gift averages, determine catch-up/prepayment coverage. For each gap, use the larger coverage from the payment after or before it, capped at the gap. A consecutive-month pair can establish Monthly again after an earlier pattern fails.
- Replacements/deletions are replayed against final surviving data, not treated as append-only changes. Removing the establishing gift can restore an earlier Yearly pattern or leave one surviving gift OneTime.

`ClassifySequence` accepts chronological history and returns one frequency per occurrence. `FrequencySequence` is its incremental form: after `Add`, `Previous` finalizes the preceding gift and `Current` is the pending latest gift. Publication retains that pending row across database page boundaries. `ClassifyDonor` uses the same state machine and returns the last gift's current pattern.

`ClassifyGift(gift, history)` requires the actual occurrence in history by reference. A detached target is rejected, not silently added or matched by date/amount. Use `ClassifyGift(index, history)` when occurrence identity must be explicit. Repository classification uses one sequence pass, not this per-gift convenience API.

## Targeted publication algorithm

1. Before deleting the replacement range, capture affected identity/fund groups and their earliest changed date from **both deleted and incoming** rows. A group losing all its incoming gifts is still reconsidered.
2. Process affected group descriptors in keyset pages of 64. No new whole-database history query is used by standard publication.
3. Probe each group's unchanged prefix using 4-, 13-, then 26-month windows before its earliest changed date. Look for an adjacent-gift consecutive-month pair. Persisted frequency values are not trusted as initial state.
4. The second gift of that pair is a proven Monthly checkpoint: whatever the earlier state, adding that gift establishes Monthly. Replay from twelve months before that checkpoint, so its rolling amount context is complete. Suppress writes before the checkpoint. From the checkpoint onward the state and window agree with complete replay, including any revision of the last retained boundary gift by an incoming gift.
5. If no such checkpoint is found, replay **only that affected donor/fund** from its beginning. Annual, sporadic, unknown-initial-state, and removed-only groups safely take this unresolved-group fallback when necessary. A short recent slice is never assumed sufficient.
6. Read history in 500-row keyset pages ordered by `(Date, Id)`, closing each reader before staging changes on the same connection. Keep one pending row for boundary revision. Stage at most 500 changed frequencies per command; do not retain all changes in application memory. A joined final update occurs within the existing publication transaction.

Application-side buffering consists of the descriptor page, one history page, one change batch, one pending row, and **one group's trailing twelve-month minimal gift context** with at most thirteen calendar-month totals. The rolling gift queue is time-bounded, not fixed-row-bounded: an unusually dense donor/fund can still have many gifts in twelve months. This is not a fixed RAM budget. Temporary tables and provider/server execution have their own memory costs.

The input write-model/evidence array and separate current-contact evidence projection are unchanged. Whole-file validation, existing financial write/advisory locks, complete snapshots, evidence, count checks, audit, commit reconciliation, invalidation, and user-run rollback behavior are not replaced. The explicit administrative recategorization and legacy identity-finalization paths now calculate each group's sequence once, but retain their pre-existing broad load/update strategy; they are not the standard targeted publication path.

## Proof and synthetic validation

`DonationFrequencyRegressionTests` covers empty/single histories, interval boundaries, annual-to-monthly boundaries, detached target rejection, same-date/split gifts, early/late processing, exact missed-month tolerance, monthly-amount catch-up/prepayment, zero/negative totals, deletion/replacement, and descending-input rejection. Seeded generated histories compare checkpoint suffix replay with full replay using page sizes 1, 2, 7, and 500, including corrected/deleted suffixes. A separate example demonstrates why just the checkpoint pair without its twelve-month context is insufficient.

These tests validate the pure state machine and checkpoint equivalence, not execution of MySQL SQL/window functions. Operator acceptance must still exercise real imports with removed-only groups, same-date page boundaries, unknown prior frequencies, normal/intern isolation, cancellation, full snapshot rollback, existing summary/overview refresh, and default/selected/non-admin 4–5-account precaching. No database or UI acceptance is claimed here.

Older stored classifications outside the replay suffix are not globally migrated by an upload; the existing explicit recategorization operation remains available for an operator-approved rebuild.

## Multi-fund summary boundary

Persisted gifts are classified independently per donor/fund. For combined selections, including All, `DonorService` uses each selected fund's latest stored frequency, falling back to classification of that fund's selected history when absent. If all funds agree, show their shared frequency; otherwise show **Mixed** in the donor grid and export. `Mixed` is summary-only and is never assigned to a gift. Donors remain deduplicated and monetary totals remain combined. Monthly missing-gift checks remain fund-specific even for Mixed summaries, so a gift in another fund cannot cover a missed month; duplicate warning-month labels are collapsed. Cache keys, invalidation, authorization, and precaching schedules are unchanged. Database-free `DonorSelectionTests` cover stored/live frequencies, ranged/All Dates selections, shared/Mixed labels, totals, Primary isolation, and warning preservation.

## Measurements

See `MEMORY_USAGE_INVESTIGATION.md` for the frozen benchmark before/after CPU results and limitations. The measured workload is synthetic and database-free. It does not establish publication SQL latency, peak live heap, retained heap, working set, or a measured retention reduction.
