# 02 — Business Rules, Formulas and Assumptions

RetailFlow POS · v0.1 · 2026-10-11

Every rule here is testable. Where a rule is configurable, the setting key is named. Where a rule is
an assumption, it is listed in §8 with the risk of being wrong.

---

## 1. Pricing and discounts

**Price resolution order** (first match wins), `PriceResolutionService`:

1. Active, authorized **manual override** on the line (requires `price.override`).
2. **Customer-specific price** for the variant.
3. **Customer-group price list** item (highest priority list where several apply).
4. **Branch price override** for the variant.
5. **Default price list** item for the organization.
6. Variant `BasePrice`.

A resolved price must be ≥ 0. Selling below `UnitCost` requires `price.below_cost` permission and is
flagged on the exception report.

As built in Phase 5 (`PriceResolution`, `PriceResolutionService`):

- A price list applies only inside its effective window and only while active; an expired list stops
  applying without anyone editing its items.
- Within one source, the **highest-priority** list wins; among equal priorities resolution is
  deterministic, because the candidate ordering is total — two identical candidates cannot swap
  places between two reads of the same data.
- Quantity tiers: the **highest tier whose `MinQuantity` the line reaches** wins, so "20 each, 18
  from a dozen" prices 11 at 20 and 12 at 18.
- A candidate price of exactly 0 is a price (a line given away deliberately), not a missing one. A
  negative candidate is ignored.
- Customer-specific prices (step 2) need the customer tables of Phase 8. The step exists in the
  resolver and no caller can supply one yet.

**Code lookup at the till** (`LookupProductHandler`), in order: **barcode**, then **SKU**, then
**product code** (resolving to the product's default variant). All three match exactly, trimmed and
upper-cased — a scanner sends a barcode, and a person reading a label types a code.

- The quantity one scan sells is the matched barcode's `PackQuantity`: a case barcode sells 24
  pieces, and the same variant found by SKU or product code sells 1. A lookup that scanned nothing
  never inherits a case quantity.
- A withdrawn (inactive or archived) product still resolves, with `IsSellable = false` and the price
  and tax it would have had. "Unknown barcode" at a till means the cashier scans it four more times;
  "this line is withdrawn" is a sentence a shop can act on.

**Discount rules**

- Line discount: percent (0–100) or fixed amount, capped by
  `Discount:MaxLinePercent` (default 20) and by the cashier's `sales.discount.max_percent` grant.
  Exceeding either requires an approval record (`DiscountApproval`).
- Bill discount is allocated across lines in proportion to each line's net-of-line-discount gross,
  using the **largest-remainder method** so that `Σ allocated = billDiscount` exactly.
- Discounts never make a line total negative. A 100% discount is allowed only with
  `sales.discount.full` (gift/sample) and posts stock as a sale at zero revenue.
- Promotions/coupons: at most one coupon per sale unless `Promotion:AllowStacking` is on; a coupon
  is consumed only when the sale commits (reserved during checkout, released on abandon).

## 2. Tax computation (VAT and SD)

Configuration per `TaxClass` and `TaxRule` version (ADR-0019). Supported shapes:

- `Exclusive`: price excludes tax; tax is added.
- `Inclusive`: the displayed price already contains tax; tax is extracted.
- `Exempt` / `ZeroRated`: tax amount 0, recorded with the reason so filings can distinguish them.

Let `q` = quantity, `p` = resolved unit price, `dl` = line discount amount, `db` = allocated bill
discount, `v` = VAT rate (fraction), `s` = SD rate (fraction).

**Exclusive pricing**

```
gross        = round4(q * p)
netOfDisc    = gross - dl - db                  # ≥ 0
sdAmount     = round4(netOfDisc * s)             # SD applies on value before VAT
vatBase      = netOfDisc + sdAmount              # SD is part of the VAT base
vatAmount    = round4(vatBase * v)
lineTotal    = netOfDisc + sdAmount + vatAmount
```

**Inclusive pricing** (`p` includes SD and VAT)

```
gross        = round4(q * p)
netOfDisc    = gross - dl - db
divisor      = 1 + s + v + (s * v)
baseValue    = round4(netOfDisc / divisor)
sdAmount     = round4(baseValue * s)
vatAmount    = round4((baseValue + sdAmount) * v)
lineTotal    = netOfDisc                         # unchanged by construction
```

Rules:

- `round4` = round to 4 decimals, half away from zero. The **bill** total is rounded to 2 decimals
  exactly once, and the rounding difference is absorbed by a `RoundingAdjustment` field — never by
  silently altering a line.
- Line totals always sum to the bill total (largest-remainder allocation guarantees this).
- Both `vatAmount` and `sdAmount`, the rates used, and the `TaxRuleVersion` id are stored **per
  line** so filings and returns are reproducible.
- VAT on a return is reversed proportionally from the original line snapshot, not recomputed at
  today's rate.

**Which rate applies** (`TaxResolutionService`), as built in Phase 5:

1. The product's `TaxClass`; a product with no class falls back to the **tenant's default class**,
   which is how a shop that never opens the tax screen is still taxed correctly.
2. A `TaxRule` naming the selling **branch** beats the organization-wide rule for that class. There
   is no nearest-match search: a rule either names this branch or names none.
3. Within the rule, the **version in force at the instant asked about** — so a line priced for an
   earlier date uses the rate of that date, and a return uses the rate on the line it reverses.

Two deliberate fail-safes, because a silent zero understates a VAT return:

- A class with **no rate in force** (asked about a date before its first version) resolves as exempt
  **with a stated reason** (`No tax rate in force for class <code>`), which travels onto the line
  and the receipt.
- A tenant with **no tax class configured at all** resolves the same way, with the reason
  `No tax class configured`, rather than charging a guessed 15%.

Adding a rate version closes the previous one at the new start instant, so the timeline has no gaps
and no overlaps, and a filtered unique index keeps exactly one open-ended version per rule.
**Backdating** before the current version's start is refused: a rate that silently changes what last
week's sales should have charged is a tax problem, not a correction. Exempt and zero-rated classes
must carry a reason, because a VAT return distinguishes them.

> Compliance note: SD-in-VAT-base is the common Bangladeshi treatment, but applicability varies by
> product and SRO. The engine takes `s`, `v`, base composition and inclusivity from configuration;
> §12 records this as an assumption requiring accountant sign-off before a production pilot.

## 3. Sale totals

```
SubTotal          = Σ line gross
TotalLineDiscount = Σ dl
BillDiscount      = db total
TotalDiscount     = TotalLineDiscount + BillDiscount
TaxableValue      = Σ (netOfDisc + sdAmount)
TotalSd           = Σ sdAmount
TotalVat          = Σ vatAmount
GrandTotal        = round2(TaxableValue + TotalVat)
AmountPayable     = GrandTotal + CashRoundingAdjustment        # adjustment only when cash-settled
AmountPaid        = Σ payment.Amount (Completed or Authorized-cash)
ChangeDue         = max(0, cashTendered - (AmountPayable - nonCashPaid))
BalanceDue        = AmountPayable - AmountPaid                 # > 0 only for credit sales
```

A sale may only commit when `BalanceDue == 0` **or** the customer has an approved credit account
with sufficient available credit (see §6).

Cash rounding: `CashRounding:Increment` (default `1.00`, set `0` to disable), mode
`Nearest|Up|Down` (default `Nearest`). Applied only to the cash portion of the final amount, posted
as `CashRoundingAdjustment` with its own GL-ready label.

## 4. Inventory rules

- **Availability**: `Available = QuantityOnHand - QuantityReserved`.
- A sale decrements `QuantityOnHand`. Reservations are used by parked bills (`Inventory:ReserveOnPark`,
  default off), transfers in dispatch state and online orders.
- `Inventory:AllowNegativeStock` (org, branch-overridable; default **false**):
  - `false` → the sale line is rejected with `inventory.insufficient_stock` after a row-locked
    re-check; the cashier may reduce quantity or remove the line.
  - `true` → the sale proceeds; balance goes negative and the line is flagged for review.
- Non-stock products (`TrackInventory = false`: services, fees) never post movements.
- **Batch selection**: FEFO by default (earliest `ExpiryDate`, then earliest receipt). Expired
  batches cannot be sold (`Inventory:BlockExpiredSale`, default true). Near-expiry sale may require
  `inventory.sell_near_expiry` within `Inventory:NearExpiryDays` (default 30).
- **Serial items**: quantity per line is 1 per serial; a serial may be `InStock` once only, and
  selling it moves it to `Sold` with the sale reference.
- **Transfers**: `Dispatch` moves stock out of the source and into `InTransit`; `Receive` moves it
  into the destination. Shortage = dispatched − received; a shortage > 0 requires a reason and posts
  a `TransferLoss` adjustment at source cost after approval.
- **Counts**: a count snapshot freezes expected quantities at a timestamp; movements after the
  snapshot are applied to the expected figure before variance so counting need not stop trading
  (`StockCount:FreezeTrading`, default false).
- Ledger immutability: movements are never updated or deleted. A correction is a new movement of
  type `AdjustmentReversal` referencing `ReversesMovementId`.

## 5. Costing and profit

Moving weighted average per `(warehouse, variant[, batch])` (ADR-0009):

```
# inbound (receipt, positive adjustment, customer return to stock, transfer-in)
newQty  = onHand + inQty
newAvg  = (onHand * avgCost + inQty * inUnitCost) / newQty          # if newQty > 0
        = inUnitCost                                                 # if onHand <= 0

# outbound (sale, write-off, transfer-out, supplier return)
unitCostUsed = avgCost            # snapshotted onto the document line
onHand       = onHand - outQty    # avgCost unchanged
```

Landed cost at receipt: `inUnitCost = (lineValue - lineTradeDiscount + allocatedCharges) / receivedQty`,
charges allocated by `Procurement:LandedCostBasis` (`Value` default, or `Quantity`/`Weight`).

Reporting formulas (used verbatim by Phase 16 queries):

```
Gross Sales      = Σ SubTotal (before discounts, excluding tax)
Net Sales        = Gross Sales - Total Discounts - Returns(net of tax)      # excludes VAT/SD
COGS             = Σ (saleLine.Quantity * saleLine.UnitCostSnapshot)
                   - Σ (returnLine.Quantity * returnLine.UnitCostSnapshot restocked)
Gross Profit     = Net Sales - COGS
Gross Margin %   = Gross Profit / Net Sales * 100                           # 0 when Net Sales = 0
Inventory Value  = Σ (QuantityOnHand * AverageUnitCost)                     # per warehouse/date
Stock Cover Days = QuantityOnHand / average daily sold qty (trailing 30d)
```

Conventions: tax is excluded from sales and profit figures (it is not revenue); damaged/expired
write-offs are an expense line, **not** part of COGS; returns reduce Net Sales and COGS in the
period of the **return**, not of the original sale; discounts reduce Net Sales and are also reported
separately.

## 6. Customer credit

- Credit sale requires `Customer.CreditAccount.IsActive` and
  `AvailableCredit = CreditLimit - OutstandingBalance - PendingOfflineReservations ≥ BalanceDue`.
- Over-limit requires `sales.credit.override` and records a `CreditOverride` with approver and reason.
- Outstanding balance is a ledger (`CustomerLedgerEntry`: invoice, payment, return credit,
  adjustment, write-off). The balance is the ledger sum; it is never edited directly.
- Collections apply oldest-first by default (`Credit:AllocationStrategy = Fifo|Manual`).
- Offline: the device uses the last-synced limit minus local reservations. On push, if the limit is
  now exceeded, the sale is **accepted** and flagged `CreditExceeded` for collection follow-up — the
  goods have already gone; rejecting the record would lose the receivable.

## 7. Loyalty

- Accrual: `points = floor(eligibleNetSales * program.PointsPerCurrencyUnit)`; excluded categories,
  discounted-item exclusion and minimum basket are configurable. Accrual is one ledger entry keyed
  by `(programId, saleId)` — a unique index makes a duplicate accrual impossible (FR-CUS-06).
- Redemption: `value = points * program.CurrencyPerPoint`, in multiples of `RedemptionStep`, capped
  by `MaxRedemptionPercent` of the bill; redemption is a payment line of method `LoyaltyPoints`.
- Expiry: points expire `ExpiryMonths` after accrual (default 12); an expiry job posts negative
  entries, oldest-first consumption (FIFO on accrual date).
- Returns: reverse `round(accruedPoints * returnedEligibleValue / originalEligibleValue)` (floor).
  If the account's balance is insufficient, the balance goes negative and future accrual repays it
  (`Loyalty:AllowNegativeBalance`, default true) — the alternative, blocking the return, is worse.
- Redeemed value on a returned sale: refund is reduced by the redeemed amount and the points are
  **restored** (`Loyalty:RestoreRedeemedOnReturn`, default true).

## 8. Cash and shift

```
ExpectedCash = OpeningFloat
             + CashSales
             + CashIn            (deposits, top-ups, collections received in cash)
             - CashRefunds
             - CashOut           (banking, safe drops)
             - CashExpenses
Variance     = CountedCash - ExpectedCash          # negative = short, positive = over
```

- `Cash:VarianceApprovalThreshold` (default 200 BDT absolute): beyond it, closing needs
  `cash.variance.approve` from a supervisor plus a reason.
- A terminal may have at most one open session; a cashier may have at most one open session per
  terminal. `Cash:RequireOpenShiftToSell` (default true).
- Closing a shift prints a Z-report, snapshots totals (so later data changes can't alter history) and
  sets the session immutable. Corrections post `ShiftAdjustment` records with approver, reason and
  audit entry.
- Non-cash payments are reconciled per method against provider settlements, not against the drawer.

## 9. Returns and voids

- Void ≠ return. A **void** cancels a sale in the same shift/business day before settlement
  reconciliation (`Sales:VoidWindow = SameShift|SameBusinessDay|None`, default `SameShift`), requires
  `sales.void`, reverses stock, payments and loyalty in full and keeps both documents.
- A **return** happens after the sale is final and can be partial (FR-RET-02/03).
- `Refundable = Σ returnedLineValue(incl. proportional discount & tax) - alreadyRefunded`.
  `refundAmount ≤ Refundable`; exceeding requires `sales.refund.override` + an exception record.
- Refund method defaults to the original method; cash refunds require cash on hand in the session
  and `sales.refund.cash`. MFS/card refunds go through the provider refund API and sit in
  `RefundPending` until confirmed.
- Restock only when the inspection result is `Good`; `Damaged` posts a damage write-off; `Opened`
  follows `Returns:OpenedPolicy` (`Restock|Damage|Quarantine`, default `Quarantine`).
- Return window `Returns:WindowDays` (default 7, 0 = unlimited); outside it, elevated permission.

## 10. Numbering

- Series per organization, branch and document type with prefix, optional branch/terminal code,
  reset period (`None|Daily|Monthly|Yearly`) and zero-padded width:
  `INV-{BRANCH}-{YYYYMM}-{000001}`.
- Online numbers come from a locked allocator row (gapless per series).
- Offline numbers come from a pre-allocated **terminal block**; blocks are recorded, so a gap maps to
  an unused block and is explainable in an audit. Receipt numbers are unique per
  `(organization, series)` by index.
- Numbers are never reused, including after a void (the void gets its own number).

## 11. Idempotency and duplicate prevention (summary)

| Operation | Idempotency key | Uniqueness guard |
|---|---|---|
| Sale | client `operation_id` | `IdempotencyRecord(OrganizationId, Key)` + `Sale(OrganizationId, TerminalId, LocalSaleId)` |
| Payment | `operation_id` per payment | `Payment(ProviderId, ProviderReference)` unique |
| Provider callback | provider reference | same unique index; replay returns 200 with no effect |
| Return | client `operation_id` | returnable-quantity check inside the transaction |
| Loyalty accrual | sale id | `LoyaltyTransaction(ProgramId, SaleId, Type)` unique |
| Goods receipt | client `operation_id` | `GoodsReceipt(OrganizationId, SupplierReference)` optional unique |
| Shift close | session id | session state transition is one-way |

## 12. Assumptions requiring confirmation

| # | Assumption | Impact if wrong | Owner |
|---|---|---|---|
| A-01 | One currency (BDT) per organization in v1; no multi-currency sales | Schema keeps `CurrencyCode`, so adding rates later is additive | Product |
| A-02 | SD is included in the VAT base; VAT = `(net + SD) × rate` | Wrong tax filings — **must be confirmed by an accountant** before pilot | Finance |
| A-03 | Prices are tax-inclusive for retail organizations by default | Receipt presentation and tax extraction | Finance |
| A-04 | Moving weighted average costing is acceptable for pilot customers | COGS/profit figures; FIFO reserved in settings | Finance |
| A-05 | Cash rounding to the nearest 1.00 BDT is acceptable and legal | Till variances; configurable per org | Finance |
| A-06 | No GL/accounting-package integration in v1 (export only) | Manual bookkeeping effort | Product |
| A-07 | Offline card/MFS payments are impossible (no provider offline flow) | Cashiers must take cash offline | Product |
| A-08 | A branch operates in one timezone, default `Asia/Dhaka`, business day starts 00:00 local | Day-boundary reporting; `BusinessDayStart` configurable | Product |
| A-09 | Thermal printers are ESC/POS compatible; Bangla printing via raster/image rendering where the printer lacks the font | Receipt layout work per model | Engineering |
| A-10 | Scanners act as keyboard HID devices | Alternative SDK adapters needed for serial/Bluetooth scales & scanners | Engineering |
| A-11 | Pilot scale: ≤ 20 branches, ≤ 100 terminals, ≤ 200 000 variants, ≤ 50 000 sales/day per tenant | Sizing, indexing, partitioning plans | Engineering |
| A-12 | NBR does not require real-time fiscal device reporting for the pilot customers' category | A fiscal/EFD integration module would be needed | Finance |
| A-13 | A lapsed subscription keeps writing for a 7-day grace period (`Tenancy:GraceDays`) before the tenant becomes read-only | A shop that cannot ring up sales stops trading, so the window is commercial, not technical | Product |
| A-14 | A branch may override the trading settings but not `InvoiceNumberFormat`; numbering stays organization-wide | Document numbers stay comparable across branches; per-branch formats would need a migration to allow | Product |
| A-15 | A tenant must keep at least one active branch, and a branch's default warehouse may not be deactivated | Both are states that make the POS unusable while looking like ordinary administration | Product |
| A-16 | A tenant with no subscription row is unrestricted, and a plan limit whose counter is not implemented yet is reported rather than enforced | Fails open on missing information instead of blocking a paying customer; provisioning always writes a subscription (ADR-0030) | Engineering |
| A-17 | A product with no tax class is taxed with the tenant's default class, and a class with no rate in force is exempt with a stated reason rather than a guessed rate | An unclassified product is taxed like the shop's ordinary goods; the alternative is an untaxed sale nobody notices | Finance |
| A-18 | A catalog import is a dry run unless asked otherwise, is all-or-nothing when it is for real, and takes at most 5 000 rows per file | A shop never half-imports a supplier spreadsheet; large catalogues are split | Product |
| A-19 | A barcode is unique per tenant and never globally, and a case barcode's pack quantity is what one scan sells | Two shops may print the same internal code; a case scan rings up the case | Product |

## 13. Open questions (asked, not assumed)

These affect money, tax or data integrity, so they are tracked as blockers for the phases that
need them rather than guessed:

1. **A-02 / VAT+SD composition and rates per product class** — needed before Phase 5 ships to a real
   customer (engine is configurable, so development proceeds).
2. **Mushak invoice fields and numbering constraints** required for the pilot customer's category —
   needed before Phase 9 receipt sign-off.
3. **bKash / Nagad merchant model** (Checkout vs Tokenized vs Merchant-to-Customer) and sandbox
   credentials — needed before live payment testing (Phase 9).
4. **Refund authority limits** per role (amount thresholds) — org-configurable; defaults proposed:
   cashier ≤ 1 000 BDT, manager ≤ 20 000, owner unlimited.
5. **Offline duration ceiling** acceptable to the business (proposed 72 h) and offline value cap
   (proposed 50 000 BDT cumulative).
