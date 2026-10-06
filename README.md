# Pairrot

Pairrot matches incoming bank payments to expected payments and reports the results to its customers. It runs as a single ASP.NET Core application that serves an API and also runs background processing. Its first customer is [NaLekci](https://nalekci.cz).

The current scope is **Feature 1: tenants, subtenants, and API access**. See [docs/features/001-tenants.md](docs/features/001-tenants.md). The target architecture is described in [docs/architektura.md](docs/architektura.md) (in Czech).

## Tech stack

- .NET 10, ASP.NET Core minimal APIs
- EF Core 10 with Npgsql (PostgreSQL), snake_case naming
- PostgreSQL 17 in Docker for local development; [Neon](https://neon.tech) is the planned production database
- xUnit, Testcontainers

## Repository layout

```
src/
  Pairrot.Api              HTTP API: API key authentication, /v1/tenant, /v1/subtenants
  Pairrot.Core             Domain entities, input rules, API key format
  Pairrot.Infrastructure   EF Core DbContext, migrations, tenant services
  Pairrot.Admin            Administrative CLI: migrations, root tenant setup, root keys
tests/
  Pairrot.UnitTests
  Pairrot.IntegrationTests API tests against a real PostgreSQL (Testcontainers)
docs/                      Specifications (Czech) and feature scopes
```

## Getting started

Prerequisites: .NET SDK 10 (see `global.json`) and Docker.

```bash
# 1. Start PostgreSQL. It listens on localhost:5433 and keeps its data in a Docker volume.
docker compose up -d

# 2. Restore local tools (dotnet-ef).
dotnet tool restore

# 3. Apply migrations and create the nalekci.cz root tenant.
#    The tenant key and management key are printed once. Store them.
dotnet run --project src/Pairrot.Admin -- setup [--contact-email info@example.com]

# 4. Run the API. It listens on http://localhost:5034.
dotnet run --project src/Pairrot.Api
```

To change the database name, credentials, or port, copy `.env.example` to `.env`. If you change them, also update `ConnectionStrings:Pairrot` in `appsettings.Development.json`.

Example requests are in [src/Pairrot.Api/Pairrot.Api.http](src/Pairrot.Api/Pairrot.Api.http).

## Configuration

The database connection is read from `ConnectionStrings:Pairrot` (environment variable: `ConnectionStrings__Pairrot`). Local development values are in `appsettings.Development.json`. To use Neon or any other PostgreSQL, set the environment variable; no code changes are needed:

```bash
ConnectionStrings__Pairrot="Host=<neon-host>;Database=pairrot;Username=<user>;Password=<password>;SSL Mode=Require"
```

Do not commit real credentials.

## API overview

All endpoints use `Authorization: Bearer <key>` and JSON with snake_case fields. Keys have the format `prt_<key-id>_<secret>`. Pairrot stores only a SHA-256 hash of the secret, so a key's secret is shown only when the key is created.

| Key purpose | Endpoints |
| --- | --- |
| Tenant key | `GET /v1/tenant`, `GET/PATCH /v1/tenant/configuration` |
| Management key (root tenant with `can_create_subtenants`) | `POST/GET /v1/subtenants`, `GET/PATCH/DELETE /v1/subtenants/{id}`, `GET/POST /v1/subtenants/{id}/api-keys`, `DELETE /v1/subtenants/{id}/api-keys/{keyId}` |

Errors use the format `{"error": {"code": "...", "message": "..."}}` with the statuses 400, 401, 403, 404, and 409. A resource that exists but belongs to another tenant returns the same 404 as an unknown identifier. The full contract is in the [feature spec](docs/features/001-tenants.md#4-api-contract).

## Administrative CLI

Root tenants and their keys are managed from the command line, never over HTTP:

```bash
dotnet run --project src/Pairrot.Admin -- migrate
dotnet run --project src/Pairrot.Admin -- setup [--name <name>] [--contact-email <email>]
dotnet run --project src/Pairrot.Admin -- keys list
dotnet run --project src/Pairrot.Admin -- keys issue --purpose tenant|management [--label <label>]
dotnet run --project src/Pairrot.Admin -- keys revoke <key-id>
```

All commands accept `--root <external-id>` (default `nalekci.cz`) and `--operator <name>`, which is recorded in the audit log. You can run `setup` again safely: it never creates a second root tenant, never changes an existing one, and never replaces keys.

## Database migrations

```bash
dotnet ef migrations add <Name> --project src/Pairrot.Infrastructure --output-dir Persistence/Migrations
dotnet run --project src/Pairrot.Admin -- migrate
```

The EF model mirrors [docs/features/001-tenants-schema.sql](docs/features/001-tenants-schema.sql), including its check constraints and unique indexes.

## Tests

```bash
dotnet test
```

Integration tests start their own PostgreSQL container through Testcontainers, so Docker must be running. They do not use the development database.
