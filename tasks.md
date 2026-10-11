# tasks.md — RetailFlow POS implementation checklist

Single source of truth for status. Update it in the same change that moves the work.

Legend: `[ ]` pending · `[~]` in progress · `[x]` done (built + tested) · `[!]` blocked ·
`[-]` deferred (with reason)

Last updated: **2026-10-11** · Current phase: **Phase 3 (complete) → Phase 4 next**

---

## Status summary

| Phase | Scope | Status |
|---|---|---|
| 1 | Foundation: requirements, decisions, structure, schema design, roadmap | `[x]` |
| 2 | Platform foundations: EF Core, tenancy, audit, outbox, idempotency, migrations, seed | `[x]` |
| 3 | Identity & authorization | `[x]` |
| 4 | Tenancy management (organizations, branches, warehouses, terminals, settings) | `[ ]` |
| 5 | Catalog, pricing & tax configuration | `[ ]` |
| 6 | Inventory core (ledger, balances, adjustments, counts, transfers) | `[ ]` |
| 7 | Procurement (suppliers, PO, GRN, invoices, returns, payments, landed cost) | `[ ]` |
| 8 | Customers, credit & loyalty | `[ ]` |
| 9 | POS sales & payments | `[ ]` |
| 10 | Returns & refunds | `[ ]` |
| 11 | Cash & shift management | `[ ]` |
| 12 | Sync engine (server side) | `[ ]` |
| 13 | Flutter foundation (core, auth, local DB, sync client) | `[ ]` |
| 14 | Flutter POS checkout (online + offline) | `[ ]` |
| 15 | Flutter admin & back-office screens | `[ ]` |
| 16 | Reporting & analytics | `[ ]` |
| 17 | Hardening: test depth, performance, security, observability | `[ ]` |
| 18 | DevOps: Docker, CI/CD, backups, runbooks | `[ ]` |

Blocked / waiting on externals:

- `[!]` bKash & Nagad production integration — needs merchant credentials and sandbox access.
  Adapters + test doubles will be built; live verification deferred (ADR-0016).
- `[!]` NBR VAT/SD rule confirmation — tax engine is configurable; rates must be validated with an
  accountant before a production pilot (ADR-0019).
- `[!]` Thermal printer / cash drawer verification — needs physical hardware; adapters + doubles
  built, device-specific validation deferred.
- `[-]` Docker-based integration tests — Docker daemon is not running on this machine; LocalDB
  fallback is used locally, Testcontainers in CI (ADR-0020).

---

## Phase 1 — Foundation `[x]`

- [x] Inspect repository (empty) and verify toolchain: .NET 8.0.303, Flutter 3.47.1, Dart 3.13.1,
      Docker 29.7.2 (daemon down), SQL Server 2022 Standard on localhost (Windows auth, verified
      with sqlcmd), LocalDB fallback
- [x] Resolve database conflict in the spec (§2 SQL Server vs §17 PostgreSQL) → SQL Server on
      localhost, provider-portable design (ADR-0003)
- [x] `CLAUDE.md` — contributor/agent guide
- [x] `decisions.md` — ADR-0001…0022
- [x] `tasks.md` — this checklist
- [x] `docs/01-requirements.md` — functional + non-functional requirements
- [x] `docs/02-business-rules.md` — rules, formulas, assumptions, open questions
- [x] `docs/03-architecture.md` — layers, module map, diagrams, transaction boundaries
- [x] `docs/04-database-schema.md` — entity catalog, ERDs, constraints, indexes, retention
- [x] `docs/05-roadmap.md` — phase plan, dependencies, definition of done
- [x] Backend solution skeleton (`Domain`, `Application`, `Infrastructure`, `Api`, `Workers`,
      3 test projects), CPM + build props + pinned EF tool, `dotnet build` and `dotnet test` green
- [x] `.gitignore`, `.editorconfig`, git repository initialised
- [x] Money/rounding primitives (`Money`, `RoundingPolicy` incl. largest-remainder allocation) with
      34 unit tests — the ADR-0006 foundation every later money calculation uses
- [x] Layer-boundary tests (Domain/Application dependency rules) and an API health-endpoint
      integration test — 39 tests green
- [x] ADR-0023: all configuration and secrets in `appsettings*.json` per user direction

## Phase 2 — Platform foundations `[x]`

- [x] `Domain/Platform`: `Entity`, `AggregateRoot`, `AuditableAggregateRoot`, `TenantEntity`,
      `ITenantOwned`, `IAuditable`, `ISoftDeletable`, `IHasChangeSequence`, `IDomainEvent`,
      `Money`, `RoundingPolicy`, `Result`/`Error`, `UuidV7`, `BusinessDay`, domain exceptions
- [x] `Application/Platform` ports: `IClock`, `IGuidGenerator`, `ITenantContext`, `ICurrentUser`,
      `ICorrelationContext`, `IUnitOfWork`, `ICacheService`, `IDistributedLock`, `IRowLocker`,
      `IEventPublisher`, `IIdempotencyStore`, `IAuditWriter`, `IOutboxMessageHandler`
- [x] Use-case pipeline: `IUseCase`, `IIdempotentRequest`, `ITransactionalRequest`, and the
      Logging / Validating / Transactional / Idempotent decorators wired in a fixed order (ADR-0025)
- [x] `RetailFlowDbContext` with decimal-precision conventions, per-entity configurations and
      fail-closed tenant query filters (ADR-0026)
- [x] `TenantAuditInterceptor`: tenant stamping and cross-tenant rejection, audit fields,
      soft-delete conversion, domain-event draining to the outbox, sync-cursor assignment
- [x] `SqlServerChangeSequenceAllocator` (one `UPDATE ... OUTPUT` per save), `SqlServerRowLocker`
      (`UPDLOCK, HOLDLOCK, ROWLOCK`, ascending key order), `SqlServerModelTuning` (clustering, ADR-0024)
- [x] `OutboxMessage` + dispatcher with lease, `READPAST` claim, exponential backoff with jitter,
      dead-lettering, and the `OutboxDispatcherWorker` background host
- [x] `IdempotencyRecord` store with replay, in-progress, key-reuse and stale-takeover handling
- [x] `AuditLog` writer (actor, tenant, branch, terminal, before/after, correlation id, ip, agent)
- [x] API cross-cutting: ProblemDetails (RFC 9457) mapping and `GlobalExceptionHandler`,
      correlation-id middleware, Serilog structured logging, API versioning, Swagger,
      `/health/live` and `/health/ready` (database + schema version + outbox backlog)
- [x] `appsettings` per environment with all secrets in the files (ADR-0023); provider options
      validated on start-up; migrations applied on start-up only when configuration allows it
- [x] Initial migration `InitialPlatformSchema` created and applied to `localhost\RetailFlow`
- [x] Dev seed: demo organization and main branch with stable ids (`DevDataSeeder`)
- [x] Integration harness: per-run SQL Server database (env var → localhost → LocalDB), migration
      applied per run, scopes with a chosen tenant
- [x] Tests (102 total, all executed): tenant isolation (8), idempotency (6), outbox (5),
      backoff arithmetic (9), persistence platform (9), health endpoints (5), money/rounding (34),
      UUIDv7 (7), business day (6), result (6), layer boundaries (2)

### Deferred out of Phase 2, with reasons

- [-] Redis cache and RabbitMQ publisher: ports exist (`ICacheService`, `IDistributedLock`,
      `IEventPublisher`) with in-memory and in-process implementations. Wiring the real services
      without a second node or a real consumer would be ceremony (ADR-0011).
- [-] Users, roles and terminals in the seed: those entities belong to Phases 3 and 4. The seeder
      creates the tenant they hang from.
- [-] Testcontainers: the Docker daemon is not running on this machine, so integration tests use
      the local SQL Server instance. CI sets `RETAILFLOW_TEST_CONNECTION` (ADR-0020).
- [-] OpenTelemetry traces and metrics: packages are pinned; exporting is a Phase 17 task together
      with the dashboards and alerts that give it a purpose.
- [-] Rate limiting: configured values exist in `appsettings.json`, enforcement lands with the auth
      endpoints it protects (Phase 3).

## Phase 3 — Identity & authorization `[x]`

- [x] Entities: `User`, `Role`, `Permission`, `RolePermission`, `UserRole`, `UserBranch`,
      `UserBranchPermission`, `RefreshToken`, `UserSession`, `LoginAttempt`, `DeviceRegistration`,
      `MfaSecret`, `UserToken` (reset + verification), with the `IdentitySchema` migration applied
- [x] Permission catalog: 111 codes in source, synced to the `Permission` table on start-up, plus
      six system role templates (Owner, Manager, Cashier, Stock Keeper, Accountant, Auditor)
- [x] Password hashing: PBKDF2-HMAC-SHA512, versioned self-describing format, iteration floor,
      rehash-on-login when the stored parameters are outdated (ADR-0028)
- [x] Login with organization code + email + password; lockout with escalating duration; per-account
      failure throttle; login history for every attempt, successful or not
- [x] Refresh with unconditional rotation, single use, and family revocation on reuse detection
      (raises `identity.refresh-token-reuse-detected` through the outbox)
- [x] Logout, and logout-everywhere which rotates the security stamp so live tokens stop working
- [x] Change password, forgot/reset password (single-use hashed tokens, enumeration-safe answers),
      email verification
- [x] Optional TOTP MFA: two-step enrolment, replay-protected codes, password required to disable
- [x] Session list and per-session revoke; device registration returning its secret once
- [x] `[HasPermission]` attribute, on-demand policy provider and one authorization handler;
      permissions are always resolved server-side (ADR-0027)
- [x] Branch scoping: `X-Branch-Id` validated against current grants in middleware
- [x] JWT bearer authentication with security-stamp validation (30-second cache), ProblemDetails
      challenge responses, layered rate limits (global budget plus a tighter `/auth/*` policy)
- [x] Audit events for privileged actions: password change and reset, session revocation,
      sign-out-everywhere, MFA on and off, device registration
- [x] Dev seed: system roles for the demo tenant and an owner account
      (`owner@demo.retailflow.local`; the development password lives in `IdentitySeeder`)
- [x] Tests (217 total, all executed): 33 domain identity (lockout escalation, stamp rotation,
      email rules, permission catalog and role template shape), 19 application (password policy,
      branch resolution), 31 crypto (PBKDF2, token generation, AES-GCM, TOTP against the RFC 6238
      vectors), 32 end-to-end over HTTP (sign-in, lockout, suspended tenant, deactivated account,
      rotation, reuse detection, logout, stale-stamp rejection, forged token, permission denial and
      allowance, branch scope, cross-tenant isolation, password change, MFA enrolment, rate limiting)

### Three behaviours the tests corrected

- MFA enrolment used to rotate the security stamp, so the request right after enrolling returned
  403. Enabling or disabling MFA no longer ends sessions (ADR-0029).
- `RequireRateLimiting` on `MapControllers` silently overrode the stricter `/auth/*` policy; the
  broad budget is now the global limiter instead (ADR-0027).
- `UseRateLimiter` placed before `UseRouting` ignored endpoint policies entirely, so no limit was
  applied at all.

### Deferred out of Phase 3, with reasons

- [-] User and role administration endpoints (create user, assign role, grant branch access): the
      permissions exist and are enforced, but the write endpoints belong with organization
      provisioning in Phase 4, which has to create the first owner account anyway.
- [!] Real email delivery: `IEmailSender` logs what it would send (`Email:Provider = None`). Token
      creation, expiry and single use are real; an SMTP adapter needs credentials.
- [-] Argon2id password hashing: PBKDF2 ships now, and the swap is a registration change plus
      rehash-on-login behind `IPasswordHasher` (ADR-0028). Tracked for Phase 17.
- [-] RS256 signing and key rotation: HS256 for now, and the token service refuses to start with any
      other algorithm rather than signing with the wrong key material. Phase 18, with key management.
- [-] Platform-operator cross-tenant endpoints: `IsPlatformOperator` and the permission codes exist;
      the endpoints that use them arrive with the admin surface.

## Phase 4 — Tenancy management `[ ]`

Entry state: authentication, authorization and branch scoping work end to end, so provisioning can
create an organization with roles, an owner and a terminal that can sign in immediately.

- [ ] Entities: `Organization`, `Branch`, `Warehouse`, `PosTerminal`, `OrganizationSettings`,
      `BranchSettings`, `SubscriptionPlan`, `OrganizationSubscription`, `FeatureLimit`
- [ ] Organization provisioning (owner user, default branch/warehouse/terminal, role templates)
- [ ] Branch/warehouse/terminal CRUD with activation state and terminal registration
- [ ] Settings resolution chain: terminal → branch → organization → platform default
- [ ] Subscription plan limits enforced (branches, terminals, users, products) + tenant suspension
- [ ] Tests: settings resolution precedence, limit enforcement, suspended-tenant lockout

## Phase 5 — Catalog, pricing & tax `[ ]`

- [ ] Entities: `Category`, `Brand`, `UnitOfMeasure`, `UomConversion`, `Product`, `ProductVariant`,
      `Barcode`, `ProductImage`, `ProductAttribute`, `TaxClass`, `TaxRule`, `TaxRuleVersion`,
      `PriceList`, `PriceListItem`, `BranchPriceOverride`, `DiscountRule`
- [ ] Variant/SKU/barcode rules, multi-barcode lookup, archival instead of delete
- [ ] Price resolution service (customer group → branch → price list → base) + tax engine
      (inclusive/exclusive, VAT + SD, effective dating, versioned snapshots)
- [ ] Batch/lot, expiry and serial tracking flags with per-product policy
- [ ] Reorder thresholds, min stock, supplier links
- [ ] CSV/Excel import & export with row-level validation report, dry-run mode
- [ ] Tests: price resolution precedence, inclusive/exclusive tax math, SD stacking, rounding,
      barcode uniqueness per tenant, import validation

## Phase 6 — Inventory core `[ ]`

- [ ] Entities: `InventoryMovement`, `StockBalance`, `Batch`, `SerialUnit`, `StockAdjustment`,
      `StockCount`, `StockCountLine`, `StockTransfer`, `StockTransferLine`, `StockReservation`
- [ ] Movement posting service (single entry point, all movement types, cost effects)
- [ ] Balance projection with row locking; negative-stock policy enforcement
- [ ] Adjustment approval workflow; damaged/expired write-off with reason codes
- [ ] Transfers: dispatch → in transit → receipt with shortage/discrepancy handling
- [ ] Stock counts: snapshot, blind count, variance, approval, posting
- [ ] Low-stock and expiry alert jobs
- [ ] Tests: ledger↔balance consistency, concurrent decrement on last unit, average-cost math,
      batch/serial selection, transfer discrepancy, count variance posting

## Phase 7 — Procurement `[ ]`

- [ ] Entities: `Supplier`, `SupplierProduct`, `PurchaseOrder(+Line)`, `GoodsReceipt(+Line)`,
      `PurchaseInvoice(+Line)`, `PurchaseReturn(+Line)`, `SupplierPayment`, `LandedCostCharge`
- [ ] PO lifecycle: draft → approval → sent → partially/fully received → closed/cancelled
- [ ] GRN posting (stock increases only on accepted receipt), over/under-delivery tolerance
- [ ] Invoice matching (PO ↔ GRN ↔ invoice), supplier balance, payments & ageing
- [ ] Landed cost allocation (by value/quantity/weight) feeding average cost
- [ ] Tests: partial receipt, over-receipt block, three-way match, landed-cost allocation,
      purchase return cost reversal

## Phase 8 — Customers, credit & loyalty `[ ]`

- [ ] Entities: `Customer`, `CustomerGroup`, `CustomerAddress`, `CreditAccount`,
      `CustomerLedgerEntry`, `LoyaltyProgram`, `LoyaltyAccount`, `LoyaltyTransaction`,
      `Coupon`, `CouponRedemption`, `Promotion`
- [ ] Credit limit checks, due tracking, collections/receipts, ageing report
- [ ] Loyalty accrual/redemption ledger with idempotency; return reversal rules
- [ ] Coupon validation (validity window, usage caps, stacking rules)
- [ ] Tests: credit limit boundary, duplicate accrual prevention, redemption reversal on return,
      coupon double-spend prevention

## Phase 9 — POS sales & payments `[ ]`

- [ ] Entities: `Sale`, `SaleLine`, `SaleLineTax`, `SaleDiscount`, `Payment`, `PaymentAttempt`,
      `ParkedBill`, `PriceOverrideLog`, `ReceiptPrintLog`, `TerminalSequenceBlock`
- [ ] `CreateSaleHandler`: the 9-step workflow from the spec, one transaction, documented boundary
- [ ] Payment abstraction + adapters: cash, card (manual/terminal ref), bKash, Nagad, bank
      transfer, customer credit, split tender, change calculation, cash rounding
- [ ] Provider callback endpoints (signature verification, idempotent, replay-safe) +
      status-query reconciliation job for `UnknownOutcome`
- [ ] Parked/suspended bills, recovery, void with authorization, reprint with audit
- [ ] Receipt generation (58/80 mm templates, Bangla/English, VAT fields, QR)
- [ ] Tests: full sale posting, concurrent stock contention, split tender allocation, idempotent
      retry, unknown-outcome recovery, void rules, price-override authorization

## Phase 10 — Returns & refunds `[ ]`

- [ ] Entities: `SalesReturn`, `SalesReturnLine`, `Refund`, `ReturnApproval`, `ExchangeLink`
- [ ] Return against original sale with returnable-quantity ledger; blind return policy
- [ ] Refund calculation from original snapshots; proportional discount/tax reversal
- [ ] Inspection policy → restock vs damaged write-off; exchanges
- [ ] Loyalty/credit reversal; provider refund references; return receipt
- [ ] Tests: partial & repeat returns, over-return prevention, refund cap, discount-proportional
      reversal, damaged-goods path, loyalty reversal

## Phase 11 — Cash & shift management `[ ]`

- [ ] Entities: `CashSession`, `CashMovement`, `CashCount`, `CashDenominationLine`, `Expense`,
      `ShiftAdjustment`
- [ ] Open/close shift, cash-in/out, expenses, expected vs counted, variance + approval
- [ ] Closed-shift immutability: corrections only via authorized adjustment records
- [ ] Daily reconciliation per branch; cashier Z-report
- [ ] Tests: expected-cash formula, variance approval thresholds, closed-shift edit rejection

## Phase 12 — Sync engine (server) `[ ]`

- [ ] `POST /sync/push` (per-operation results), `GET /sync/pull` (keyset + tombstones),
      `GET /sync/status`
- [ ] Operation handlers: sale, return, payment, shift events, customer create, stock count
- [ ] Conflict classification + `SyncConflict` records and resolution endpoints
- [ ] Terminal sequence block allocation; offline-limit re-validation; clock-skew flagging
- [ ] Tests: lost-ack replay, duplicate batch, partial batch failure, stale-config sale,
      credit-exceeded path, resumable pull

## Phase 13 — Flutter foundation `[ ]`

- [ ] App scaffold (`frontend/retailflow_app`), flavors, theming tokens, responsive shell
- [ ] `core/`: Dio client + interceptors, error mapping, secure storage, logging, permissions,
      localization (en/bn), Drift database + migrations, outbox + sync engine, connectivity
- [ ] Auth feature: login, token refresh, device registration, session restore, logout
- [ ] Tests: token refresh interceptor, outbox retry/backoff, Drift migration, error mapping

## Phase 14 — Flutter POS checkout `[ ]`

- [ ] Keyboard-first desktop checkout + touch tablet layout over one controller
- [ ] Scanner input, product search, cart, discounts, price override, customer select, split
      payment, change, park/recall, void, receipt preview & print
- [ ] Offline mode: local pricing/tax, local sale commit, LocallyAccepted badge, sync status panel,
      conflict/recovery screen
- [ ] Hardware adapters: printer (58/80 mm), cash drawer, scale + test doubles
- [ ] Tests: cart math, offline commit→sync round-trip (against the real API), shortcut map

## Phase 15 — Flutter admin & back-office `[ ]`

- [ ] Dashboard, organizations/branches, users & roles, catalog & pricing, inventory
      (balances, adjustments, counts, transfers), procurement, customers & loyalty, cash, settings
- [ ] Shared table/list/filter/export components, empty & error states, optimistic UI rules

## Phase 16 — Reporting & analytics `[ ]`

- [ ] Report queries + projections: sales (daily/monthly, gross/net), discounts & tax, COGS,
      gross profit, by branch/terminal/cashier, product performance, slow movers, inventory
      valuation, stock movement history, low stock & expiry, purchases & suppliers, customer
      balances, payment reconciliation, shift reconciliation, refunds/voids, loyalty, audit
- [ ] Explicit formulas (see `docs/02-business-rules.md`), consistent tenant/branch/date filters,
      pagination, indexes/projections for large data, CSV/Excel export
- [ ] Tests: formula verification against fixtures, filter isolation, pagination stability

## Phase 17 — Hardening `[ ]`

- [ ] Architecture tests (layer/module boundaries), authorization regression test over all endpoints
- [ ] E2E suite: org → branch/terminal → product → stock receipt → sale → payment → stock check →
      return → shift reconciliation → offline sale → sync
- [ ] Load tests (concurrent checkout, barcode latency, large catalog, big sync batch, reports)
- [ ] Security review: dependency scan, secret scan, authz fuzzing, tenant isolation sweep
- [ ] OpenTelemetry traces/metrics/logs, SLOs, dashboards, alerts
- [ ] Ledger reconciliation job, drift alerts, data retention/archival jobs

## Phase 18 — DevOps `[ ]`

- [ ] Dockerfiles (api, workers), `docker-compose` (sqlserver, redis, rabbitmq, nginx, api, workers)
- [ ] GitHub Actions: build, format, analyze, test, migration check, flutter analyze/test, artifacts
- [ ] Controlled migration deployment (script review + apply), rollback procedure
- [ ] Backup + tested restore scripts, restore drill documentation
- [ ] Secret handling per ADR-0023 (restricted ACLs on the environment appsettings file, excluded
      from images, CI secret scanning), HTTPS/Nginx config, resource limits, graceful shutdown
- [ ] Runbooks: deploy, rollback, payment reconciliation, sync incident, DB failover, pilot checklist
