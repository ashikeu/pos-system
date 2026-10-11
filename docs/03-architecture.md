# 03 — Architecture

RetailFlow POS · v0.1 · 2026-10-11 · decisions are justified in `../decisions.md`

---

## 1. System context

```mermaid
flowchart LR
  subgraph Clients
    DESK["Flutter Desktop POS<br/>(Windows, offline-first)"]
    TAB["Flutter Tablet POS<br/>(Android, offline-first)"]
    WEB["Flutter Web Admin<br/>(back office)"]
    MOB["Flutter Mobile<br/>(owner dashboard)"]
  end

  subgraph Edge
    NGX["Nginx / reverse proxy<br/>TLS, rate limit"]
  end

  subgraph Server["RetailFlow server"]
    API["RetailFlow.Api<br/>(modular monolith)"]
    WRK["RetailFlow.Workers<br/>(outbox, jobs, alerts)"]
  end

  subgraph Data
    DB[("SQL Server<br/>RetailFlow")]
    RDS[("Redis (optional)<br/>cache, locks")]
    MQ[("RabbitMQ (optional)<br/>integration events")]
  end

  subgraph External
    BKASH["bKash API"]
    NAGAD["Nagad API"]
    SMTP["Email / SMS"]
    OTEL["OpenTelemetry collector"]
  end

  DESK & TAB & WEB & MOB --> NGX --> API
  API --> DB
  API --> RDS
  WRK --> DB
  WRK --> MQ
  WRK --> BKASH
  WRK --> NAGAD
  WRK --> SMTP
  API --> BKASH
  API --> NAGAD
  API -. traces/metrics .-> OTEL
  WRK -. traces/metrics .-> OTEL
  DESK -. "local SQLite (Drift)" .-> DESK
  TAB -. "local SQLite (Drift)" .-> TAB
```

Device-side payment provider calls are **not** made from the terminal; the terminal asks the API,
which owns provider credentials. Offline therefore means cash/credit only (FR-OFF-08).

## 2. Layers

```mermaid
flowchart TD
  API["RetailFlow.Api<br/>controllers · auth · middleware · OpenAPI · DI composition"]
  WRK["RetailFlow.Workers<br/>outbox dispatcher · schedulers"]
  APP["RetailFlow.Application<br/>use-case handlers · DTOs · validators · ports (interfaces) · decorators"]
  DOM["RetailFlow.Domain<br/>entities · value objects · enums · domain services · invariants · events"]
  INF["RetailFlow.Infrastructure<br/>EF Core · repositories · query services · interceptors · adapters · outbox · providers"]

  API --> APP
  WRK --> INF
  INF --> APP
  APP --> DOM
  INF --> DOM
  API -. "DI only, no compile-time logic" .-> INF
```

Rules (ADR-0002):

- `Domain` has **zero** external dependencies — no EF Core, no ASP.NET, no DTOs.
- `Application` defines ports (`ISaleRepository`, `IStockLedger`, `IPaymentProvider`, `IClock`, …)
  and never references EF Core.
- `Infrastructure` is the only place with `DbContext`, SQL, HTTP clients and file I/O.
- `Api` wires everything and translates HTTP ↔ use cases. It contains no business rule.
- Phase 17 adds architecture tests that fail the build on a forbidden reference.

## 3. Modules

```mermaid
flowchart TB
  PLAT["Platform<br/>audit · outbox · idempotency · tenancy · clock · money · errors"]
  TEN["Tenancy"]
  ID["Identity"]
  CAT["Catalog"]
  PRI["Pricing & Tax"]
  INV["Inventory"]
  PRO["Procurement"]
  CUS["Customers & Credit"]
  LOY["Loyalty"]
  SAL["Sales / POS"]
  RET["Returns"]
  PAY["Payments"]
  CSH["Cash & Shifts"]
  SYN["Sync"]
  REP["Reporting"]

  SAL --> PRI & INV & PAY & CUS & LOY & CSH & CAT
  RET --> SAL & INV & PAY & LOY & CSH
  PRO --> INV & CAT
  PRI --> CAT
  LOY --> CUS
  SYN --> SAL & RET & CSH & CAT & CUS
  REP --> SAL & INV & PRO & PAY & CSH & CUS
  TEN --> PLAT
  ID --> TEN
  CAT & INV & PAY & CSH & CUS --> PLAT
```

Each module owns its tables. A module reads another module's data only through that module's
Application contract (e.g. Sales asks Pricing for a quote, asks Inventory to post movements).
`Reporting` is the sole exception: it owns read-only projections/queries that may span modules,
because reports inherently cross boundaries.

## 4. Request pipeline

```mermaid
sequenceDiagram
  participant C as Client
  participant M as Middleware
  participant Ctl as Controller
  participant D as Decorators
  participant H as Handler (use case)
  participant R as Repository / EF
  participant DB as SQL Server

  C->>M: HTTP + JWT + X-Branch-Id + Idempotency-Key
  M->>M: correlation id · request log · rate limit · exception→ProblemDetails
  M->>Ctl: authenticated principal
  Ctl->>Ctl: [HasPermission] · branch-scope check · model bind
  Ctl->>D: request DTO
  D->>D: FluentValidation → idempotency lookup → audit scope → transaction
  D->>H: validated request
  H->>R: load aggregates (locking where required)
  R->>DB: SQL
  H->>H: domain invariants, domain services
  H->>R: persist + outbox message + audit + idempotency result
  R->>DB: COMMIT
  D-->>Ctl: Result<T>
  Ctl-->>C: 200/201 + DTO, or ProblemDetails
```

Decorator order is fixed and explicit, outermost first: `Logging → Validation → Idempotency or
Transaction → Handler`. Idempotency and the transaction are **one** decorator, because the stored
response has to commit atomically with the business change and two independent decorators cannot
guarantee that (ADR-0025). It looks the key up before opening the transaction, then reserves it,
runs the handler and stores the response inside it. A request that is retryable implements
`IIdempotentRequest`; one that merely writes implements `ITransactionalRequest`; a read-only query
implements neither and gets no transaction. Audit rows are written by the handler through
`IAuditWriter` on the same context, so they commit with the change.

## 5. Transaction boundaries

| Operation | One DB transaction covers | Explicitly outside |
|---|---|---|
| **Any idempotent operation** | idempotency reservation + the handler's work + the stored response snapshot + outbox messages + audit rows + sync-cursor allocation (ADR-0025) | the key lookup that precedes it |
| **Create sale** | sale + lines + line taxes + discounts + stock movements + balance updates + reservations release + cash/credit payments + loyalty accrual + customer ledger + numbering allocation + outbox messages + idempotency record | provider payment calls, printing, email/SMS, report refresh |
| **Card/MFS payment** | tx1: payment row `Pending`; tx2: outcome applied (`Completed`/`Failed`/`UnknownOutcome`) + sale state + cash/loyalty effects | the provider HTTP call between tx1 and tx2 |
| **Provider callback** | verify + payment state transition + sale settle + outbox | nothing |
| **Return** | return + lines + refund(s) + stock movements (restock or damage) + loyalty reversal + customer ledger + approvals + outbox + idempotency | provider refund call (then a second tx), printing |
| **Goods receipt** | receipt + lines + stock movements + average-cost update + batch/serial creation + PO status + outbox | supplier notification |
| **Transfer dispatch / receive** | movements on one side + in-transit balance + transfer state + outbox | — |
| **Stock count posting** | count lines + correction movements + balances + approval record | — |
| **Shift close** | counts + totals snapshot + session state + approval + outbox | Z-report printing |
| **Sync push (batch)** | **one transaction per operation**, never per batch | the HTTP batch itself |

Isolation level: `READ COMMITTED` (SQL Server default) plus explicit row locks where correctness
needs them (`IRowLocker` → `UPDLOCK, HOLDLOCK, ROWLOCK` on `StockBalance`, numbering allocators,
credit accounts, cash sessions). Locks are always acquired in ascending key order to avoid
deadlocks. Retries come from the EF execution strategy (`EnableRetryOnFailure`), which the unit of
work wraps around the whole transaction — a retry re-runs the operation from a cleared change
tracker, never half of it.

The per-tenant sync cursor is allocated inside the same transaction by a single
`UPDATE ... OUTPUT` statement under a row lock, in one block per save (ADR-0018), so a 50-line sale
costs one allocation rather than fifty.

Long-running work (reports, imports, exports, reconciliation) never holds a transaction: it reads
with `AsNoTracking` and, where appropriate, snapshot reads.

## 6. The sale workflow (spec §7, implemented)

```mermaid
flowchart TD
  A["1 Validate context<br/>org · branch · terminal · open shift · permissions"] --> B["2 Validate lines<br/>product status · qty · price resolution · discount caps · tax rules"]
  B --> C["3 Validate stock<br/>row-lock balances · policy check · batch/serial pick"]
  C --> D["4 Create sale + lines<br/>snapshots: name, price, discount, tax, cost"]
  D --> E["5 Record payments<br/>cash/credit settle now · card/MFS → Pending"]
  E --> F["6 Post inventory movements<br/>ledger + balance projection"]
  F --> G["7 COMMIT<br/>+ idempotency record + audit"]
  G --> H["8 Publish events via outbox<br/>SaleCompleted · StockMoved · LoyaltyAccrued"]
  H --> I["9 Settle external payment<br/>then second tx applies outcome"]
  I --> J["10 Receipt<br/>generate · print · log print"]
  J -. "printer failure" .-> J2["Reprint (audited)<br/>never re-charge, never re-post"]
  I -. "timeout / ambiguous" .-> K["UnknownOutcome<br/>status-query job + reconciliation report"]
```

## 7. Offline and synchronization

```mermaid
flowchart LR
  subgraph Device
    UI["Checkout UI"] --> CTRL["GetX controllers"]
    CTRL --> LDOM["Local pricing/tax engine<br/>(same rules, versioned config)"]
    LDOM --> LDB[("Drift / SQLite")]
    LDB --> OBX["outbox_operations<br/>operation_id · attempts · state"]
    OBX --> SYNC["Sync engine<br/>backoff + jitter · batches"]
  end
  SYNC -- "POST /sync/push (Idempotency-Key = operation_id)" --> API["API"]
  API -- "per-operation results: applied | duplicate | conflict | rejected" --> SYNC
  SYNC -- "GET /sync/pull?since=<changeSequence>" --> API
  API -- "keyset page + tombstones + high-water mark" --> LDB
  SYNC --> ISS["Sync issues screen<br/>retry · amend · discard (audited)"]
```

Key properties (ADR-0017, ADR-0018):

- A lost acknowledgement is indistinguishable from a failure on the client, so the client **always**
  retries with the same `operation_id`; the server replays the stored result instead of re-posting.
- Pull is cursor-based on a per-tenant `ChangeSequence`, so an interrupted pull resumes exactly and
  never skips a record.
- The server is authoritative for price, tax, stock and credit; divergence is recorded and reported,
  not silently overwritten, because the customer already paid the printed amount.
- A sale is `LocallyAccepted` until acknowledged; the UI and the receipt say so.

## 8. Cross-cutting concerns

| Concern | Mechanism |
|---|---|
| Tenancy | `ITenantContext` from token · EF global filters · SaveChanges guard · composite FKs (ADR-0004) |
| AuthZ | `[HasPermission]` + branch-scope filter; permission set cached by `(user, version)` |
| Validation | FluentValidation per request DTO; domain invariants in entities |
| Errors | `Result<T>`/`Error` in Application; one exception handler → RFC 9457 ProblemDetails |
| Audit | `IAuditWriter`, automatic field diffs for flagged entities + explicit business audit events |
| Idempotency | `IdempotencyRecord` + decorator (ADR-0010) |
| Events | `OutboxMessage` + dispatcher with lease/backoff/dead-letter (ADR-0011) |
| Caching | `ICacheService` (in-memory default, Redis opt-in). Never caches stock, credit or payment state |
| Logging | Serilog structured JSON, correlation id, user/org/branch/terminal, PII redaction |
| Tracing/metrics | OpenTelemetry: HTTP, EF, outbox lag, sync queue depth, payment outcomes |
| Concurrency | `ConcurrencyToken` on aggregates; row locks for stock/cash/numbering; deadlock retry |
| Background work | `RetailFlow.Workers`: outbox dispatch, provider status queries, alerts, expiry/loyalty jobs, reconciliation, retention |
| Config | strongly-typed options with startup validation; environment overrides; secrets from the environment or a secret store |
| Health | `/health/live`, `/health/ready` (DB, cache, outbox backlog, migrations applied) |

## 9. Flutter architecture

```mermaid
flowchart TD
  V["Presentation (widgets, screens, dialogs)"] --> C["Controllers (GetX)"]
  C --> UC["Feature use cases / domain services<br/>(pricing, cart math, validation)"]
  UC --> R["Repositories (feature)"]
  R --> RDS["Remote data source (Dio)"]
  R --> LDS["Local data source (Drift DAOs)"]
  R --> OB["Outbox service"]
  OB --> SE["Sync engine"]
  RDS --> API["RetailFlow API"]
  SE --> API
  C --> HW["Hardware adapters<br/>printer · drawer · scale · scanner"]
```

- Feature-first folders; `core/` holds api, auth, database, errors, logging, network, permissions,
  sync, storage, widgets, localization.
- Routing by `GetPage` + `Bindings`; one controller per screen; app-wide services only for auth,
  api, db, sync, settings, connectivity.
- Two checkout layouts (keyboard desktop grid, touch tablet) share one controller, so business
  behaviour cannot diverge between form factors.
- Hardware is behind adapters with test doubles, so checkout logic is testable without devices.

## 10. Deployment (Phase 18 target)

```mermaid
flowchart LR
  subgraph Host["Single VM / on-prem server (pilot)"]
    NG["nginx:443"] --> A1["api container"]
    NG --> A2["api container (n)"]
    W["workers container"]
    S[("sqlserver container or managed instance")]
    R[("redis container (optional)")]
  end
  A1 & A2 & W --> S
  A1 & A2 --> R
  BK["backup job → off-host storage"] --> S
```

Migrations are **not** applied automatically on startup in production: CI produces an idempotent
script, a human reviews and applies it in a maintenance step, and the API refuses to start if the
schema version is older than the binary expects. Dev and test apply migrations automatically.
Rollback = redeploy the previous image + the documented down-script or restore point; runbooks in
`infra/runbooks/`.
