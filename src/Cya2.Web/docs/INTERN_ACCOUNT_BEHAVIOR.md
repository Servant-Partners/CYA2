# Intern Account Behavior

## Administration

Admin account management offers Primary, Sub, and Intern modes. Intern creation and editing require only a display name and the source `Tribute System Record ID`. No email, login user, accounting class, account number, or overhead entry is required. `AccountKind = 'Intern'`, `InternSourceId`, and `DisplayName` are explicit account metadata. The internal Fund label is not an intern identity or display name.

Intern mutations revalidate the current administrator inside the database transaction and persist required audit records. Duplicate source IDs are rejected; duplicate display names are allowed. Source IDs are opaque and case-sensitive, preserving leading zeros. Existing initialized databases are not automatically altered; schema changes belong only to `database/schema/001_InitialSchema.sql` during pre-production.

Deleting an account removes its configuration and account-access links and clears references to it as a default account. It never deletes or changes donations, expenses, accounting transactions, or source evidence. Only upload/rollback processes may change monetary transaction data. Recreating an account with the same source ID exposes the retained matching donations without rewriting those transactions.

## Users and Access

Intern is no longer a login role offered by user administration. Supported role choices are User, Viewer, and Admin. No migration of legacy Intern users is included; this implementation assumes none exist.

Manage Users grants and revokes viewing access through the existing `AccountsUsers` relationships. Multiple users may access one intern account, and a user may access multiple intern and primary accounts. Current authorization is revalidated before financial data access. Names and source IDs are excluded from general diagnostic logs and audit payloads.

## Data Lookup and Presentation

Donation and donor reads use exact `DonationData.InternSourceId` matches, never the shared donation Fund or the intern display name. Creating the matching account makes previously imported donations available through source-ID lookup. Unknown IDs remain accepted during import and do not create accounts.

`DonationAccountAssociations` derives the current AccountId from configuration. Nonblank intern IDs are exclusively associated with the exactly matching intern account; an unknown ID never falls back to a normal Fund. Account deletion removes the association immediately without changing the donation. Recreating the exact ID associates retained source rows with the new AccountId. These are configuration-derived views, not stored monetary foreign keys, so rollback restores source evidence and association follows current account configuration.

Some existing DTO/interface members retain the legacy name `InternName`; in account-selection and donation-read contracts these now carry the source ID. Human labels remain separate and display as `<intern name> (Intern)`. Source IDs must not be used as display labels. Source `DonationData.InternName` remains descriptive import data, not a lookup key.

Home preserves its donation-only presentation when the selected account is an intern account, regardless of the authenticated user's role. Donations and Donors use that same selected-account scope. Users retain normal account selectors and navigation; no special intern login navigation is created. Settings permits an authorized intern account to be selected as the default account.

Intern accounts have no accounting or expense data. Expenses remains reachable and displays a no-accounting-data notice for an intern selection, including navigation to Expenses with an intern already selected. The service returns empty accounting results without querying accounting for that intern.

## Caches

Account metadata and authorization caches are invalidated on intern CRUD; account access changes invalidate authorization. Contexts and cached fund references carry the source ID separately from the display name. Home summary keys include AccountId, the exact intern source identity, relevant generations, and calendar period. Central and fallback overview/warmup paths load intern donations by source ID and skip accounting; intern balances are zero.

## Validation

Application/build and non-Docker regression checks are run during implementation. Docker lifecycle and rollback simulations are left to the user. `LiveDonationImportTests.InternAccountLifecycleRetainsTransactionsAndUsesSourceId` covers creation without a login/accounting identity, exact-ID lookup, access links, authorization rejection, transaction retention after deletion, and reassociation after recreation when explicitly run against the disposable MySQL fixture.
