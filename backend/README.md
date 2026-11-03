# Backend

ASP.NET Core 10 Web API with PostgreSQL. Schema changes are SQL scripts applied by DbUp on startup.

## Layout

| Project | Contents |
|---|---|
| `src/BuoyAI.API` | Controllers, middleware, `Program.cs` |
| `src/BuoyAI.Core` | Entities, interfaces, DTOs |
| `src/BuoyAI.Infrastructure` | EF Core context, services, integrations, `Migrations/Sql/*.sql` |
| `src/BuoyAI.AppHost` | .NET Aspire host that starts the API and its dependencies for local development |
| `src/BuoyAI.ServiceDefaults` | Shared Aspire telemetry and health defaults |
| `tests/BuoyAI.UnitTests` | Unit tests |
| `tests/BuoyAI.IntegrationTests` | API tests on an in-memory database |
| `tools/BuoyAI.AiBench` | Benchmark tool for AI models |

## Run it locally

You need the .NET 10 SDK and a PostgreSQL database. Only three values are required, and none of them
come from a third party:

| Setting | What it is |
|---|---|
| `ConnectionStrings:DefaultConnection` | Npgsql connection string |
| `Jwt:Key` | Random string, at least 32 characters |
| `Encryption:Key` | Random string, at least 32 characters |

The simplest way is Docker Compose, which starts Postgres and the API together:

```bash
cd backend
cp .env.example .env        # fill in POSTGRES_PASSWORD, JWT_KEY, ENCRYPTION_KEY (openssl rand -base64 36)
docker compose up --build   # API on http://127.0.0.1:5000
```

Or run the API on its own against a Postgres you already have:

```bash
cd backend
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Database=BuoyAI;Username=postgres;Password=..." --project src/BuoyAI.API
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 36)" --project src/BuoyAI.API
dotnet user-secrets set "Encryption:Key" "$(openssl rand -base64 36)" --project src/BuoyAI.API
dotnet run --project src/BuoyAI.API --environment Development
```

Pass `--environment Development`; without it configuration does not resolve correctly. Migrations
run on startup, so there is no `dotnet ef` step. Swagger is at `/swagger` in Development.
`src/BuoyAI.API/appsettings.Example.json` lists every setting if you prefer a file.

## Optional keys

Everything below is off until you set it. The API starts without any of them.

| Setting | Turns on |
|---|---|
| `Plaid:ClientId`, `Plaid:SecretKey` | Bank connection through Plaid |
| SimpleFIN (access URLs are entered per user in the app) | Bank connection through SimpleFIN |
| `GOOGLE_API_KEY` or `Gemini:ApiKey`; `Ai:*` for another provider | AI chat, categorization, insights |
| `Resend:ApiKey` or `Smtp:*` | Outgoing email (verification, password reset, alerts) |
| `Stripe:SecretKey`, `Stripe:WebhookSecret` | Paid plans and the Stripe webhook |
| `RevenueCat:*` | iOS in-app purchases |
| `Google:ClientId`, `AppleAuth:*` | Sign in with Google and Apple |
| `Firebase:CredentialsJson` or `Firebase:CredentialsPath` | Push notifications (sends are no-ops without it) |
| `Mcp:Enabled=true` | Read-only MCP server at `/mcp` |

## Tests

```bash
cd backend
dotnet test                              # everything that can run without extra setup
dotnet test tests/BuoyAI.UnitTests       # unit tests only
./run-coverage.sh                        # tests with a coverage summary
```

No test needs Docker itself. Some need a real Postgres and skip themselves otherwise. Point
`BUOYAI_MIGRATION_TEST_CONNECTION` at a superuser connection string for a throwaway server (each test
creates and drops its own database) to run them:

```bash
docker run -d --name bm-test-pg -e POSTGRES_PASSWORD=pw -p 55432:5432 postgres:15-alpine
export BUOYAI_MIGRATION_TEST_CONNECTION="Host=localhost;Port=55432;Username=postgres;Password=pw"
dotnet test tests/BuoyAI.UnitTests
```

This covers the migration runner, upgrade paths, backup, pgvector and the Postgres-backed stores.
`CloudGatewayEndToEndTests` skips unless the gateway and its fakes are running
(`scripts/dev/gateway-e2e.sh`). `AndroidPasskeyOriginConfigTests` skips when the hosted
`appsettings.Production.json` and `appsettings.Staging.json` are absent.

## Adding a migration

Add `NNNN_short_description.up.sql` and `.down.sql` under `src/BuoyAI.Infrastructure/Migrations/Sql/`.
Rules, naming and the cutover behaviour are in that folder's `README.md`.
