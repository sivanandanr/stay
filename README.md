# Stay Platform — Dev Kit

Everything to set up the **Stay** booking platform (hotel/stay/villa, .NET 10 modular monolith) locally and start implementing, in the layout to drop into your existing JAP mono-repo.

## Where each thing goes

| In this kit | Put it at | Purpose |
|---|---|---|
| `CLAUDE.md` | **`Stay/CLAUDE.md`** (nested, NOT repo root) | Comprehensive constitution + rules: golden rules, shared-service integration, local setup, backend/frontend/UI-UX/performance/security/notifications/RazorPay/auditing rules, and the phase plan. Your existing root `CLAUDE.md` (food) stays untouched. |
| `stay-setup.md` | repo root or `Stay/` | Step-by-step setup. **Start here.** |
| `scaffold-stay.sh` | run from inside `Stay/` | Creates the .NET solution skeleton. |
| `docker-compose.yml` | merge into `docker/` | Take only PostGIS + OpenSearch (reuse your Postgres/Redis/Kafka). |
| `db/schema.sql` | `Stay/db/schema.sql` | Validated PostgreSQL DDL (`guest_id NOT NULL`, partitions, no-overbooking CHECK). |
| `docs/*` | `docs/` at repo root | Design + standards + runbooks. Referenced from `CLAUDE.md`; read per task. |

## docs/ (reading order)
1. `system-design.md` — architecture, booking saga, search, concurrency.
2. `frd-v2.md` — actors, use cases, integration requirements, business rules.
3. `database-design.md` (+ `database-design-extended.md`) — schema, partitioning, indexing.
4. `implementation-plan-v2.md` — 9 phases, gates G0–G6, coverage matrix.
5. `phase0-backlog.md` — sprint-ready Phase-0 stories.
6. `capacity-plan.md` — 50k-concurrent / two-region sizing.
7. `engineering-standards.md` — extended frontend/backend/performance/security + mobile API contract.
8. `stay-frontend-runbooks.md` — run/build/operate the portal + mobile app.
9. `future-roadmap.md` — next-gen capabilities (AI/dynamic pricing/agentic commerce) sequenced Phase 9+.
10. `_superseded/` — v1 docs, kept for history.

## CLAUDE.md now includes (per request)
Performance · Security · UI/UX · Notifications · RazorPay payment gateway · Auditing — plus local dev setup and the phase-by-phase work plan. It's intentionally comprehensive; the `docs/` files carry the same rules in more depth if you want to trim sections back to pointers.

## First steps
1. `docs/` + `CLAUDE.md` (→ `Stay/CLAUDE.md`) in place; keep your existing root `CLAUDE.md`.
2. `cd Stay && bash scaffold-stay.sh` → builds, arch-test green.
3. `createdb stay && psql -d stay -f Stay/db/schema.sql`.
4. Add PostGIS + OpenSearch to `docker/`; wire shared-service endpoints (UserService, PaymentGateway, NotificationService, StorageService) in `appsettings` with local fakes.
5. Add `ocelot.stay.json` (additive); walking skeleton green = **Gate G0**.
6. Phase 1: owner registration + admin approval in `stay-portal`.

See `stay-setup.md` for full detail.

## Transactional outbox (P0-A6)

The reliable DB-write → publish → consume rail lives in `BuildingBlocks/Outbox`:

- **`IOutboxWriter`** writes a domain event into a context's `outbox_message` table using the caller's
  open connection + transaction, so the event commits atomically with the state change (no dual-write, BR-11).
  See the producer at `Modules/Catalog/.../CatalogModule.cs` (`POST /api/v1/catalog/test-event`).
- **`OutboxDispatcher`** (hosted service) polls each configured schema (`FOR UPDATE SKIP LOCKED`), publishes
  to Kafka via **`KafkaEventPublisher`**, then marks the row processed — publish-before-commit gives
  at-least-once; a crash mid-batch republishes on restart.
- **`OutboxConsumer`** subscribes to the topic and dedupes by event id (`IdempotentReceiver`) so redelivery is
  effectively-once (BR-5). The demo handler logs receipt; real consumers register their own `IIntegrationEventHandler`.

Wire-up is `AddOutbox(...)` + `AddOutboxConsumer()` in `Stay.Api/Program.cs`; config under `Kafka:*` and
`ConnectionStrings:Stay`. The round-trip + atomicity are proven in `tests/IntegrationTests` (Testcontainers
Postgres + Kafka — Docker required to run them).
