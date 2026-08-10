# CmsEvents Service

A .NET 9 web service that ingests CMS webhook events, persists them to SQL Server, and exposes them via a REST API with role-based access. Built with Clean Architecture, MediatR command/query handlers, EF Core (reader/writer split), Basic Auth, and OpenTelemetry.

Design decisions live in `docs/decisions.md` (16 ADRs). Companion docs cover architecture, response contracts, deferred improvements, and operational runbooks.

## Design philosophy — product-first, not time-boxed PoC

The assignment is *"intentionally open-ended"* and does not specify a deadline. That framing was read as an invitation to treat the deliverable as the first sprint of a real service rather than a minimum-viable proof of concept — favoring long-term structural cost over near-term implementation speed.

Consequences of this framing that go beyond strict spec compliance:

| Feature | Spec required? | Rationale for inclusion |
|---------|---------------|-------------------------|
| Rate limiting (ADR-013) | No | Defensive posture for a public HTTP endpoint. Isolated in `RateLimitingSetup.cs` — trivial to remove if rescoped. |
| OpenTelemetry distributed tracing (ADR-014) | Spec item 6 asks for "log processed events" only | Adds `traceparent` propagation + span attributes per event. Console exporter in dev, Application Insights in prod. |
| Application Insights Serilog sink (ADR-014) | No | Conditional on `Observability:ApplicationInsightsConnectionString` — off in dev, on in prod. |
| Docker + docker-compose (ADR-012, README) | Spec asks for platform-agnostic build (Mac/Windows) | Docker satisfies this and gives a one-command local DB. |
| GitHub Actions CI (`.github/workflows/ci.yml`) | Spec asks for a GitHub repo only | CI runs the three test tiers on every push, adds a Docker build validation. |
| Managed Identity + Azure Key Vault (ADR-012) | Spec asks for random-GUID passwords | Design shows the intended production secret-store hookup; local dev uses User Secrets. |
| 90-day secret rotation runbook (`docs/runbook-secret-rotation.md`) | No | Operational documentation that would be expected in production. |
| Repository pattern with ORM-agnostic Application (ADR-010) | No | Chose stricter Clean Architecture even though a simpler `IApplicationDbContext` pattern would satisfy the spec. |

If this were scoped as a two-hour PoC, several of these would be trimmed. Documented explicitly so a reviewer can distinguish "spec compliance" from "additional production polish" and evaluate each independently. Every feature above is orthogonal — remove any one and the rest continues to work.

**Spec priorities** (`"correctness, structure, and clarity of the system. Correct event processing is the most important part."`) are the primary drivers of every design decision. The additions above should not obscure the core: idempotent per-event processing (ADR-005, ADR-006, ADR-008), reader/writer split (ADR-010), authenticated API surface (ADR-011), and the boundary discipline that keeps them separable (ADR-001, ADR-002).

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (>= 9.0.100).
- [Docker](https://www.docker.com/) — for local SQL Server (via `docker-compose`) and for integration tests (via Testcontainers).
- Git.
- Any editor: Rider, Visual Studio 2022 17.11+, or VS Code with the C# Dev Kit.

Platform-agnostic — verified on macOS, Windows, and Linux. All commands below work in `bash`, `zsh`, or `pwsh`.

## Quick start

**Every command below assumes you are in the `CmsEvents/` repo root** (the folder that contains `CmsEvents.sln` and `docker-compose.yml`). If in doubt, `cd` there first and run `ls` — you should see `src/`, `tests/`, `docs/`, and `docker-compose.yml` all listed.

All commands are **written as one-liners** so they work identically in bash, zsh, and PowerShell (which does not accept `\` as a line-continuation character). Copy the whole line.

If you have `dotnet ef` missing (or want to make sure it is the latest), run:

```bash
dotnet tool update --global dotnet-ef
```

If it is already installed at a newer version, this is a no-op. If it is not installed at all, this installs the latest.

### Step 1: Clone the repo

Run from the folder where you want the project to live (e.g., `~/source/repos/`):

```bash
git clone <this-repo-url> CmsEvents
cd CmsEvents
```

**All subsequent steps run from inside `CmsEvents/`.**

### Step 2: Start SQL Server via Docker

Working directory: **`CmsEvents/`** (the repo root — `docker-compose.yml` lives here).

Make sure Docker Desktop (or your Docker daemon) is running first.

```bash
docker compose up -d
```

Verify the container is healthy:

```bash
docker compose ps
```

Expected: one row for `cmsevents-sqlserver`, `STATUS` should say `Up ... (healthy)` after ~30 seconds. If it says `starting` or `unhealthy`, wait and rerun `docker compose ps` — SQL Server's health probe takes some time on first boot.

If Docker is not running, you will see an error like `Cannot connect to the Docker daemon` — start Docker Desktop and retry.

### Step 3: Initialize User Secrets (optional — usually a no-op)

Working directory: **`CmsEvents/`**.

```bash
dotnet user-secrets init --project src/CmsEvents.Api
```

The project ships with `<UserSecretsId>cmsevents-api-dev-secrets</UserSecretsId>` already set in `src/CmsEvents.Api/CmsEvents.Api.csproj`. If you see `The MSBuild project ... has already been initialized with a UserSecretsId`, that message is expected and safe to ignore — skip to Step 4.

### Step 4: Set the connection strings

Working directory: **`CmsEvents/`**. Both connection strings point to the container from Step 2. The reader uses the same DB with `ApplicationIntent=ReadOnly` (per ADR-010) to catch accidental writes in dev.

```bash
dotnet user-secrets set "ConnectionStrings:Writer" "Server=localhost,1433;Database=CmsEventsDb;User Id=sa;Password=Local_Dev_Password_123!;TrustServerCertificate=True;Encrypt=False" --project src/CmsEvents.Api
```

```bash
dotnet user-secrets set "ConnectionStrings:Reader" "Server=localhost,1433;Database=CmsEventsDb;User Id=sa;Password=Local_Dev_Password_123!;TrustServerCertificate=True;Encrypt=False;ApplicationIntent=ReadOnly" --project src/CmsEvents.Api
```

`Local_Dev_Password_123!` is the SA password baked into `docker-compose.yml`. **Do not use this password in production.**

### Step 5: Generate a BCrypt hash for the seed users

Working directory: **`CmsEvents/`**. Pick a local dev password (any string). This command hashes it and prints the result to stdout:

```bash
dotnet run --project tools/BcryptHash -- --password "LocalDevPassword-1!"
```

Expected output: a single line starting with `$2a$11$` (60 chars). **Copy this hash** — you will paste it three times in the next step.

For local dev we use the same hash (and therefore the same password) for all three seed users. In production, each user has its own password per the rotation runbook.

### Step 6: Register the three seed users

Working directory: **`CmsEvents/`**. Replace `<PASTE_HASH_HERE>` with the hash from Step 5 (same value all three times):

```bash
dotnet user-secrets set "Users:CmsWebhookUser:Username" "cms-webhook-user" --project src/CmsEvents.Api
dotnet user-secrets set "Users:CmsWebhookUser:PasswordHash" "<PASTE_HASH_HERE>" --project src/CmsEvents.Api

dotnet user-secrets set "Users:ReadonlyUser:Username" "readonly-user" --project src/CmsEvents.Api
dotnet user-secrets set "Users:ReadonlyUser:PasswordHash" "<PASTE_HASH_HERE>" --project src/CmsEvents.Api

dotnet user-secrets set "Users:AdminUser:Username" "admin-user" --project src/CmsEvents.Api
dotnet user-secrets set "Users:AdminUser:PasswordHash" "<PASTE_HASH_HERE>" --project src/CmsEvents.Api
```

Verify all six keys are set:

```bash
dotnet user-secrets list --project src/CmsEvents.Api
```

Expected: eight entries total (two connection strings + six user entries).

### Step 7: Ensure the initial EF Core migration exists

Working directory: **`CmsEvents/`**.

Check whether `src/CmsEvents.Infrastructure/Migrations/` contains an `InitialSchema` migration. If it does (this is the normal case when cloning), **skip to Step 8** — the app will apply any pending migrations at startup automatically via `DbInitializer`.

If the folder is missing or empty (fresh scaffold), generate it once:

```bash
dotnet ef migrations add InitialSchema --project src/CmsEvents.Infrastructure --startup-project src/CmsEvents.Api --context WriterDbContext
```

Expected: `Build succeeded.` followed by `Done.`. Commit the generated files under `src/CmsEvents.Infrastructure/Migrations/` — they are part of the codebase.

**Why this matters**: EF Core 9 treats a model with no matching migration as an error at startup (`PendingModelChangesWarning`) — this catches drift between the C# model and the shipped schema. If you see that exception at Step 8, come back here and run the `migrations add` command.

You do **not** need to run `dotnet ef database update` separately — the app runs it at startup via `DbInitializer` (per ADR-010).

### Step 8: Run the service

Working directory: **`CmsEvents/`**.

```bash
dotnet run --project src/CmsEvents.Api
```

Expected log lines (roughly, in order):

```
info: Microsoft.Hosting.Lifetime[14] Now listening on: http://localhost:5000
info: Microsoft.Hosting.Lifetime[14] Now listening on: https://localhost:5001
info: CmsEvents.Infrastructure.Persistence.DbInitializer[0] Applying pending migrations...
info: CmsEvents.Infrastructure.Persistence.DbInitializer[0] Migrations applied.
info: CmsEvents.Infrastructure.Persistence.DbInitializer[0] Seeding users from configuration...
info: CmsEvents.Infrastructure.Persistence.DbInitializer[0] User seeding completed.
```

Leave this terminal open — the service runs in the foreground.

### Step 9: Verify the setup with a curl ping

Open a **new terminal** (leave Step 8 running). Working directory can be anywhere — this is a network call.

```bash
curl -u "cms-webhook-user:LocalDevPassword-1!" -H "Content-Type: application/json" -X POST http://localhost:5000/cms/events -d "[]"
```

Note for PowerShell users: PowerShell aliases `curl` to `Invoke-WebRequest`, which has different arguments. Use `curl.exe` explicitly, or install a real `curl` (Windows 10+ ships one at `C:\Windows\System32\curl.exe`):

```powershell
curl.exe -u "cms-webhook-user:LocalDevPassword-1!" -H "Content-Type: application/json" -X POST http://localhost:5000/cms/events -d "[]"
```

Expected response:

```json
{
  "batchId": "<some-guid>",
  "correlationId": "<some-guid>",
  "totalEvents": 0,
  "processed": 0,
  "skipped": 0,
  "failed": 0,
  "errors": []
}
```

If you get `401 Unauthorized`, revisit Steps 5-6 (the password in the curl command must match the plain-text password you hashed). If you get connection errors, revisit Step 8.

You are done — the service is running end-to-end.

### To stop everything

```bash
# In the terminal running the service: Ctrl+C

# From CmsEvents/ root — stops SQL Server but keeps the data volume:
docker compose down

# Alternative: also delete the DB data (fresh start next time):
docker compose down -v
```

## Running the tests

Three tiers per ADR-016:

```bash
# Unit tests (fast, in-process SQLite)
dotnet test tests/CmsEvents.Unit.Tests

# Architecture tests (NetArchTest — no DB needed)
dotnet test tests/CmsEvents.Architecture.Tests

# Integration tests (real SQL Server via Testcontainers — requires Docker)
dotnet test tests/CmsEvents.Integration.Tests

# All tests + coverage
dotnet test --collect:"XPlat Code Coverage"
```

CI (`.github/workflows/ci.yml`) runs all three tiers plus a Docker image build on every push and PR.

## API surface

All endpoints require Basic Authentication.

| Endpoint | Role required | Purpose |
|----------|---------------|---------|
| `POST /cms/events` | `Organization` | Ingest CMS webhook batch (per ADR-008) |
| `GET /entities` | `User` or `Admin` | List entities (role-aware filter per ADR-007) |
| `GET /entities/{id}` | `User` or `Admin` | Fetch a single entity |
| `POST /entities/{id}/disable` | `Admin` | Local admin override |
| `POST /entities/{id}/enable` | `Admin` | Local admin override |

Full request/response schemas with examples in [`docs/responses.md`](docs/responses.md).

**Try it end-to-end**: [`samples/CmsEvents.http`](samples/CmsEvents.http) contains 21 ready-to-run requests exercising every endpoint (send from Visual Studio / Rider / VS Code REST Client). [`samples/scenarios.md`](samples/scenarios.md) walks through each one with expected responses and `sqlcmd` snippets to verify DB state.

## Project structure

```
CmsEvents/
├── src/
│   ├── CmsEvents.Api/            HTTP surface, auth middleware, DI composition root
│   ├── CmsEvents.Application/    Use cases (MediatR handlers), validation, event dispatch
│   ├── CmsEvents.Domain/         Entities, value objects, domain interfaces
│   ├── CmsEvents.Infrastructure/ EF Core adapters, secrets, clock, auth handler
│   └── CmsEvents.Contracts/      Public DTOs (request/response shapes)
├── tests/
│   ├── CmsEvents.Unit.Tests/
│   ├── CmsEvents.Integration.Tests/
│   └── CmsEvents.Architecture.Tests/
├── tools/
│   └── BcryptHash/               Console utility used by the secret rotation runbook
├── docs/
│   ├── decisions.md              16 ADRs
│   ├── architecture.md           Structural + operational reference
│   ├── responses.md              API response schemas
│   ├── future-improvements.md    Deferred design ideas
│   └── runbook-secret-rotation.md
├── docker-compose.yml            Local SQL Server 2022 container
├── CmsEvents.sln
├── global.json                   Pins .NET 9 SDK
├── Directory.Build.props         Shared compiler settings (nullable, warnings-as-errors, ...)
├── Directory.Packages.props      Central Package Management (all NuGet versions in one file)
├── .editorconfig                 Code style enforcement
└── .github/workflows/ci.yml      GitHub Actions CI
```

## Local secrets

Secrets never live in the repo. Local development uses [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets); production uses Azure Key Vault (per ADR-012). The secret store is:

- **Local dev**: `~/.microsoft/usersecrets/cmsevents-api-dev-secrets/secrets.json` (macOS/Linux) or `%APPDATA%\Microsoft\UserSecrets\cmsevents-api-dev-secrets\secrets.json` (Windows). Managed by `dotnet user-secrets`.
- **Production**: Azure Key Vault named `kv-cmsevents-prod`. Application picks it up automatically when the `KeyVaultUri` environment variable is set — see `Program.ConfigureConfiguration`.

Secrets consumed by the service:

| Configuration key | Description |
|-------------------|-------------|
| `ConnectionStrings:Writer` | Primary DB (writes, migrations) |
| `ConnectionStrings:Reader` | Read replica (in prod) or same DB with `ApplicationIntent=ReadOnly` (in dev) |
| `Users:CmsWebhookUser:Username` | Username for the CMS webhook caller (10-20 chars per spec) |
| `Users:CmsWebhookUser:PasswordHash` | BCrypt hash (generate via `tools/BcryptHash`) |
| `Users:ReadonlyUser:*` | Same shape as above, for `User` role |
| `Users:AdminUser:*` | Same shape as above, for `Admin` role |
| `Observability:ApplicationInsightsConnectionString` | (Production only) telemetry endpoint |
| `KeyVaultUri` | (Production only) Key Vault URL — presence enables the Key Vault config provider |

Rotate every 90 days per [`docs/runbook-secret-rotation.md`](docs/runbook-secret-rotation.md).

## Common tasks

### Add a new EF Core migration

```bash
dotnet ef migrations add <MigrationName> --project src/CmsEvents.Infrastructure --startup-project src/CmsEvents.Api --context WriterDbContext
```

Migrations are owned by `WriterDbContext` per ADR-010. Never generate migrations against `ReaderDbContext`.

### Regenerate a BCrypt hash for password rotation

```bash
dotnet run --project tools/BcryptHash -- --password "NewPassword"
# Copy the output hash and set it in User Secrets (dev) or Key Vault (prod).
```

Details in [`docs/runbook-secret-rotation.md`](docs/runbook-secret-rotation.md).

### Reset the local database

```bash
docker compose down -v
docker compose up -d
dotnet ef database update --project src/CmsEvents.Infrastructure --startup-project src/CmsEvents.Api --context WriterDbContext
```

`docker compose down -v` removes containers **and** the data volume, giving you a clean DB on next start.

### View the OpenAPI (Swagger) spec

Development-only. Once the service is running:

```
GET http://localhost:5000/openapi/v1.json
```

## Architecture at a glance

Clean Architecture with five projects (per ADR-001):

- **Api** — HTTP surface, auth middleware, DI composition root, endpoints.
- **Application** — MediatR handlers, validators, event Strategy dispatch. ORM-agnostic (no EF Core reference per ADR-010).
- **Domain** — Entities with rich behavior (idempotency rule, sticky admin flag). No framework dependencies.
- **Infrastructure** — EF Core writer/reader DbContexts, repository implementations, Basic Auth handler, seeder, clock. Implements ports defined in Application.
- **Contracts** — Wire DTOs (event schema, response shapes).

Boundary rules enforced by [`tests/CmsEvents.Architecture.Tests`](tests/CmsEvents.Architecture.Tests/BoundaryTests.cs) using NetArchTest — build fails if any layer breaches its allowed dependencies. See `docs/architecture.md` § Architectural Boundaries for the full list and rationale.

## Documentation

- [`docs/decisions.md`](docs/decisions.md) — 16 ADRs covering the full design.
- [`docs/architecture.md`](docs/architecture.md) — structural and operational reference, with sequence diagrams and deployment topology.
- [`docs/responses.md`](docs/responses.md) — full API response schemas with examples for every status code.
- [`docs/future-improvements.md`](docs/future-improvements.md) — deferred design ideas with triggers.
- [`docs/runbook-secret-rotation.md`](docs/runbook-secret-rotation.md) — 90-day secret rotation procedure.

## Contributing

- Adhere to `.editorconfig`.
- Every architectural decision goes in an ADR — don't slip a design change into a code PR silently.
- Boundary rules (NetArchTest) fail the build if a layer breaches its allowed dependencies.
- Warnings are treated as errors (`Directory.Build.props`).
