# DocumentInvite

Secure document-collection platform for commercial lenders. See [docs/architecture.md](docs/architecture.md) for the design.

Deployment (Azure App Service, Vercel, Bicep): see [docs/deployment.md](docs/deployment.md).

## Prerequisites

- .NET SDK 10
- Node.js 22 or later (with npm)
- Docker (for local SQL Server and Azurite)

## Local infrastructure

```bash
docker compose up -d
```

Starts SQL Server (port 1433) and Azurite (blob 10000, queue 10001, table 10002). Override host ports with `SQL_PORT`, `AZURITE_BLOB_PORT`, `AZURITE_QUEUE_PORT` and `AZURITE_TABLE_PORT`, and the SA password with `MSSQL_SA_PASSWORD` (for example in an untracked `.env` file).

## File storage

Uploads go straight from the browser to Azure Blob Storage with a write-only SAS URL (valid 10 minutes), and previews and downloads use read-only SAS URLs (valid 5 minutes). `Blob:ConnectionString` selects the account (`UseDevelopmentStorage=true` targets Azurite) and `Blob:ContainerName` the private container.

Browser `PUT` uploads need a CORS rule on the storage account's Blob service. In development the browser only talks to the API through the dev proxy, but direct uploads go to the storage endpoint, so configure the rule for the web origin:

| Setting | Value |
| --- | --- |
| Allowed origins | The web app origin, for example `https://app.example.com` (not `*` in production) |
| Allowed methods | `PUT`, `GET`, `HEAD`, `OPTIONS` |
| Allowed headers | `x-ms-blob-type`, `content-type`, `x-ms-*` |
| Exposed headers | `ETag`, `x-ms-*` |
| Max age | `3600` |

```bash
az storage cors add --services b --methods PUT GET HEAD OPTIONS --origins <origin> \
  --allowed-headers "x-ms-*" content-type --exposed-headers "x-ms-*" ETag --max-age 3600 --account-name <account>
```

## API

```bash
dotnet build
dotnet test
dotnet run --project src/DocumentInvite.Api
```

- Health check: `GET /health`
- OpenAPI document: `/openapi/v1.json` (Development only)
- Scalar UI: `/scalar` (Development only, or when `Scalar__Enabled=true`)

## Web app

```bash
cd src/DocumentInvite.Web
npm ci
npm run lint
npm run test
npm run build
npm run dev
```

## Running several instances side by side

Ports are never hard-coded, so multiple instances can run at once.

- API port: `ASPNETCORE_URLS=http://localhost:5301 dotnet run --project src/DocumentInvite.Api`, or `dotnet run --project src/DocumentInvite.Api --urls http://localhost:5301`.
- Web port: `npm run dev -- --port 5174`, or set `VITE_PORT=5174`.
- Point the dev proxy at the matching API with `VITE_API_PROXY_TARGET=http://localhost:5301` (`VITE_API_BASE_URL` is also accepted as the proxy target). In development the browser always calls relative `/api` URLs and the dev server proxies them to the API, so no CORS configuration is needed. `VITE_API_BASE_URL` only takes effect in production builds. Put these in an untracked `src/DocumentInvite.Web/.env.local`.

- E-mail link origin: links in e-mails (portal and loan links) are built from `Portal__BaseUrl`. It has no default and must be the origin the web app is served from, for example `Portal__BaseUrl=http://localhost:5174` for the second instance below. Sending an e-mail fails with a clear error while it is unset. Set it per instance as an environment variable (or with `dotnet user-secrets`).

Example for a second instance:

```bash
ASPNETCORE_URLS=http://localhost:5301 Portal__BaseUrl=http://localhost:5174 dotnet run --project src/DocumentInvite.Api
VITE_PORT=5174 VITE_API_PROXY_TARGET=http://localhost:5301 npm run dev
```

## Audit trail database permissions

Migrations create the `audit_writer` database role: `SELECT` and `INSERT` are granted and `UPDATE` and `DELETE` are denied on `dbo.AuditEvents`. Add the runtime application database user to this role (`ALTER ROLE audit_writer ADD MEMBER [app_user]`). The DENY overrides broader grants such as `db_datawriter`. Run migrations under a separate privileged identity (`ConnectionStrings:Migrations`).

## Staff authentication

- Sign-in and token refresh are served at `POST /api/auth/login` and `POST /api/auth/refresh` (ASP.NET Core Identity bearer tokens). Registration, password reset and e-mail confirmation are disabled; Admins create users under `/api/users`.
- Apply the migrations (`dotnet ef database update --project src/DocumentInvite.Api`) to create the Identity tables and seed the roles Admin, LoanOfficer and Auditor.
- The first Admin is created on startup when `Bootstrap:AdminEmail` and `Bootstrap:AdminPassword` are configured (`Bootstrap__AdminEmail` and `Bootstrap__AdminPassword` as environment variables) and no Admin exists yet. `appsettings.Development.json` sets `admin@documentinvite.local` for local use; production must supply its own values through secrets and remove them after the first start.

## Demo mode

Set `Demo:Enabled=true` to seed a demo on startup (idempotent). Credentials come from configuration only:

```
Demo__Enabled=true
Demo__Accounts__Admin__Email / Demo__Accounts__Admin__Password
Demo__Accounts__LoanOfficer__Email / Demo__Accounts__LoanOfficer__Password
Demo__Accounts__Auditor__Email / Demo__Accounts__Auditor__Password
```

Passwords must meet the 12-character policy. Development settings enable it with local-only accounts. The seeder creates the three accounts, three sample contacts and loans (DEMO-1001 just sent, DEMO-1002 partly uploaded with one rejected item, DEMO-1003 fully approved) with sample PDFs, the matching emails in the Outbox and audit events. The sign-in page shows every configured demo login, Admin, LoanOfficer and Auditor, with email and password (from `GET /api/demo/config`) only while demo mode is on. Admins can use Settings, Demo data, "Reset demo data" (`POST /api/demo/reset`) to delete and re-seed loans, contacts, emails and blobs; the audit log is kept and the reset is audited.
