# RetailFlow POS

Multi-tenant Point of Sale and Retail Management platform — ASP.NET Core 8 + SQL Server backend,
Flutter clients (Windows desktop, Android/iOS, web admin), offline-first checkout.

**Status: Phase 1 complete (foundation).** The backend solution builds and its tests pass; no
business endpoints exist yet. See `tasks.md` for what is next.

## Start here

| Document | Purpose |
|---|---|
| [CLAUDE.md](CLAUDE.md) | How to work in this repo: stack, layout, commands, coding rules |
| [tasks.md](tasks.md) | Implementation checklist and current status |
| [decisions.md](decisions.md) | Architecture decision log (ADR-0001…0023) |
| [docs/01-requirements.md](docs/01-requirements.md) | Functional and non-functional requirements |
| [docs/02-business-rules.md](docs/02-business-rules.md) | Business rules, formulas, assumptions, open questions |
| [docs/03-architecture.md](docs/03-architecture.md) | Architecture, modules, diagrams, transaction boundaries |
| [docs/04-database-schema.md](docs/04-database-schema.md) | Entities, ERDs, constraints, indexes, retention |
| [docs/05-roadmap.md](docs/05-roadmap.md) | Phase plan, exit criteria, pilot gate, risks |

## Quick start (backend)

```bash
cd backend
dotnet tool restore      # pinned dotnet-ef 8.0.10
dotnet build             # warning-free (warnings are errors)
dotnet test              # 39 tests
dotnet run --project src/RetailFlow.Api   # Swagger at /swagger, health at /health/live
```

Development database: `Server=localhost;Database=RetailFlow;Trusted_Connection=True` (SQL Server
2022 on localhost). Configuration and secrets live in `appsettings*.json` (ADR-0023);
`appsettings.Production.json` is git-ignored — copy it from the `.example` file.
