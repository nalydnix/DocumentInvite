# Changelog

## Unreleased

### Added

- First-run admin guide (`docs/admin-guide.md`): production checklist, secrets, staff users, templates, first invite and retention. The recipient portal now shows a "Need help?" section, and the privacy notice states that data is deleted after a limited period.

### Changed

- Loans are now Requests everywhere: UI text, emails, API routes (`/api/requests`), web routes (`/requests`, with `/loans` redirecting), code and database. The staff role `LoanOfficer` is now `Requester`; staff are requesters and the external people who complete requests are recipients.
- Migration `RenameLoansToRequests` renames the `Loans` table, its columns, keys and indexes in place, so existing data is kept. Portal links and emails already sent keep working (they use `/p/{token}` and the portal API).

### Audit log reset

- The same migration deletes all existing audit events, because action and target names changed. The chain restarts from the genesis hash with the first event written under the new names. Deleting audit rows is denied to `audit_writer` (`di_app`) by design, so the migration must run under the admin connection (`ConnectionStrings__Migrations`), as the deploy workflow does. Export the audit log first if you need to keep a copy.
