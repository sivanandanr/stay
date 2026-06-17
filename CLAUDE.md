# CLAUDE.md — Stay Booking Platform

The working constitution **and** rule set for the **Stay** vertical (hotel/stay/villa booking). Place at **`Stay/CLAUDE.md`** (nested — your existing repo-root `CLAUDE.md` for the food platform stays untouched; Claude Code layers this on when working inside `Stay/`). Deep detail lives in `docs/` — this file is the day-to-day rules; read the referenced doc when a task needs depth.

> Comprehensive by request. If adherence slips, trim a section here to a one-line pointer into `docs/` (same rules, more detail there).

---

## 0. What this is

Agoda/MakeMyTrip-class booking platform. **.NET 10 / C# 14**, **modular monolith** (hard module boundaries, extractable later). **PostgreSQL 16 + PostGIS** (system of record), **OpenSearch** (search read model), **Redis** (cache/locks), **Kafka** (events).

- **Gateway = Ocelot** (existing, shared with the food app). Add Stay routes **additively** (`ocelot.stay.json`); never touch food routes.
- **Angular 20 web app (`stay-portal`) = Owner/Admin portal only** — owner registration, **admin approval** of owners before they can list, property/ARI/booking/earnings management. *Not* the guest funnel.
- **Mobile app (`jap-stay-app`) = the guest experience** over a versioned REST API (`/api/v1`). The API is a first-class product.
- **No guest checkout — every booking requires UserService login** (`booking.guest_id NOT NULL`).

Repo folders: `Stay/` (backend), `stay-portal/` (Angular), `jap-stay-app/` (mobile).

---

## 1. GOLDEN RULES (never violate)

1. **No overbooking (BR-1).** Inventory hold = one atomic conditional `UPDATE … WHERE (total_allotment - units_sold - units_held) >= :qty` over the whole night range + row-count check (all-nights-or-none). DB `CHECK (units_sold + units_held <= total_allotment)` is the backstop. Never read-then-write, never per-night separate transactions.
2. **Frozen quote (BR-2).** Price at hold time is the contract; store per-night breakdown on `booking_room.nightly_breakdown`; never recompute a confirmed booking.
3. **Idempotency at every external boundary (BR-5).** UserService provisioning, PaymentGateway, NotificationService, channel sync, public API — all idempotent by key; webhooks de-dupe by provider event id.
4. **No cross-context foreign keys.** Real FKs only within a schema; cross-context = plain `xref:` columns. Integrity via domain logic + events.
5. **Module boundaries are compile-time enforced.** A module references another module's `*.Contracts` only. Arch-test stays green.
6. **Saga never blocks on side effects (BR-11).** Inventory + payment commit synchronously; notifications/indexing/payout flow async via outbox.
7. **One service, one database for Booking + ARI + Pricing.** The hold is a single DB transaction across them — never split these into separate services/DBs.
8. **Authenticated bookings only.** No guest/anonymous checkout; bookings tied to a UserService `sub`.
9. **Don't rebuild shared services; flag bugs, don't paper over them.**

---

## 2. Shared services — INTEGRATE, never rebuild

Shared with the existing **food app**. The booking platform is a *second consumer*. **Every change is additive + namespace-isolated — never alter food-facing behavior.** A shared-service outage now affects both apps (accepted coupling) and Stay adds load they must be sized for.

- **UserService (identity/auth):** OIDC/JWT; validate issuer/audience/JWKS/expiry. Reference users by `sub`. New OIDC clients (`stay-portal`, `jap-stay-app`); food client untouched. First-login provisioning is idempotent + race-safe. Roles mapped in `admin.role_assignment` (not in UserService). No passwords/tokens stored here.
- **PaymentGateway (RazorPay):** call **through `IPaymentGateway`**, never the RazorPay SDK directly (§9). `source=stay` discriminator; separate Route accounts from food.
- **NotificationService:** emit typed commands via outbox; categories namespaced `stay.*`; NS owns delivery (§8).
- **StorageService (MinIO):** separate buckets `stay-media`, `stay-documents`; store only object keys; presigned-URL access.

---

## 3. Local development setup

Prereqs: .NET 10 SDK, Node 20+, Docker (reuse your existing `docker/`).

1. **Docs in place:** `docs/` at repo root; this file at `Stay/CLAUDE.md`.
2. **Infra:** reuse existing Postgres/Redis/Kafka; add **PostGIS** (extension on Postgres) + **OpenSearch** to `docker/`. `docker compose up -d`.
3. **Scaffold backend:** `cd Stay && bash scaffold-stay.sh` → builds the solution (BuildingBlocks, Stay.Api, Catalog+Booking modules, arch-test). Add remaining modules with `add_module`.
4. **Database:** `createdb stay && psql -d stay -f Stay/db/schema.sql` (PostGIS available). Per-module EF migrations over time; `schema.sql` is the reference + fast bootstrap.
5. **Wire integrations** in `appsettings`: `Stay` connection string; `Auth` (UserService authority/audience); `Services` (PaymentGateway base+`source=stay`, NotificationService base+`stay.` prefix, Storage endpoint+buckets, Redis); `Kafka`. Provide **local fakes** implementing each port so the service runs without the real food services.
6. **Gateway:** add `ocelot.stay.json` (additive) routing `/stay/api/v1/*` → `Stay.Api`.
7. **Run:** `dotnet run --project src/Stay.Api` → hit `GET /stay/api/v1/catalog/ping` with a UserService token. A traced request reaching Postgres + outbox→Kafka = **Gate G0**.
8. **Frontends:** `ng serve` in `stay-portal`; mobile build per your `jap-*-app` stack. See `docs/stay-frontend-runbooks.md`.

---

## 4. Architecture & module structure

```
Stay/src/
  Stay.Api/                         # ASP.NET Core REST /api/v1 — composition root; mobile + portal consume this
  BuildingBlocks/                   # Result<T>, CQRS pipeline, outbox, messaging, IModule
  Modules/
    Catalog/ ARI/ Pricing/ Booking/ Payment/ Search/
    Reviews/ Promotion/ Channel/ Admin/ NotificationAdapter/
      <Module>.Domain .Application .Infrastructure .Contracts
```
Each module owns a **PostgreSQL schema** + its own EF migration history. Booking is the only strongly-consistent core; read-facing modules (Search, Catalog, Reviews) are eventually consistent + cached. Payment, NotificationAdapter, media, and the UserService-backed guest profile are thin integration layers.

---

## 5. Backend rules

- **CQRS** via mediator pipeline (validation → authorization → logging → transaction → handler). Commands mutate; queries don't.
- **`Result<T>`** for expected failures; exceptions only for the exceptional.
- **EF Core 10** for write CRUD + portal/admin reads; **Dapper** for hot paths (the hold, calendar reads, search-time pricing). EF uses **snake_case** matching `schema.sql`.
- **Outbox per writing context**; domain events written in the state-change transaction; dispatched to Kafka. No dual-write.
- **API:** controllers thin (validate → dispatch → map `Result` to HTTP). Errors as **RFC 7807 `problem+json`** with stable machine-readable `type` (`…/errors/sold-out`, `…/errors/price-changed`, `…/errors/hold-expired`). Versioned `/api/v1`, additive within a version. OpenAPI published; clients generated from it.
- **Idempotency-Key** header required on `POST /holds`, `confirm`, `cancel`, payment callbacks.
- **OpenTelemetry** span per handler; booking saga is one end-to-end trace.
- **Migrations:** per-module EF, **expand/contract** (add → backfill → enforce → drop) for zero-downtime.
- **Money** `decimal`/`NUMERIC(12,2)` + `CHAR(3)` currency, never float. **Time** `TIMESTAMPTZ`; stay-night/cancellation math in **property IANA timezone (BR-4)**. **Concurrency** `row_version` on every mutable aggregate.

---

## 6. Frontend rules

### stay-portal (Angular 20, Owner/Admin)
- Standalone, **zoneless, signals**; no NgModules/Zone.js. RxJS only for streams/HTTP.
- PKCE against UserService (`stay-portal` client); access token **in memory**, refresh via `HttpOnly` cookie/silent renew — **never localStorage**.
- **Role guards:** owner vs admin/ops routes separated; unapproved owner → `/pending`, blocked from listing routes (UI convenience; **API authorizes for real**).
- Typed reactive forms; client validation mirrors server; server is authoritative.
- API client generated from OpenAPI; interceptors attach token + `traceparent`, surface `problem+json` uniformly.
- Build the five journeys (owner registration → **admin approval** → property/room/media → ARI calendar → bookings/earnings) — approval first.

### jap-stay-app (mobile, guest)
- Match your existing `jap-*-app` stack. PKCE with app redirect scheme; tokens in secure storage (Keychain/Keystore).
- Browse/search anonymous; **hold/confirm/cancel/profile require token**. Generate API client from `/api/v1` OpenAPI.
- Idempotent retries; branch on `problem+json` types; push via NotificationService.

---

## 7. UI/UX rules

- **Design system first:** shared tokens (color/spacing/typography), consistent components; no ad-hoc styles. Dark-mode-ready tokens.
- **Accessibility WCAG 2.1 AA:** semantic HTML, labels, keyboard nav, focus management, 4.5:1 contrast, screen-reader-tested critical flows.
- **Responsive:** portal is desktop-first/data-dense; mobile guest app is touch-first; both adapt gracefully.
- **Every async view has loading / empty / error states** — never a blank screen or silent failure. Use skeletons for perceived speed.
- **Forms:** inline validation, clear actionable messages, preserve input on error, disable submit while in-flight, **confirm destructive actions** (cancel booking, reject owner).
- **Booking funnel UX (no dark patterns):** show **full price incl. taxes/fees before payment** (no surprise totals), a **visible hold countdown**, the cancellation policy **before** confirm, accessible date/guest pickers, clear SOLD_OUT/price-changed recovery.
- **Portal UX:** sortable/filterable/paginated tables; **bulk ARI editing** (date-range painter); approval queue shows full KYC context + mandatory reason on reject; optimistic UI only where safe (**never** for money/inventory).
- **Error messaging:** human and actionable; never raw codes or stack traces.
- **i18n + locale/currency formatting** from day one; no hard-coded user strings. Render times in the property timezone with offset.

---

## 8. Notifications rules (via NotificationService)

- **Event-driven through the outbox:** platform owns *when/what* (event → typed command); NS owns *how/where* (templating, channels, delivery, retries). Saga never blocks on it (BR-11).
- **Idempotent:** `event_id`-keyed (e.g. `BookingConfirmed:{booking_id}`); redelivery → single send. Recorded in `notify.notification_emission` (audit/dedupe ledger).
- **Categories namespaced `stay.*`** (`stay.booking_confirmed`, `stay.host_new_booking`, `stay.owner_approved`, `stay.cancellation`, `stay.refund`, `stay.pre_arrival`, `stay.balance_due`). Never edit food templates.
- **Transactional/legal messages never suppressed** by marketing preferences: booking confirmation, payment receipt, cancellation, refund, owner approval/rejection. Marketing is opt-in.
- **Scheduling** (pre-arrival T-48h/T-24h, balance-due) in **property timezone (BR-4)** — platform-owned scheduler if NS lacks scheduling.
- **Channels:** email/SMS/push. Mobile registers its push token **post-login**; the app does not poll.
- Recipient is always a known user `sub` (no guest path) → no raw-email delivery concerns.

---

## 9. RazorPay payment gateway rules

**The Stay Payment module calls the existing `PaymentGateway` service (the RazorPay wrapper) through an `IPaymentGateway` port. Never integrate the RazorPay SDK directly in Stay.** PaymentGateway encapsulates the Razorpay Orders/Checkout/signature/webhook mechanics; Stay records state and reacts.

**Flow (what PaymentGateway does, what Stay must respect):**
- **Order → authorize → capture.** Stay requests an order/intent via PaymentGateway at confirm time; the mobile client completes payment (cards/**UPI**/netbanking/wallets); **3DS/SCA is handled by Razorpay checkout** — the saga **suspends and resumes** on the challenge callback within the hold TTL.
- **Capture on confirm:** commit inventory (`units_held → units_sold`) on authorization, then capture. **Capture-fails-after-auth → never block the guest**; booking is CONFIRMED, push to a **finance retry queue**.
- **Webhooks are the source of truth** for async state (`payment.captured`, `payment.failed`, `refund.processed`, …); PaymentGateway verifies Razorpay signatures. Stay's webhook handler is **idempotent by provider event id** (`payment.webhook_event` unique on `(psp, psp_event_id)`). A **poll-reconciler** covers missed webhooks.

**Stay-side rules:**
- **Idempotency key = `stay:{booking_id}:{attempt}`** on every PaymentGateway call; replays return the original result.
- **`source=stay`** on every call so RazorPay orders/webhooks/settlements are tagged and reconciled separately from food.
- **Record** `payment`, `refund`, `webhook_event` rows locally; PaymentGateway performs the PSP ops.
- **Refunds:** idempotent; **restore inventory before issuing the refund** (cancellation flow); on refund failure, inventory is already restored → queue idempotent retry + finance alert.
- **Payouts to owners** via **Razorpay Route linked accounts**, separate from food merchant settlements; commission deducted; statements generated; reconciled.
- **Currency:** INR primary; if international is enabled, snapshot the FX rate onto the booking (BR-2). Amounts always carry currency.
- **PCI SAQ-A:** raw card data **never** touches Stay — tokenize via Razorpay through PaymentGateway; store only PSP tokens in `guest.payment_method_token`.
- **Reconciliation** runs daily: PaymentGateway/Razorpay ledger ⟷ `payment`/`refund`/`payout` — zero unexplained deltas (Gate G2).

---

## 10. Auditing rules

A privileged action without an audit entry is a defect. Audit is **business-event evidence**, separate from operational/debug logs.

**Must audit (to `admin.audit_log`, append-only):**
- **Owner approval / rejection** decisions (+ reason).
- **Role grants/revocations** (`admin.role_assignment` changes).
- **Manual booking overrides/adjustments**, manual cancellations, manual refunds (reason **mandatory**).
- **Payment status transitions**, refunds, payout runs.
- **ARI bulk changes** (who changed allotment/rates/restrictions, before/after).
- **Property/media moderation** decisions (DRAFT→LIVE / reject).
- **PII access/export/erasure** and any data-subject request action.
- **Channel sync conflict resolutions**; **config/feature-flag** changes; admin impersonation/"login-as" if it exists.

**How:**
- Record `actor_sub`, `action`, `entity_type`, `entity_id`, `before`/`after` (JSON), `reason`, correlation/trace id, timestamp.
- **Immutable:** no UPDATE/DELETE on audit rows (enforce via DB grants/triggers); append-only.
- **Domain trails complement it:** `booking.status_history`, `channel.ari_sync_log`, `notify.notification_emission` — use them, don't duplicate into the audit log.
- **Access-controlled:** only admin/finance/compliance roles read audit; reading sensitive entries may itself be audited.
- **PII discipline:** mask/minimize PII in `before`/`after`; align with erasure (BR-8) — retain financial evidence, anonymize personal data where required.
- **Retention** per legal/compliance; never silently purge.

---

## 11. Performance rules

- **Budgets:** search p95 <500 ms; autocomplete p95 <100 ms; hold p99 <200 ms (G1); general API p95 <300 ms; index lag p99 <2 min. Sustained breach = regression.
- **Cache-first reads:** Redis price/availability (1–5 min TTL); CDN for media + cacheable GETs; keep ~80% cache hit honest.
- **Hot paths use Dapper** (the hold is one round trip). **No N+1** — project exact columns, batch. **Pagination mandatory** (cursor for search/changing lists) — no unbounded result sets.
- **Async all the way**; tuned Npgsql pooling; read replicas for catalog/ARI/history; the write primary stays for writes.
- ARI calendars partitioned monthly; hot ~18-month window in RAM; keep the partition-creation job running.
- **Frontend:** lazy routes, initial bundle <250 KB gz (CI-enforced), virtualized lists, deferred/responsive images, Lighthouse budget.
- **Load-test hot paths (k6)** before merge; gate suites (G1/G2/G3) run nightly.

---

## 12. Security rules

- **All auth via UserService (OIDC/JWT):** validate issuer/audience/JWKS/expiry, skew ≤60 s; never downgrade. **Authorize server-side for every protected action** — UI guards are convenience only.
- **Tenancy (BR-9):** owners read/write only their own properties' data (EF global query filters + tests proving cross-tenant fails). **The owner-approval gate is a server-side authz rule** — unapproved owners' listing calls are rejected.
- **Secrets:** none in code/images; external secrets operator; rotation supported.
- **TLS everywhere**, HSTS at the edge. **CORS** locked to known origins; no `*` with credentials.
- **Input:** validate + whitelist server-side; parameterized SQL only. **Output:** Angular encoding + strict **CSP**; no `bypassSecurityTrust*` without review.
- **Rate limiting at Ocelot** per-user + per-IP (stricter on auth/hold/search); `429` + `Retry-After`. Idempotency keys also defend against replay.
- **PCI SAQ-A** (§9). **MinIO** private buckets, short-lived presigned URLs, expiring document links.
- **Dependency + secret scanning** in CI; pen test on auth/booking/payment before launch (G4).

---

## 13. Phase-by-phase work plan (gates are hard stops)

Full detail in `docs/implementation-plan-v2.md` and `docs/phase0-backlog.md`; next-gen capabilities (Phase 9+) in `docs/future-roadmap.md`. Risk-first: prove the booking core and money before the conversion surface, and don't pull future-roadmap items forward onto an unproven core.

| Phase | Build | Gate |
|---|---|---|
| **0** Foundation & Integrations | Scaffold, Ocelot routes, JWT validation, outbox round-trip, OpenTelemetry, first-login provisioning, NotificationAdapter contract | **G0** walking skeleton + integrations proven |
| **1** Catalog & Host Core | **stay-portal owner registration + admin approval**, property/room/media, moderation, master data | — |
| **2** ARI & Pricing | partitioned calendars, restrictions, deterministic quote pipeline, taxes/FX | — |
| **3** Booking Core (saga) | atomic hold, hold→confirm (mock pay), reaper, multi-room, confirmation + host-alert notifications | **G1** no-overbooking under load 🔴 |
| **4** Payments ∥ Search | RazorPay via PaymentGateway (auth/capture/3DS/UPI), receipts; OpenSearch funnel | **G2** money idempotency 🔴 + **G3** search latency |
| **5** Cancellation/Modification/Reminders | cancel+refund, modify, no-show, pay-at-property/deposit, reminders | — |
| **6** Guest Account & Reviews | profile, trips, erasure (BR-8), verified reviews | **G4** soft launch (mobile funnel + portal) 🔴 |
| **7** Channel Manager/PMS | connect, ingest (ordered/idempotent), reverse sync, reconciliation, conflict resolution | **G5** sync integrity 🔴 |
| **8** Admin/Ops/Fraud/Payouts/Reporting | roles, disputes, overrides (audited), Route payouts, promotions, dashboards, reconciliation | — |
| **9** Loyalty/Partner API/Scale-out | loyalty, partner API, extract Search/Pricing | G6 |

**First week:** Phase 0 → G0. **First real feature:** Phase 1 owner-registration + admin-approval (the portal's reason to exist).

---

## 14. Definition of Done (per story)

API/UI slice + tests (unit + integration + the relevant gate scenario) green in CI · OpenAPI updated if API changed · OpenTelemetry spans named · arch-test green (boundaries intact) · **authorization enforced server-side** · privileged actions audited · no secrets committed · runbook note for anything operational.

---

## 15. Anti-patterns (do NOT)

- ❌ Read-then-write availability, or per-night separate transactions.
- ❌ Recompute a confirmed booking's price; trust the search index for availability truth.
- ❌ Cross-context FKs; reading another module's tables directly.
- ❌ Split Booking/ARI/Pricing into separate services or databases.
- ❌ Guest/anonymous checkout (`guest_id` is `NOT NULL`); guest funnel in the Angular portal.
- ❌ Integrate the RazorPay SDK directly (go through PaymentGateway); store card data, passwords, or file bytes in Postgres.
- ❌ Modify a shared service's existing food-facing behavior/contract — additive + isolated only; flag it instead.
- ❌ Block the saga on notifications/indexing.
- ❌ Authorize only in the UI; skip the audit trail on a privileged action.
- ❌ Date/cancellation math in server/guest timezone instead of the property's.
- ❌ Surprise totals, hidden fees, or missing hold countdown in the funnel; blank loading/error states.
- ❌ Silent fallbacks that hide a bug.
