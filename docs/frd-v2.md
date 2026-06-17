# Booking Platform — Requirements & Use Cases (v2)

**Document type:** Functional Requirements Document — Revision 2
**Supersedes:** FRD v1
**Change driver:** Identity/User Service and Notification Service **already exist** in the organization. This platform **integrates** with them; it does not build them.
**Companions:** *System Design* doc (architecture, DB, flows) · *Implementation Plan v2*

---

## 1. Revision Summary (what changed from v1)

| Area | v1 | v2 |
|---|---|---|
| Identity & user accounts | Build in-platform (ASP.NET Identity + OpenIddict) | **Integrate** existing Identity/User Service (OIDC/JWT consumer; user profile via service API) |
| Notifications | Build Notification module (templating, dispatch, channels) | **Integrate** existing Notification Service (publish typed events/commands; templates owned per agreed contract) |
| M5 (Account) UCs | Platform-owned | Split: auth/profile = integration; booking-specific data (trips, travelers, wishlist) = platform-owned |
| M10 (Notification) UCs | Platform-owned | Reframed as **notification trigger contracts** — platform owns *when/what*, existing service owns *how/where* |
| New section | — | §6 External Service Integration Requirements (INT-x) |

Everything else (search, booking, payments, ARI, host, channel, pricing, reviews, admin, loyalty, partner API, reporting) is unchanged in intent and remains platform scope.

---

## 2. Actors (revised)

| ID | Actor | Description |
|---|---|---|
| A1 | **Guest (anonymous)** | Unauthenticated visitor; can search/browse and use guest checkout. |
| A2 | **Guest (registered)** | Authenticated via existing Identity Service; booking profile lives in this platform. |
| A3 | **Host / Property Manager** | Manages listings, ARI, payouts. Authenticated via Identity Service with platform-specific roles. |
| A4 | **Ops / Admin** | Internal staff: support, overrides, adjustments. |
| A5 | **Content Moderator** | Moderates listings, media, reviews. |
| A6 | **Finance Admin** | Settlements, refunds, disputes, reconciliation. |
| A7 | **Channel Manager / PMS** | External B2B inventory system. |
| A8 | **Payment Service Provider** | External gateway (Stripe/Adyen/Razorpay). |
| A9 | **Partner / Affiliate API consumer** | B2B metasearch/affiliate. |
| **A10** | **Notification Service (existing)** | **External internal-org service.** Receives typed notification commands/events from this platform; owns channel delivery (email/SMS/push), retries, opt-out enforcement at delivery level. |
| **A11** | **Identity / User Service (existing)** | **External internal-org service.** OIDC provider; issues JWTs; owns credentials, registration, password/MFA, core user profile. This platform validates tokens and maps users by `sub`. |

---

## 3. Scope Boundary (explicit)

**This platform OWNS:**
- All booking-domain data keyed by `user_id` (= Identity `sub` claim): bookings, traveler profiles, wishlist, preferences *that affect booking behavior* (currency, locale for documents), loyalty balance.
- Authorization *within* the platform: role/permission mapping from Identity claims to platform roles (`guest/host/ops/moderator/finance/partner`), tenancy enforcement (BR-9).
- Notification **triggers and payloads**: deciding when an event warrants a notification and supplying the structured data; the per-category opt-in/out *preference model for booking notifications* (synced or delegated per INT-N3 decision).

**This platform DOES NOT own:**
- Credentials, login UX, registration, password reset, MFA, social login → Identity Service.
- Email/SMS/push delivery, channel selection, delivery retries, bounce handling, unsubscribe-link mechanics → Notification Service.
- Core user identity attributes (name, email, phone verification status) → Identity Service (read via API/claims; cached, never mastered here).

**Boundary risks to resolve early** (tracked as open questions, §8):
- OQ-1: Does Identity Service support the `host` onboarding flow's extra KYC state, or does KYC state live entirely in this platform keyed by `sub`? *(Recommended: platform-owned KYC state; Identity stays generic.)*
- OQ-2: Does Notification Service support templated transactional messages with our payload schema, localization, and attachments (voucher/invoice PDFs)? If attachments unsupported → platform sends links, not files.
- OQ-3: Guest checkout (no account): can Notification Service deliver to a raw email without a user record? If not, platform needs a lightweight contact-delivery path agreed with that team.

---

## 4. Use Case Inventory (v2 — complete)

Legend: **[P]** platform-built · **[I]** integration with existing service · **[P+I]** platform logic + existing-service delivery. Priority M/S/C as v1 unless noted.

### M1 — Search & Discovery — all [P]
| UC | Name | Pri |
|---|---|---|
| UC-1.1 | Search stays by destination, dates, guests | M |
| UC-1.2 | Filter & sort results | M |
| UC-1.3 | Map / geo-radius search | S |
| UC-1.4 | Destination autocomplete | M |
| UC-1.5 | View property detail page | M |
| UC-1.6 | Live price & availability check | M |
| UC-1.7 | Wishlist | S |
| UC-1.8 | Compare properties | C |
| UC-1.9 | Recently viewed & recommendations | C |

### M2 — Booking / Reservation — all [P]
| UC | Name | Pri |
|---|---|---|
| UC-2.1 | Select room/unit & rate plan | M |
| UC-2.2 | Re-price & validate selection | M |
| UC-2.3 | Create hold (atomic, all-nights-or-none) | M |
| UC-2.4 | Enter guest & contact details | M |
| UC-2.5 | Apply promo / coupon | S |
| UC-2.6 | Review & confirm booking | M |
| UC-2.7 | Handle hold expiry (reaper) | M |
| UC-2.8 | Multi-room booking (cart atomicity) | S |
| UC-2.9 | Guest checkout vs registered booking | M |
| UC-2.10 | Special requests | C |

### M3 — Payments — all [P] (PSP = A8 external)
| UC | Name | Pri |
|---|---|---|
| UC-3.1 | Authorize payment | M |
| UC-3.2 | Capture on confirmation | M |
| UC-3.3 | Pay-at-property / pay later | S |
| UC-3.4 | Deposit / partial payment | C |
| UC-3.5 | Payment failure & retry | M |
| UC-3.6 | 3DS / SCA challenge | M |
| UC-3.7 | Multi-currency payment | S |
| UC-3.8 | Process refund | M |
| UC-3.9 | Saved payment methods (PSP tokens) | S |
| UC-3.10 | Host payout / settlement | S |

### M4 — Cancellation & Modification — all [P]
| UC | Name | Pri |
|---|---|---|
| UC-4.1 | Cancel booking | M |
| UC-4.2 | Compute refund per cancellation policy | M |
| UC-4.3 | Modify dates / guests / room | S |
| UC-4.4 | Partial cancellation | S |
| UC-4.5 | No-show handling | S |
| UC-4.6 | Host-initiated cancellation | S |

### M5 — Account & Profile (revised split)
| UC | Name | Type | Pri |
|---|---|---|---|
| UC-5.1 | Authenticate via Identity Service (OIDC login, token refresh, logout) | **[I]** | M |
| UC-5.1a | First-login provisioning (map `sub` → platform booking profile) | **[P+I]** | M |
| UC-5.2 | Manage booking preferences (currency, locale, default traveler) | **[P]** | M |
| UC-5.2a | View/edit core identity attributes | **[I]** deep-link or embedded Identity UI | S |
| UC-5.3 | View upcoming & past bookings | **[P]** | M |
| UC-5.4 | Manage saved travelers | **[P]** | C |
| UC-5.5 | Download invoice / voucher | **[P]** | M |
| UC-5.6 | Data export & deletion — **platform-side**: booking PII export + anonymization, **coordinated** with Identity Service erasure (joint workflow) | **[P+I]** | M |

### M6 — Host / Property Management — all [P] (auth via [I])
| UC | Name | Pri |
|---|---|---|
| UC-6.1 | Host onboarding & KYC (KYC state platform-owned per OQ-1; identity via A11) | M |
| UC-6.2 | Create / edit property listing | M |
| UC-6.3 | Manage room types / units | M |
| UC-6.4 | Upload & manage media | M |
| UC-6.5 | Configure policies & house rules | M |
| UC-6.6 | Manage rate plans | M |
| UC-6.7 | Manage rate calendar | M |
| UC-6.8 | Manage availability / allotment | M |
| UC-6.9 | Restrictions (LOS, CTA/CTD, stop-sell) | M |
| UC-6.10 | View bookings & occupancy calendar | M |
| UC-6.11 | Respond to reviews | S |
| UC-6.12 | Earnings & payout reports | S |
| UC-6.13 | Create & manage promotions | S |

### M7 — Channel Manager / PMS — all [P] (CM = A7 external)
| UC | Name | Pri |
|---|---|---|
| UC-7.1 | Connect & authenticate channel manager | S |
| UC-7.2 | Ingest ARI push (ordered, idempotent) | S |
| UC-7.3 | Reverse-sync bookings to PMS | S |
| UC-7.4 | Full snapshot reconciliation | S |
| UC-7.5 | Detect & resolve overbooking conflict | M |
| UC-7.6 | Map external room codes | S |

### M8 — Pricing & Promotions — all [P]
| UC | Name | Pri |
|---|---|---|
| UC-8.1 | Compute quote (deterministic pipeline) | M |
| UC-8.2 | Create promotion / campaign | S |
| UC-8.3 | Validate & redeem coupon | S |
| UC-8.4 | Member / loyalty pricing | C |
| UC-8.5 | Taxes & fees | M |
| UC-8.6 | Currency conversion (FX snapshot) | S |

### M9 — Reviews & Ratings — all [P]
| UC | Name | Pri |
|---|---|---|
| UC-9.1 | Submit verified post-stay review | S |
| UC-9.2 | Moderate review | S |
| UC-9.3 | Aggregated ratings | S |
| UC-9.4 | Host respond to review | C |
| UC-9.5 | Report / flag review | C |

### M10 — Notification Triggers (reframed) — all [P+I]
Platform emits **typed notification commands** to the existing Notification Service; that service owns delivery. Each UC below = a trigger contract this platform must implement.

| UC | Trigger | Payload essentials | Pri |
|---|---|---|---|
| UC-10.1 | Booking confirmed → guest | reference, property, dates, amount, voucher link/PDF (per OQ-2) | M |
| UC-10.2 | Payment captured / invoice → guest | receipt data, invoice link | M |
| UC-10.3 | Pre-arrival reminder (T-48h/T-24h, property timezone) | check-in info, directions, host contact | S |
| UC-10.4 | Cancellation / modification notice → guest (+ refund detail) | old/new state, refund amount & timeline | M |
| UC-10.5 | New booking / cancellation alert → host | booking summary, guest count, payout impact | M |
| UC-10.6 | Booking-notification preference management (category opt-in/out; mechanism per INT-N3) | — | S |
| UC-10.7 *(new)* | Hold-expiring nudge (optional, T-5 min) → guest in checkout | resume link | C |
| UC-10.8 *(new)* | Balance-due reminder (deposit bookings) | amount, due date, pay link | S→ with UC-3.4 |

### M11 — Loyalty — all [P]
| UC | Name | Pri |
|---|---|---|
| UC-11.1 | Earn points on completed stay | C |
| UC-11.2 | Redeem points at checkout | C |
| UC-11.3 | Tiers & benefits | C |

### M12 — Admin / Ops — all [P] (role grants read Identity claims)
| UC | Name | Pri |
|---|---|---|
| UC-12.1 | Manage platform roles & permission mapping (Identity `sub` → platform roles) | M |
| UC-12.2 | Moderate listings & media | S |
| UC-12.3 | Dispute / chargeback workflow | S |
| UC-12.4 | Manual booking override (audited, BR-7) | S |
| UC-12.5 | Fraud detection & blocking | S |
| UC-12.6 | Master data (amenities, taxes, geo) | M |
| UC-12.7 | Audit log | S |

### M13 — Partner API — all [P] (client-credentials via Identity Service, [I])
| UC | Name | Pri |
|---|---|---|
| UC-13.1 | Partner auth & rate limits (tokens issued by A11) | C |
| UC-13.2 | Search via API | C |
| UC-13.3 | Book via API (idempotent) | C |
| UC-13.4 | Commission / markup management | C |

### M14 — Reporting — all [P]
| UC | Name | Pri |
|---|---|---|
| UC-14.1 | Operational dashboards | S |
| UC-14.2 | Conversion funnel reporting | S |
| UC-14.3 | Financial reconciliation | S |

**Count:** 87 use cases (85 from v1, restructured in M5/M10, +UC-5.1a, 5.2a, 10.7, 10.8; v1's build-identity and build-notification internals removed from scope).

---

## 5. Business Rules (carried + revised)

- **BR-1 No overbooking** — unchanged (sold ≤ allotment, truth at hold time only).
- **BR-2 Frozen quote** — unchanged.
- **BR-3 Hold TTL** — unchanged (default 12 min, configurable).
- **BR-4 Property-timezone math** — unchanged (incl. UC-10.3 reminder scheduling).
- **BR-5 Idempotency at every external boundary** — now explicitly includes Notification Service commands (notification key = `event_id`; duplicate commands must not double-send) and Identity Service provisioning (UC-5.1a re-entrant).
- **BR-6 Verified reviews** — unchanged.
- **BR-7 Refund authority & audited overrides** — unchanged.
- **BR-8 Data privacy** — revised: erasure is a **two-party workflow** — platform anonymizes booking PII + retains financial records; Identity Service erases the identity record; orchestration owned by platform with completion confirmation from Identity (UC-5.6).
- **BR-9 Tenancy isolation** — unchanged; host identity = Identity `sub`, ownership table in platform.
- **BR-10 *(new)* Identity is the only credential authority** — the platform never stores passwords, never issues primary tokens, and treats Identity claims as the source for name/email; local copies are caches with TTL, refreshed on login.
- **BR-11 *(new)* Notification is fire-and-forget with audit** — the booking saga never blocks on notification delivery; every command emission is recorded (outbox) so delivery issues are the Notification Service's domain, traceable by `event_id`.

---

## 6. External Service Integration Requirements

### 6.1 Identity / User Service (INT-I)

| ID | Requirement |
|---|---|
| INT-I1 | Platform validates Identity-issued JWTs (issuer, audience, signature via JWKS, expiry, clock skew ≤60 s). Token validation failures never fall back to weaker auth. |
| INT-I2 | Angular app uses authorization-code + PKCE against Identity; Gateway/BFF validates and forwards; no implicit flow. |
| INT-I3 | **First-login provisioning (UC-5.1a):** on first valid token, platform creates a booking profile keyed by `sub`; operation idempotent; race-safe (unique constraint on `sub`). |
| INT-I4 | Platform role model maps from Identity claims/groups via a platform-owned mapping table (UC-12.1); platform roles can be granted without Identity-side changes. |
| INT-I5 | Required claims contract (to be confirmed with Identity team): `sub`, `email`, `email_verified`, `name`, `locale?`, `phone?`. Missing optional claims degrade gracefully. |
| INT-I6 | Partner API (M13) uses client-credentials grant issued by Identity; scopes `partner:search`, `partner:book`. |
| INT-I7 | Erasure coordination (BR-8): platform exposes an internal endpoint/event for Identity-initiated deletion AND can initiate deletion requests toward Identity; both paths idempotent with completion acknowledgment. |
| INT-I8 | Availability decoupling: Identity outage must not break **already-authenticated** sessions (token validation is local via cached JWKS) or anonymous search/browse; only new logins degrade. |
| INT-I9 | Guest checkout (UC-2.9) requires no Identity record; post-booking "claim booking" links a guest booking to a `sub` after login, verified by booking reference + email challenge. |

### 6.2 Notification Service (INT-N)

| ID | Requirement |
|---|---|
| INT-N1 | Platform → Notification contract: typed commands (preferred: async via bus topic; fallback: REST) with schema-registry-versioned payloads. Each command carries `event_id` (idempotency), `category`, `recipient` (user `sub` *or* raw contact for guest checkout per OQ-3), `locale`, structured `data`. |
| INT-N2 | Template ownership decision per category: platform supplies data, Notification renders templates (preferred) — template change ≠ platform deploy. Template catalog co-designed at integration kickoff. |
| INT-N3 | Preferences: confirm whether Notification Service owns per-category opt-out. If yes → platform deep-links to it (UC-10.6 = thin). If no → platform stores booking-category preferences and includes `suppress` evaluation before emitting. **Transactional/legal messages (confirmation, receipt, cancellation) are never suppressed.** |
| INT-N4 | Delivery feedback: platform consumes delivery-status events (delivered/bounced/failed) if available, for ops dashboards (UC-14.1) — nice-to-have, not blocking. |
| INT-N5 | Attachments (OQ-2): if unsupported, voucher/invoice are signed, expiring links into platform document storage. |
| INT-N6 | Scheduled sends (UC-10.3, 10.8): if Notification Service lacks scheduling, platform owns the scheduler (property-timezone aware, BR-4) and emits at send time. |
| INT-N7 | SLO assumption to confirm: transactional command-to-delivery p95 ≤ 2 min. If unachievable, set guest expectations in UI copy. |

### 6.3 Integration use cases — detailed (critical path)

**UC-5.1a — First-login provisioning [P+I] `[M]`**
*Actor:* A2/A3 via A11. *Trigger:* first request with a valid token whose `sub` has no platform profile.
*Main flow:* validate token (INT-I1) → attempt provision (INT-I3, idempotent insert) → hydrate cached identity attributes from claims → apply default role `guest` → continue original request.
*Exceptions:* **E1** concurrent first requests → unique-constraint winner proceeds, loser re-reads (no error to user). **E2** missing `email_verified` → allow browse, block booking confirmation until verified (policy flag).
*Postcondition:* platform profile exists; all later requests are pure local lookups.

**UC-5.6 — Data export & deletion [P+I] `[M]`**
*Main flow:* user requests via platform → platform compiles booking-domain export (bookings, travelers, reviews, documents) → platform anonymizes PII while retaining financial records (BR-8) → platform calls/notifies Identity for identity-record erasure (INT-I7) → completion only when both sides acknowledge; status visible to user.
*Exceptions:* **E1** active future booking → block deletion with explanation (cancel first) or proceed with deferred anonymization at stay completion — policy decision OQ-4. **E2** Identity erasure fails → platform side holds in `PENDING_IDENTITY`, retries, ops alert after N attempts.

**UC-10.1 — Booking confirmation trigger [P+I] `[M]`**
*Trigger:* `BookingConfirmed` event from saga (outbox-published, BR-11).
*Main flow:* trigger handler builds payload (reference, property, stay, amounts, voucher per INT-N5), resolves recipient (sub or raw email for guest checkout), `event_id = BookingConfirmed:{booking_id}` → emit command → record emission.
*Exceptions:* **E1** Notification Service unavailable → bus buffering/retry absorbs it; saga unaffected (BR-11). **E2** duplicate event redelivery → same `event_id`, Notification de-dupes (INT-N1).

(UC-10.2/10.4/10.5 follow the same pattern with their own payloads; UC-10.3/10.8 add the scheduler per INT-N6.)

---

## 7. Traceability to Design & Plan

| Module | Design-doc context | Plan phase (see Implementation Plan v2) |
|---|---|---|
| M1, M8 | Search, Pricing | P4, P2 |
| M2, M4 | Booking saga | P3, P5 |
| M3 | Payment | P3b/P4 per plan v2 |
| M5 | Booking-profile + INT-I | P0 (auth), P6 (profile surface) |
| M6, M7 | Catalog, ARI | P1–P2, P7 |
| M9 | Reviews | P6 |
| M10 | Trigger contracts + INT-N | P0 contract, P3+ per trigger |
| M11–M14 | Loyalty/Admin/Partner/Reporting | P8–P9 |

---

## 8. Open Questions (to close at integration kickoff — blocking items flagged)

| OQ | Question | Owner to engage | Blocking? |
|---|---|---|---|
| OQ-1 | KYC state location (recommended: platform) | Identity team | No (proceed with recommendation) |
| OQ-2 | Notification attachments support (voucher/invoice PDF) | Notification team | No (link fallback INT-N5) |
| OQ-3 | Delivery to raw email without user record (guest checkout) | Notification team | **Yes — blocks UC-2.9 confirmation email** |
| OQ-4 | Deletion policy with active future bookings | Product/Legal | No (default: block with explanation) |
| OQ-5 | Claims contract confirmation (INT-I5) | Identity team | **Yes — blocks P0 auth integration** |
| OQ-6 | Notification command transport: bus topic vs REST | Notification team | **Yes — blocks contract freeze in P0** |
| OQ-7 | Per-category preference ownership (INT-N3) | Notification team | No (default: platform-side suppress) |
