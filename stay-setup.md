# Stay — Setup Guide (inside the existing JAP mono-repo)

The booking platform is the **Stay** vertical. It slots into your existing microservices repo as one cohesive service (internally a modular monolith), behind your existing **Ocelot** `Gateway`, reusing the shared services. Ignore the `Hotel`/`hotel-portal`/`jap-hotel-app` folders.

## New folders (matching your conventions)

| Folder | What | Convention match |
|---|---|---|
| `Stay/` | Backend — the modular-monolith booking service (.NET 10) | PascalCase, like `Payment`, `Inventory` |
| `stay-portal/` | Angular 20 **Owner/Admin portal** (owner registration + admin approval + property/ARI mgmt) | kebab, like `hotel-portal`, `business-portal` |
| `jap-stay-app/` | Mobile **guest** app — consumes `/api/v1` | like `jap-hotel-app`, `jap-user-app` |

Reused as-is (integrate, never modify their food behavior): `Gateway` (Ocelot), `Notification`, `Payment` (RazorPay), `CacheService` (Redis), your UserService, your storage service (MinIO).

## Critical structural rule

Build the **entire Stay backend as ONE service in `Stay/`** — do not scatter it across new top-level folders like your other microservices. The reason: **Booking + ARI + Pricing share one database and one transaction** (the no-overbooking guarantee is a single atomic SQL statement). Splitting ARI into its own service breaks that. Search, Reviews, and Channel sync *can* be peeled off later (they're eventually consistent), but the transactional core stays together.

---

## Prerequisites
- .NET 10 SDK
- Node 20+ (Angular 20)
- Docker (you already have a `docker/` setup — reuse it)
- The validated `schema.sql` and the design docs + `CLAUDE.md` from your kit

---

## Step 1 — Land the docs

```
repo-root/
  CLAUDE.md                      # at root; your .claude/ already exists, so Claude Code picks it up
  docs/
    system-design.md  frd-v2.md  database-design.md
    implementation-plan-v2.md  phase0-backlog.md
    capacity-plan.md  engineering-standards.md
  Stay/
    db/schema.sql
```
Update the doc paths inside `CLAUDE.md` to match (`docs/...`). Add `Stay`-aware note: the service folder is `Stay/`, portal is `stay-portal/`, mobile is `jap-stay-app/`.

## Step 2 — Scaffold the Stay backend

From inside the `Stay/` folder, run `scaffold-stay.sh` (companion file). It creates the solution, `BuildingBlocks`, `Stay.Api` (the `/api/v1` host), the `Catalog` and `Booking` reference modules (4-layer pattern), and the architecture boundary test — a building skeleton you extend module by module. Add the remaining modules (`ARI Pricing Payment Search Reviews Promotion Channel Admin NotificationAdapter`) with the same `add_module` function.

## Step 3 — Database

The Stay service owns its own database (don't share a DB with food services — that would couple them). Create a `stay` database on your existing Postgres and apply the schema:

```bash
createdb stay
psql -d stay -f Stay/db/schema.sql        # needs the postgis extension available
```
Per-module EF migrations replace the consolidated file over time; `schema.sql` is the canonical reference and the fast local-bootstrap.

## Step 4 — Infra additions (reuse what you have)

You already run Postgres + Redis (CacheService) + likely Kafka. Stay adds two things — add them to your existing `docker/` compose, don't run a parallel stack:

1. **PostGIS** — either switch your Postgres image to `postgis/postgis:16-3.4` or `CREATE EXTENSION postgis;` in the `stay` DB.
2. **OpenSearch** — new service:
```yaml
  opensearch:
    image: opensearchproject/opensearch:2.13.0
    environment: [ "discovery.type=single-node", "DISABLE_SECURITY_PLUGIN=true", "OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx512m" ]
    ports: [ "9200:9200" ]
```

## Step 5 — Wire the shared-service integrations (additive, config-driven)

`Stay/src/Stay.Api/appsettings.json` (real values per environment via secrets, not committed):
```jsonc
{
  "ConnectionStrings": { "Stay": "Host=localhost;Database=stay;Username=...;Password=..." },
  "Auth":        { "Authority": "https://userservice/.well-known", "Audience": "stay-api" },
  "Services": {
    "PaymentGateway":      { "BaseUrl": "http://payment", "Source": "stay" },   // RazorPay, source-tagged
    "NotificationService": { "BaseUrl": "http://notification", "CategoryPrefix": "stay." },
    "Storage":             { "Endpoint": "http://minio", "MediaBucket": "stay-media", "DocsBucket": "stay-documents" },
    "Redis":               { "Configuration": "localhost:6379" }
  },
  "Kafka": { "BootstrapServers": "localhost:9092" }
}
```
Each service is called through a port (`IPaymentGateway`, `INotificationCommands`, `IObjectStorage`) — provide local fakes implementing the same ports so the service builds and tests without the real food services running.

## Step 6 — Ocelot routes (additive — do not touch food routes)

Your `Gateway` likely merges multiple `ocelot.*.json` files. Drop a new `ocelot.stay.json` so nothing existing changes:
```jsonc
{
  "Routes": [
    {
      "DownstreamPathTemplate": "/api/v1/{everything}",
      "DownstreamScheme": "http",
      "DownstreamHostAndPorts": [ { "Host": "stay-api", "Port": 8080 } ],
      "UpstreamPathTemplate": "/stay/api/v1/{everything}",
      "UpstreamHttpMethod": [ "Get", "Post", "Put", "Delete", "Patch" ],
      "AuthenticationOptions": { "AuthenticationProviderKey": "UserService" },
      "RateLimitOptions": { "EnableRateLimiting": true, "Period": "1s", "Limit": 20 }
    }
  ]
}
```
Public/mobile clients hit `/stay/api/v1/...` at the gateway; anonymous is allowed for search/browse, token required for hold/confirm/cancel (enforced in `Stay.Api`, not just the gateway).

## Step 7 — Owner/Admin portal (`stay-portal/`)

```bash
ng new stay-portal --standalone --routing --style=scss --ssr=false
```
Zoneless + signals; PKCE against UserService (new OIDC client); role-guarded routes (owner vs admin). Build the five journeys from `engineering-standards.md` §2 — owner registration and the **admin approval gate** first (an unapproved owner cannot list).

## Step 8 — Mobile guest app (`jap-stay-app/`)

Consumes the published `/api/v1` OpenAPI. No guest web UI — the mobile app is the guest funnel. Generate its API client from `Stay.Api`'s Swagger.

## Step 9 — Walking skeleton = Gate G0

Bring it up: `docker compose up -d` (infra) → `dotnet run` in `Stay.Api` → hit `GET /stay/api/v1/catalog/ping` through Ocelot with a UserService token → confirm a traced request reaches Postgres and the outbox publishes to Kafka. That's G0; then proceed through `docs/phase0-backlog.md`.

---

## Order of operations (first week)
1. Docs + `CLAUDE.md` in place (Step 1).
2. Run `scaffold-stay.sh`; solution builds; arch-test green (Step 2).
3. `stay` DB created + schema applied; PostGIS + OpenSearch added to `docker/` (Steps 3–4).
4. Shared-service ports wired with local fakes (Step 5); Ocelot route added (Step 6).
5. Walking skeleton green end-to-end (Step 9) = G0.
6. Then Phase 1: `stay-portal` owner-registration + admin-approval, backed by the `Catalog` + `Admin` modules.

Everything else (the full module set, the saga, payments, search) follows the phase plan — but this gets you from an empty `Stay/` folder to a running, gateway-routed, authenticated skeleton.
