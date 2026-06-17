# Booking Platform — Implementation Plan (v2)

**Document type:** Implementation Plan & Delivery Roadmap — Revision 2
**Supersedes:** Roadmap v1
**Change driver:** Identity/User Service and Notification Service already exist → Phase 0 integrates instead of builds; the Notification module shrinks to a **trigger adapter**; timeline compresses **54 → 46 weeks** with soft launch moved up **38 → 34 weeks**.
**Companions:** *System Design* doc · *FRD v2* (use cases UC-x, integration requirements INT-x, open questions OQ-x)

---

## 1. What Changed vs Plan v1

| Item | v1 | v2 |
|---|---|---|
| Phase 0 identity work | Build ASP.NET Identity + OpenIddict (~2 wks effort) | Integrate Identity Service: JWT validation, PKCE flow, first-login provisioning (UC-5.1a), role mapping (~1 wk) |
| Notification module | Full build: templating, channels, localization, delivery | **Trigger adapter**: outbox → typed commands per INT-N contract; scheduler only if INT-N6 requires |
| New Phase-0 dependency | — | **Integration kickoff** with Identity + Notification teams; OQ-3/5/6 must close before contract freeze |
| Timeline | 54 wks, launch wk 38 | **46 wks, soft launch wk 34** |
| Phase numbering | 0–10 | 0–9 (notification phase absorbed; payments split noted inline) |
| New risk class | — | Cross-team dependency risk (R9, R10) |

Everything risk-ordered as before: booking-core gate (G1) then money gate (G2) before any conversion-surface investment.

---

## 2. Phase Overview

| Phase | Name | Weeks | Gate | Headline outcome |
|---|---|---|---|---|
| 0 | Foundation & Integrations | 1–4 | **G0** | Walking skeleton; Identity login works; Notification contract frozen + round-trip proven |
| 1 | Catalog & Host Core | 5–9 | — | Hosts list properties end-to-end (moderated to LIVE) |
| 2 | ARI & Pricing Engine | 10–14 | — | Partitioned calendars + deterministic quotes |
| 3 | Booking Core (Saga) | 15–20 | **G1** 🔴 | Zero-overbooking proven under load; confirmation/host-alert triggers live |
| 4 | Payments ∥ Search | 21–26 | **G2** 🔴 + G3 | Real money idempotent + sub-second search (two parallel streams) |
| 5 | Lifecycle: Cancel / Modify / Reminders | 27–30 | — | Full booking lifecycle incl. refunds |
| 6 | Guest Account & Reviews | 31–34 | **G4** 🔴 | **Soft launch** (direct inventory) |
| 7 | Channel Manager / PMS | 35–40 | **G5** 🔴 | Third-party inventory via shadow-mode rollout |
| 8 | Admin, Ops, Fraud, Payouts & Reporting | 41–44 | — | Operable, financially closed-loop |
| 9 | Loyalty, Partner API & Scale-out | 45–46+ | G6 | Growth features; Search/Pricing extraction readiness |

Phase 4 runs **two parallel streams** (Payments backend-heavy, Search index/frontend-heavy) — this is where v2 recovers most of the calendar, enabled by the team capacity freed from not building identity/notifications.

---

## 3. Phase 0 — Foundation & Integrations (Weeks 1–4)

**Objective:** walking skeleton in a prod-like environment **plus both external integrations proven**, because they are now on the critical path of every later phase.

### Workstream A — Platform engineering (as v1)
- Solution scaffold: Gateway (YARP) + module projects (`Catalog, ARI, Pricing, Booking, Payment, Search, Reviews, NotificationAdapter, Admin`) with Domain/Application/Infrastructure/Api layering; `BuildingBlocks` (Result, CQRS pipeline, outbox, messaging).
- Architecture tests (ArchUnitNET) enforcing module boundaries in CI from week 1.
- CI/CD: build → test → arch-test → container → Helm deploy dev/staging; trunk-based.
- Infra: Postgres+PostGIS, Redis, Kafka, OpenSearch; per-module schemas with independent EF Core migration histories.
- OpenTelemetry end-to-end; correlation id from Angular through Kafka consumers.
- Transactional outbox + dispatcher proven with one round-trip event.
- Angular 20 shell: standalone/zoneless, signals, auth interceptor, guards, design-system seed.

### Workstream B — Identity integration (UC-5.1, 5.1a; INT-I1…I5, I8)
- **Week 1: integration kickoff with Identity team — close OQ-5 (claims contract).**
- JWT validation middleware: issuer/audience/JWKS with local caching (INT-I8 — Identity outage must not kill authenticated sessions), clock-skew policy.
- Angular PKCE flow against the real Identity Service (dev tenant).
- First-login provisioning: idempotent, race-safe profile creation keyed by `sub` (unique constraint; concurrent-first-request test).
- Platform role mapping table + claims→role resolver (seed `guest`, `host`, `ops`, `moderator`, `finance`).
- Negative tests: expired/garbled/wrong-audience tokens, revoked-key rotation drill.

### Workstream C — Notification contract (INT-N1…N3, N6; UC-10.x foundation)
- **Week 1: integration kickoff with Notification team — close OQ-3 (raw-email delivery) and OQ-6 (transport).**
- Freeze the command schema (schema-registry versioned): `event_id`, `category`, `recipient`, `locale`, `data`.
- Build the **NotificationAdapter** module: consumes platform domain events from the outbox → maps to commands → emits via agreed transport; emission ledger for audit (BR-11).
- Prove one round trip end-to-end: synthetic `TestEvent` → adapter → Notification Service → an actual email lands in a dev inbox.
- Decide template ownership per INT-N2; book template-design sessions for the Phase-3 categories now (lead time!).

### Exit criteria — **Gate G0**
- [ ] Traced request: browser → Gateway → module → Postgres → outbox → Kafka → consumer, visible in tracing UI.
- [ ] Real Identity login → JWT → protected endpoint → first-login profile created exactly once under 10 concurrent first requests.
- [ ] Notification round trip: domain event → adapter → delivered test message, idempotent under event redelivery (same `event_id` ×5 → one delivery).
- [ ] OQ-3, OQ-5, OQ-6 formally closed (or explicit fallback accepted and documented).
- [ ] CI deploys on merge; rollback demonstrated; arch-test proven to fail on seeded violation.

---

## 4. Phase 1 — Catalog & Host Core (Weeks 5–9)

Unchanged in scope from v1; auth now real from day one.

**Use cases:** UC-6.1 (KYC state platform-owned per OQ-1 recommendation; host role granted via UC-12.1 mapping, not Identity-side change), UC-6.2, 6.3, 6.4, 6.5 · UC-12.2, 12.6 · UC-1.5 (detail page v1 from Catalog).

**Key notes**
- Catalog schema per design doc §5.2; `row_version` optimistic concurrency.
- Domain events (`PropertyCreated/Updated/WentLive`, `RoomTypeChanged`) flowing to Kafka from day one — the future indexer's diet.
- BR-9 tenancy: EF global query filter on host ownership + tests proving cross-tenant access fails.
- Angular: host listing wizard, ops moderation queue.
- Seed importer: 50+ realistic properties for all downstream phases.

**Exit:** scripted demo new-host→KYC→listing→moderation→LIVE→public page; tenancy tests green; seed data loaded.

---

## 5. Phase 2 — ARI & Pricing Engine (Weeks 10–14)

Unchanged from v1.

**Use cases:** UC-6.6, 6.7, 6.8, 6.9 · UC-8.1, 8.5, 8.6 · UC-1.6 (advisory live price/avail on detail page).

**Key notes**
- `inventory_calendar` / `rate_calendar` with monthly range partitions **and the partition-creation job from day one**.
- Pricing stateless + deterministic; golden-file suite (50 fixtures: occupancy, LOS, seasonal, tax, FX) — the permanent pricing regression net.
- `pricing_rule` engine general; LOS + seasonal rule types implemented now.
- Dapper on hot calendar reads; bulk calendar writes batched + idempotent.

**Exit:** 365-day bulk rate/allotment set <5 s; golden tests 100% deterministic; calendar reads p99 <50 ms at 10k properties.

---

## 6. Phase 3 — Booking Core: the Saga (Weeks 15–20)

The heart, unchanged in substance; notifications now flow through the adapter.

**Use cases:** UC-2.1, 2.2, 2.3, 2.4, 2.6 (mock-pay), 2.7, 2.8, 2.9, 2.10 · UC-6.10 (host bookings/occupancy) · **Triggers UC-10.1, UC-10.5** via NotificationAdapter (guest-checkout recipients exercise the OQ-3 path).

**Key notes**
- Saga states `DRAFT → HELD → CONFIRMED | EXPIRED | FAILED`; every transition idempotent + audited.
- The atomic all-nights-or-none hold SQL (design doc §6.2) — hand-written Dapper, the project's most-reviewed code.
- Redis per-cart lock only against double-submit; never inventory truth.
- Reaper with partial index on live holds; confirm-vs-expiry race resolved transactionally in the DB.
- UC-2.9 guest checkout: booking keyed by contact email; "claim booking" deferred to Phase 6 (INT-I9).

### Exit criteria — **Gate G1: the no-overbooking proof** 🔴 *(non-negotiable; nothing downstream starts until green)*
- [ ] 5,000 concurrent holds on one room-night with allotment 3 → exactly 3 confirms, 0 oversell, clean SOLD_OUT for the rest.
- [ ] 1-hour mixed load (10k concurrent multi-night holds / 100 properties): per-second invariant check `units_sold + units_held ≤ total_allotment`, zero violations, zero deadlocks, hold p99 <200 ms.
- [ ] Reaper kill+restart mid-run → all expired holds released, no orphans.
- [ ] Pod kill mid-saga → resume or compensate; no HELD bookings stuck past TTL+grace.
- [ ] Confirmation + host-alert messages delivered for every confirmed booking in the run (adapter ledger reconciles 1:1 with confirms).

---

## 7. Phase 4 — Payments ∥ Search (Weeks 21–26, two parallel streams)

### Stream A — Payments (backend-led)
**Use cases:** UC-3.1, 3.2, 3.5, 3.6, 3.7, 3.9 · UC-5.5 (invoice/voucher PDF — QuestPDF; delivery per INT-N5 link-vs-attachment decision) · **Trigger UC-10.2** (receipt).

**Key notes (as v1):** `IPaymentGateway` port + one PSP adapter for the launch market; idempotency keys `booking_id+attempt`; webhooks idempotent by PSP event id + poll-reconciler for missed webhooks; capture-fail-after-auth → finance retry queue, never blocks guest; SAQ-A PCI posture (PSP-hosted fields, zero PAN in our systems).

**Gate G2: money correctness** 🔴
- [ ] Idempotency torture: every PSP call replayed 5× under injected timeouts → zero duplicate charges/refunds in sandbox ledger.
- [ ] 3DS happy + abandon paths; abandoned challenge releases hold at TTL.
- [ ] EOD reconciliation across a 1k-booking simulated day: zero unexplained deltas.
- [ ] 30-min webhook blackhole → poll-reconciler converges all states.

### Stream B — Search & Discovery (index/frontend-led)
**Use cases:** UC-1.1, 1.2, 1.3, 1.4, 1.7, 1.9 (heuristic v1) · UC-14.2 (funnel events).

**Key notes (as v1):** indexer consumes catalog/ARI events → denormalized docs; blue/green full-reindex via alias swap; search-time exact price via Redis(1–5 min TTL)/Pricing for the visible page only; ranking v1 deterministic with an ML seam; Angular results page (virtualized list + map split, URL-addressable state).

**Gate G3: funnel performance**
- [ ] Search p95 <500 ms, autocomplete p95 <100 ms at 100k synthetic properties.
- [ ] Rate/availability change visible in search ≤2 min p99.
- [ ] Zero-downtime full reindex of 100k properties <30 min.
- [ ] Funnel events with session stitching verified.

---

## 8. Phase 5 — Lifecycle: Cancellation, Modification & Reminders (Weeks 27–30)

**Use cases:** UC-4.1, 4.2, 4.3, 4.4, 4.5, 4.6 · UC-3.3, 3.4, 3.8 · **Triggers UC-10.3, 10.4, 10.8** (scheduler built iff INT-N6 says platform-owned; property-timezone aware per BR-4) · UC-10.6 preference handling per OQ-7 resolution.

**Key notes (as v1):** policy evaluator = pure function with DST/timezone golden matrix; modification = compensate+rebook in one saga (BR-2 intact); inventory restored *before* refund; refund retry queue + finance alerting.

**Exit:** timezone/DST matrix 100% green; cancel→refund→restore idempotent under replay; modification produces correct delta charge in sandbox.

---

## 9. Phase 6 — Guest Account & Reviews → **Soft Launch** (Weeks 31–34)

**Use cases:** UC-5.2, 5.2a (deep-link to Identity profile UI), 5.3, 5.4, 5.6 (joint erasure workflow per INT-I7 — schedule Identity-team work **now**, it's their backlog too) · UC-2.9 completion: claim-booking flow (INT-I9) · UC-9.1–9.5 · UC-6.11.

### Exit criteria — **Gate G4: soft-launch readiness** 🔴
- [ ] Full regression green: every M-priority UC automated.
- [ ] Security pass: dependency audit, OWASP top-10 pen test on auth/booking/payment surfaces, secrets scan; **token-handling review with Identity team**.
- [ ] Load: 500 concurrent users full-funnel, error rate <0.1%, G1 invariant monitor clean.
- [ ] PDPA/GDPR export + two-party erasure demonstrated end-to-end with Identity acknowledgment.
- [ ] Runbooks + on-call + alerting (hold anomalies, payment failures, index lag, **notification emission-vs-delivery drift**); backup/restore drill done.
- → **Soft launch wk 34: limited market, direct-inventory hosts.**

---

## 10. Phase 7 — Channel Manager / PMS (Weeks 35–40)

Unchanged from v1 (shifted earlier by 4 weeks).

**Use cases:** UC-7.1, 7.6 → UC-7.2 (per-property ordered partitions, sequence dedupe) → UC-7.3, 7.4 → UC-7.5.

**Rollout:** wks 35–36 shadow mode (shadow calendar diffed vs direct, no live effect) → wks 37–38 live ingest for pilots + reverse sync → wks 39–40 reconciliation + conflict workflow, scale out.

**Gate G5: sync integrity** 🔴
- [ ] 2-week shadow: ARI drift <0.5% of property-nights.
- [ ] Out-of-order/replay injection → final state correct regardless of delivery order.
- [ ] Reverse-sync lag p99 <60 s, alerted (this is the external-oversell risk window).
- [ ] Forced-conflict drill: detection <5 min → auto-rebook or ops ticket with full context.

---

## 11. Phase 8 — Admin, Ops, Fraud, Payouts & Reporting (Weeks 41–44)

**Use cases:** UC-12.1 (full role-mapping admin), 12.3, 12.4, 12.5, 12.7 · UC-3.10 + UC-6.12 (payouts + statements) · UC-6.13, UC-8.2, 8.3 → unlocks **UC-2.5** (promo at checkout) · UC-14.1, 14.3.

**Exit:** month-end close simulation balances to zero unexplained; 100-host payout dry run matches hand-computed; fraud rules block scripted attacks with <1% false positives on replayed legit traffic.

---

## 12. Phase 9 — Loyalty, Partner API & Scale-out (Weeks 45–46+)

**Use cases:** UC-11.1–11.3 + UC-8.4 (loyalty plugs into UC-8.1 promo stage) · UC-13.1–13.4 (partner tokens via Identity client-credentials, INT-I6) · UC-1.8 · UC-1.9 ranking-upgrade seam.

**Scale-out workstream:** extract Search and Pricing into independent deployables against their frozen contracts; Catalog read replicas; CDN for property pages; ARI shard-readiness assessment (execute only if Postgres headroom <40%).

**Gate G6:** pilot partner completes search→book→cancel in sandbox; extracted Search/Pricing pass their original phase gate suites unchanged; capacity model documented.

---

## 13. Complete Use-Case Coverage Matrix (FRD v2 → phase)

| Module | UC → Phase |
|---|---|
| **M1** | 1.1→P4 · 1.2→P4 · 1.3→P4 · 1.4→P4 · 1.5→P1 · 1.6→P2 · 1.7→P4 · 1.8→P9 · 1.9→P4/P9 |
| **M2** | 2.1→P3 · 2.2→P3 · 2.3→P3 · 2.4→P3 · 2.5→P8 · 2.6→P3 · 2.7→P3 · 2.8→P3 · 2.9→P3(+P6 claim) · 2.10→P3 |
| **M3** | 3.1→P4 · 3.2→P4 · 3.3→P5 · 3.4→P5 · 3.5→P4 · 3.6→P4 · 3.7→P4 · 3.8→P5 · 3.9→P4 · 3.10→P8 |
| **M4** | 4.1→P5 · 4.2→P5 · 4.3→P5 · 4.4→P5 · 4.5→P5 · 4.6→P5 |
| **M5** | 5.1→P0 · 5.1a→P0 · 5.2→P6 · 5.2a→P6 · 5.3→P6 · 5.4→P6 · 5.5→P4 · 5.6→P6 |
| **M6** | 6.1→P1 · 6.2→P1 · 6.3→P1 · 6.4→P1 · 6.5→P1 · 6.6→P2 · 6.7→P2 · 6.8→P2 · 6.9→P2 · 6.10→P3 · 6.11→P6 · 6.12→P8 · 6.13→P8 |
| **M7** | 7.1→P7 · 7.2→P7 · 7.3→P7 · 7.4→P7 · 7.5→P7 · 7.6→P7 |
| **M8** | 8.1→P2 · 8.2→P8 · 8.3→P8 · 8.4→P9 · 8.5→P2 · 8.6→P2 |
| **M9** | 9.1→P6 · 9.2→P6 · 9.3→P6 · 9.4→P6 · 9.5→P6 |
| **M10** | 10.1→P3 · 10.2→P4 · 10.3→P5 · 10.4→P5 · 10.5→P3 · 10.6→P5 · 10.7→P9(optional) · 10.8→P5 · *contract foundation→P0* |
| **M11** | 11.1→P9 · 11.2→P9 · 11.3→P9 |
| **M12** | 12.1→P0(seed)/P8(full) · 12.2→P1 · 12.3→P8 · 12.4→P8 · 12.5→P8 · 12.6→P1 · 12.7→P8 |
| **M13** | 13.1→P9 · 13.2→P9 · 13.3→P9 · 13.4→P9 |
| **M14** | 14.1→P8 · 14.2→P4 · 14.3→P8 |

**Coverage check:** all 87 FRD-v2 use cases assigned. All M-priority UCs land by soft launch (wk 34) except UC-2.5 promos (P8 — launching without coupons is acceptable) and UC-7.5 conflict resolution (P7 — only meaningful once channels connect). Same two exceptions as v1; flag if either bothers you.

---

## 14. Cross-Team Dependency Plan (new in v2)

The two existing services put other teams on your critical path. Manage them explicitly:

| Week | Dependency event | Counterpart |
|---|---|---|
| 1 | Integration kickoff ×2; close OQ-3, OQ-5, OQ-6 | Identity + Notification teams |
| 2 | Contract freeze: claims set (INT-I5) + command schema (INT-N1); both under schema-registry versioning | Both |
| 3–4 | Dev-tenant access, JWKS endpoint, template-design sessions for P3 categories | Both |
| 14 | Template sign-off for booking-confirmation + host-alert (needed live in P3) | Notification |
| 21 | Receipt/invoice template + attachment-vs-link decision executed (INT-N5) | Notification |
| 27 | Scheduler ownership executed (INT-N6); reminder + balance-due templates | Notification |
| 28–32 | Joint erasure workflow build (INT-I7) — **their sprint capacity, book it now** | Identity |
| 44 | Client-credentials scopes for partner API (INT-I6) | Identity |

**Escalation rule:** any blocking OQ open >2 weeks past its kickoff target escalates to engineering leadership with the documented fallback activated (e.g., OQ-3 fallback: require account creation before checkout at soft launch — a conversion cost, so escalate early).

---

## 15. Team Shape

| Role | Count | Notes |
|---|---|---|
| Backend (.NET) | 3–4 | One permanent owner for Booking/ARI saga; one carries the integration relationships (Identity/Notification liaison) |
| Frontend (Angular) | 2 | Guest funnel · host/ops portal |
| Platform/DevOps | 1 | CI/CD, K8s, observability, gate load-test rigs |
| QA/SDET | 1–2 | Gate suites are first-class deliverables; owns the G1/G2 rigs |
| Product/BA | 1 | FRD grooming, market/tax research, OQ closure tracking |

Phase 4's parallel streams need backend ≥3 + both frontend; otherwise serialize (Payments first) and accept +3 weeks.

---

## 16. Risks & Mitigations

| # | Risk | Phase | Mitigation |
|---|---|---|---|
| R1 | Overbooking defect found late | P3 | G1 executable + non-negotiable; invariant monitor runs in prod forever |
| R2 | Payment double-charge / lost refund | P4–P5 | G2 idempotency torture; daily reconciliation from P4 day one |
| R3 | Channel drift → external oversell | P7 | Shadow mode; reverse-sync lag SLO; reconciliation; rehearsed conflict drill |
| R4 | Timezone/DST bugs in policy windows | P5 | BR-4 + dedicated DST golden matrix |
| R5 | Search index drift vs ARI truth | P4+ | Truth only at hold (BR-1); index-lag SLO; reindex runbook |
| R6 | Partitioning retrofit pain | P2 | Partitions + creation job before data volume exists |
| R7 | Module-boundary erosion blocks extraction | P0+ | Arch tests in CI wk 1; extraction proven at P9 |
| R8 | Tax/regulatory complexity per market | P2, P8 | Limit launch markets; tax rules as data |
| **R9** | **Identity team unavailable for joint work (claims, erasure, partner scopes)** | P0, P6, P9 | §14 dependency calendar booked at kickoff; fallbacks documented; 2-week escalation rule |
| **R10** | **Notification contract gaps (guest-checkout delivery, attachments, scheduling)** | P0, P3–P5 | OQ-3 is a G0 blocker; INT-N5/N6 fallbacks (links, platform scheduler) pre-designed |
| R11 | Identity Service outage takes down the funnel | All | INT-I8: local JWKS validation, anonymous browse unaffected; chaos-test in G4 |

---

## 17. Working Agreements

- **DoD per UC:** API + UI slice + automated tests (unit/integration/gate scenario) + named OTel spans + runbook note if operational.
- **Every external boundary idempotent** (BR-5) — PR checklist item for PSP, channel, Notification-command, and provisioning code.
- **Events and integration commands are contracts:** schema-registry versioned; additive change only; breaking = new version + migration window — this now applies to the Notification command schema and Identity claims expectations equally.
- **Gate suites are permanent:** G1/G2/G5 run nightly against staging forever.
- **Saga never blocks on notifications** (BR-11) — enforced by design (outbox → adapter) and asserted in G1.

---

## 18. Immediate Next Actions (this week)

1. **Book both integration kickoffs** (Identity, Notification) — OQ-3/5/6 are G0 blockers; nothing else this week matters more.
2. Confirm launch market(s) → PSP choice (P4), tax rules (P2), channel managers (P7).
3. Stand up the Phase-0 backlog from §3 and provision infrastructure.
4. Nominate the permanent Booking/ARI owner and the integration liaison (§15).
5. Start host-acquisition planning for week-34 soft launch — with the compressed timeline, signing direct hosts is now even more clearly the real critical path.
