# Deployment

The API runs on Azure App Service (Linux, .NET 10) with Azure SQL (free offer) and an Azure Storage account. The web app runs on Vercel through its Git integration. See [architecture.md](architecture.md) section 14 for the design.

Only the repository owner provisions cloud resources. Agents and CI never run `infra/provision.sh`.

## Azure resources

`infra/main.bicep` declares all of these, parameterised by name prefix, location and web origin:

| Resource | Notes |
| --- | --- |
| App Service plan and web app | Linux, `DOTNETCORE\|10.0`, HTTPS only, TLS 1.2, FTPS disabled, health check on `/health` |
| Azure SQL server and database `DocumentInvite` | Serverless free offer (`useFreeLimit`), auto-pause when the allowance is used; the firewall allows Azure services |
| Storage account | Private `documents` container, no public blob access, CORS allowing `PUT`, `GET`, `HEAD`, `OPTIONS` from the web origin |

Outputs: `apiUrl`, `webAppName`, `appServicePlanName`, `sqlServerName`, `sqlServerFqdn`, `sqlDatabaseName`, `storageAccountName`, `blobContainerName`. The SQL admin password is a secure parameter and nothing secret is committed.

## Setup steps

1. Install the Azure CLI ([install guide](https://learn.microsoft.com/cli/azure/install-azure-cli)): `winget install Microsoft.AzureCLI` on Windows, `brew install azure-cli` on macOS, or the install script for Linux.
2. Sign in: `az login` (and `az account set --subscription <id>` if you have several).
3. Create the Vercel project from this repository first (root directory `src/DocumentInvite.Web`) so you know the web origin, for example `https://documentinvite.vercel.app`.
4. Provision. The script checks that `az` is installed and logged in, then creates the resource group, deploys the template, sets the App Service settings and prints what to configure next. It is safe to run again.

   ```bash
   SQL_ADMIN_PASSWORD='<strong password>' infra/provision.sh <name-prefix> <location> <web-origin>
   ```

   ```powershell
   $env:SQL_ADMIN_PASSWORD = '<strong password>'
   ./infra/provision.ps1 -NamePrefix <name-prefix> -Location <location> -WebOrigin <web-origin>
   ```

5. Create the runtime database user (next section).
6. Set the remaining App Service settings (below) and the GitHub secrets and Vercel variable that the script printed.
7. Run the **Deploy API** workflow (Actions, Run workflow), or push to `main`.

Validate the template locally with `az bicep build --file infra/main.bicep`; the **Infra check** workflow does the same in CI.

## Runtime database user

The application connects as a least-privilege user, never the SQL admin. As the admin (Azure portal Query editor, or `sqlcmd` after adding your IP to the SQL firewall), run this in the `DocumentInvite` database after the first migration has created the `audit_writer` role:

```sql
CREATE USER [di_app] WITH PASSWORD = '<strong password>';
ALTER ROLE db_datareader ADD MEMBER [di_app];
ALTER ROLE db_datawriter ADD MEMBER [di_app];
ALTER ROLE audit_writer ADD MEMBER [di_app];
```

`audit_writer` grants `SELECT` and `INSERT` and denies `UPDATE` and `DELETE` on `dbo.AuditEvents`. A `DENY` overrides the `db_datawriter` grant, so the runtime user can append to the audit trail but never change or remove an event. The user has no DDL rights; schema changes only happen through migrations under the admin connection string. Use this user in `ConnectionStrings__Default`.

## Configuration

Set these on the App Service (Settings, Environment variables). `provision` sets the ones marked "script".

| Key | Purpose |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` (script) |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` so HTTPS redirection sees the original scheme (script) |
| `ConnectionStrings__Default` | Runtime connection string for `di_app` |
| `Blob__ConnectionString` | Storage account connection string (script) |
| `Blob__ContainerName` | Private container name, `documents` (script) |
| `Cors__Origins__0` | Allowed web origin, for example `https://documentinvite.vercel.app`; add `Cors__Origins__1` and so on for more. Nothing is allowed when empty (script) |
| `Portal__BaseUrl` | Web origin used in e-mailed links (script) |
| `Portal__Session__SigningKey` | Long random key for portal sessions (set it so sessions survive restarts) |
| `Jobs__ApiKey` | Key for `POST /internal/jobs/reminders`; must equal the `REMINDERS_API_KEY` secret |
| `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` | Creates the first Admin; remove after the first start |
| `Email__Provider`, `Email__From`, `Resend__ApiKey` | Outbound e-mail (Resend) |
| `Demo__Enabled` | `false` in production (script); see the README for the demo account keys |
| `Scalar__Enabled` | `false` (script). Set `true` only to expose `/openapi/v1.json` and `/scalar` outside Development |

`Blob__CorsAllowedOrigins` is for development with Azurite only; in Azure the CORS rule comes from the Bicep template.

Production behaviour: HTTP redirects to HTTPS, responses carry `Strict-Transport-Security` (one year, subdomains), `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a restrictive `Content-Security-Policy` and a `Permissions-Policy`. CORS only allows the configured origins.

## GitHub secrets

| Secret | Used by | Value |
| --- | --- | --- |
| `AZURE_CREDENTIALS` | Deploy API | Service principal JSON from `az ad sp create-for-rbac ... --sdk-auth` (the script prints the command) |
| `AZURE_WEBAPP_NAME` | Deploy API | App Service name |
| `AZURE_RESOURCE_GROUP` | Deploy API | Resource group name |
| `AZURE_SQL_SERVER_NAME` | Deploy API | SQL server name (the runner opens a temporary firewall rule for the migration and removes it afterwards) |
| `AZURE_SQL_MIGRATIONS_CONNECTION_STRING` | Deploy API | Privileged (admin) connection string, used only for `dotnet ef database update` |
| `REMINDERS_API_URL`, `REMINDERS_API_KEY` | Reminders cron | API base URL and the `Jobs__ApiKey` value |

**Deploy API** builds and tests on every push to `main` and on manual dispatch. When any of its secrets is missing it logs a notice, skips the Azure steps and still succeeds.

## Web app (Vercel)

Vercel deploys from the Git integration; there is no deploy workflow. In the Vercel project set the root directory to `src/DocumentInvite.Web` and add `VITE_API_BASE_URL` set to the API URL (`apiUrl` output, for example `https://<app>.azurewebsites.net`). `src/DocumentInvite.Web/vercel.json` rewrites every path to `index.html` for the single-page app.

## Tear down

```bash
az group delete --name <resource-group> --yes --no-wait
```

This deletes every Azure resource in the group, including the database and uploaded documents. Remove the Vercel project and GitHub secrets separately.
