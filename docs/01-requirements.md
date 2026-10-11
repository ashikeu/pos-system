# 01 — Requirements Specification

RetailFlow POS · v0.1 · 2026-10-11 · Phase 1 deliverable

---

## 1. Purpose and scope

A multi-tenant POS and retail management platform for Bangladeshi retail: small shops,
supermarkets, pharmacies, electronics stores, wholesalers and multi-branch chains. It covers
selling, stock, purchasing, customers, payments, cash control, and the reporting a shop owner and
an accountant need.

**In scope (v1)**: organization/branch administration, user & permission management, product
catalog & pricing, inventory ledger, purchasing, POS checkout (online and offline), payments
(cash, card reference, bKash/Nagad adapters, bank transfer, customer credit), returns & refunds,
cash & shift control, loyalty, reporting, synchronization, audit.

**Out of scope (v1)**, deliberately: full double-entry accounting/GL, payroll, HR, manufacturing
or BOM, e-commerce storefront, delivery/logistics routing, table-service restaurant flows
(KOT/kitchen), supplier portals, franchise royalty settlement. Hooks are left where a later
integration is likely (export of journals, webhook events).

## 2. Actors

| Actor | Description | Typical permissions |
|---|---|---|
| Platform Administrator | Operates the platform; manages tenants and plans | Cross-tenant, audited |
| Organization Owner | Owns a tenant; full business access | All within tenant |
| Branch Manager | Runs one or more branches | Branch-scoped admin, approvals |
| Cashier | Operates a POS terminal | Sell, park, limited refund/void |
| Stock Keeper | Receives and moves stock | Inventory, GRN, counts, transfers |
| Accountant / Auditor | Reconciles money and reviews | Read-only + reconciliation actions |
| Supplier / Customer | External parties (data subjects, no login in v1) | — |
| Background worker | Outbox dispatch, jobs, alerts, reconciliation | System identity |

## 3. Functional requirements

Each requirement has an ID used by tests and by `tasks.md`.

### FR-TEN — Multi-tenancy & organization management

- FR-TEN-01 An organization owns branches; a branch owns warehouses and POS terminals.
- FR-TEN-02 Every tenant-owned record is scoped to exactly one organization and is unreachable from
  another tenant regardless of IDs supplied in URLs, bodies or tokens.
- FR-TEN-03 Users are granted access per branch; permissions may differ per branch.
- FR-TEN-04 Configuration resolves terminal → branch → organization → platform default.
- FR-TEN-05 Subscription plans define feature limits (branches, terminals, users, products,
  monthly transactions); exceeding a limit blocks the creating action with a clear error.
- FR-TEN-06 A suspended or expired tenant can authenticate only to a read-only/renewal surface.
- FR-TEN-07 Administrative changes (org, branch, user, role, settings, subscription) are audited
  with actor, before/after values and correlation id.

### FR-SEC — Authentication & authorization

- FR-SEC-01 Email/username + password login returning an access token and a refresh token.
- FR-SEC-02 Access tokens are short-lived (default 10 min, configurable per organization).
- FR-SEC-03 Refresh tokens rotate on use, are stored hashed, and reuse of a consumed token revokes
  the whole token family and raises a security event.
- FR-SEC-04 Logout revokes the current session; logout-all revokes every session of the user.
- FR-SEC-05 Password reset by emailed single-use token (expiry 30 min); email verification where
  applicable.
- FR-SEC-06 Optional TOTP MFA, enforceable per role (required for Owner/Manager when enabled).
- FR-SEC-07 Session and device list with revoke; POS terminals register as devices.
- FR-SEC-08 Login history: timestamp, ip, device, terminal, outcome, failure reason.
- FR-SEC-09 Account lockout after N failures (default 5 / 15 min, escalating) plus per-IP and
  per-account rate limiting on auth endpoints.
- FR-SEC-10 Every protected operation is authorized server-side against a named permission; branch
  context is validated against the user's branch grants.
- FR-SEC-11 Privileged actions (price override, void, refund above threshold, shift adjustment,
  stock adjustment approval, permission change) write an audit record.
- FR-SEC-12 Passwords, refresh tokens and device secrets are never stored in plaintext; no secret
  or credential is committed to source control.

### FR-CAT — Product catalog & pricing

- FR-CAT-01 Products with optional variants (size/colour/pack); SKU unique per tenant; multiple
  barcodes per variant, unique per tenant.
- FR-CAT-02 Categories (hierarchical), brands, suppliers, units of measure with conversions
  (e.g. box = 24 pcs) including fractional quantities for weighed goods.
- FR-CAT-03 Images and attachments per product.
- FR-CAT-04 Cost price (derived from purchases) and selling prices via one or more price lists;
  branch-specific and customer-group-specific prices.
- FR-CAT-05 Tax classification per product (VAT rate, SD applicability, exempt/zero-rated).
- FR-CAT-06 Product status: draft, active, inactive, archived. Archival instead of deletion once a
  product appears on any transaction.
- FR-CAT-07 Batch/lot tracking with expiry; serial-number tracking; per-product toggle.
- FR-CAT-08 Minimum stock and reorder thresholds per warehouse.
- FR-CAT-09 CSV/Excel import and export with per-row validation, dry-run preview and an error
  report; a failed row never partially imports.
- FR-CAT-10 Transaction lines store a snapshot of name, unit, unit price, discount, tax rate and
  tax amount; later catalog edits never change historical documents.

### FR-POS — POS checkout

- FR-POS-01 Barcode scanning (scanner behaves as keyboard input) and search by name/SKU/barcode
  with sub-200 ms local lookup target on the device catalog.
- FR-POS-02 Cart: add, change quantity, remove, clear, line note; weighed-item quantity entry.
- FR-POS-03 Line-level and bill-level discounts (percent or amount) limited by configured caps and
  permissions.
- FR-POS-04 Manual price override only with `price.override` permission; every override is logged
  with old/new price, reason and approver.
- FR-POS-05 Customer selection (walk-in default), quick create, credit status display.
- FR-POS-06 Tax computed from the active, effective-dated tax configuration; inclusive or exclusive
  pricing per organization.
- FR-POS-07 Park/suspend a bill and recall it on the same terminal or (if permitted) another
  terminal in the branch.
- FR-POS-08 Split payment across multiple methods; change calculation for cash; configurable cash
  rounding shown as an explicit adjustment.
- FR-POS-09 Receipt preview and print (58 mm and 80 mm templates, English/Bangla); reprint requires
  permission and is logged.
- FR-POS-10 Void a sale under policy (same business day, same shift, permission, reason) — always as
  a reversing document, never a deletion.
- FR-POS-11 Returns and exchanges from the POS (see FR-RET).
- FR-POS-12 Credit sales when the organization and the customer's credit account allow it.
- FR-POS-13 Shift open/close with cash reconciliation (see FR-CSH); no selling without an open shift
  when the organization requires one.
- FR-POS-14 Cashier-level sales summary (X-report) and shift close report (Z-report).
- FR-POS-15 Keyboard-first operation on desktop: every checkout action reachable without a mouse.

### FR-INV — Inventory

- FR-INV-01 Every stock change is an append-only ledger movement with type: opening, purchase
  receipt, sale, sales return, purchase return, transfer-out, transfer-in, adjustment,
  damage, expiry write-off, count correction, reservation release.
- FR-INV-02 Balances per warehouse/variant/batch: on hand, reserved, in transit, available
  (= on hand − reserved).
- FR-INV-03 Concurrent sales of the last unit must not oversell when negative stock is disallowed.
- FR-INV-04 Negative stock is a configurable policy per organization (and overridable per branch);
  the configured policy is enforced identically online and at sync.
- FR-INV-05 Transfers track dispatch, in-transit, receipt, shortage and discrepancy with approval.
- FR-INV-06 Adjustments require a reason code and, above a configurable value/quantity threshold,
  approval before posting.
- FR-INV-07 Stock counts: snapshot, (optionally blind) counting, variance review, approval, posting
  as count-correction movements.
- FR-INV-08 Valuation by moving weighted average; valuation report at any date from the ledger.
- FR-INV-09 Low-stock and near-expiry alerts (configurable windows) delivered to the dashboard.
- FR-INV-10 Batch selection policy FEFO (first-expiry-first-out) by default, overridable by the
  cashier where permitted; serial items require explicit serial selection.

### FR-PUR — Purchasing & suppliers

- FR-PUR-01 Supplier profiles with contacts, terms, tax id, opening balance.
- FR-PUR-02 Purchase orders with lines, expected dates, approval workflow above thresholds.
- FR-PUR-03 Goods receipts against a PO (full or partial) or without a PO when permitted; stock
  increases **only** on an accepted receipt.
- FR-PUR-04 Over/under delivery handled with configurable tolerance and a discrepancy record.
- FR-PUR-05 Purchase invoices matched to PO and receipt (three-way match) with variance reporting.
- FR-PUR-06 Purchase returns to supplier with stock and cost reversal.
- FR-PUR-07 Supplier payments, outstanding balance, ageing, payment history.
- FR-PUR-08 Landed costs (freight, duty, clearing) allocated by value, quantity or weight into unit
  cost at receipt time.

### FR-CUS — Customers, credit & loyalty

- FR-CUS-01 Customer profiles (name, phone, email, addresses, tax id), phone unique per tenant.
- FR-CUS-02 Customer groups driving pricing and discounts.
- FR-CUS-03 Credit account with limit, due balance, ageing; credit sales blocked past the limit
  unless an authorized override is recorded.
- FR-CUS-04 Customer payments/collections against invoices or on account, with receipts.
- FR-CUS-05 Loyalty program: accrual rules, point ledger, redemption as discount or value, expiry.
- FR-CUS-06 Loyalty accrual and redemption are idempotent — a retried sale never double-credits.
- FR-CUS-07 A return reverses the proportional accrual and, where configured, restores or cancels a
  redeemed reward.
- FR-CUS-08 Coupons and promotions with validity window, usage caps and stacking rules.

### FR-PAY — Payments

- FR-PAY-01 Supported methods: cash, card (terminal reference), bKash, Nagad, bank transfer,
  customer credit, loyalty redemption, gift/advance (reserved).
- FR-PAY-02 Split tender across methods with per-payment state.
- FR-PAY-03 Payment state machine: Pending, Authorized, Completed, Failed, Reversed, RefundPending,
  Refunded, UnknownOutcome.
- FR-PAY-04 A payment is Completed only on a verified provider confirmation — never because a
  request was sent.
- FR-PAY-05 Provider callbacks verify signatures and are idempotent on `(provider, reference)`.
- FR-PAY-06 Interrupted/timed-out provider calls become UnknownOutcome and are resolved by a status
  query job or a reconciliation report; duplicate charges are detectable and reportable.
- FR-PAY-07 Refunds/reversals reference the original payment and the provider's refund reference.
- FR-PAY-08 No raw card data (PAN, CVV, track) is captured, stored, logged or transmitted.
- FR-PAY-09 Daily payment reconciliation per method and provider, with unmatched exception lists.

### FR-CSH — Cash & shift management

- FR-CSH-01 Shift (cash session) per terminal and cashier with opening float.
- FR-CSH-02 Records cash sales, cash refunds, cash-in, cash-out, expenses.
- FR-CSH-03 Expected cash computed by formula (see `02-business-rules.md`); actual cash counted by
  denomination; variance computed and classified.
- FR-CSH-04 Variance beyond a threshold requires supervisor approval with a reason to close.
- FR-CSH-05 A closed shift is immutable; corrections are authorized adjustment records that
  reference the shift and appear in the audit trail.
- FR-CSH-06 Daily branch cash reconciliation across shifts and terminals.

### FR-RET — Returns & refunds

- FR-RET-01 A return references the original sale when available; blind returns are a configurable
  policy requiring elevated permission.
- FR-RET-02 Returnable quantity per line = sold − already returned; over-return is rejected.
- FR-RET-03 Partial returns and repeat partial returns are supported.
- FR-RET-04 Return reasons are captured from a configurable list.
- FR-RET-05 Refund amount is computed from the original line snapshots including proportional
  discount and tax reversal; it may not exceed the refundable amount without an authorized
  exception record.
- FR-RET-06 Refund method policy: same-method-as-original by default; cash refunds limited by shift
  cash availability and permission.
- FR-RET-07 Stock is restored only after the applicable inspection policy passes; damaged goods post
  to a damage location/write-off instead.
- FR-RET-08 Loyalty points earned on the returned value are reversed; redeemed rewards are handled
  per configuration.
- FR-RET-09 Exchanges link the return and the new sale as one operation.
- FR-RET-10 Return receipt is generated; all approvals are recorded.

### FR-OFF — Offline operation & synchronization

- FR-OFF-01 Checkout works with no network: catalog, prices, tax rules, customers (bounded set),
  terminal config and permissions are available locally.
- FR-OFF-02 Offline sales, returns (restricted), cash movements and shift events are stored locally
  and queued durably.
- FR-OFF-03 Each queued operation has a client-generated operation id used as the server idempotency
  key; retries use the same key forever.
- FR-OFF-04 Retry uses exponential backoff with jitter; batches are sent where useful; sync is
  resumable after interruption.
- FR-OFF-05 Operation states: pending, in flight, synced, failed, conflict — visible to the user.
- FR-OFF-06 The server returns the original result for an already-processed operation; it never
  creates a second sale.
- FR-OFF-07 The UI distinguishes a locally accepted sale from a server-confirmed one, including on
  the printed receipt.
- FR-OFF-08 Offline card/MFS payments are blocked (no provider offline mechanism integrated); cash
  and customer credit are allowed.
- FR-OFF-09 Configurable offline limits: max offline duration, max single sale value, max cumulative
  offline value, and which operations are blocked offline.
- FR-OFF-10 Conflicts and rejected records are listed with reasons and authorized recovery actions
  (retry, amend, discard with reason), all audited.
- FR-OFF-11 Local database migrations are versioned and tested; a downgrade is refused safely.
- FR-OFF-12 Device clock skew is detected; the server's timestamp governs accounting periods.

### FR-REP — Reporting

- FR-REP-01 Sales: daily, monthly, gross vs net, by branch, terminal, cashier, hour-of-day.
- FR-REP-02 Discounts and taxes collected (VAT/SD breakdown for filing).
- FR-REP-03 COGS and gross profit by period, product, category, branch.
- FR-REP-04 Product performance: best sellers, slow movers, no-sale items, stock cover days.
- FR-REP-05 Inventory valuation and stock movement history with running balance.
- FR-REP-06 Low-stock and expiry alert reports.
- FR-REP-07 Purchases by supplier, receipt vs invoice variance, supplier ageing.
- FR-REP-08 Customer balances, ageing and collection history.
- FR-REP-09 Payment reconciliation by method and provider; unmatched items.
- FR-REP-10 Cash shift reconciliation and variance history by cashier.
- FR-REP-11 Refunds, voids and price overrides (exception reports for owner review).
- FR-REP-12 Loyalty activity: accrued, redeemed, expired, liability outstanding.
- FR-REP-13 Audit history search by actor, entity, action, date.
- FR-REP-14 All reports honour tenant, branch, role and date filters consistently, paginate detail
  rows, and export to CSV/Excel.

### FR-LOC — Localization & compliance (Bangladesh)

- FR-LOC-01 BDT default currency with configurable symbol and decimal display.
- FR-LOC-02 Full English and Bangla UI; Bangla numerals optional; Bangla-capable receipt rendering.
- FR-LOC-03 Configurable date and number formats per organization.
- FR-LOC-04 Configurable VAT rates and supplementary duty rules, effective-dated and versioned.
- FR-LOC-05 Configurable invoice numbering (prefix, reset period, width) meeting NBR expectations;
  numbers are gapless per series or gaps are explainable and reported.
- FR-LOC-06 Mushak-style VAT invoice fields on receipts/invoices where applicable, driven by
  configuration rather than code.
- FR-LOC-07 bKash and Nagad adapters (credentials required for live use), card via terminal
  reference.
- FR-LOC-08 Due/credit tracking and collection, common in local retail.
- FR-LOC-09 Tax behaviour must be validated against current NBR rules before production use; rules
  are versioned with effective dates and transactions keep snapshots.

## 4. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-01 | Offline-capable POS; no sale lost due to network or API outage. |
| NFR-02 | Correctness first: all money/stock mutations are transactional, idempotent and auditable. |
| NFR-03 | Targets to be validated by load test (Phase 17), not assumed: checkout commit p95 < 500 ms server-side at 20 concurrent terminals per branch; local barcode lookup p95 < 150 ms over a 50 000-item catalog; sync push of 500 operations < 30 s. Targets are provisional until measured. |
| NFR-04 | Availability target for the pilot: 99.5% monthly on the server; POS remains operable offline during outages. |
| NFR-05 | RPO ≤ 15 minutes (log backups), RTO ≤ 2 hours, verified by a restore drill. |
| NFR-06 | Security: OWASP ASVS L2 as the yardstick; TLS everywhere; secrets from a secret store; dependency and secret scanning in CI. |
| NFR-07 | Observability: structured logs with correlation id, metrics, traces; alerts on outbox backlog, sync failures, payment UnknownOutcome count, stock drift, auth anomalies. |
| NFR-08 | Maintainability: layer boundaries enforced by tests; no business logic in controllers or widgets; every module documented. |
| NFR-09 | Data retention: transactional data retained ≥ 6 years (tax), audit ≥ 6 years, sync/idempotency records 90 days, logs 30–90 days by severity. |
| NFR-10 | Accessibility & usability: keyboard-complete checkout, 44 px touch targets on tablet, visible focus, legible at 100% scale on 1366×768. |
| NFR-11 | Portability: Windows desktop and Android are the pilot targets; iOS and web admin build from the same codebase. |
| NFR-12 | Deployability: single-command local bring-up; migrations applied through a controlled procedure with a reviewed script and rollback plan. |

## 5. Assumptions

Recorded in full, with owners, in `02-business-rules.md` §8. Summary: single currency (BDT) per
organization in v1; one timezone (`Asia/Dhaka`) default with per-branch override; weighted-average
costing; VAT configured per product tax class; no GL integration in v1; offline MFS payments not
possible; printers are ESC/POS-compatible thermal units.

## 6. Traceability

Requirement IDs appear in `tasks.md` phase items and in test names
(`FR_POS_08_split_payment_allocates_exactly`). Phase 17 includes a traceability check that every
`FR-` id has at least one automated test or an explicit documented exception.
