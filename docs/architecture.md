# DocumentInvite – Architecture

DocumentInvite is a secure document-collection platform for commercial lenders. A lender team requests documents and data from each party on a loan. Each party completes the request through a secure link, with no account. The team reviews each item, then exports a package with consistent file names. Every significant action, including reads, is recorded in a tamper-evident audit trail.

This is a portfolio-grade MVP. The priorities are code quality, security, privacy compliance (SOC 2 / GDPR minded) and a clear demo, ahead of feature count. The product has no AI or LLM features.

## 1. Users and roles

| Actor | Auth | Capabilities |
|---|---|---|
| Admin | Identity account | Everything, including users and roles, settings, retention, demo reset, running the reminder job, and audit integrity checks. |
| LoanOfficer | Identity account | Manages contacts, templates, loans and invites; reviews items; sends messages and internal notes; exports packages; handles GDPR export and erasure requests (erasure is Admin only, see §7). |
| Viewer | Identity account | Read-only access to loans, items, previews and messages. Cannot download, export or change data. |
| Auditor | Identity account | Read-only access to the audit log and its CSV export, and can run integrity verification. The account stops working after an Admin-set `AccessExpiresAt` date. |
| Party (borrower, guarantor, broker and so on) | Portal token (plus an optional password) | Sees only their own invite: uploads files, fills fields, reads and sends messages. |

## 2. Technology stack

| Layer | Choice |
|---|---|
| API | .NET 10, ASP.NET Core controllers organised in vertical feature slices |
| Data | EF Core 10 with SQL Server (Azure SQL free offer in production, a SQL Server container or LocalDB locally). SQLite is not used, because the audit role permissions and rowversion rely on SQL Server. |
| Auth | ASP.NET Core Identity with `MapIdentityApi` bearer tokens and refresh tokens; role-based authorisation policies |
| Validation and errors | FluentValidation; RFC 7807 ProblemDetails for every error |
| API docs | `Microsoft.AspNetCore.OpenApi` plus the Scalar UI at `/scalar` (enabled in Development and Demo) |
| Logging | Serilog with structured logging, a PII-masking enricher or destructuring policy, and a correlation id on every log line |
| Files | Azure Blob Storage (Azurite locally), uploaded directly from the browser with short-lived single-blob SAS URLs |
| PDF | QuestPDF (Community licence) converts images to PDF |
| Email | An `IEmailSender` abstraction with two implementations: `DemoOutboxEmailSender` (the default, stored in the database and viewable in the UI) and `ResendEmailSender` (selected through configuration) |
| Web | React with TypeScript and Vite, React Router, Tailwind CSS, and TanStack Query for server state. The UI is mobile-first and visually similar to FileInvite: a clean white and blue look, card-based checklists and progress bars. |
| Tests | xUnit, FluentAssertions, and `WebApplicationFactory` integration tests against SQL Server (Testcontainers, or the CI service container); Vitest and React Testing Library for the web app; Playwright for a few end-to-end smoke tests |
| CI/CD | GitHub Actions: build and test on each PR, deploy the API to Azure App Service, and an hourly reminders cron; the web app deploys through Vercel's Git integration |

## 3. Repository layout

```
/
├─ src/
│  ├─ DocumentInvite.Api/            # ASP.NET Core host
│  │  ├─ Features/                   # vertical slices (one folder per feature)
│  │  │  ├─ Auth/  Users/  Contacts/  Templates/  Loans/  Invites/
│  │  │  ├─ Portal/  Review/  Reminders/  Messaging/  Audit/
│  │  │  ├─ Files/  Export/  Gdpr/  Outbox/  Demo/  Settings/
│  │  ├─ Infrastructure/             # DbContext, interceptors, blob, email, PDF, clock
│  │  ├─ Common/                     # ProblemDetails, validation filter, auth policies, correlation
│  │  └─ Program.cs
│  └─ DocumentInvite.Web/            # React + Vite app
├─ tests/
│  ├─ DocumentInvite.Api.Tests/      # unit and integration tests
│  └─ DocumentInvite.Web.e2e/        # Playwright smoke tests (optional)
├─ docs/architecture.md
├─ .github/workflows/                # ci.yml, deploy-api.yml, reminders-cron.yml
└─ docker-compose.yml                # SQL Server + Azurite for local development
```

Each feature slice contains its controller, request and response DTOs, validators, and a small service or handler. Slices share only `AppDbContext`, the domain entities and the infrastructure abstractions. Do not create generic repositories or a separate Application project.

## 4. Domain model

All primary keys are `Guid`. Mutable aggregates have a rowversion concurrency token. Timestamps are UTC `DateTimeOffset` values, taken from an injected `TimeProvider` so tests can control time.

- **AppUser** (Identity): `DisplayName`, `AccessExpiresAt?` (used by the Auditor role; enforced on sign-in and on every request).
- **Contact**: `FirstName`, `LastName`, `Email`, `Phone?`, `PartyType` (Borrower, CoBorrower, Guarantor, Broker, Attorney, Accountant), `IsPseudonymised`, `ErasedAt?`.
- **Template**: `Name`, `Description`, `IsSeeded`, `ArchivedAt?` (archived templates are hidden by default and cannot be edited until restored), and its **TemplateItems**: `Name`, `Kind` (FileUpload or FormField), `Instructions`, `AllowMultiple` (FileUpload), `FieldType` (Text, Number, Date, YesNo; FormField only), `IsRequired`, `SortOrder`.
- **Loan**: `Reference` (unique, used in file names), `Name`, `Amount` (decimal), `LoanType`, `CreatedBy`, `CompletedAt?`, plus a derived `Status`.
- **Invite** (one per party per loan): `LoanId`, `ContactId`, `DueDate`, `ReminderIntervalDays`, `LastReminderAt?`, `OverdueNoticeSentAt?`, `TokenHash`, `TokenExpiresAt`, `RevokedAt?`, `PasswordHash?`, `PrivacyAcceptedAt?`, `SentAt`, plus a derived `Status`.
- **RequestItem** (copied from template items onto an invite, then editable): `Name`, `Kind`, `Instructions`, `AllowMultiple`, `FieldType`, `Status` (Outstanding, Submitted, Approved, Rejected), `RejectionReason?`, `FieldValue?`, `PartyNote?`, `ReviewedBy?`, `ReviewedAt?`.
- **StoredFile**: `RequestItemId`, `BlobName` (an opaque GUID path), `OriginalFileName`, `StoredFileName` (the canonical name, see §8), `ContentType`, `SizeBytes`, `Sha256`, `Sequence` (n), `Status` (PendingUpload, Ready, Superseded), `UploadedAt`.
- **Message**: `InviteId`, `AuthorType` (Team, Party or System; System messages are written by the application, for example when an item is rejected), `AuthorUserId?`, `Body`, `IsInternal` (internal notes are never returned to the portal), `CreatedAt`.
- **OutboxEmail**: `To`, `Subject`, `HtmlBody`, `TextBody`, `CreatedAt`, `Provider`, `ProviderMessageId?`, `DeliveryError?`. Every email is stored here, so `/outbox` can render it.
- **AuditEvent** (append-only): `Id` (a `bigint` identity, which gives the chain order), `OccurredAt`, `ActorType` (User, Party, System, Anonymous), `ActorId?`, `Action` (a dotted code such as `item.approved`), `TargetType`, `TargetId?`, `IpAddress?`, `UserAgent?`, `CorrelationId`, `DetailsJson` (ids and enum values only), `PrevHash`, `Hash`.
- **AppSettings** (a single row): `RetentionDaysAfterCompletion`, `DefaultReminderIntervalDays`, and similar values.
- **JobRun**: `JobName`, `WindowKey`, `StartedAt`, `FinishedAt?`, `Result`. This table makes jobs idempotent (see §6).

### Derived status

- **Invite**: Sent (no items submitted and the portal not yet opened) → In progress (portal opened or any item submitted) → Submitted (every required item is Submitted or Approved) → Completed (every item Approved). A rejection moves the invite back to In progress.
- **Loan**: derived from its invites using the same four states. A loan is Completed when all of its invites are Completed, and `CompletedAt` is stamped at that moment for the retention purge.
- Status is calculated by one pure `StatusCalculator`, which has unit tests. Its result is stored on the entity (and recalculated whenever an item changes), so dashboards can filter on it.

## 5. Security

- **Staff auth**: Identity bearer tokens from `MapIdentityApi` (`/auth/login`, `/auth/refresh`). Public self-registration is disabled, and Admins create users. Lockout locks the account for 15 minutes after 5 failed attempts. Password policy: at least 12 characters. A custom sign-in wrapper or event hook audits both successful and failed sign-ins. Authorisation policies are `RequireAdmin`, `RequireStaff` (Admin or LoanOfficer), `CanRead` (Admin, LoanOfficer or Viewer) and `CanAudit` (Admin or Auditor). The `AccessExpiresAt` check runs both at sign-in and in a request-time authorisation handler.
  - Only `/api/auth/login` and `/api/auth/refresh` are exposed from `MapIdentityApi`; every other route under `/api/auth` returns 404. The first Admin is created at startup from `Bootstrap:AdminEmail` and `Bootstrap:AdminPassword` when no Admin exists. The request-time handler answers 401 for an expired account, and access tokens last 15 minutes because roles travel inside them.
- **Portal tokens**: 32 random bytes (from `RandomNumberGenerator`), encoded as base64url and placed in the link `/p/{token}`. Only the SHA-256 hash is stored, and lookup matches on the hash. Tokens expire (default: due date plus 30 days) and can be revoked or reissued by staff. An optional portal password is hashed with Identity's `PasswordHasher`. After a valid token (and password, if set), the portal receives a short-lived portal session JWT scoped to `inviteId` (signed with `Portal:Session:SigningKey`; a random key is generated when unset, so set it when running several instances). A session stops working as soon as its invite is revoked, expires or its link is reissued. Portal audit events use the Party actor with the contact id; uploads, fields and notes are refused until the privacy notice is accepted. Portal endpoints accept only this scheme. Login attempts are rate-limited with the ASP.NET Core rate limiter.
- **Uploads**: the client asks for an upload slot, and the API returns a write-only SAS for a single new blob, valid for 10 minutes. The content type and size limit are checked against an allow-list (PDF, JPG, PNG, DOCX, XLSX, CSV; at most 25 MB). After the upload, the client calls `complete`. The server then checks that the blob exists, re-checks its size and type, calculates the SHA-256, converts images to PDF and assigns the canonical name. The storage account allows no public access. Read access uses short-lived read SAS URLs (5 minutes), and each one is audited.
- **Transport and headers**: HTTPS only, HSTS, a strict CORS allow-list (the Vercel origin), `X-Content-Type-Options`, a CSP on the web app, and `Referrer-Policy: no-referrer` on portal pages so the token does not leak.
- **Secrets**: kept in App Service settings or GitHub secrets, never in the repository. The internal job API key is compared in constant time.
- **PII in logs**: Serilog masks emails, phone numbers, tokens and names. Request logging excludes query strings that carry tokens.
- **Validation**: FluentValidation runs on every request DTO. Every error is returned as ProblemDetails, and stack traces never reach clients in production.

## 6. Key flows

1. **Create and send**: a LoanOfficer creates a loan, adds parties (contacts), applies a template and picks the items for each party, then sets the due date and reminder interval. On send, the API creates the invites, generates tokens and emails each party its link. The status becomes Sent.
2. **Portal**: the party opens the link. The API logs `portal.opened`, then the party enters a password if one is set and accepts the privacy notice (`PrivacyAcceptedAt` is stamped) before uploading. The party sees a checklist with a progress bar, drag-and-drop or tap-to-upload (the camera on mobile), form fields, per-item notes and rejection reasons, plus the message thread.
3. **Review**: the dashboard lists loans with progress, status and overdue flags (due date passed and items still outstanding). On the loan page the reviewer can preview items inline (PDF.js or `<iframe>` for PDFs, `<img>` for images, through a read SAS), then Approve with one click or Reject with a reason. A rejection reopens the item, appends a system message and emails the party.
4. **Reminders**: `POST /internal/jobs/reminders` is protected by the `X-Api-Key` header. For each active, non-revoked invite with outstanding items, a reminder is sent when `now - (LastReminderAt ?? SentAt) >= interval`. A single overdue notice is sent once the due date passes (tracked by `OverdueNoticeSentAt`). Idempotency comes from these per-invite timestamps, updated in the same transaction as the outbox write, plus a `JobRun` row with a unique `(JobName, WindowKey=yyyy-MM-ddTHH)` constraint. A second call in the same hour is a no-op that returns the earlier result. The Admin "Run now" button calls the same service, with a manual window key. A GitHub Actions cron calls the endpoint every hour. Because only the token hash is stored, each reminder or overdue email carries a freshly issued portal link and the previous link stops working (expiry and password are unchanged); if the email cannot be delivered the change is rolled back, so the old link keeps working and the invite is retried in the next window. Only items that are Outstanding or Rejected count as outstanding. The job endpoint is attributed to the System audit actor, and each run writes one `job.reminders_run` audit event holding counts only.
5. **Export**: "Download approved" streams a zip (built with `System.IO.Compression` and written straight to the response, never buffered whole) organised as `{LoanRef}/{PartyName}/{file}`. Only Approved items are included. Form-field answers go into a `fields.csv` file for each party. The export is audited.
6. **Messaging**: each invite has one thread, paginated newest first (page and size are kept in component state, not the URL, because several threads can share a screen). Admins and Loan officers post team messages (`POST /api/invites/{id}/messages`) and internal notes (`.../notes`); the party posts through `POST /api/portal/messages` once the privacy notice is accepted. Viewers can read the thread but not post, and, like the party, never receive internal notes (the portal queries filter `IsInternal` in the query itself and the portal DTO has no such field). A team message emails the party a freshly issued link (only token hashes are stored, so, as for reminders and rejections, the previous link stops working; if delivery fails nothing is saved). A party message emails the loan's creator a link to the loan page. Emails never carry message text, and `message.posted` / `note.posted` audit events hold ids and the entry kind only.

## 7. Audit trail

- **Model**: the `AuditEvent` table described in §4. Details hold only ids, enum values and counts. They never hold names, emails, file names or field values. A typed `IAuditLog.RecordAsync(action, target, details)` API (details are built with `AuditDetails`, which accepts only ids, enum values and counts) is the only way to write events, so the rule can be reviewed in one place.
- **Hash chain**: `Hash = SHA256(PrevHash || canonical JSON of the event fields)`. The first event uses a fixed genesis hash. Appends are serialised with an application lock (`sp_getapplock`) inside the transaction, so the chain cannot fork under concurrent writes. "Verify integrity" (Admin or Auditor) walks the chain and reports the first broken id, and the check itself is audited.
- **Append-only enforcement**:
  - An EF Core `SaveChangesInterceptor` throws if any `AuditEvent` entry is Modified or Deleted.
  - The database has a dedicated role `audit_writer` with only `INSERT` and `SELECT` on `AuditEvents`, and `UPDATE` and `DELETE` are denied to it. The application's runtime database user must be a member of this role, so it is denied `UPDATE` and `DELETE` on that table, through a migration with raw SQL. Migrations run under a separate, privileged identity.
  - Erasure and the retention purge never touch audit rows. Because events hold ids only, the chain stays valid after pseudonymisation.
- **Read events**: previews, downloads, exports, portal opens, sign-in success and failure, role and settings changes, user access-expiry changes, job runs, integrity checks, GDPR exports and erasures, and demo resets.
- **Capture**: middleware sets the correlation id (from the `X-Correlation-Id` header, or a new one) and makes the actor, IP and user agent available through `IAuditContext`.
- **UI**: a filterable table (date range, actor, action, target type and id) with paging, plus a CSV export of the current filter. The export is itself audited.

## 8. Files and naming

- Blobs are stored under opaque names (`invites/{inviteId}/{fileId}`). The canonical download name is stored separately.
- JPG and PNG files are converted to a one-page PDF with QuestPDF (the image fitted to an A4 page), and the PDF replaces the original. PDFs pass through unchanged. Other allowed types keep their extension.
- The canonical name is `{LoanRef}_{PartyLastName}_{ItemName}_{n}.{ext}`. Each part is sanitised (only `[A-Za-z0-9-]` is kept, spaces become `-`, and length is capped), and `n` is a 1-based sequence for each item.
- For pseudonymised parties, the party segment becomes the pseudonym (for example `Party-7F3A`).

## 9. GDPR

- **Export** (for each contact): a zip with `contact.json` (the contact, their invites, items, field values and messages) plus their files. The export is audited.
- **Erasure** (Admin only, with a confirmation step): deletes the contact's blobs, clears field values, notes and message bodies written by the party, and replaces the name, email and phone with a pseudonym (for example `Erased-7F3A`, `erased+{id}@invalid`). It revokes the contact's invites, sets `ErasedAt` and audits `gdpr.erased` with ids only. Audit rows are unchanged, so the chain still verifies.
- **Retention purge**: `AppSettings.RetentionDaysAfterCompletion` sets the period. A dry-run endpoint lists the loans and file counts that would be purged. The real run deletes the blobs and loan data of loans completed before the cut-off and pseudonymises contacts that no longer have active loans. It is audited, idempotent, and triggered by an Admin (and can optionally share the job endpoint pattern).
  - `GET /api/gdpr/retention/preview` (paginated, audited as `retention.previewed`) and `POST /api/gdpr/retention/run` are Admin only. A run audits `loan.purged` per loan, `contact.pseudonymised` per contact and one `retention.run` with counts. Only contacts that were on a purged loan and have no loan left are pseudonymised, using the same routine as erasure. Blobs are deleted before the transaction commits, so a failure leaves everything in place and the run can be repeated.

## 10. Email

- `IEmailSender.SendAsync(EmailMessage)`. Templates are simple Razor-free C# string builders or embedded HTML templates covering: invite, reminder, overdue notice, item rejected, and new message.
- Every email is written to `OutboxEmail` first (in the caller's transaction when one is open). When `Email:Provider=Resend`, it is then sent through the Resend HTTP API (`Resend:ApiKey` and `Email:From` come from configuration). A Resend failure is logged without addresses, recorded in `DeliveryError`, and returned to the caller as `EmailSendResult(Delivered=false)`; it is not thrown and the stored copy is kept.
- `GET /api/outbox` (paged, newest first) and `GET /api/outbox/{id}` need `RequireStaff`, because stored emails hold live portal links.
- `/outbox` (a staff route in the web app) lists the stored emails and renders the HTML in a sandboxed iframe, so portal links can be clicked. In demo mode this is how the demo is driven.

## 11. Demo mode

- The seeder (idempotent, enabled through `Demo:Enabled`) creates an Admin and an Auditor account, plus a LoanOfficer and a Viewer. Their credentials come from configuration and are shown on the sign-in page only when demo mode is on. It also creates both seeded templates (an SBA 7(a) business acquisition checklist and a commercial real estate purchase checklist), sample contacts and three loans: one just sent, one partly uploaded with one rejection, and one fully approved, with sample PDFs.
- The Admin "Reset demo data" action deletes and re-seeds the domain data and blobs, but keeps the audit log, which is append-only. The reset is recorded as an audit event.
- A dismissible "How to try this" panel walks through the demo: sign in, open the Outbox, open a portal link as the borrower, review as the lender, then check the audit log.

## 12. API surface (summary)

All routes are under `/api` unless noted. Staff routes need a bearer token, and portal routes need a portal session.

- `/api/auth/*` (MapIdentityApi, with registration disabled), `/api/me`
- `/api/users` (Admin: create, list, set role (exactly one per user), set access expiry)
- `/api/contacts`, `/api/templates` (list, get, create, edit with item reorder, `archive`, `restore`), `/api/loans`, `/api/loans/{id}/invites`, `/api/invites/{id}` (revoke, reissue, items)
- `/api/items/{id}/approve|reject`, `/api/files/{id}/preview-url|download-url`
- `/api/loans/{id}/export` (zip)
- `/api/invites/{id}/messages`, `/api/invites/{id}/notes`
- `/api/audit` (query), `/api/audit/export.csv`, `/api/audit/verify`
- `/api/gdpr/contacts/{id}/export|erase`, `/api/gdpr/retention/preview|run`
- `/api/settings`, `/api/outbox`, `/api/demo/reset`, `/api/jobs/reminders/run` (Admin)
- `/api/portal/session` (exchange token and password), `/api/portal/invite`, `/api/portal/privacy` (accept the privacy notice), `/api/portal/items/{id}/upload-slot|complete|field|note`, `/api/portal/messages`
- `/internal/jobs/reminders` (API key)
- `/health`, `/scalar`, `/openapi/v1.json`

### Pagination

Any endpoint that returns a collection must be paginated with the shared contract. Exceptions are bounded lookups (a small, fixed set such as roles or enum values) and CSV or zip exports, which are streamed.

- **Request**: query `page` (default 1, minimum 1) and `pageSize` (default 25, maximum 100). A query record implements `IPagedQuery` and its validator calls `Include(new PagedQueryValidator<T>())` from `Common/Paging`, so invalid values return a ProblemDetails 400. An endpoint with no filters can bind `PageQuery`.
- **Response**: `PagedResponse<T>(Items, Total, Page, PageSize)`, serialised as `items`, `total`, `page`, `pageSize`.
- **Ordering**: every paginated query must have a deterministic order that ends in a unique key (for example `OrderBy(LastName).ThenBy(FirstName).ThenBy(Id)`), so pages never overlap or skip rows. Build the query with that order and finish it with `ToPagedResponseAsync(query, map, ct)`.
- **Past the end**: the API returns an empty `items` list with the real `total`. The web app falls back to the last page.
- **Web**: use `usePaging()` (`src/lib/paging.ts`) and the `Pagination` component (`src/components/Pagination.tsx`). The page and page size live in the URL query string, and a filter or search change calls `resetPage()` to return to page 1.

## 13. Testing strategy

- Unit tests cover the status calculator, file naming and sanitisation, token hashing, hash-chain calculation, reminder eligibility and the PII-masking policy.
- Integration tests (`WebApplicationFactory` with SQL Server and Azurite or a fake blob store) cover authorisation for each endpoint and role, portal token expiry and revocation, upload completion, the approve and reject flow, reminder idempotency, audit immutability (the interceptor and the database permission), audit verification after tampering, GDPR erasure keeping the chain valid, and export layout.
- Web tests cover key components (upload card, checklist progress, review actions) with Vitest and React Testing Library.
- CI runs `dotnet build`, `dotnet test`, `npm ci`, `npm run lint`, `npm run test` and `npm run build` on every PR.

## 14. Deployment

- **API**: Azure App Service (Linux, .NET 10), deployed by GitHub Actions on pushes to `main`. EF migrations are applied by a deploy step using the privileged connection string. The app runs as a least-privilege SQL user.
- **Database**: Azure SQL Database (free offer).
- **Storage**: an Azure Storage account with a private container, and CORS allowing `PUT` from the web origin for SAS uploads.
- **Web**: Vercel via its Git integration (no deploy workflow), with `VITE_API_BASE_URL` pointing at the API and SPA rewrites to `index.html`.
- **Cron**: `.github/workflows/reminders-cron.yml` runs hourly and calls `POST /internal/jobs/reminders` with `X-Api-Key` from repository secrets.
- **Configuration**: `ConnectionStrings__Default`, `ConnectionStrings__Migrations`, `Blob__ConnectionString`, `Jwt`/Identity settings, `Email__Provider`, `Resend__ApiKey`, `Jobs__ApiKey`, `Demo__Enabled`, `Cors__Origins`, `Portal__BaseUrl`, `Portal__Session__SigningKey`, `Blob__CorsAllowedOrigins` (development only), `Email__From`, `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword`.
- **Local**: `docker compose up` (SQL Server and Azurite), then `dotnet run` and `npm run dev`. The ports can be configured (`--urls` / `ASPNETCORE_URLS` for the API, `--port` / `VITE_PORT` for the web app, `VITE_API_PROXY_TARGET` for the dev proxy target; in development the browser uses relative `/api` URLs through that proxy, and `VITE_API_BASE_URL` applies to production builds only), so several test instances can run side by side.

## 15. Assumptions and exclusions

- Excluded: CRM and cloud-storage integrations, SSO, e-signatures, custom branding, virus scanning, analytics and AI features.
- The deployment has a single lender organisation (single tenant).
- Allowed upload types: PDF, JPG, PNG, DOCX, XLSX and CSV, up to 25 MB each.
- Real-time updates are not required; the UI refreshes through TanStack Query refetch.
- Azure and Vercel account provisioning and secrets are supplied by the repository owner. The deployment issues document the steps needed.

## Decisions

- **Vertical slices in one API project**: the simplest structure that keeps each feature self-contained and easy for an agent to review; no separate Application or Domain projects.
- **SQL Server everywhere (including tests)**: the audit append-only database role, `sp_getapplock` and rowversion need SQL Server, and it matches Azure SQL.
- **Direct-to-blob SAS uploads**: large files never pass through the API, and the SAS is limited to one blob for 10 minutes, so its blast radius stays small.
- **Hashed, revocable portal tokens exchanged for a short-lived portal JWT**: raw tokens never sit in the database, and later portal calls do not carry the long-lived token.
- **Audit with ids only, hash-chained, enforced twice (interceptor plus database grants)**: tamper-evident, and compatible with GDPR erasure because no PII lives in the chain.
- **Outbox-first email**: every email is stored, which makes the demo self-contained and auditable, while Resend can be enabled through configuration.
- **Stored derived status, recalculated by one pure calculator**: dashboards can filter efficiently, and the rules live in one tested place.
- **Job idempotency via per-invite timestamps plus a JobRun window key**: duplicate calls from the cron or the "Run now" button cannot double-send.
