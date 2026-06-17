# Booking Platform — Engineering Standards & Rules

Companion to `CLAUDE.md`. The constitution (`CLAUDE.md`) holds the inviolable rules; this document holds the detailed standards an agent (or engineer) follows when building. Covers: backend, frontend (Owner/Admin portal), the mobile/public API contract, performance, and security.

**Context recap (changed):** Ocelot is the edge gateway (existing). Angular 20 is the **Owner/Admin portal** (owner registration + admin approval + property/ARI management) — *not* the guest funnel. The **guest experience is the mobile app over a versioned REST API**. **No guest checkout — every booking requires login.** UserService, PaymentGateway (RazorPay), NotificationService, StorageService (MinIO) are shared with the food app — reuse additively, never alter their food-facing behavior.

---

## 1. Backend Rules

### 1.1 Structure & patterns
- Modular monolith; one ASP.NET Core solution, modules with `Domain / Application / Infrastructure / Api / Contracts`. Cross-module references = `*.Contracts` only (arch-test enforced).
- **CQRS** through a mediator pipeline (validation → authorization → logging → transaction → handler). Commands mutate; queries never mutate.
- **`Result<T>` for expected failures**; exceptions only for the truly exceptional (a thrown exception is a bug or an infra fault, never an expected business outcome).
- **EF Core 10** for write-side CRUD and owner/admin reads; **Dapper** for hot paths (the atomic hold, calendar reads, search-time price resolution). EF uses **snake_case**, matching `schema.sql` exactly.
- One **outbox per writing context**; domain events are written in the same transaction as the state change and dispatched to Kafka. No dual-write to the bus, ever.

### 1.2 The booking-core invariants (restate, because they're the point)
- Atomic all-nights-or-none hold via conditional `UPDATE` + row-count check; DB `CHECK` backstop. No read-then-write, no per-night transactions.
- Frozen quote on `booking_room.nightly_breakdown`; never recompute a confirmed booking.
- Saga (`DRAFT → HELD → CONFIRMED | EXPIRED | FAILED`) is idempotent at every step; compensations release holds / void auths. Reaper releases expired holds.
- `guest_id NOT NULL` — bookings are always tied to an authenticated `sub`.

### 1.3 API surface (backend side)
- All endpoints under `Platform.Api`, versioned `/api/v1`. Controllers are thin: validate → dispatch command/query → map `Result<T>` to HTTP.
- Errors as **RFC 7807 `application/problem+json`** with a stable `type`, `title`, `status`, `detail`, `traceId`. Never leak stack traces or SQL.
- Validation with FluentValidation (or equivalent) in the pipeline; 400 with field-level problem details.
- Idempotency: state-changing endpoints (`hold`, `confirm`, `cancel`, payment callbacks) accept an `Idempotency-Key` header; replays return the original result, not a duplicate effect.
- Every handler emits an OpenTelemetry span; the booking saga is one trace end-to-end.

### 1.4 Data & migrations
- Money `decimal`/`NUMERIC(12,2)` + `CHAR(3)` currency; never float. Time `TIMESTAMPTZ`; stay-night math in property IANA timezone (BR-4).
- Optimistic concurrency via `row_version` on every mutable aggregate.
- Migrations are **per-module EF migrations**, expand/contract (add nullable/new → backfill → enforce → drop old) so deploys are zero-downtime.
- No cross-context FKs; cross-context refs are plain `xref:` columns.

---

## 2. Frontend Rules — Angular 20 Owner/Admin Portal

**Scope:** owner registration, the admin approval workflow that vets owners before they can list, and owner-facing property/ARI/booking/earnings management. Build only these journeys; the guest funnel is mobile.

### 2.1 Core journeys (build these, in order)
1. **Owner registration & KYC submission** — owner signs up (UserService), submits business/identity/payout/tax details → status `PENDING`.
2. **Admin approval workflow** — admin queue lists pending owners; admin reviews KYC, approves/rejects with reason; approval grants the `host` role (mapped in `admin.role_assignment`, not UserService). **An owner cannot create a listing until approved.** This gate is the portal's reason to exist — enforce it on both UI and API.
3. **Property & room-type management** — create/edit listing, units, media (upload via StorageService presigned URLs), policies. New listings start `DRAFT` → content moderation → `LIVE`.
4. **ARI management** — rate calendar, availability/allotment, restrictions; bulk date-range edits.
5. **Bookings & earnings** — occupancy calendar, booking list, payout statements.

### 2.2 Technical rules
- **Standalone components, zoneless, signals** for state. No NgModules. No `Zone.js`. Prefer signals + `computed` over RxJS for component state; use RxJS only for streams/HTTP.
- **Auth:** PKCE against UserService (the booking platform's OIDC client). Access token in memory; refresh via secure, `HttpOnly` cookie or silent renew — **never `localStorage`/`sessionStorage`** for tokens.
- **Route guards by role:** `owner` routes vs `admin`/`ops` routes are guard-separated; a pending (unapproved) owner is routed to a "pending approval" state and blocked from listing routes. The UI guard is convenience; the API authorizes for real (defense in depth).
- **Forms:** typed reactive forms; client validation mirrors server rules but the server is authoritative.
- **HTTP:** a typed API client generated from the OpenAPI spec (keep client and server in sync); interceptors attach the token, add `traceparent`, and surface `problem+json` errors uniformly.
- **State:** server state via signal stores keyed by query; optimistic UI only where safe (never for money/inventory actions).
- **Accessibility:** WCAG 2.1 AA — semantic HTML, labels, focus management, keyboard nav, 4.5:1 contrast.
- **i18n** from day one (owners/admins may be multi-locale); no hard-coded user-facing strings.

### 2.3 Frontend performance
- Lazy-load every feature route; route-level code splitting. Initial bundle budget **< 250 KB gzipped**; fail CI if exceeded.
- Signals + `OnPush` semantics (zoneless gives this) — no unnecessary change detection.
- Virtualize long lists (booking lists, calendars). Debounce search/filter inputs.
- Defer images; serve media via CDN-fronted StorageService; responsive sizes.
- Lighthouse performance budget in CI (target ≥ 90 on the portal's key routes).

---

## 3. Mobile / Public API Contract (first-class)

The mobile app is the guest product; the API **is** the contract. Treat breaking changes as production incidents.

### 3.1 Conventions
- **Versioned** `/api/v1/...`; additive changes only within a version. Breaking change → `/api/v2` with an overlap + deprecation window (min 6 months, `Sunset` header on the old version).
- **REST + JSON**, resource-oriented nouns, plural collections (`/api/v1/properties`, `/api/v1/bookings`). Standard verbs/status codes.
- **Auth:** `Authorization: Bearer <UserService JWT>`. Anonymous allowed for search/property browse; **hold/confirm/cancel/profile require a valid token**. The API authorizes; never trust the client.
- **Errors:** RFC 7807 `problem+json`, stable machine-readable `type` URIs the app can branch on (e.g. `…/errors/sold-out`, `…/errors/price-changed`, `…/errors/hold-expired`).
- **Pagination:** cursor-based for large/changing collections (search results, bookings); `?limit=&cursor=`. Never offset-paginate search.
- **Filtering/sorting:** explicit query params; document allowed fields. No arbitrary query passthrough.
- **Idempotency:** `Idempotency-Key` header **required** on `POST /holds`, `POST /bookings/{id}/confirm`, `POST /bookings/{id}/cancel`, and payment callbacks — mobile networks retry, and a retried hold/confirm must not double-act.
- **OpenAPI** spec published and versioned; the mobile and Angular clients generate from it.

### 3.2 Core guest endpoints (the funnel)
| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/v1/search` | anon | geo/date/guest params; cursor paginated; cache-friendly |
| GET | `/api/v1/properties/{id}` | anon | detail; `ETag` + `Cache-Control` |
| GET | `/api/v1/properties/{id}/availability` | anon | live price+avail for dates (advisory) |
| POST | `/api/v1/holds` | **token** | atomic hold; `Idempotency-Key`; returns hold + frozen quote + TTL |
| POST | `/api/v1/bookings/{id}/confirm` | **token** | pay + confirm saga; `Idempotency-Key` |
| GET | `/api/v1/bookings` | **token** | the user's bookings; cursor paginated |
| POST | `/api/v1/bookings/{id}/cancel` | **token** | policy-based refund; `Idempotency-Key` |
| GET | `/api/v1/me` | **token** | profile (first-login provisions it) |

### 3.3 Mobile-specific rules
- **Lean payloads:** return what the screen needs; support sparse fieldsets/expansion rather than fat default responses. Compress (`gzip`/`br`).
- **Caching:** `ETag` + `Cache-Control` on search/detail so the app and CDN can cache; never cache authenticated booking responses.
- **Push:** notifications go through NotificationService (push channel), triggered by platform events — the API does not long-poll.
- **Resilience:** the app retries idempotently; design every state-changing endpoint to tolerate retry. Surface `Retry-After` on 429/503.
- **Time/locale:** return `TIMESTAMPTZ` ISO-8601 with offset and the property timezone; let the client localize. Amounts always carry currency.
- **Rate limiting:** per-user and per-IP quotas at Ocelot; documented limits + `429` with `Retry-After`.

---

## 4. Performance Rules

### 4.1 Latency budgets (enforced by gates G1/G3 + monitoring)
- Search p95 **< 500 ms**; autocomplete p95 **< 100 ms** (at target catalog size).
- Inventory hold p99 **< 200 ms** under concurrency (G1).
- General API p95 **< 300 ms**; if exceeded sustained, it's a regression, not a tuning nicety.
- Search index lag (rate/avail change → visible) p99 **< 2 min**.

### 4.2 Backend performance
- **Cache-first reads:** Redis for price/availability (1–5 min TTL) and search-page resolution; CDN for media + cacheable GETs. Keep the ~80% cache hit rate honest — it's what keeps Pricing and Postgres quiet.
- **Hot paths use Dapper**, not EF; the hold is a single round trip.
- **No N+1.** Project to exactly the columns needed; batch; use `IN`/joins over per-row queries. Review every list endpoint for N+1.
- **Pagination is mandatory** on all collections — no unbounded result sets, ever.
- **Async all the way** (`async`/`await`, no sync-over-async); non-blocking I/O.
- **Connection pooling** tuned (Npgsql); read replicas for catalog/ARI/history reads; the write primary stays for writes only.
- ARI calendars partitioned monthly; the hot ~18-month window stays in RAM. Keep the partition-creation job running.
- **Load-test before merge** on hot paths (k6); the gate suites run nightly against staging forever.

### 4.3 Frontend performance
- Bundle budget (§2.3), lazy routes, virtualized lists, deferred/responsive images, Lighthouse budget in CI.

---

## 5. Security Rules

### 5.1 AuthN / AuthZ
- **All auth via UserService (OIDC/JWT).** Validate issuer, audience, signature (cached JWKS), expiry, clock-skew ≤ 60 s. Reject on any failure — never downgrade to a weaker check.
- **Authorize on the server for every protected action** — UI guards are convenience only. Role policies (`owner/admin/ops/finance`) + **tenancy (BR-9)**: an owner can only read/write their own properties' data, enforced by EF global query filters and tested to fail cross-tenant access.
- **The owner-approval gate is a server-side authorization rule**, not just a UI state: an unapproved owner's listing-creation calls are rejected by the API.
- Booking endpoints require a valid token (no guest path); `me`/profile/bookings are scoped to the token's `sub`.

### 5.2 Secrets, transport, PCI
- **No secrets in code or images** — external secrets operator; rotation supported. Connection strings, JWKS URLs, service creds all injected.
- **TLS everywhere**; HSTS at the edge. No plaintext service hops in prod.
- **PCI SAQ-A:** raw card data never touches our systems — tokenize via RazorPay **through PaymentGateway**. Store only PSP tokens (`guest.payment_method_token`).
- **CORS** locked to known origins (the Angular portal, the mobile app's web fallback if any); no `*` with credentials.

### 5.3 Input, output, abuse
- **Validate and whitelist all input** server-side (pipeline validators); reject unexpected fields. Parameterized SQL only (Dapper params/EF) — no string-concatenated SQL.
- **Output encoding** in Angular (built-in) + a strict **Content-Security-Policy**; sanitize any HTML. No `bypassSecurityTrust*` without review.
- **Rate limiting / throttling at Ocelot** per-user and per-IP; stricter limits on auth, hold, and search. `429` + `Retry-After`.
- **Idempotency keys** also defend against replay of state-changing calls.
- **MinIO/StorageService:** private buckets; access only via short-lived presigned URLs; documents (voucher/invoice) links expire.

### 5.4 Privacy & audit
- **PII** concentrated in `guest.*`, `booking.contact_*`, `booking_guest`, `saved_traveler.document`; consider app-level encryption for identity documents. Erasure (BR-8) is the two-party workflow with UserService; anonymize PII, retain financial records.
- **Audit** all privileged actions in `admin.audit_log` (actor `sub`, before/after, reason) — append-only. Owner-approval decisions are audited.
- **Dependency scanning + secret scanning** in CI; pen test on auth/booking/payment surfaces before launch (Gate G4).

---

## 6. Delivery Plan Adjustments (from the scope change)

These refine the phases in `docs/implementation-plan-v2.md`; gates G0–G6 are unchanged.

- **Phase 0** — add Ocelot route config for `/api/v1` (additive to existing food routes) and the OpenAPI publishing pipeline. JWT validation as before.
- **Phase 1 = the Angular portal's core** — owner registration + **admin approval workflow** + property/room/media management. This is now the primary web-frontend deliverable, not a guest UI. Approval gate enforced API-side.
- **Phases 3–6 ship the mobile API funnel** — search, hold, confirm, bookings, cancel — as `/api/v1` endpoints with the contract in §3. The mobile app team consumes the published OpenAPI; no guest web UI is built.
- **Drop the guest-checkout work entirely** — UC-2.9's anonymous path is removed; `guest_id NOT NULL`; FRD open question OQ-3 (notify a raw email) is **moot** (every recipient is a known user).
- **Soft launch (Gate G4)** = approved owners with live inventory + the mobile app against `/api/v1`. The Angular portal is internal/owner-facing; the consumer launch is the mobile app.

---

## 7. Definition of Done (per story)

API/UI slice + tests (unit + integration + the relevant gate scenario) green in CI · OpenAPI updated if the API changed · OpenTelemetry spans named · module boundaries intact (arch-test green) · authorization enforced server-side · no secrets committed · runbook note for anything operational.
