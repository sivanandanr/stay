# Stay — Future Roadmap (North Star)

Forward-looking capabilities for the Stay platform, sequenced **after** the core launch. The premise: the architecture was deliberately seeded with seams (rules-as-data pricing, the event spine, the read-model search split, the inventory-agnostic saga, the versioned idempotent API) so these are **additive features, not rewrites**. Each item below names the seam it builds on, where it slots in the phase plan, and the catch.

> Sequencing rule: ship the core (Phases 0–6, soft launch G4) and channel/ops (7–8) first. Everything here is **Phase 9+** unless noted. Don't pull these forward — a next-gen feature on an unproven booking core is wasted effort.

---

## Horizon 1 — Activate existing seams (Phase 9, high ROI, low architectural risk)

### H1.1 ML dynamic pricing / yield management
- **Seam:** `pricing.pricing_rule` is rules-as-data; the pricing pipeline is deterministic. An ML model becomes another writer to `rate_calendar` — like a channel manager.
- **How it'll be:** model reads demand, occupancy, lead-time, comp-set; adjusts nightly rates within host-set floor/ceiling. Host keeps a kill-switch.
- **Change needed:** a pricing-recommendation service + a write path with guardrails; offline training on funnel + booking data.
- **Catch:** fairness + floor/ceiling governance, or you get erratic prices and angry hosts. Never personalize *displayed* base price by user in a way that looks discriminatory — personalize offers/discounts instead.

### H1.2 Learned, personalized search ranking
- **Seam:** Search already separates the OpenSearch read model from a pluggable ranking score.
- **How it'll be:** replace the deterministic score with a conversion-optimized model fed by the funnel events you already emit.
- **Change needed:** a ranking service (the design names Search as a first extraction candidate) + a feature store.
- **Catch:** guard against filter-bubble degradation; keep a transparent "sort by price/rating" override.

### H1.3 Flexible-date discovery
- **Seam:** ARI calendars are partitioned for fast range scans.
- **How it'll be:** price-calendar UX — "cheapest nearby dates," "± 3 days," "weekend finder."
- **Change needed:** a date-range price aggregation endpoint + caching. Small feature, large conversion lift.

### H1.4 Real-time availability push
- **Seam:** `AvailabilityChanged` already flows on the event spine.
- **How it'll be:** SSE/WebSocket push to clients for honest scarcity ("2 left") and flash-sale drops, replacing polling.
- **Change needed:** a push/fan-out service subscribed to the event topic.
- **Catch:** fan-out cost at scale — gate to high-demand inventory; never fabricate scarcity (ties to the no-dark-patterns UI rule).

---

## Horizon 2 — New capabilities (Phase 10+, medium build)

### H2.1 Conversational + AI trip planning
- **Seam:** API-first `/api/v1`; your existing `ExperienceAPI` for things-to-do.
- **How it'll be:** natural-language search ("quiet beach villa in Goa, long weekend, under ₹15k, pool") parsed to a structured query the assistant runs, holds, and presents; itinerary generation combining Stay + Experiences; a review-grounded Q&A ("good for families?") via RAG over **verified** reviews.
- **Change needed:** an NL→query service, an itinerary composer, a RAG pipeline grounded in `reviews` (no fabrication).
- **Catch:** grounding/hallucination control; never invent amenities or policies.

### H2.2 Flexible inventory products
- **Seam:** booking + payment + pricing modules; saga isolates these.
- **How it'll be:** cancel-for-any-reason add-on, split-payment among travelers, group booking, book-now-decide-later, subscription/credits/wallet, long-stay monthly pricing.
- **Change needed:** product/pricing extensions + payment-split logic (RazorPay Route already gives multi-party settlement).
- **Catch:** each new cancellation/payment variant needs its own test matrix (timezone + refund tiers).

### H2.3 Immersive discovery
- **Seam:** `media.kind = THREE_SIXTY` already exists; StorageService + CDN.
- **How it'll be:** 3D/360° tours on the detail page, AR "view the room" on mobile.
- **Change needed:** media pipeline for 3D assets; client viewers.

### H2.4 Real-time trust & risk
- **Seam:** the event stream; BR-6 verified reviews; `admin.block_list`.
- **How it'll be:** a risk score on the hold/confirm path (velocity, device, payment signals); verified-review trust badges; host reputation.
- **Change needed:** a risk-scoring service consuming booking/payment events; a feedback loop into fraud rules.

---

## Horizon 3 — Strategic bet: Agentic Commerce (Phase 10+, highest leverage)

**Expose Stay as an agent-callable surface (an MCP server / scoped agent API) so AI assistants can search, hold, and book on a user's behalf.** This is the standout because the design is *unusually* ready for it — and most competitors aren't.

**Why it's feasible here specifically:**
- The API is **versioned** and **idempotent** (every state-changing call takes an `Idempotency-Key`) — exactly what an agent needs to retry safely over flaky conditions.
- Errors are **machine-readable `problem+json` types** (`sold-out`, `price-changed`, `hold-expired`) — an agent can *reason* about them and recover (re-quote, re-select), not just see an HTTP 409.
- Auth is **OAuth scopes via UserService** — delegated, limited access is a config change, not a redesign.
- The discipline we baked in for mobile-network retries is the same discipline agents require, so most of the groundwork is already paid for.

**How it'll be:**
> A traveler tells their assistant "rebook my usual Goa villa for the same weekend next month." The assistant holds an on-behalf-of token scoped to `stay.book` with a spending cap, searches, hits a `price-changed` error, re-quotes, and surfaces the new total for the user to **confirm** before it calls `confirm`.

**What you'd add:**
- Delegated, scoped tokens (OAuth on-behalf-of) with **per-agent spending limits**.
- A **mandatory human-confirmation step for money** — agents may search/hold autonomously, but `confirm` requires explicit user sign-off.
- Clean machine-readable inventory + an MCP tool manifest mapping to `/api/v1`.
- Tighter rate limits + anomaly detection on the agent surface.

**Catch:** agentic spending without guardrails is the failure mode — caps, confirmation, and audit (every agent action lands in `admin.audit_log`) are non-negotiable.

---

## Horizon 3 — Platform play: Bundling & distribution

### H3.2 Packages & marketplace
- **Seam:** the booking saga is inventory-agnostic; new inventory types plug in as Catalog property types + their own ARI.
- **How it'll be:** Stay + Experiences + transport sold as a single package/cart; cross-sell at checkout.
- **Change needed:** a package/cart composer spanning verticals; combined cancellation rules.

### H3.3 White-label / B2B distribution
- **Seam:** the Partner API (M13, already planned for Phase 9).
- **How it'll be:** other brands distribute your inventory via API with per-partner commission/markup; "channel-as-a-platform."

---

## Cross-cutting enablers & guardrails

**Why any of this is cheap** — the architecture discipline is the payoff. Keep these intact or the roadmap gets expensive:
- Rules-as-data pricing, the event spine, the read-model split, the inventory-agnostic saga, the versioned idempotent API.
- A feature store + offline training pipeline (shared by H1.1, H1.2, H2.4) — build once.

**Guardrails (the features are only as good as these):**
- **Pricing fairness:** floors/ceilings, no discriminatory displayed pricing; personalize offers, not base price.
- **Agentic safety:** scoped delegation, spending caps, human confirmation for money, full audit.
- **AI grounding:** RAG/answers grounded in real data (reviews, policies); never fabricate amenities, prices, or availability.
- **No dark patterns** (already in the UI/UX rules): honest scarcity, full price before pay — doubly important once an algorithm or agent is in the loop.
- **Privacy:** personalization respects BR-8 erasure; models don't memorize PII.

---

## Phased integration plan (interwoven, not parked at the end)

Each capability has two tracks: **Seed** (cheap design/data hooks — do early, expensive to retrofit) and **Build** (the feature itself — gated behind a working, proven core). Pulling *Seed* forward costs almost nothing; pulling *Build* forward either adds risk or is impossible until the data exists.

### Seed from the start (Phases 0–4 — mostly already required)
- API **idempotency + `problem+json` error types + OAuth scopes** → agentic readiness (you're building these anyway).
- `pricing_rule` **rules-as-data** (Phase 2) → dynamic-pricing write path ready.
- Rich, versioned booking/hold/**funnel events** in a model-friendly schema → the training data for future pricing/ranking/risk. Backfilling later is painful — define the signals now.
- `THREE_SIXTY` **media capability** (Phase 1 Catalog) → hosts upload immersive media from day one, even before viewers ship.
- Structured **review sub-scores** (already in schema) → corpus for future review-grounded Q&A.

### Brought forward (genuinely safe / high-leverage)
| Item | Was | Now | Why it's safe |
|---|---|---|---|
| Flexible-date discovery (H1.3) | P9 | **P4–5 (with Search)** | a near-term search feature, not deep next-gen; rides existing ARI + Search |
| Agentic-commerce MCP adapter (H3.1) | P10 | **right after G4 (~P7)** | thin adapter once `/api/v1` is stable; the discipline is already there |
| Real-time availability push (H1.4) | P9 | **P8** | rides the existing event spine |

### Gated by data, not preference (cannot meaningfully move earlier)
| Item | Earliest realistic | Why |
|---|---|---|
| ML dynamic pricing (H1.1) | **P8** | needs booking history to train — that data only exists after Phases 3–7 run live |
| Learned ranking (H1.2) | **P8** | needs funnel volume to train |
| Trust/risk scoring (H2.4) | **P8** | needs booking/payment history + fraud patterns |

> These three aren't parked late by choice — a model with no data is a random-number generator. They move to **Phase 8**, as early as the data they need exists.

### Stays at Phase 9+ (build-heavy breadth)
Conversational/AI planning (needs stable search + content), flexible inventory products (extend the *proven* payment core), packages, white-label. Pulling these before G4 delays launch for breadth the launch doesn't need.

### The tradeoff to own
Every *Build* item pulled before **G4 (soft launch, ~week 34)** delays launch. The shape that wins: **launch lean, seed every cheap hook from Phase 0** (so you never retrofit), do the **agentic adapter immediately after launch**, and let the data-dependent models land in **Phase 8** when their inputs finally exist. The agentic surface is the one bet worth reaching for the moment `/api/v1` stabilizes — highest leverage, lowest marginal cost.
