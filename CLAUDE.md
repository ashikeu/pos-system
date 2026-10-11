# CLAUDE.md — RetailFlow POS

Working guide for any AI/human contributor in this repository. Read this first, then
`tasks.md` (what to build next) and `decisions.md` (why things are the way they are).

---

## 1. What this is

**RetailFlow POS** — a production-oriented, multi-tenant Point of Sale and Retail Management
platform for small shops, supermarkets, pharmacies, electronics stores, wholesalers and
multi-branch retailers. Primary market: **Bangladesh** (BDT, VAT/SD, bKash/Nagad, Bangla + English).

It is a real business application, not a demo. Non-negotiables:

- **Financial correctness** over feature count.
- **Tenant isolation** enforced server-side, always.
- **Offline-first POS**: a sale must be recordable with no network, and must never
  double-post when connectivity returns.
- **Auditability**: stock and money move only through ledger/journal records.

## 2. Stack (fixed; see `decisions.md` before changing)

| Layer | Choice |
|---|---|
| Backend | ASP.NET Core Web API, **.NET 8** (`net8.0`), C# 12, nullable enabled, warnings-as-errors |
| ORM | EF Core 8 (`Microsoft.EntityFrameworkCore.SqlServer`) |
| Database | **SQL Server on localhost** (dev). Schema and code kept provider-portable (MySQL/PostgreSQL later) — ADR-0003 |
| Auth | JWT access tokens + rotating refresh tokens, permission-based authorization |
| Validation | FluentValidation, invoked by an application pipeline behaviour |
| Messaging | Transactional outbox + in-process dispatcher; RabbitMQ only when a real consumer exists — ADR-0011 |
| Cache | `ICacheService` abstraction, in-memory default, Redis opt-in via config |
| Frontend | Flutter (Windows desktop, Web admin, Android, iOS), Dart null-safe, **GetX**, **Dio**, **Drift/SQLite** |
| Infra | Docker Compose (dev), Nginx, GitHub Actions CI, OpenTelemetry |
| Config | All settings and secrets in `appsettings*.json` (ADR-0023); no vault or env-var requirement |

Verified on this machine (2026-10-11): .NET SDK 8.0.303 · Flutter 3.47.1 / Dart 3.13.1 ·
Docker 29.7.2 (**daemon not currently running**) · **SQL Server 2022 Standard 16.0.1000.6 reachable
on `localhost` with Windows authentication** · SQL Server LocalDB (`MSSQLLocalDB`) as a fallback ·
`sqlcmd`.

> The global `dotnet-ef` is 10.0.9 and needs a .NET 10 runtime that is **not** installed here.
> Always use the repo-local pinned tool: `dotnet tool restore`, then `dotnet ef ...` from `backend/`.

## 3. Repository layout

```
pos-system/
├── CLAUDE.md                     # this file
├── tasks.md                      # implementation checklist (single source of truth for status)
├── decisions.md                  # ADR log — append, never silently rewrite
├── docs/
│   ├── 01-requirements.md        # functional + non-functional requirements
│   ├── 02-business-rules.md      # business rules, formulas, assumptions, open questions
│   ├── 03-architecture.md        # architecture, module map, diagrams, transaction boundaries
│   ├── 04-database-schema.md     # entities, ERDs, constraints, indexes, retention
│   └── 05-roadmap.md             # phase plan, dependencies, definition of done
├── backend/
│   ├── RetailFlow.sln
│   ├── Directory.Build.props     # shared compiler settings
│   ├── Directory.Packages.props  # central package versions
│   ├── .config/dotnet-tools.json
│   ├── src/
│   │   ├── RetailFlow.Domain/          # entities, value objects, domain services, invariants
│   │   ├── RetailFlow.Application/     # use cases, DTOs, ports (interfaces), validators
│   │   ├── RetailFlow.Infrastructure/  # EF Core, repositories, adapters, outbox, providers
│   │   ├── RetailFlow.Api/             # controllers, auth, middleware, OpenAPI, composition root
│   │   └── RetailFlow.Workers/         # background host: outbox dispatch, jobs, alerts
│   └── tests/
│       ├── RetailFlow.Domain.UnitTests/
│       ├── RetailFlow.Application.UnitTests/
│       └── RetailFlow.IntegrationTests/   # real SQL Server (LocalDB or Testcontainers)
├── frontend/                     # Flutter app (feature-first) — Phase 13+
└── infra/                        # docker, nginx, ci, backup/restore scripts, runbooks
```

### Module boundaries (modular monolith)

`Domain`, `Application` and `Infrastructure` each use the same module folders. A module may
depend on `Platform` (shared kernel) and on other modules **only through their
Application-layer contracts** — never on another module's entities or EF configuration.

`Platform` · `Tenancy` · `Identity` · `Catalog` · `Pricing` · `Inventory` · `Procurement` ·
`Sales` · `Returns` · `Payments` · `Customers` · `Loyalty` · `Cash` · `Sync` · `Reporting`

Dependency rule, enforced by project references:
`Api → Application → Domain`, `Infrastructure → Application → Domain`, `Workers → Infrastructure`.
**Domain references nothing.** No EF Core types in Domain or Application.

## 4. Commands

Run from `backend/` unless stated otherwise.

```bash
dotnet tool restore                  # restore pinned dotnet-ef (once per clone)
dotnet restore
dotnet build                         # must be warning-free (warnings are errors)
dotnet format --verify-no-changes    # style gate
dotnet test                          # all tests
dotnet test tests/RetailFlow.Domain.UnitTests   # one project

# EF Core (always pass the startup project)
dotnet ef migrations add <Name> -p src/RetailFlow.Infrastructure -s src/RetailFlow.Api
dotnet ef database update          -p src/RetailFlow.Infrastructure -s src/RetailFlow.Api
dotnet ef migrations script        -p src/RetailFlow.Infrastructure -s src/RetailFlow.Api -i -o ../artifacts/migrate.sql

dotnet run --project src/RetailFlow.Api    # http://localhost:5274, Swagger UI at /swagger
#   Development applies migrations, syncs the permission catalog and seeds the demo tenant:
#   organization DEMO (0193f000-...-0001), branch MAIN (...-0002), owner (...-0003).
#   Sign in with organizationCode DEMO, email owner@demo.retailflow.local and the
#   development password in IdentitySeeder.
```

Integration tests create their own database per run. They look for a server in this order:
`RETAILFLOW_TEST_CONNECTION` (used by CI with Testcontainers), then `localhost`, then LocalDB.

```bash
dotnet test tests/RetailFlow.IntegrationTests            # local SQL Server
RETAILFLOW_TEST_CONNECTION="Server=...;Integrated Security=True" dotnet test   # explicit server
```

Flutter (from `frontend/retailflow_app/`, Phase 13+):

```bash
flutter pub get
dart run build_runner build --delete-conflicting-outputs   # Drift / json_serializable
flutter analyze
dart format --set-exit-if-changed lib test
flutter test
flutter run -d windows            # desktop POS
flutter run -d chrome             # web admin
```

Local SQL Server connection (dev default, overridable by `ConnectionStrings__Default`):

```
Server=localhost;Database=RetailFlow;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True
```

## 5. Coding rules

**Backend**

1. No business logic in controllers. Controllers: bind → authorize → call use case → map response.
2. No EF Core (`DbContext`, `DbSet`, `Include`, migrations) outside `Infrastructure`.
3. Money is `decimal` — never `double`/`float`. Column types per ADR-0006:
   `decimal(19,4)` amounts, `decimal(18,3)` quantities, `decimal(19,6)` unit cost, `decimal(9,6)` rates.
4. All persisted instants are **UTC** `datetime2(3)` and named `...AtUtc`. Business dates are
   `date` in branch-local time — ADR-0007.
5. Every tenant-owned entity carries `OrganizationId`, derives from the tenant base type and is
   covered by the global query filter. Never build a query that trusts a client-supplied
   `OrganizationId` — ADR-0004.
6. Server-side authorization on every endpoint: `[HasPermission(Permissions.X)]`. Hiding a button
   is not authorization.
7. Stock changes only through inventory ledger entries. Balances are projections, never a
   free-form `UPDATE`.
8. Retryable business operations require an `Idempotency-Key` and return the stored result on
   replay — ADR-0010.
9. Entities are never API contracts. DTOs live in `Application/<Module>/Contracts`.
10. Errors are RFC 9457 `application/problem+json` carrying `traceId` and `correlationId` — ADR-0014.
11. External calls (payment providers, printers, SMS) never run inside a DB transaction.
12. Always `SaveChangesAsync`; the synchronous overload throws by design (ADR-0026).
13. Every endpoint carries `[HasPermission(...)]` or a documented reason to be anonymous. Never
    check a role name directly - permissions are the contract (ADR-0013).
14. Permissions are resolved server-side per request; a permission list in a token or a request
    body is decoration, never authorization (ADR-0027).
15. A retryable operation's request implements `IIdempotentRequest`, a plain write implements
    `ITransactionalRequest`, a query implements neither — the pipeline reads those markers to decide
    transactions and replay (ADR-0025).

**Flutter**

1. No business logic in widgets. GetX controllers orchestrate; repositories do I/O.
2. One controller per screen/feature, wired by `Bindings`. Avoid global singletons except true
   app-wide services (auth, api, db, sync, settings).
3. All user-facing strings go through localization (`en`, `bn`). No hardcoded copy in widgets.
4. Local writes that must reach the server go through the device outbox with a client-generated
   idempotency key — never a bare HTTP call.
5. A sale is visibly either *locally accepted* or *server-confirmed*. Never blur the two.

## 6. Workflow for every change

1. Read `tasks.md`; take the next unblocked item in the current phase.
2. Implement one complete vertical slice: domain → persistence → use case → API → tests → docs.
3. Run `dotnet build`, `dotnet format`, `dotnet test` (plus `flutter analyze` / `flutter test` for
   frontend work). Fix failures before moving on.
4. Update `tasks.md` status, append new ADRs to `decisions.md`, update the affected `docs/` pages.
5. Report: files changed, commands run, **actual** output, known limitations.

Hard rules:

- Never claim a test, build or migration passed unless it was executed and its output is at hand.
- No pseudocode, stub services, hardcoded fake responses or dead buttons in delivered work.
- Ambiguity → record a reasonable assumption in `docs/02-business-rules.md` and continue, **unless**
  it affects money, security, data integrity or tax compliance; those get asked, not assumed.
- Configuration and secrets live in `appsettings*.json` (ADR-0023). `appsettings.json` holds
  dev-only values and is committed; `appsettings.Production.json` / `appsettings.Staging.json` hold
  real credentials and are git-ignored. Never commit those two, or real customer data.
- Don't regenerate the project to make a local change.
