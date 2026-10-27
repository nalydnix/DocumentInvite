# DocumentInvite

Secure document-collection platform for commercial lenders. See [docs/architecture.md](docs/architecture.md) for the design.

## Prerequisites

- .NET SDK 10
- Node.js 22 or later (with npm)
- Docker (for local SQL Server and Azurite)

## Local infrastructure

```bash
docker compose up -d
```

Starts SQL Server (port 1433) and Azurite (blob 10000, queue 10001, table 10002). Override host ports with `SQL_PORT`, `AZURITE_BLOB_PORT`, `AZURITE_QUEUE_PORT` and `AZURITE_TABLE_PORT`, and the SA password with `MSSQL_SA_PASSWORD` (for example in an untracked `.env` file).

## API

```bash
dotnet build
dotnet test
dotnet run --project src/DocumentInvite.Api
```

- Health check: `GET /health`
- OpenAPI document: `/openapi/v1.json` (Development only)
- Scalar UI: `/scalar` (Development only)

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
- Point the web app at the matching API with `VITE_API_BASE_URL=http://localhost:5301`. The dev server proxies `/api` requests to it. Put these in an untracked `src/DocumentInvite.Web/.env.local`.

Example for a second instance:

```bash
ASPNETCORE_URLS=http://localhost:5301 dotnet run --project src/DocumentInvite.Api
VITE_PORT=5174 VITE_API_BASE_URL=http://localhost:5301 npm run dev
```
