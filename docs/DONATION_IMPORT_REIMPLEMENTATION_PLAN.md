# Donation import and intern-account reimplementation plan

## Status and purpose

Frequency completion follow-up: `DONATION_FREQUENCY.md` now specifies the agreed chronological gift-pattern rules and the executed completion plan. Standard publication captures deleted/incoming change boundaries, probes affected groups for a proven Monthly checkpoint with full twelve-month context, and replays bounded pages; only unresolved affected groups fall back to complete history. This supersedes this document's earlier requirement to preserve the old whole-history classification behavior. Synthetic validation does not replace operator-run MySQL/UI/rollback acceptance, and no publication retention savings have been measured.

Latest AccountId checkpoint (supersedes older pending account-mapping wording below): current configuration resolves unique normal designations and exact intern source IDs through invoker-security views, without modifying monetary rows on account CRUD. Donation reads enforce resolved AccountId and preserve primary/merged/separate Fund allocations; unknown intern IDs never fall back to normal Funds and ambiguous normal mappings are unavailable. Preview unresolved counts are informational and current-configuration based. Mapping changes clear/fence shared/overflow donation and derived donor caches. The views must be applied by the operator to an existing database; no runtime DDL or initialized-database changes occurred. Provider association/rollback acceptance remains user-run. This does not complete resource-budget or address-precedence work.

The standard donation upload processor and preview/confirmation paths now use normalized source-ID validation. Publication persists donation-linked evidence, rebuilds affected contacts and frequencies, and includes the new fields in full snapshots/rollback. This is not a production-ready completion of the full plan: intern account administration/read cutover and coordinated admission/cancellation remain incomplete. The separately approved preparatory cleanup removes the unused `Accounts.OtherFunds` column. Existing databases have not been altered. The user explicitly waived further CPU/baseline work; no performance improvement is claimed.

### Implementation checkpoint

- Added immutable normalized donation/evidence contracts under `src/Cya2.Core/Imports`. Source rows are transient; canonical anonymous rows suppress donor names and contacts while retaining the applicable source ID and the separate intern association.
- Registered `DonationWorkbookReader` in the standard preview flow and connected it to the standard donation processor, including the required `Tribute System Record ID` header, worksheet complexity limits, decimal money validation, opaque donor IDs, whole-file rejection, cancellation checks, and existing honor-name aliases. These limits do not replace coordinated admission or bound EPPlus decompression.
- Preserved the existing duplicate classifier and added intern-recipient isolation and source-ID compatibility checks. Contact-expanded duplicates retain distinct evidence without adding donation amounts.
- Added a pure surviving-evidence contact projector and the strict `>380` calendar-day/unbounded-above replacement policies. Projection tests cover deleted-source fallback, later blanks, source-ID isolation, and defensive anonymity/soft-credit suppression.
- Conflicting same-day address observations currently fail publication atomically rather than selecting an arbitrary row. This is a conservative temporary implementation, not a settled business precedence rule; user-facing ambiguity handling remains pending. Latest-nonblank email/phone sets are characterized synthetically, not verified against a real export.
- Standard Blazor confirmation shows canonical counts and the unbounded replacement boundary. Both queued UI and inline API acceptance enforce the strict >380-day acknowledgment against the owned retained preview, preserving the preview on rejection. A supplied client boundary is never used.
- DonationData and DonationDataBackup now include InternSourceId and SourceEvidenceJson in the initial schema only. Global name reconciliation is no longer invoked on standard publication; affected frequencies use final surviving history and batched changed updates. Anonymous SQL projections independently suppress donor labels. The queue rechecks the actor's current Admin role before processing. Enqueue-time broad cache invalidation was removed.
- Validation checkpoint: Database.Tests non-Docker tests 74 passed; Application.Tests 229 passed; Web.Tests 77 passed. An explicitly enabled generated Docker MySQL test passed for evidence publication, deleted-source fallback, and snapshot rollback. Older MySQL tests asserting name fallback/contact replacement have not been rewritten or revalidated as a suite.
- No real-database imports, runtime DDL, or initialized-database alignment has been performed. Do not deploy this intermediate state: intern administration/read/cache source-ID cutover, AccountId mapping, total admission/cancellation, legacy cleanup, timestamp/name projection refinements, and full acceptance remain pending. Existing initialized databases require separate operator-approved alignment and source reload; existing donor projections cannot reconstruct missing historical evidence.

### Intern administration follow-up

Admin now offers Primary/Sub/Intern management with name-and-source-ID-only intern CRUD. Interns are non-login accounts; ordinary account-access grants/revocations apply. Account kind, opaque source ID, and display name are explicit metadata. Single/bulk donation reads and cached account contexts carry source IDs separately from labels. Home donation-only presentation follows the selected account, and Expenses displays an informational no-data message for intern selections. Account deletion touches only configuration/access records, never monetary transactions or source evidence. No legacy Intern-user migration is included, as instructed.

This supersedes the earlier checkpoint's pending intern UI/read cutover wording; broader import admission/cancellation and full acceptance remain separate unfinished work. Docker/lifecycle/rollback simulations were interrupted and are now explicitly left to the user. Do not retry Docker during this implementation. Existing initialized databases remain unchanged and require operator-approved initial-schema alignment before UI testing.

This plan records the requirements agreed during the upload review. It replaces the earlier proposal to preserve global name-based donor reconciliation. It also supersedes earlier suggestions for a special historical-load mode, rejection of unknown account mappings, and indefinite retention of contact values after their source donation is removed.

Existing documentation describes today's implementation. Do not treat the ID-based intern and donor design below as already implemented. Update the applicable repository instructions and behavior documentation when the corresponding code changes land, not in advance of working behavior.

## 1. Agreed scope

### Included

- One standard donation upload process for rolling approximately one-year exports and historical uploads of the same size.
- Authoritative source donor IDs, with existing soft-credit detection retained.
- ID-based non-login intern accounts and normal account-access administration.
- Donation-linked contact evidence and rebuildable current-state donor/contact projections.
- Bounded validation, preview retention, queue admission, staging, and transactional publication.
- Affected-history frequency calculation without changing classification rules.
- Backup/rollback, required auditing, progress outcomes, cache coherence, documentation, and synthetic acceptance tests.

### Not included

- A special initial-load procedure, append-only imports, or an upsert-only replacement contract.
- Donor ID merge/change handling, name-based identity discovery, or guessed account creation.
- New frequency business rules, arbitrary last-N-gift classification, or narrower backups without equivalence proof.
- Durable distributed job infrastructure, multiple active application processes, automatic publication replay, local-infile loading, or external audit alerts.
- Rewriting the AccountingData parser/repository or the separate account-definition import service. Shared queue, progress, cache, account-model, and rollback changes must preserve accounting behavior, including the recently corrected Cash Basis footer rule.

## 2. Non-negotiable business contract

| Area | Required behavior |
| --- | --- |
| Replacement | Find the minimum accepted donation calendar date anywhere in the workbook. Delete every existing donation on or after that date, with no upper boundary. Preserve earlier donations. Do not restrict deletion to the file's maximum date or to donors/funds appearing in the file. |
| Validation | Reject empty/header-only files and reject the entire upload if any populated row is invalid. Unknown intern IDs and unmapped funds are not validation failures. |
| Old-date warning | Require additional confirmation if the minimum date is earlier than today minus 380 calendar days. Exactly 380 days does not trigger it. Always show the replacement boundary. Use one injected clock and the application's agreed calendar/time-zone convention consistently. |
| Donor identity | Equal direct constituent and soft-credit recipient IDs identify the same donor. Preserve IDs as opaque text, including leading zeros and case-sensitive distinctions. No name fallback. |
| Applicable donor ID | Use the direct constituent ID or recipient ID according to the existing soft-credit detection. Reject non-anonymous rows lacking the applicable ID. Never substitute an intermediary's ID for a missing recipient ID. |
| Soft credits | Preserve the current name-based detection of third-party soft credits; only donor identity resolution becomes ID-only. Preserve existing duplicate/no-double-counting behavior. |
| Anonymity | Accept anonymous gifts with or without IDs. Retain the applicable donor ID when supplied. Do not persist donor names/contact values from anonymous rows; independently suppress identifying information in UI, exports, and shared projections. |
| Intern identity | Require the Tribute System Record ID header; allow blank cells. Nonblank values are recipient identities, unrelated to donor constituent IDs. Names are display information only. |
| Accounts | AccountId is authoritative for application account relationships. Accept unresolved source designations, but never create accounts during import or expose unmatched data through an unrelated account. |
| Contacts | Keep multiple eligible email/phone values from contact-expanded duplicate rows without duplicating gifts. Use the most recent eligible address as a coherent address. Blanks are no observation, not an instruction to erase all older evidence. |
| Contact correction | After replacement, derive current contacts from surviving eligible direct-donation evidence. Deleted donations stop contributing. Fall back to the newest remaining evidence; remove projected values with no remaining basis. Soft-credit rows do not supply intermediary contacts. |
| Intern access | Interns are non-login accounts. Admin grants authenticated users access through the normal account-access mechanism. Creating a matching account makes already-imported data available through the source intern ID. |
| Frequency | Classify gifts in historical context using the existing rules, including retrospective changes. Include effects of removed gifts, not just incoming gifts. |
| Publication | Validation, authorization, required audit persistence, and all-or-nothing publication cannot be weakened for speed. |

## 3. Current implementation touchpoints

Verify paths and call sites again at the start of each implementation phase; line numbers are intentionally not used as durable task identifiers.

- Browser/confirmation: src/Cya2.Web/Components/Pages/Admin.razor; Components/Shared/UploadConfirmationDialog.razor and UploadProgressDialog.razor.
- Application orchestration: src/Cya2.Application/Services/ImportOrchestrationService.cs, ImportUploadValidator.cs, ImportPreviewStore.cs, ImportUploadOptions.cs; import interfaces/results.
- Background lifecycle: src/Cya2.Web/Services/Imports/ImportWorkQueue.cs, ImportProgressService.cs, ImportRetentionCleanupService.cs; ImportProgressController and UploadController.
- Parsing/consolidation: src/Cya2.Infrastructure/Services/DonationImportProcessor.cs; src/Cya2.Core/Services/DonationDuplicateClassifier.cs and DonorIdentityResolver.cs.
- Persistence: src/Cya2.Infrastructure/Repositories/DonationRepository.cs and DonationRepository.Identity.cs; source DTOs and canonical write models.
- Frequency: src/Cya2.Core/Services/DonorFrequencyService.cs.
- Safety: FinancialWriteCoordinator.cs, TransactionalAudit.cs, ImportTransaction.cs, ImportCompletion.cs, RollbackExecutor.cs, and rollback repositories/orchestration.
- Intern administration: Account.cs, User.cs, AdminUserDtos.cs, UserManagementService.cs, UserAdministrationStore.cs, InternAccountUtility.cs, and Admin.razor.
- Read/access paths: DonationReadRepository.cs, AccountFactDataService.cs, DonationService.cs, DonorService.cs, user/account authorization and context, selectors, summaries, exports, overview, and snapshot warmups.
- Schema: database/schema/001_InitialSchema.sql only.
- Existing safety tests: tests/Cya2.Database.Tests/ImportValidationTests.cs and DatabaseTests.cs; Application/Web upload, authorization, preview, queue/progress, audit, cache, and rollback tests.

## 4. Target architecture

### 4.1 Domain and schema boundaries

Define explicit normalized import models instead of passing an EPPlus worksheet into publication logic. Keep donor identity, recipient identity, account mapping, donation allocation, contact evidence, and display labels separate.

Proposed schema responsibilities (final names follow repository conventions):

1. Accounts have an explicit account kind, an intern source ID for intern accounts, and an independent display name. Enforce unique nonblank intern IDs without requiring unique intern names. Do not depend on an Intern: prefix, an email, or an INT-<user id> account number to establish intern identity. Adapt normal-account validation and accounting-only field constraints so intern accounts require no fabricated accounting identity.
2. DonationData retains the direct/recipient source donor IDs and the source intern ID, plus explicit applicable donor identity and nullable resolved account relationships as needed. Unmapped source rows must remain valid and queryable for later association. Keep original fund/allocation distinctions for primary, merged, and separate views; account mapping must not erase the allocation.
3. Donors remain shared, ID-keyed display projections across funds. Anonymous persistence must not populate named/contact projection fields. Design anonymous projection/read isolation explicitly so a join to a legitimately named donor with the same source ID cannot reveal an anonymous gift's identity.
4. Anonymous gifts without IDs use a clearly internal unidentified representation, never a fabricated source constituent ID. This is not evidence that separate unidentified gifts came from the same person. Do not group their frequency histories as one donor or merge unrelated gifts through that sentinel.
5. Store eligible source contact observations linked to each canonical donation, including the multi-value sets collected from contact-expanded rows and coherent address observations. These observations exist only with their surviving donation, not as an append-only contact-change history. Donors and DonorContacts remain current-state projections.
6. Add corresponding snapshot/rollback coverage for every new source-evidence field/table. Complete backups remain the initial safety contract.
7. Candidate indexes cover source donor lookup, donor/fund/date histories, source intern ID/date, account/date, and contact evidence by donor/donation/date. Select final indexes using disposable-MySQL plans and measurements rather than adding all candidates automatically.

All schema work belongs in 001_InitialSchema.sql. No incremental migrations, schema-history entries, runtime DDL conversion, or automatic populated-database reset. Plan existing development-data alignment explicitly; the original donor-only projections cannot reconstruct all older contact observations. Reloading source workbooks may be necessary and requires separate operator approval.

### 4.2 Contact projection algorithm

- Sanitize anonymous and ineligible third-party contact data before persistence/staging parameters. Retain only eligible direct-donation observations.
- Consolidate contact-expanded rows using the existing gift/allocation rules; collect their distinct email/phone values before choosing projections.
- For each affected donor, use retained pre-boundary evidence plus incoming replacement evidence. Do not use deleted live rows or an old projection as evidence.
- Address selection uses the newest surviving nonblank eligible address observation as a whole; do not manufacture an address by mixing components from different gifts.
- Email and phone projection uses the newest surviving nonblank observation set for each source contact category, retaining all distinct values in that set, including values from contact-expanded duplicate rows. Newer blank fields fall back to earlier evidence. Do not blindly union all historical phone/email values forever.
- The preceding email/phone selection is the proposed precise interpretation of current-state multi-value contacts. Verify it with synthetic examples before coding the projector; escalate if it does not match the actual export representation.
- Source gift dates do not order conflicting observations within the same date. Characterize existing export ordering; define a deterministic, tested tie rule and surface true ambiguity rather than asserting an arbitrary row is chronologically newer. Do not add a silent new contact precedence rule.
- Preserve DateCreated for retained normalized projected values and DateModified semantics for actual stored-value changes; unchanged values must not churn timestamps. Rebuilding projections twice over identical evidence must be idempotent.
- Rebuild donors affected only by deletions too. If no eligible contact evidence survives, clear those projected contacts even if soft-credit gifts remain. Soft-credit-only uploads do not overwrite surviving verified direct evidence.
- Derive display names by ID from appropriate non-anonymous source evidence, with deterministic precedence; anonymous rows must never contribute names.

### 4.3 Duplicate behavior and intern isolation

Freeze characterization of the existing duplicate key: gift import ID, fund allocation, date, rounded amount, payment type, gift type, resolved credited donor, and direct/soft source-ID compatibility checks. A missing gift ID must not suddenly make unrelated rows mergeable.

Preserve existing behavior except for the required ID identity substitution and the necessary recipient-isolation guard: two allocations to different Tribute System Record IDs must not collapse merely because the existing key lacks the new recipient dimension. Add a failing characterization example before the smallest required key extension. Do not replace current split logic with gift-ID-only deduplication.

### 4.4 Bounded upload, validation, and confirmation

1. Authorize the current administrator before accepting upload work.
2. Enforce configured compressed size, concurrent validation, expanded-workbook complexity, and total retained/in-flight resource limits. Charge preview, admission waiters, queued work, parsed/evidence data, and active work to one coordinated budget.
3. Initially keep the configured normal-year limit unless synthetic sizing demonstrates a change is required. Do not add a special historical pathway. Evaluate protected temporary storage only if measured memory/size justifies it; never log workbook contents or use unbounded retention.
4. Parse/validate the entire workbook and produce an immutable bounded result/manifest. Dispose EPPlus before database publication. Bind preview identity, import kind, owner, validated boundary, and any old-date acknowledgment to the actual retained payload; do not trust a client-supplied boundary.
5. Show canonical gift count, dates, duplicates consolidated, unresolved mapping counts, and the unbounded-above deletion rule. Unknown mappings are informational, not fatal.
6. Require the >380-day acknowledgment on every applicable entry point, including API confirmation. Reevaluate the clock rule consistently at acceptance. Cancellation/discard releases preview capacity promptly; expiry is checked on access, not only periodic cleanup.
7. Reserve queue capacity without losing the owned preview or accumulating unbounded waiting byte arrays. Save required Started evidence before actual import processing/enqueue. Preserve a stable operation ID throughout. Do not silently retry a required audit-start failure.
8. Revalidate administrator permission for queued work before mutation, particularly after waiting. Closing a dialog is not cancellation; cancellation must be an explicit owner-authorized operation.

### 4.5 Stage and publish

- Prepare connection-local temporary staging in bounded parameterized batches (including donor staging). Budget by command bytes as well as row count. Never fall back to partial live inserts or local-infile loading.
- Preserve the process financial-write gate and MySQL advisory publication lock. Move only proven database-independent parsing outside the gate and only after total admission is bounded. Keep final database-dependent reads/derivations consistent under write coordination.
- Look up existing donors by the staged source IDs using joins; upsert only the relevant donor projections. Eliminate global name evidence reads, historical name promotion/splitting, and per-retained-gift relinking/contact-copy commands.
- Under coordination, collect affected donor IDs and donor/fund groups from ALL existing rows at/after the replacement boundary plus staged incoming rows. Include groups/donors disappearing completely from the file.
- Map standard designations through configured AccountId relationships; map intern rows by source intern ID only. Never assign an intern row through a coincidentally shared normal fund. Leave unmatched data unassociated and inaccessible to unrelated accounts.
- Begin one persistent InnoDB transaction; create complete snapshot metadata/payload, delete ALL live donations/evidence at/after the boundary, publish staged donations/evidence, and rebuild affected projections/frequencies.
- Verify canonical insertion counts, evidence ownership, account isolation, and referential integrity; persist required success audit in that same transaction; then commit.
- Contact evidence is replaced/deleted atomically with its donation. Projection removal, earlier-contact fallback, donation frequency changes, and backups roll back together on pre-commit failure.
- No names, donor/intern source IDs, contact values, Fund/SubFund values, or raw database exception text in general diagnostics or restricted audit payloads. Restricted audit retains only the existing allowed operation metadata, numeric user references, counts, and fixed labels.

### 4.6 Chronological frequency patterns and targeted replay

The agreed follow-up replaces donor-wide historical relabeling with gift-level chronological patterns. See `DONATION_FREQUENCY.md` for the precise rules, checkpoint proof, API occurrence contract, validation scope, and approved Mixed label when selected funds' frequencies differ.

1. Classify independently per donor/fund and preserve distinct same-date/split gifts. A new regular interval can revise its preceding boundary, not the whole history.
2. Capture earliest changes from deleted and incoming rows before replacement. Replay final surviving data so corrections/deletions can invalidate prior patterns.
3. Probe unchanged prefix windows of 4, 13, and 26 months for a consecutive-month Monthly checkpoint. Replay with its full twelve-month amount context; do not trust a stored frequency as an initial-state certificate.
4. Fall back to the complete history only for affected groups without a proven checkpoint. Read 500-row keyset pages, process group descriptors in pages of 64, and stage changed updates in batches of at most 500.
5. Use one rolling sequence per group, carrying a pending gift across pages for boundary revision. Its twelve-month gift queue is time-bounded, not fixed-row-bounded; no fixed RAM budget or measured publication retention saving is claimed.
6. Synthetic regressions compare targeted suffix replay to full replay, including replacements/deletions and page boundaries. Provider transaction/cancellation/rollback, query-plan, and UI acceptance remain operator-run.

### 4.7 Non-login intern administration and reads

- Introduce intern-account creation/edit UI with source intern ID and display name, no email/Google identity, and no new Users row for the intern.
- Reuse AccountsUsers for access: one authenticated user can access multiple interns and one intern can have multiple authorized users, subject to existing Admin/Viewer rules. No custom supervisor-role authorization shortcut.
- Retain current Admin revalidation, transactional administrative mutations/required audit, and last-admin/self-lockout safeguards. Do not repurpose login role strings to identify the recipient account.
- On matching account creation, associate existing unresolved source-ID rows through a set-based mapping operation in the account-management workflow, or an equally explicit ID-based association design. Never require re-upload or direct SQL edits to make previously imported data appear.
- Concurrent import/account creation must serialize mapping-sensitive writes or revalidate mappings so rows cannot remain incorrectly unresolved. Handle account deletion/unlink/recreation without deleting source donations or broadening access.
- Change all single/bulk intern reads, selectors, exports, donor selection, missing-gift views, summaries, caches, overview, and warmups to source intern ID through AccountId. No name/Fund fallback after cutover.
- Keep '<intern name> (Intern)' display and stable ID-based selections; duplicate names and typo corrections must not move data.
- Preserve no accounting/expense data or accounting caches for intern accounts. Keep primary/merged/separate account semantics and global donor deduplication in All views.
- Plan existing legacy Intern-user/account transition explicitly before enabling the new path. Do not silently delete authenticated users, access grants, or accounts; never infer an intern source ID from a name.

### 4.8 Outcomes, rollback, caches, and availability

Implementation checkpoint: duplicate full-invalidation scheduling removed; same-generation concurrent warmups coalesced while preserving later-generation/summary-upgrade follow-ups. Background admission is immediate and bounds active-plus-pending workbook count/bytes; preview claims preserve capacity and retryability. Validation admission now covers semantic parsing. Owned preview expiry/discard and explicit owner-authorized cancellation are wired into Admin. Live donation validation uses the shared reader and the unused private legacy parser is removed. Progress reads now return immutable snapshots captured under the same per-import lock as updates, and retention cleanup keeps active progress. Separate progress updates can still be observed between calls; provider cancellation/late-commit/rollback scenarios and same-date address precedence remain acceptance items. Do not treat this checkpoint as completion of those gates.

- Explicit states: queued, validating/preparing, staging, publishing, committing, committed, rejected, cancelled-before-commit, and unknown/reconciliation-required. Cache readiness is distinct from financial publication.
- Use synchronized immutable progress snapshots and non-overlapping/throttled polling or notifications. Preserve owner checks and current authorization. Publish staged counts separately from committed counts; prevent active jobs from expiring as abandoned previews.
- Honor cancellation before commit; once commit starts, preserve non-cancellable acknowledgment and independent marker verification. A failed verification is unknown, not proof of rollback. No automatic replay after unknown outcomes or notification failures.
- Keep the existing required audit-start/success policy, sanitized monitoring events, restart reconciliation, 90-day audit retention, and supported single-active-process assumption. Queue storage remains volatile unless separately approved otherwise.
- Extend complete backup/rollback to source intern IDs, source contact evidence, all donor/contact projections, and modified historical frequencies. Preserve pin-aware retention and verify new snapshot payload counts. Do not restore identifying evidence for anonymous rows.
- Invalidate Donations and DonorContacts only after committed publication, or conservatively on unknown outcome. Rejected/confirmed pre-commit failures do not bump generations. Remove enqueue-time full invalidation and redundant warmup scheduling as part of this phase.
- Intern account creation/mapping/access changes invalidate the appropriate metadata/authorization and affected financial generations, fencing in-flight loads and refreshing selectors immediately. Test the auto-association case with warm caches.
- FinancialSummary DTOs and Accounts Overview must rebuild automatically using their existing range contracts; broad fact/page DTO caches need not all exist first. Retain broad snapshot coverage reuse for narrower UI ranges. Revalidate authorization before financial access even on cache hits.
- Preserve DatabaseStartupInitializer, IDatabaseStartupProbe, database guards, monitor recovery, and limited-mode behavior. New schema/queue/read services must not make startup fail when MySQL is unavailable.

## 5. Implementation phases and gates

| Phase | Deliverable | Exit gate |
| --- | --- | --- |
| 0. Characterize and measure | Synthetic duplicate/contact/ID/anonymity fixtures; isolated performance baseline exercising real current import paths. | Existing safeguards characterized; observed time/allocation/command metrics recorded before performance edits. No real database/configuration used. |
| 1. Schema and core contracts | Initial-schema changes, normalized import/evidence models, account-kind/intern-ID contracts, source-ID donor rules, backup shapes. | Fresh disposable schema and restricted runtime grants pass; no incremental migration/history; explicit development-data alignment checklist. |
| 2. Pure normalization/projectors | ID selection, anonymity sanitization, existing duplicate consolidation plus recipient guard, coherent address/multi-contact selection. | Synthetic tests prove sums, identities, fallback, blank behavior, and no retained anonymous PII. Same-date precedence is explicit. |
| 3. Atomic repository | ID joins, bounded stages, affected donors/groups, evidence replacement, current projections, batched frequency writes, extended rollback/audit. | Disposable-MySQL success/fault/cancellation/rollback tests pass; no global identity scan or per-gift update loop. |
| 4. Intern account lifecycle | Non-login intern creation, standard access grants, source-ID association of existing data, read/selector/cache cutover. | UI-driven creation/access/revocation works without SQL edits; no accounting or cross-intern leakage; legacy-data transition verified. |
| 5. Blazor orchestration | Semantic preview, >380-day confirmation, admission accounting, queue authorization/cancellation, truthful progress. | Browser/API enforce same contract; overload and expiry bounded; publication and refresh warnings remain distinct. |
| 6. Cache/recovery integration | Post-publication domain invalidation, one effective rebuild request, rollback/unknown paths, startup outage recovery. | Warm-cache views show replacement/contact fallback/new intern associations; injected outages do not crash startup. |
| 7. Acceptance and documentation | Full regression runs, same-workload before/after comparison, synthetic browser acceptance, runbook updates. | Gates below met; measured gains/regressions reported; no claim that unit tests prove deployment/provider behavior. |

Phases may be split into small buildable commits. Keep ID-based intern schema, writer, readers, authorization, and cache cutover coordinated; do not deploy a mixed ID-writer/name-reader state. Do not silently leave old production paths as fallbacks. Any scope deviation must be stated and explained before implementation.

## 6. Measurement and validation strategy

### Baseline safety

- Use synthetic workbooks and fake persistence for parser/projector measurements; use disposable loopback-only MySQL for actual repository/transaction measurements as documented in DATABASE_TESTS.md.
- Never load application user secrets, normal connection strings, or real donor files. Do not start the runnable application against the user's database to establish a baseline.
- Reuse suitable existing measurement artifacts; discover benchmarks before generating new .NET ones. New benchmarks must call the actual production path. If repository profiling needs a unit-test target, use an explicit disposable-database fixture with a fully qualified test name.
- Freeze successful performance workloads/setup; remeasure the same target with the same collection type and synthetic distribution. Requirements-changing regression tests are separate from frozen performance artifacts.
- Record parse/validation time, allocations/peak memory where available, retained payload/evidence bytes, queue wait, command count, donor/contact/frequency time, staging time, transaction/backup/commit time, and cache-ready time. Do not compare instrumentation wall time to uninstrumented benchmark time as a speedup.
- Vary retained history independently of incoming size, source-ID donor count, contact-expanded duplicates, intern distribution, and affected-group count. Include a normal-year upload and maximum permitted upload. Do not invent percentage improvement targets before the baseline.

### Required regression matrix

| Area | Minimum cases |
| --- | --- |
| IDs | Equal direct/recipient IDs share identity across funds; distinct IDs stay distinct; leading zeros; missing applicable ID rejects; identical names never merge IDs; soft-credit detection unchanged. |
| Anonymity | With/without IDs; multiple anonymous IDs; unrelated unidentified gifts; duplicate contact-expanded anonymous rows; a source ID also present on a named gift; no PII in live/stage/evidence/backup payloads; hostile identifying DB values still suppressed in UI/export. |
| Replacement | Unordered dates; min-date boundary including calendar-time normalization; deletion beyond maximum incoming date; donors/funds absent from file deleted in window; preserved earlier gifts; empty/malformed upload unchanged. |
| Confirmation | 379, 380, and 381 days; leap/calendar boundaries; intentional historical upload uses same flow; canceled/stale/tampered confirmation; API cannot bypass acknowledgment. |
| Duplicates | Contact-expanded rows keep all eligible emails/phones with one gift amount; legitimate fund splits; absent gift ID; existing compatibility checks; distinct intern recipients cannot collapse. |
| Contacts | Latest coherent address; multiple values in latest nonblank category set; blanks fall back; deletion restores earlier address/contact sets; no surviving evidence clears projection; no intermediary data; same-date precedence; unchanged timestamps; exact/casing-only updates; idempotent re-upload. |
| Interns | Required header/blank non-intern rows; unknown IDs accepted; later account creation reveals existing data; duplicate names with different IDs; display typo correction; multiple supervisors/accounts; revoked access and unauthorized export; unmatched data not exposed through shared Fund; no intern accounting reads. |
| Frequency | Unchanged classifier equivalence; removed-only groups; yearly-to-monthly retrospective change; no changes to unaffected histories; full affected-history fallback when a narrower window is unproven. |
| Transactions | Failure after backup/delete/evidence/contact/frequency/audit operations preserves prior state; staging never publishes; inserted-count mismatch rejects; rollback restores all new evidence/projections/IDs/frequencies and snapshot pin state. |
| Concurrency/recovery | Admission saturation; waiting/queued cancellation; role revoked while queued; concurrent account creation/import; shutdown with queued work; commit acknowledgment lost; unavailable verification DB; no automatic replay; progress/cache failure after commit. |
| Cache/UI | Warm Donations/Donors/Home/Overview/export views after corrections; primary/merged/separate semantics; All-view donor deduplication; new intern auto-association; selector/access refresh; broad snapshot reuse for narrow dates. |
| Availability/privacy | DB unavailable at startup then recovered; schema mismatch fails safely; sanitized logs/audit; audit failure events remain monitoring-only; no external alert delivery. |
| Accounting regression | Existing accounting validation/footer, transactional replacement, audit, rollback, shared write coordination, and post-publication invalidation remain valid. |

Build changed projects during implementation and run the complete solution build at completion. Run focused tests first, then Application/Web suites and explicitly enabled disposable-MySQL integration tests. Record exit codes and skipped tests; a skipped database suite is not acceptance. Process-kill, lost-acknowledgment, browser, and target-provider acceptance must be exercised separately from ordinary unit tests.

## 7. Documentation and cutover checklist

Update in the same implementation phases as behavior:

- .github/copilot-instructions.md: ID-based intern lookup, non-login lifecycle, and current-state projection/source-evidence distinction; do not retain obsolete name-lookup or blank-clears-contact guidance.
- docs/IMPORT_SAFETY.md: strict IDs, anonymous sanitization, standard-year replacement, confirmation, contact fallback, affected-history frequency, snapshot/evidence publication.
- docs/CACHE_ARCHITECTURE.md and src/Cya2.Web/docs/CACHE_ARCHITECTURE.md: ID-based keys/scopes, mapping invalidation, post-publication notification/rebuild behavior.
- src/Cya2.Web/docs/INTERN_ACCOUNT_BEHAVIOR.md and related source comments: source-ID recipients, ordinary account access, no login/email requirement, no accounting data.
- docs/AUDIT_OPERATIONS.md, docs/DATABASE_TESTS.md, and docs/PRODUCTION_REVIEW.md: new acceptance evidence and remaining limits without claiming unexecuted tests passed.
- Any CSS/UI changes must follow src/Cya2.Web/docs/CSS-STYLE-GUIDE.md and RADZEN_OVERIDES.md.

Before deployment, explicitly align the evolving initial schema in an approved nonproduction environment, provision actual intern source IDs through the new UI, and approve how existing legacy intern accounts/users and incomplete source evidence are handled. Do not infer IDs, modify the user's database, reset accounts, or reload data as an incidental implementation step. Annual historical files use the exact same standard replacement workflow; because deletion is unbounded above, replaying an older year after a newer year removes that newer data. Document the required chronological replay order without adding a separate upload mode.

Completion requires correctness/rollback/privacy/access acceptance, a measured before/after report with any regressions, and no unrelated worktree changes. A planning document is not authorization to run imports against the real database.
