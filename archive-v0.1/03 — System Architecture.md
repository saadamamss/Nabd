# 03 — System Architecture

**Modular Monolith Architecture — Asset Telemetry & Anomaly Detection SaaS**

*Blueprint v0.1 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file defines the **shape of the building**: the modules ("rooms"), how data flows, what each module owns, and the big technical decisions with their reasons (ADRs). Requirements come from 02; this file says **how the code will be organized to satisfy them**.

Golden constraint from 02 §3.2: **the data model is type-agnostic** — verticals are templates/configuration, never hardcoded concepts. Any module below that bakes in "temperature" is a defect.

## 2. The One-Paragraph Summary

It's **one codebase, multiple deployable processes, clearly separated modules.** Devices push readings to an MQTT broker or an HTTP endpoint. A processing worker validates and stores readings in PostgreSQL + TimescaleDB. A rules engine evaluates thresholds and offline-detection, produces alerts with deduplication/cooldown, and notifies (dashboard + email). A web app (Next.js) shows the live dashboards via SignalR. Multi-tenancy is enforced at the application layer in every API path, with PostgreSQL Row-Level Security as the second line of defense on a tenant-scoped schema.

## 3. High-Level Component Map

```
Devices (sensors / gateways)
      │  MQTT (QoS1, TLS)                         Devices (HTTP-capable)
      ▼                                                        │  HTTPS POST
┌───────────────────────┐                                       ▼
│      MQTT Broker      │  (EMQX single node — auth + revocation API)
│  Per-device credentials│                                    │
└──────────┬────────────┘                       ┌────────────────────┐
           │ subscribe                          │   API Host (HTTP)  │
           ▼                                    │  ingest endpoint   │
┌───────────────────────────────────────────────┴─────┐              ▼
│              Processing Host (Worker)               │      RabbitMQ (queue)
│  ┌────────────┐   ┌──────────────┐   ┌────────────┐ │              │
│  │ Ingestion  │──▶│ Rules Engine │──▶│  Alerting  │ │◀─────────────┘
│  │ (validate, │   │ (thresholds, │   │ (dedup,    │ │
│  │  batch     │   │  offline     │   │  cooldown, │ │
│  │  store)    │   │  watchdog)   │   │  email)    │ │
│  └─────┬──────┘   └──────────────┘   └─────┬──────┘ │
│        ▼                                   ▼        │
│  PostgreSQL + TimescaleDB  ◀──────────── tenancy RLS │
│  ├─ telemetry.readings (hypertable)                  │
│  ├─ telemetry.readings_hourly (aggregates)           │
│  └─ schemas: identity / assets / rules / alerting    │
└──────────────────────────────────────────────────────┘
           │ publish status updates
           ▼
     ┌───────────┐   SignalR (WebSocket)   ┌─────────────────┐
     │ API Host  │ ──────────────────────▶ │  Web App (Next.js) │
     │  (+REST)  │                          │  dashboards, alerts │
     └───────────┘                          └─────────────────┘
                     ┌─────────────┐
                     │  Simulator  │  (console/service — demo + tests)
                     └─────────────┘
```

## 4. Modules (the "Rooms")

One solution, one module per folder. Each module = implementation project + contracts project + own DB schema.

| Module | Owns (data) | Public door (Contracts) | Publishes events | Consumes |
| --- | --- | --- | --- | --- |
| **Identity & Tenancy** | tenants, users, roles, sessions | user/tenant lookups, permission checks, tokens | UserCreated, RoleChanged | — |
| **Assets** | assets, devices, device types, channel templates, rules presets (seed) | asset/device CRUD APIs, token issue/revoke, channel template lookup | AssetAdded, DeviceRegistered, DeviceTokenRevoked | UserCreated |
| **Ingestion** | ingest session registry, validation logs | HTTP ingest API | ReadingsAccepted (batch) | DeviceTokenRevoked |
| **TelemetryStore** | readings hypertable, aggregates, retention jobs | write readings, query history, query aggregates | — | ReadingsAccepted |
| **Rules** | rule definitions, rule evaluation state | rule CRUD API | CrossingDetected, OfflineDetected | ReadingsAccepted, device heartbeats |
| **Alerting** | alerts, alert events (state changes), notification log | alert APIs, register external channel | AlertRaised, AlertAcknowledged, AlertResolved | CrossingDetected, OfflineDetected |
| **Notifications** | channels config (SMTP), email templates (ar/en) | send helper | — | AlertRaised, AlertResolved |
| **Realtime** | live hub connections | SignalR hub, push helper | — | AssetStatusChanged, AlertRaised |
| **Admin (Platform)** | none — reads via cross-module contract views only | tenant list/usage APIs, enable/disable tenant | TenantDisabled | — |

Rules that make the rooms real:

1. **The only public door is `*.Contracts`** — the compiler enforces it (internal types + NetArchTest in CI: module A referencing module B internals = build failure).
2. **Each module owns its schema.** No cross-schema SQL joins. Cross-module reads go through contracts or contract-provided views/DTOs.
3. **Module-to-module communication uses events** where possible, and only injected contracts where sync is unavoidable (e.g., permission checks).
4. **Hosts are thin.** `Api.Host` and `Processing.Host` only wire modules together and configure startup — zero business logic.
5. Work on a single module = open a solution filter (`.slnf`) containing that module + its contracts only.

## 5. Physical Deployables at MVP

| Deployable | What it is | Why separate |
| --- | --- | --- |
| **API Host** | ASP.NET Core: REST API, SignalR hub, HTTP ingest endpoint | Serves browsers; must restart independently of data pipeline |
| **Processing Host** | Worker service: MQTT client, queue consumer, batch writer, rules engine, offline watchdog, email sender | heavy/bursty load; restart must not interrupt the API |
| **MQTT Broker** | EMQX single node (container) | infrastructure, not our code |
| **PostgreSQL + TimescaleDB** | container | infrastructure |
| **RabbitMQ** | container | buffer between ingestion and heavy processing (NFR-04 durability) |
| **Redis** | container (from V1; SignalR backplane + caching — MVP can run single-node SignalR without it) | upgrade path |
| **Web App** | Next.js container | UI only |
| **Simulator** | console app (or service in demo env) | dev/testing/demo data |

**Docker Compose** wires all of this for dev and MVP production. No Kubernetes at MVP (see ADR-007).

## 6. Key Data Flows

### 6.1 Happy path (MQTT)

1. Device publishes reading batch (tenant topic, authenticated with device credential) → broker → Processing Host MQTT consumer.
2. Ingestion validates payload + device token; checks idempotency key `(deviceId, eventTime, seq)`; writes batch to `telemetry.readings` (COPY batch insert); **only now is the message acknowledged** → 0 data loss (NFR-04).
3. In-queue event per batch → Rules Engine: evaluates each active rule for those channels → threshold crossing? → Alerts module: dedup/cooldown check → AlertRaised (if new).
4. AlertRaised → Notifications (email, ar/en template) + Realtime (SignalR push) + dashboard status.
5. Aggregates refresh via Timescale continuous aggregates automatically.

### 6.2 Offline detection

1. Processing Host runs an **offline watchdog** every minute: for each enabled device, compare now − last-heard vs configured quiet window (default 30 min).
2. Past window → OfflineDetected → Alerts module with a lower-urgency treatment per customer's rules → same notify path.
3. Device comes back → watchdog resolves the offline alert automatically (no manual close).

### 6.3 HTTP ingest path

1. Web/device with internet access (e.g., a gateway posting batches) → API Host `/ingest` (HTTPS, device token).
2. API Host validates credentials, **enqueues the batch to RabbitMQ immediately after basic validation** (fast accept, bounded queue), returns 202.
3. Processing Host consumes → same pipeline as 6.1 (including full validation; invalid batches are logged and dead-lettered).

> Note: 6.3's "accept early, validate later" design is deliberate: the queue is the durability guarantee between hosts. This is verified in 09 tests: queue-batched payloads also go through validated-then-stored processing, and a dead-letter queue collects bad payloads so nothing disappears silently. (Reconciliation of FR-22: "accepted means stored" applies at the point of full validation — tested in 09.)

### 6.4 Login/status

1. Browser → JWT access token → REST/SignalR.

## 7. Technology Decisions (ADR summary)

Each ADR will also live as a short file inside `docs/adr/` in the repo. Full reasoning here.

| ADR | Decision | Why | Rejected alternatives |
| --- | --- | --- | --- |
| **ADR-001** | Modular Monolith with room-per-module | One person + AI agents; local code boundaries with compiler enforcement; cheapest ops; each room can become a service later if load demands | Microservices from day one (rejected: distributed complexity, deployments, no payoff at our scale) |
| **ADR-002** | .NET 8 LTS — C#, ASP.NET Core, Workers | LTS support till late 2026+; single language across API + workers; good MQTT/async libs; your chosen stack with <1 year experience — one language lowers cognitive load | Node.js (weaker time-series ecosystem for this shape), Go (steeper learning curve for you) |
| **ADR-003** | PostgreSQL 16 + TimescaleDB extension | One DB for relational (tenants/assets/alerts) AND time-series (readings/aggregates); continuous aggregates, retention & downsampling built-in; row-level security; single backup story; standard SQL skills | InfluxDB (no joins with relational data, second system), ClickHouse (great scale but more moving parts & learning). **Note:** challenged — "relational DBs don't suit telemetry read/write volume." Response on record: (a) our target is 2,000 readings/sec, modest vs Timescale's tens-of-thousands/sec batch-write headroom on the NFR-01 box; (b) dashboard reads hit pre-computed continuous aggregates, not raw scans; (c) alert/rule/tenant state is transactional relational data that would otherwise need a second synchronous DB; (d) RLS tenant isolation requires a full-relational capability. **Falsification criteria:** if check #5 in 02 (2K msg/sec × 10 min, p95 alert ≤ 5s) fails after chunk/index tuning, or any tenant sustains ≫ design load, revisit — expected only at V2 scale |
| **ADR-004** | EMQX (single node) as MQTT broker | Per-device credential auth via HTTP plugin/API, immediate revocation (FR-12 requires revocability), MQTT 5, WebSockets, known scale headroom, good single-container ops for MVP | Mosquitto (lighter but weaker dynamic auth/revocation story), AWS IoT Core (rejected in 01: pricing model + no control) |
| **ADR-005** | RabbitMQ as the ingestion buffer queue | Bounded quorum queues; dead-lettering; matches the ack-after-store durability pattern (NFR-04); simple ops | NATS JetStream (fine too, Rabbit nicer DLQ UX), Postgres-as-queue (processing cheaper but bloats the main DB and complicates 2K msg/s target) |
| **ADR-006** | In-process events between modules within a host; queue only between hosts | MVP simplicity: no event bus tax inside one process; the only inter-host queue is the ingestion buffer | Kafka (massive overkill — explicitly listed for later in 00/telemetry notes) |
| **ADR-007** | Docker Compose on a single managed VM (e.g., Hetzner/DO) for MVP; Kubernetes deferred | NFR-01 (2K msg/s) fits comfortably in one box; K8s is an operational tax with no MVP payoff | Kubernetes (deferred to V2 per file 00) |
| **ADR-008** | Frontend: React (Next.js) + TypeScript | RTL-ready component ecosystem, charts libs (Recharts/visx), hiring/maintenance common, AI-codegen quality | Blazor (weaker Arabic RTL/chart ecosystem) |
| **ADR-009** | Auth: JWT for API + device tokens; OpenIddict deferred | MVP needs users + devices, not enterprise SSO; keeping identity module swappable for OpenIddict/Keycloak in V2 (00 has SSO there) | Keycloak at MVP (rejected: another stateful system to operate without a customer asking for SSO) |
| **ADR-010** | Monitoring: health endpoint + structured logs (Serilog) + basic Prometheus metrics; Grafana deferred to 08 infra phase | Minimum to notice breakage; full observability stack follows the infra file | Full OpenTelemetry from day 1 (deferred, stays in plan) |

## 8. Cross-Cutting Concerns

| Concern | Approach |
| --- | --- |
| **Multi-tenancy** | Every table carries `tenant_id`; PostgreSQL Row-Level Security enforces it as a second line of defense (the app sets `current_tenant` per request/connection); every API filter tenant-scoped. Automated isolation tests in CI (02 acceptance #1) |
| **Clock/time** | Readings carry event time (device UTC) + received-at (server). Dashboard uses event time; rules use event time with a small allowed delay; watchdog uses server time |
| **Idempotency** | Reading uniqueness on `(device_id, event_time, seq)`; alert dedup on `(asset_id, rule_id)` with cooldown table |
| **Time zones** | Store everything UTC; convert in UI per user/tenant preference |
| **Localization** | All strings from resource dictionaries AR/EN; emails have both templates; DB seed strings bilingual (channels, asset types) |
| **Config** | 12-factor — env vars only; no config in code; per-environment compose files |
| **Errors** | Result objects in modules; exceptions only at boundaries; API problem-details responses |

## 9. What This Architecture Deliberately Does NOT Have

- No CQRS/MediatR framework ceremonies (per the earlier telemetry discussion — plain services).
- No microservices (rooms may graduate later, per load evidence — "measure before you optimize").
- No ML, no Kafka, no K8s, no multi-region.
- No GraphQL, no gRPC between hosts (REST + queue only).
- No plugin system for new verticals — templates are seed data, that's enough.

## 10. Open Points for This File

| # | Point | Default for now |
| --- | --- | --- |
| A | Exact MQTT topic scheme | `t/{tenantId}/dev/{deviceId}/v1` with tenant prefix (broker auth binds device→tenant) |
| B | Readings payload schema (JSON field names) | Drafted in 05 (Device Connectivity) |
| C | Timescale chunk interval | 1 day at MVP (standard); revisit under load test (09) |
| D | Where email provider lives (SMTP vendor) | Any SMTP relay for MVP; per-tenant provider config in V1 |

## 11. Next Step

- [ ] Review the ADRs — push back on any that smell wrong
- [ ] Then → **04 — Domain & Data Model** (concrete tables, the generic template mechanism, RLS design)
