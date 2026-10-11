# decisions.md — Architecture Decision Log

Append-only. One entry per decision, newest at the bottom of its phase section. Never delete an
entry — supersede it with a new one and mark the old `Superseded by ADR-XXXX`.

Status values: `Accepted` · `Proposed` · `Superseded` · `Deprecated`.

| ADR | Title | Status | Date |
|---|---|---|---|
| [0001](#adr-0001) | Modular monolith, not microservices | Accepted | 2026-10-11 |
| [0002](#adr-0002) | Four-project clean architecture with module folders | Accepted | 2026-10-11 |
| [0003](#adr-0003) | SQL Server as primary database, provider-portable design | Accepted | 2026-10-11 |
| [0004](#adr-0004) | Shared-schema multi-tenancy with enforced tenant context | Accepted | 2026-10-11 |
| [0005](#adr-0005) | UUIDv7 primary keys, client-generatable | Accepted | 2026-10-11 |
| [0006](#adr-0006) | Money, quantity and rounding policy | Accepted | 2026-10-11 |
| [0007](#adr-0007) | UTC instants, branch-local business dates, server-authoritative clock | Accepted | 2026-10-11 |
| [0008](#adr-0008) | Append-only inventory ledger with projected balances | Accepted | 2026-10-11 |
| [0009](#adr-0009) | Moving weighted average costing, COGS snapshot on sale line | Accepted | 2026-10-11 |
| [0010](#adr-0010) | Idempotency keys with stored responses | Accepted | 2026-10-11 |
| [0011](#adr-0011) | Transactional outbox now, RabbitMQ/Redis only when justified | Accepted | 2026-10-11 |
| [0012](#adr-0012) | Password hashing and token strategy | Accepted | 2026-10-11 |
| [0013](#adr-0013) | Permission catalog in code, assignments in database | Accepted | 2026-10-11 |
| [0014](#adr-0014) | REST conventions, ProblemDetails errors, hybrid pagination | Accepted | 2026-10-11 |
| [0015](#adr-0015) | Use-case services without a mediator library | Accepted | 2026-10-11 |
| [0016](#adr-0016) | Payment states and the no-DB-transaction-around-provider rule | Accepted | 2026-10-11 |
| [0017](#adr-0017) | Offline-first client: device outbox and configuration snapshots | Accepted | 2026-10-11 |
| [0018](#adr-0018) | Sync cursor via per-tenant change sequence | Accepted | 2026-10-11 |
| [0019](#adr-0019) | Versioned, effective-dated tax and pricing configuration | Accepted | 2026-10-11 |
| [0020](#adr-0020) | Testing stack and integration-test database strategy | Accepted | 2026-10-11 |
| [0021](#adr-0021) | Central package management, warnings-as-errors, pinned EF tooling | Accepted | 2026-10-11 |
| [0022](#adr-0022) | GetX + Drift structure on the Flutter side | Accepted | 2026-10-11 |
| [0023](#adr-0023) | Configuration and secrets live in appsettings files | Accepted | 2026-10-11 |
| [0024](#adr-0024) | Non-clustered GUID keys with purpose-chosen clustered indexes | Accepted | 2026-10-11 |
| [0025](#adr-0025) | The idempotency decorator owns its transaction | Accepted | 2026-10-11 |
| [0026](#adr-0026) | Asynchronous saves only, and a fail-closed tenant filter | Accepted | 2026-10-11 |
| [0027](#adr-0027) | Token model: scope in the token, permissions from the database | Accepted | 2026-10-11 |
| [0028](#adr-0028) | Cryptographic choices for passwords, TOTP and secrets at rest | Accepted | 2026-10-11 |
| [0029](#adr-0029) | What invalidates a session, and what deliberately does not | Accepted | 2026-10-11 |

---

## Phase 1 — Foundation

### ADR-0001
**Modular monolith, not microservices** · Accepted · 2026-10-11

*Context.* The platform spans POS, inventory, procurement, payments, loyalty, cash and reporting.
A single shop pilot will run on one small server; the deployment target is on-premise or a single
cloud VM per region.

*Decision.* One deployable API (`RetailFlow.Api`) plus one background host (`RetailFlow.Workers`),
internally partitioned into modules with explicit contracts. Cross-module calls go through
Application-layer interfaces only. Each module owns its tables; no module reads another module's
tables directly — it calls the owning module or reads a reporting projection.

*Consequences.* Simple ops, one transaction scope for a sale (which is what correctness needs).
Extraction of a module later requires replacing in-process contract calls with transport calls —
cheap because the seam already exists. Enforced by review, plus (Phase 17) an architecture test
asserting no forbidden namespace references.

---

### ADR-0002
**Four-project clean architecture with module folders** · Accepted · 2026-10-11

*Decision.* `Domain` (no dependencies) → `Application` (depends on Domain; defines ports) →
`Infrastructure` (implements ports, owns EF Core) → `Api` (composition root, HTTP only).
`Workers` references Infrastructure. Module folders are repeated identically across the three
inner projects.

*Rejected.* A project per module (≈45 projects: slow builds, ceremony for a team of this size).
A generic repository over `DbSet<T>` (hides EF capabilities, adds nothing). Repositories exist only
where they earn it: aggregates with invariants (Sale, StockBalance, CashSession) and anything needing
locking hints. Queries for lists and reports use read-only EF projections in Infrastructure behind
a named query interface.

---

### ADR-0003
**SQL Server as primary database, provider-portable design** · Accepted · 2026-10-11

*Context.* The specification conflicts: §2 names SQL Server as primary with later portability to
MySQL/PostgreSQL; §17 says PostgreSQL. The user confirmed on 2026-10-11: **SQL Server on localhost**.

*Decision.* `Microsoft.EntityFrameworkCore.SqlServer`, dev database `RetailFlow` on `localhost`
(LocalDB `MSSQLLocalDB` is an acceptable fallback). To keep MySQL/PostgreSQL reachable later:

- No vendor-specific EF APIs in `Application`/`Domain`; all provider calls live in
  `Infrastructure/Platform/Persistence`.
- No `rowversion` as a public concept — concurrency uses a `byte[] ConcurrencyToken` mapped to
  `rowversion` on SQL Server and to a provider equivalent elsewhere, behind one mapping convention.
- No T-SQL in migrations unless wrapped in a provider guard; no computed columns, no
  `SEQUENCE`-only features in business logic, no `MERGE`.
- Locking hints (`UPDLOCK, ROWLOCK`) are isolated in a single `IRowLocker` adapter with a
  provider-specific implementation (`FOR UPDATE` on PostgreSQL/MySQL).
- Identifiers ≤ 30 chars, lower-snake-case-safe names, no reserved words, explicit index names.
- Migrations are generated per provider; the model is shared. Provider is selected by
  `Database:Provider` config.

*Consequences.* A slight loss of SQL Server-specific optimisation; `rowversion`'s automatic update
semantics must be emulated for other providers (planned: an interceptor stamping a GUID token).
PostgreSQL row-level security as a second isolation layer is **not** available on SQL Server; the
equivalent safeguard is ADR-0004's interceptor.

---

### ADR-0004
**Shared-schema multi-tenancy with enforced tenant context** · Accepted · 2026-10-11

*Decision.* One database, one schema, `OrganizationId uniqueidentifier NOT NULL` on every
tenant-owned table. Defence in depth:

1. `ITenantContext` is resolved per request from the **access token**, never from a body/query
   parameter. Missing tenant context on a tenant-scoped operation is a 500-class bug, not a default.
2. EF global query filter on every entity implementing `ITenantOwned`.
3. A `SaveChanges` interceptor stamps `OrganizationId` on inserts and **rejects** any tracked
   entity whose `OrganizationId` differs from the ambient tenant (`TenantMismatchException`).
4. Composite foreign keys include `OrganizationId` wherever a child could otherwise reference a
   parent in another tenant (e.g. `SaleLine → Product`), making cross-tenant references
   unrepresentable at the database level.
5. Unique constraints are per tenant: `(OrganizationId, Code)`, `(OrganizationId, Barcode)` etc.
6. Branch scope: the requested `X-Branch-Id` must appear in the caller's `UserBranch` grants;
   validated in an authorization filter, not in handlers.
7. A platform-admin ("host") role may cross tenants only through explicitly marked endpoints that
   log every access to the audit log.

*Consequences.* Cheap operationally; noisy keys. Tenant-isolation integration tests are mandatory
(Phase 3 onward) and run on every CI build.

---

### ADR-0005
**UUIDv7 primary keys, client-generatable** · Accepted · 2026-10-11

*Context.* Offline terminals must create sales, lines, payments and ledger rows locally and keep
those identifiers after sync. Server-assigned `bigint` identities cannot do that.

*Decision.* All business entity keys are `Guid` generated as **UUIDv7** (time-ordered) by a
`IGuidGenerator`. Time ordering keeps SQL Server clustered-index inserts sequential, so GUIDs cost
little fragmentation. Reference/lookup tables that never originate offline may use `int` identity.
Human-facing numbers (invoice no., PO no., receipt no.) are **separate** generated strings with
their own uniqueness rules — never the PK.

*Consequences.* 16-byte keys. A malicious client could choose an ID; the server therefore rejects
IDs that already exist with different content (idempotency, ADR-0010) and never trusts a client ID
to imply ownership.

> **Amended by [ADR-0024](#adr-0024).** The claim that time ordering keeps SQL Server clustered
> inserts sequential is wrong: SQL Server orders `uniqueidentifier` by its *last* six bytes, so a
> UUIDv7 primary key is effectively random for clustering. The identifier format stands; the
> physical layout changed.

---

### ADR-0006
**Money, quantity and rounding policy** · Accepted · 2026-10-11

*Decision.*

| Concept | CLR | Column | Notes |
|---|---|---|---|
| Amount (line, total, payment) | `decimal` | `decimal(19,4)` | stored at 4 dp, presented at 2 dp |
| Quantity | `decimal` | `decimal(18,3)` | supports weighed goods (kg, litre) |
| Unit cost / landed cost | `decimal` | `decimal(19,6)` | averaging needs the extra digits |
| Rate (tax %, discount %) | `decimal` | `decimal(9,6)` | 7.5% stored as 7.500000 |
| Currency | — | `char(3)` | `BDT` default, per organization |

- A `Money` value object (amount + currency) guards arithmetic; mixing currencies throws.
- Rounding: `MidpointRounding.AwayFromZero` ("half-up"), the retail convention in BD.
- Order of computation per sale line: `gross = qty × unitPrice` → line discount → taxable base →
  tax → line total. Each step is rounded to 4 dp; the bill total is rounded to 2 dp **once**.
- Bill-level discounts are allocated to lines by the **largest-remainder method** so the sum of
  line amounts always equals the bill total exactly (no penny drift) and so returns/tax reporting
  can be computed per line.
- Cash rounding (to the nearest configured increment, default 1.00 BDT) is a separate, explicit
  `CashRoundingAdjustment` line on the payment, never a silent change to the sale total.

---

### ADR-0007
**UTC instants, branch-local business dates, server-authoritative clock** · Accepted · 2026-10-11

*Decision.* Persisted instants are UTC `datetime2(3)` named `...AtUtc`, via `IClock`
(`DateTimeOffset UtcNow`) — never `DateTime.Now` in business code. Each branch stores an IANA
timezone (default `Asia/Dhaka`, UTC+6, no DST) and a `BusinessDayStart` time; a `BusinessDate`
(`date`) column is stamped on sales, shifts and inventory movements for reporting, derived from the
branch timezone — reports group by `BusinessDate`, never by UTC day.

*Offline clock skew.* Devices record `DeviceCreatedAtUtc`; the server also records
`ServerReceivedAtUtc` and `ClockSkewSeconds`. The **server's** value drives accounting periods. If
skew exceeds a configurable threshold (default 15 min), the sale syncs but is flagged for review.
A device whose clock is behind the last-issued terminal sequence timestamp may not reuse sequence
numbers — sequence allocation is per terminal and monotonic (ADR-0017).

---

### ADR-0008
**Append-only inventory ledger with projected balances** · Accepted · 2026-10-11

*Decision.* `InventoryMovement` is append-only (insert only; no update, no delete) and is the single
source of truth for stock. `StockBalance` is a projection row per
`(OrganizationId, WarehouseId, ProductVariantId, BatchId?)` holding `QuantityOnHand`,
`QuantityReserved`, `QuantityInTransit`, `AverageUnitCost`, `ConcurrencyToken`, maintained inside the
same transaction as the movement that caused it. A reconciliation job (Phase 17) re-sums the ledger
and alerts on drift.

*Overselling.* When the product is stock-controlled and the organization/branch policy forbids
negative stock, the sale transaction takes a **row lock** on the affected `StockBalance` rows
(`IRowLocker`, ordered by key to avoid deadlocks), re-reads the quantity, validates, then writes.
Optimistic concurrency alone is not enough for the "last unit sold on two terminals" case. Where
negative stock is allowed, balances are updated optimistically with retry.

*Corrections.* A wrong movement is fixed by a compensating movement (`AdjustmentReversal`) that
references the original; the original stays.

---

### ADR-0009
**Moving weighted average costing, COGS snapshot on sale line** · Accepted · 2026-10-11

*Decision.* Default valuation is **moving weighted average** per
`(warehouse, variant[, batch])`:
`newAvg = (onHand × avg + receivedQty × receivedUnitCost) / (onHand + receivedQty)`, recomputed on
every inbound movement (purchase receipt, customer return to stock, positive adjustment, transfer-in
at source cost). Outbound movements consume at the current average, and that unit cost is **copied
onto the sale line** (`UnitCostSnapshot`) so COGS and gross profit are reproducible even if the
average later changes. Batch-tracked items average within the batch; serial-tracked items carry
their own actual cost.

The costing method is stored on `OrganizationSettings` (`WeightedAverage` today; `Fifo` reserved) so
FIFO can be added without a schema break. Negative on-hand freezes the average (no divide-by-zero,
no cost inversion) and records a `CostingAnomaly` audit entry.

*Consequences.* COGS is deterministic per line. Retroactive cost edits are not supported — a cost
correction is a new adjustment movement, which is what auditability requires.

---

### ADR-0010
**Idempotency keys with stored responses** · Accepted · 2026-10-11

*Decision.* Every retryable mutating operation (sale, return, payment, shift close, sync push,
transfer, receipt) requires an `Idempotency-Key` header (client-generated UUIDv7). An
`IdempotencyRecord` table keyed by `(OrganizationId, Key)` stores: endpoint, request body hash,
status, HTTP status code, response payload, created/completed timestamps.

- First call: record inserted `InProgress` **in the same transaction** as the business work; on
  commit, status becomes `Completed` with the response snapshot.
- Replay with the same key and same body hash → stored response returned verbatim (200/201 as
  recorded), nothing re-executed.
- Same key, **different** body hash → `409 idempotency_key_reuse`.
- Replay while `InProgress` → `409 operation_in_progress` with `Retry-After`; the client retries
  with the same key.
- Retention: 90 days (configurable), purged by a background job.

*Rationale.* A lost acknowledgement must never create a second sale. A timeout tells the client
nothing about the server's outcome, so the same key must be safe to retry forever.

---

### ADR-0011
**Transactional outbox now, RabbitMQ/Redis only when justified** · Accepted · 2026-10-11

*Decision.* Domain events that must not be lost (`SaleCompleted`, `StockMoved`,
`PaymentSettled`, `LoyaltyAccrued`, `GoodsReceived`) are written to `OutboxMessage` in the same
transaction as the state change. A dispatcher in `RetailFlow.Workers` claims batches with a lease,
delivers to in-process handlers, retries with exponential backoff + jitter, and moves exhausted
messages to a dead-letter state with an alert.

RabbitMQ is wired behind `IEventPublisher` but **off by default** (`Messaging:Provider=InProcess`) —
turned on when a genuine out-of-process consumer exists. Redis likewise sits behind `ICacheService`
and `IDistributedLock`, defaulting to in-memory implementations so single-node dev and small pilots
need neither service.

---

### ADR-0012
**Password hashing and token strategy** · Accepted · 2026-10-11

*Decision.*

- Passwords: ASP.NET Core `PasswordHasher<T>` (PBKDF2-HMAC-SHA512, 210 000 iterations, 128-bit salt,
  versioned format with automatic rehash-on-login). Chosen over Argon2id to avoid a third-party
  native dependency in v1; the hasher is behind `IPasswordHasher`, so an Argon2id upgrade is a
  swap plus rehash-on-login. Documented as a planned hardening item.
- Access token: JWT, HS256 in dev / RS256 in production (key from secret store), default lifetime
  **10 minutes**, claims: `sub`, `org`, `roles`, `perm` (hashed permission set version), `sid`,
  `terminal`. Permissions are **not** inlined as dozens of claims; the API resolves the permission
  set from cache keyed by `(userId, permissionsVersion)` so a revocation takes effect immediately
  on version bump.
- Refresh token: 64 bytes of CSPRNG randomness, stored **hashed** (SHA-256), default lifetime 30
  days, single-use with rotation. Tokens form a family (`FamilyId`); presenting a previously-used
  token revokes the whole family and raises a security audit event (reuse detection).
- Logout revokes the session; "logout all devices" revokes every family for the user.
- Lockout: 5 failed attempts → 15-minute lock, exponential on repeat; per-IP and per-account rate
  limits on `/auth/*`.
- POS terminals additionally hold a long-lived **device registration** (device id + secret, hashed)
  so an offline terminal can refresh after an extended outage without re-entering credentials; the
  device secret is revocable per terminal and never grants permissions beyond the signed-in user's.

---

### ADR-0013
**Permission catalog in code, assignments in database** · Accepted · 2026-10-11

*Decision.* Permission codes are `const string` in `Domain/Identity/Permissions.cs`
(`sales.create`, `sales.void`, `price.override`, `inventory.adjust.approve`, …) and are seeded into
a `Permission` table on startup so roles can reference them. Roles are per organization (with
system role templates: Owner, Manager, Cashier, Stock Keeper, Accountant, Auditor). Authorization
uses one `HasPermissionAttribute` + requirement handler; no bespoke `if (role == "Admin")` checks
anywhere. Branch-scoped grants live in `UserBranch` with their own permission overrides, so a user
can be Manager in one branch and Cashier in another.

*Consequences.* Adding a permission is a code change plus seed, which is correct — permissions are
part of the application's contract, not user data. Tests assert every endpoint carries an explicit
policy (authorization regression test, Phase 17).

---

### ADR-0014
**REST conventions, ProblemDetails errors, hybrid pagination** · Accepted · 2026-10-11

*Decision.* `/api/v1/...`, URL-segment versioning, plural nouns, verbs only for genuine actions
(`/sales/{id}/void`). Request/response DTOs only. Errors are RFC 9457 `application/problem+json`:

```json
{ "type": "https://retailflow.dev/errors/insufficient-stock", "title": "Insufficient stock",
  "status": 409, "detail": "...", "code": "inventory.insufficient_stock",
  "traceId": "00-...", "correlationId": "...", "errors": { "lines[0].quantity": ["..."] } }
```

Status mapping: 400 validation · 401 unauthenticated · 403 permission/tenant · 404 not found ·
409 business-rule conflict/concurrency/idempotency · 422 semantically invalid · 429 rate limited ·
503 dependency down. Every response carries `X-Correlation-Id` (echoed from the request when
supplied) and is logged with it.

Pagination: offset (`page`, `pageSize`, max 200) for admin lists; **cursor** (keyset on
`(ChangeSequence, Id)`) for sync pulls and large exports. Sorting/filtering via explicit allow-lists,
never free-form field names.

---

### ADR-0015
**Use-case services without a mediator library** · Accepted · 2026-10-11

*Decision.* One class per use case (`CreateSaleHandler`, `ReceiveGoodsHandler`) exposing
`Task<Result<TResponse>> Handle(TRequest, CancellationToken)`, registered by convention and injected
into controllers directly. Cross-cutting concerns (validation, transaction, idempotency, audit) are
applied by explicit decorators registered in a fixed order, not by a runtime pipeline resolved
through reflection.

*Rationale.* Keeps stack traces and navigation direct, avoids MediatR's licensing change, and still
gives one concern per class. Not full CQRS: reads are plain query services; there is no separate
write model or event sourcing (not justified here, per §3 of the spec).

---

### ADR-0016
**Payment states and the no-DB-transaction-around-provider rule** · Accepted · 2026-10-11

*Decision.* `Payment` is a state machine:
`Pending → Authorized → Completed`, plus `Failed`, `Reversed`, `RefundPending`, `Refunded`, and
`UnknownOutcome` (reconciliation required). Legal transitions are enforced in the domain; an illegal
transition throws rather than silently correcting.

- Cash and customer-credit payments settle synchronously inside the sale transaction.
- Card/bKash/Nagad go through `IPaymentProvider` adapters **outside** any DB transaction: the sale is
  committed with the payment in `Pending`/`Authorized`, the provider is called, and the outcome is
  applied in a second short transaction. A network timeout or ambiguous reply sets `UnknownOutcome`
  and queues a provider status query — never `Completed`.
- Provider callbacks verify signature, replay-protect by `(ProviderId, ProviderReference)` unique
  index, and are idempotent.
- No raw PAN, CVV or track data is ever stored, logged or sent to our API — only provider tokens,
  references, masked last-4 and brand.
- Printing is **after** commit and settlement. A printer failure can only trigger a reprint
  (audited), never a re-charge or a second sale.
- Each provider's reconciliation report is imported daily and matched on reference; mismatches raise
  an exception report rather than auto-adjusting ledgers.

---

### ADR-0017
**Offline-first client: device outbox and configuration snapshots** · Accepted · 2026-10-11

*Decision.* The Flutter app keeps a Drift/SQLite database holding: catalog + permitted prices, tax
and discount configuration (versioned, ADR-0019), terminal/branch config, a bounded customer set,
pending sales/returns/payments, and sync checkpoints. Every local mutation that must reach the
server is appended to a local `outbox_operations` table with a client-generated UUIDv7
`operation_id` used as the server idempotency key, plus attempt count, next-attempt time, status
(`pending|in_flight|synced|failed|conflict`) and the last server error.

- A sale saved offline is **LocallyAccepted** and labelled as such in the UI and on the receipt
  ("Pending server confirmation"); it becomes **ServerConfirmed** only on a server acknowledgement.
- Receipt numbers offline come from a per-terminal pre-allocated sequence block
  (`TerminalSequenceBlock`, e.g. 500 numbers, renewed on sync), so numbers are unique without the
  server and gaps are explainable.
- Offline limits are configurable per organization and enforced on the device **and** re-validated
  on push: max offline duration (default 72 h), max offline sale value, max cumulative offline
  value, and a hard block on offline card/MFS payments unless the provider supports an offline
  mechanism (none currently integrated — cash and customer credit only).
- Offline refunds are allowed only against a sale present in the local database, are capped by
  policy, and require a supervisor PIN validated against a locally cached credential hash;
  everything else queues for online authorization.
- Customer credit offline uses the last-synced limit and a local reservation; the server
  re-validates on push and may mark the sale `CreditExceeded` for follow-up rather than reject it
  (the goods have left the shop; this is a receivable, not a validation error).
- Drift migrations are versioned with tested upgrade steps; the device refuses to run against a
  newer schema than the installed binary understands.

---

### ADR-0018
**Sync cursor via per-tenant change sequence** · Accepted · 2026-10-11

*Decision.* Every syncable table carries `ChangeSequence bigint NOT NULL` and `UpdatedAtUtc`.
`ChangeSequence` is assigned by a `SaveChanges` interceptor from a per-organization monotonic
allocator (`TenantChangeSequence` row updated with a locked read inside the same transaction), not
from `rowversion` — this keeps the cursor meaning identical across providers (ADR-0003) and lets a
client pull with `?since=<sequence>` and resume exactly.

- Pull is keyset-paginated on `(ChangeSequence, Id)`, returns the high-water mark, and includes
  tombstones (`IsDeleted`) so deletions propagate.
- Push accepts a batch of operations, processes each independently, and returns a per-operation
  result (`applied | duplicate | conflict | rejected` + error code), so one bad record never blocks
  the batch. Partial success is the normal case.
- Server is authoritative for price, tax, stock and credit; a client value that disagrees is
  recorded on the sale as a snapshot plus a `PriceVarianceFlag` for reporting rather than silently
  overwritten (the customer already paid the printed price).
- Conflicts surface in a "Sync issues" screen with the record, the reason, and the authorized
  actions (retry, edit, discard with reason) — all audited.

---

### ADR-0019
**Versioned, effective-dated tax and pricing configuration** · Accepted · 2026-10-11

*Decision.* Tax rules (VAT, supplementary duty), price lists and discount rules are **versioned**
with `EffectiveFromUtc` / `EffectiveToUtc` and an integer `Version`. Transactions store a snapshot of
what was applied (rate, rule id, version, computed amounts) on the line. Nothing is hardcoded: rates,
inclusive/exclusive behaviour, rounding, SD applicability and invoice numbering formats all live in
configuration per organization and, where legally appropriate, per branch.

The client caches the active version set and sends `TaxConfigVersion` + `PriceListVersion` with each
offline sale; the server records whether the device used a stale version and flags affected sales
for the daily exception report. Tax behaviour must be validated against current NBR rules before any
production pilot — tracked as a compliance task, not a code assumption.

---

### ADR-0020
**Testing stack and integration-test database strategy** · Accepted · 2026-10-11

*Decision.* xUnit + FluentAssertions + NSubstitute; `WebApplicationFactory` for API tests.
Integration tests run against a **real SQL Server**, never the in-memory provider (it cannot model
transactions, locking or constraints — the very things being tested):

1. Testcontainers (`mcr.microsoft.com/mssql/server:2022-latest`) when a Docker daemon is available
   (CI, and locally once Docker Desktop is started).
2. Fallback to LocalDB (`MSSQLLocalDB`) with a per-run database name when Docker is absent — the
   current state of this machine.

Each test class gets a fresh database (migrations applied once per collection, then per-test
transaction rollback where possible). Mandatory suites from Phase 3 on: tenant isolation,
concurrent-sale-on-last-unit, duplicate idempotent request, partial return, outbox retry, offline
push/pull round-trip.

---

### ADR-0021
**Central package management, warnings-as-errors, pinned EF tooling** · Accepted · 2026-10-11

*Decision.* `Directory.Packages.props` holds all versions (`ManagePackageVersionsCentrally`), so a
version appears once. `Directory.Build.props` sets `net8.0`, `Nullable=enable`,
`TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, deterministic builds and
`LangVersion=12`. `backend/.config/dotnet-tools.json` pins `dotnet-ef` to the 8.x line because the
machine's global `dotnet-ef` 10.0.9 requires an uninstalled .NET 10 runtime.

---

### ADR-0022
**GetX + Drift structure on the Flutter side** · Accepted · 2026-10-11

*Decision.* Feature-first layout (`lib/features/<feature>/{presentation,controllers,domain,data}`)
with GetX for state, DI and routing. Rules: `Get.put` only for app-wide services; features use
`Bindings` with `Get.lazyPut` and are disposed with the route. Dio with interceptors for auth
refresh, correlation id, retry and structured error mapping onto the backend's ProblemDetails codes.
Drift for local persistence with generated DAOs. Two checkout layouts over one controller: a
keyboard-first desktop grid (barcode focus always armed, F-key shortcuts) and a touch tablet layout.
Hardware (thermal printer 58/80 mm, cash drawer, scale, scanner) sits behind
`PrinterAdapter`/`DrawerAdapter` interfaces with platform implementations and test doubles, so
checkout logic never imports a device package.

---

### ADR-0023
**Configuration and secrets live in appsettings files** · Accepted · 2026-10-11

*Context.* The user directed on 2026-10-11: "keep all secret in appsettings.json file". The default
engineering practice (and §22 of the specification) would be environment variables plus a secret
store; the user owns this call and it is recorded as given.

*Decision.* Every setting the system needs — connection string, JWT signing key, password-hashing
parameters, payment provider keys, SMTP, Redis and RabbitMQ credentials — is read from the
`appsettings*.json` chain. No setting *requires* an environment variable, user-secrets or a vault to
start the app. Layering:

| File | Contents | Committed |
|---|---|---|
| `appsettings.json` | all keys with safe local-development values; secret-bearing keys are present but empty or clearly dev-only | yes |
| `appsettings.Development.json` | developer overrides (verbose logging, auto-migrate) | yes |
| `appsettings.Staging.json` / `appsettings.Production.json` | real credentials for those environments | **no — git-ignored** |
| `appsettings.Production.json.example` | the shape of the production file, with placeholders | yes |

ASP.NET Core's environment-variable provider still overrides these files (it is on by default and
useful for containers), but nothing depends on it.

*Consequences and the risk accepted.* Secrets sit on disk in plain JSON next to the binaries, so
their protection is filesystem permissions and deployment hygiene rather than a managed store:

- anyone with read access to the deployment folder (or a backup of it, or a container image layer)
  can read production keys;
- rotating a key requires a file edit and restart rather than a store update;
- a mis-copied file is the most likely way a production key leaks.

Mitigations that remain in force: the two environment files are git-ignored and the production
example carries placeholders only; CI secret scanning (Phase 18) runs on every commit; the signing
key in the committed file is labelled dev-only and the API refuses to start in Production with it;
deployment runbooks will require the environment file to be written on the server with restricted
ACLs (`icacls` on Windows, `chmod 600` on Linux) and excluded from image builds. Card data remains
out of scope entirely (ADR-0016) — no provider ever hands us a PAN to store.

*Superseding.* If the deployment later moves to a managed host, this decision should be superseded
by an ADR switching the secret-bearing sections to a key store; `IConfiguration` consumers would not
change.

---

## Phase 2 — Platform foundations

### ADR-0024
**Non-clustered GUID keys with purpose-chosen clustered indexes** · Accepted · 2026-10-11

*Context.* Implementing the schema exposed an error in ADR-0005. SQL Server compares
`uniqueidentifier` values starting at bytes 10–15, not 0–5, so a UUIDv7 key — whose millisecond
timestamp occupies the first six bytes — is random as far as a clustered index is concerned. Keeping
the primary key clustered would scatter every insert across the table and cause page splits on the
highest-volume tables in the system (`InventoryMovement`, `SaleLine`, `OutboxMessage`).

*Decision.* Keep UUIDv7 identifiers, and choose each table's clustered index for how the table is
written and read rather than defaulting to the key:

| Table | Primary key | Clustered on |
|---|---|---|
| `OutboxMessage` | non-clustered | `(CreatedAtUtc, Id)` — append-only queue, oldest first |
| `IdempotencyRecord` | non-clustered | `(OrganizationId, Key)` unique — the replay check is one seek |
| `AuditLog` | non-clustered | `(OrganizationId, AtUtc, Id)` — written once, read by tenant and time |
| `Branch` | non-clustered | `(OrganizationId, Code)` unique |
| `Organization` | clustered | the key; a handful of rows, fragmentation irrelevant |

Planned for later phases: `Sale` clusters on `(OrganizationId, BusinessDate, Id)`, `SaleLine` on
`(SaleId, LineNumber)`, `InventoryMovement` on `(OrganizationId, OccurredAtUtc, Id)`, `StockBalance`
on its natural unique key.

*Alternatives rejected.* A `bigint identity` clustered surrogate on every table (an extra column
plus a key lookup for every foreign-key join); re-ordering GUID bytes for SQL Server's collation
(the stored value would no longer be the application's value, breaking cross-provider comparison and
client-generated ids).

*Consequences.* Portable index declarations live in the entity configurations; the clustering
choices live in `SqlServerModelTuning`, which another provider simply does not apply. Every
clustered key chosen here is immutable, so a row never physically moves.

---

### ADR-0025
**The idempotency decorator owns its transaction** · Accepted · 2026-10-11

*Context.* ADR-0010 and `docs/03-architecture.md` describe the pipeline as
`Logging → Validation → Idempotency → Transaction → Handler`, with the idempotency lookup outside
the transaction and the write inside it. Two independent decorators cannot express that: the
response snapshot has to commit atomically with the business change, and only the decorator that
opens the transaction can guarantee it.

*Decision.* `IdempotentUseCase` performs the lookup before opening a transaction, then opens one and
inside it reserves the key, runs the handler, stores the response and commits. A request that is not
idempotent but is transactional gets `TransactionalUseCase` instead. The effective order is
`Logging → Validation → (Idempotency ∪ Transaction) → Handler`, and a read-only request gets no
transaction at all.

A business failure rolls the transaction back **including the reservation**, so a rejected request
stays retryable with the same key once the client has corrected the cause. An abandoned reservation
(the process died mid-flight, leaving its transaction rolled back by the database) is taken over
after five minutes — otherwise that key would be permanently unusable and the terminal could never
retry the sale.

*Consequences.* One class holds both concerns, which is why its transaction boundary is documented
in `docs/03-architecture.md` §5. Verified by integration tests: replay returns the stored response
without re-running the handler; a key reused with a different body is rejected; a rejected operation
leaves neither the business row nor the reservation behind.

---

### ADR-0026
**Asynchronous saves only, and a fail-closed tenant filter** · Accepted · 2026-10-11

*Decision.* Two deliberate restrictions in the persistence layer:

1. **`SaveChanges` (synchronous) throws `NotSupportedException`.** Stamping the sync cursor requires
   a database round trip inside the save pipeline; doing that synchronously blocks a thread-pool
   thread under load. Every write path in this system is async, so the synchronous overload can only
   be reached by mistake — and failing loudly is better than silently skipping tenancy and audit
   stamping.
2. **The tenant query filter resolves to `Guid.Empty` when no tenant context exists**, which matches
   no rows. A missing context therefore returns nothing rather than everything. Cross-tenant reads
   require an explicit `IgnoreQueryFilters()` at the call site, which review and the authorization
   tests can look for.

*Consequences.* Seeders, background jobs and tests must set a tenant before writing tenant-owned
rows, exactly as a request does. A write without context raises `MissingTenantContextException`, and
a write whose row belongs to another tenant raises `TenantMismatchException`; both map to 403 with no
information about the other tenant.

---

## Phase 3 — Identity and authorization

### ADR-0027
**Token model: scope in the token, permissions from the database** · Accepted · 2026-10-11

*Context.* A cashier can hold over sixty permission codes and an owner over a hundred. Inlining
them as claims would produce large tokens on every POS request and, worse, would mean a permission
revoked by a manager keeps working until the token expires.

*Decision.*

- An access token carries **identity and scope only**: `sub`, `org`, `sid` (session), `stamp`
  (security stamp), `pv` (permission-set version), optional `branch`, `terminal`, `device`, and role
  names for display. Default lifetime 10 minutes, HS256 in development.
- **Permissions are resolved server-side on every authorized request** from roles plus per-branch
  overrides, cached by `(userId, permissionsVersion)`. A client that edits its own token gains
  nothing, and a revocation takes effect on the next request.
- **Sign-in requires the organization code** alongside email and password. An address can exist in
  more than one tenant; guessing which one is wrong, and answering "which organizations is this
  address in?" to an unauthenticated caller leaks customer relationships. The app stores the code
  after first setup, so a cashier types it once.
- **`X-Branch-Id` selects the branch per request**, and is validated against the user's grants in
  middleware. A branch grant removed after the token was issued stops working immediately, because
  the check reads current state rather than the token.
- The **security stamp in the token is compared with current state** on every request, through a
  30-second cache. That is the window in which a revoked session can still act; it is the price of
  not reading the user row on every call, and it is far shorter than the token lifetime.
- **Rate limiting is layered**: a global per-address budget plus a tighter policy on `/auth/*`. An
  endpoint can only carry one rate-limit policy, and the last one applied wins — so the broad limit
  is global and the endpoint policy tightens it rather than replacing it. (Found by test: a
  `RequireRateLimiting` on `MapControllers` silently overrode the stricter auth policy, and
  `UseRateLimiter` must come after `UseRouting` or endpoint policies are ignored entirely.)

*Consequences.* One database read per authorized request in the worst case, none in the common
cached case. Permission changes are immediate; credential changes take effect within 30 seconds.
RS256 and key rotation are deferred to the deployment phase, and the token service refuses to start
with any other algorithm configured rather than quietly signing with a shared secret.

---

### ADR-0028
**Cryptographic choices for passwords, TOTP and secrets at rest** · Accepted · 2026-10-11

*Decision.*

| Concern | Choice | Why |
|---|---|---|
| Passwords | PBKDF2-HMAC-SHA512, 210 000 iterations, 128-bit salt, format `v1.{iterations}.{salt}.{hash}` | No native dependency; the self-describing format is what allows the iteration count to be raised later, with the sign-in path re-hashing a weaker stored hash while it has the plaintext |
| Password floor | iterations clamped to ≥ 100 000 at construction | A mis-set configuration value must not quietly weaken every password in the database |
| Refresh tokens, device secrets, reset tokens | 256 bits of CSPRNG randomness, stored as SHA-256, compared by hash | High-entropy values need a fast digest, not a slow one; there is nothing to brute-force, and lookup by hash happens on every refresh |
| TOTP | RFC 6238, HMAC-SHA1, 30-second step, 6 digits, ±1 step drift, implemented directly | What authenticator apps actually implement; the algorithm is small and verifiable against the RFC's published vectors, which the tests do. SHA-256 TOTP would be stronger on paper and unusable in practice |
| TOTP replay | the accepted time step is stored, and a code from that step or earlier is refused | A code seen over the shoulder or recovered from a log cannot be used inside its own window |
| MFA secrets at rest | AES-GCM with a key from `appsettings`, random nonce per value | Readable by the application, not by someone holding a database backup; the tag makes tampering fail loudly |
| Password comparison | `CryptographicOperations.FixedTimeEquals` | A short-circuiting compare leaks how much of a hash matched |

*Consequences.* Argon2id remains the preferred end state and is a registration change plus
rehash-on-login behind `IPasswordHasher`; it is tracked as a hardening item. Rotating
`Security:SecretProtectionKey` makes existing MFA secrets unreadable, which the production example
file states plainly — those users must re-enrol.

---

### ADR-0029
**What invalidates a session, and what deliberately does not** · Accepted · 2026-10-11

*Context.* Rotating a user's security stamp invalidates every access token they hold within the
30-second validation window. Applying that too eagerly turns ordinary administration into a
surprise sign-out at a till mid-transaction; too rarely, and a compromised credential keeps working.

*Decision.*

| Event | Sessions ended | Stamp rotated | Reasoning |
|---|---|---|---|
| Password change by the user | no | yes | The credential changed, so old tokens must die; but the person at the till keeps working in the session they are using |
| Password reset by token | **yes** | yes | A reset usually follows a suspected compromise, so leaving an attacker's session alive would defeat it |
| Sign out everywhere | yes | yes | That is what was asked for |
| Account deactivated | — | yes | Access must stop within seconds, not at token expiry |
| Refresh-token reuse detected | yes (whole family) | no | The family is burned; the account itself is not suspect |
| Branch grant removed | no | no | The branch check reads current state on every request, so removal is already immediate |
| MFA enabled or disabled | no | no | The credential has not changed. Signing someone out of the session they just used to enrol is a confusing reward for improving their security — and disabling requires the password, so intent is already proven. (Found by test: enrolment returned 403 on the very next call.) |

*Also decided.* A lockout is reported plainly as `identity.account_locked` rather than hidden behind
`identity.invalid_credentials`, even though it reveals that an account exists. A cashier at a till
needs to know whether to wait or call a manager, and the alternative — a correct password that
silently fails — produces support calls and shoulder-surfed retries. Unknown accounts and unknown
organization codes still both answer `identity.invalid_credentials`, so the endpoint cannot be used
to enumerate either.
