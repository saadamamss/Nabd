# 02 — Requirements

**Functional & Non-Functional Requirements — Asset Telemetry & Anomaly Detection SaaS**

*Blueprint v0.2 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file turns the vision (00) and the market picture (01) into **concrete, numbered requirements** that files 03–09 (architecture, data model, connectivity, detection, infra, testing) will implement and be tested against.

Rules:

- Every requirement has an **ID** so files and code can reference it (e.g., FR-12).
- Priority levels: **M** (must — MVP blocks without it), **S** (should — MVP ships better with it, deferred automatically if schedule slips), **L** (later — V1/V2, listed for context).
- Assessment method: any requirement without an acceptance check is treated as exists-but-unverified.
- Strategy reminder (from 01): **strictly minimal MVP**. Everything marked V1 in file 00 stays out.

## 2. Actors

| Actor | Description | Notes |
| --- | --- | --- |
| Tenant Viewer | Related maintenance engineer/technician | Views dashboards, receives alerts |
| Tenant Admin | Operations manager assigning owner/supervisor | Manages assets, users, rules |
| Platform Admin | You | Monitors all tenants and the health of the pipeline |
| Device | Sensor/controller/gateway | Sends readings via MQTT or HTTP |
| Simulator | Test tool generating fake devices/readings | MVP requirement, doubles as launch demo |

## 3. Functional Requirements (FR)

### 3.1 Identity, Tenancy & Access

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-01 | Tenant registration; each customer (tenant) gets an isolated workspace | M |
| FR-02 | Email + password login; sessions expire securely | M |
| FR-03 | Three roles: Viewer, Tenant Admin, Platform Admin (permissions in §5) | M |
| FR-04 | Tenant Admin can add/remove users and assign roles within their tenant | M |
| FR-05 | All read/write APIs and all SQL enforced tenant-scoped — **MVP kept to 3 roles exactly** | M |
| FR-06 | Arabic/English UI toggle + RTL layout, persisted per user | M |
| FR-07 | Optional tenant data region/backup policy selection | L |

### 3.2 Assets & Devices

> **Design constraint (applies to every file after this one):** the data model is **type-agnostic** — Asset → Sensor channels → Readings, with no vertical-specific concepts in the schema. Verticals (fridge, generator, tank...) exist only as **templates/configuration**: a set of named channels + default alert rule presets. The **temperature/cold template ships first only as the default choice** (fastest MVP, simplest launch story, per 00 §4.1). Adding a generator or tank template later must require **zero schema changes and zero core code changes** — only seed data/config. Any file 03+ that hardcodes a vertical is a defect against this rule.

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-10 | Create/edit/archive assets (name, type, location, notes) — type is free-form/template ref | M |
| FR-11 | Register devices (one device can send for one asset) | M |
| FR-12 | Each device has a revocable token/credential; ops disabling token blocked at ingestion | M |
| FR-13 | Device types with templates: list of sensor channels expected — **channels are config, not schema** (from a seed table, e.g. temperature, humidity, level, rpm, pressure) | M |
| FR-14 | Asset status computed from feeds + rules: healthy / warning / alert / offline / muted | M |
| FR-15 | Cold-chain template preset: default alert thresholds (e.g., fridge: high > 8°C, low < 2°C sample, all user-editable) — **a seed data preset, not hard-coded rules** | S |
| FR-16 | Controller/discovery (Modbus/OPC UA) — postponed to V2 (see 00 §6) | L |
| FR-17 | Reference Starter Kit supported and documented: one tested sensor + gateway profile with a provisioning/install guide (05 §13) | M |

### 3.3 Ingestion & Telemetry

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-20 | Accept readings via MQTT (QoS1) and HTTP POST | M |
| FR-21 | Payload validation and size limits; oversizing rejected with meaningful error | M |
| FR-22 | Each reading is committed to the database before the processing consumer acknowledges the broker/queue message; end to end the guarantee is at-least-once delivery plus idempotent storage (05 §7) — prevents loss across restarts (00 principle #4) | M |
| FR-23 | Idempotency: re-sent reading with the same (device, channel, timestamp, seq) stored once; `seq` is NOT NULL (0 when the device sends none) | M |
| FR-24 | Device clock-drift handling: server receive time recorded, event time from device kept but sanity-checked | M |
| FR-25 | HTTP batched readings endpoint for gateways | S |
| FR-26 | Raw payload replay from a corrupted store for reprocessing | L |

### 3.4 Detection & Alerting

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-30 | Threshold rules per sensor channel: exactly-above / below / outside range | M |
| FR-31 | Device-offline detection: no readings for X minutes (configurable per rule, default 30) raises an alert | M |
| FR-32 | Alert deduplication and cooldown: same asset+rule doesn't re-notify inside cooldown (default 1h, configurable) | M |
| FR-33 | Alert states: open → acknowledged → resolved; alert is against the asset, not the raw reading | M |
| FR-34 | Alert channels in MVP: in-app + email + Telegram push (one real-time channel; WhatsApp/SMS in V1) | M |
| FR-35 | Mute window per asset/rule (e.g., planned maintenance) | S |
| FR-36 | Statistical detection (Z-score/EWMA) | L |
| FR-37 | Alert → work order conversion | L |
| FR-38 | Telegram linking: a user links their chat with a one-time code (10 min expiry), picks which severities push, and can unlink anytime | M |

### 3.5 Dashboards & History

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-40 | Tenant dashboard: all assets with live status (live via SignalR for UI updates) | M |
| FR-41 | Asset history page: last readings graph with time-range selection (last 24h/7d/30d/custom) | M |
| FR-42 | Alerts list page with filtering by status/asset/severity | M |
| FR-43 | Downsampled aggregates for long ranges (Timescale continuous aggregates) | M |
| FR-44 | CSV export of history (basic); printable PDF temperature log in V1 — cold-chain buyers often need logs for inspections | S |
| FR-45 | Scheduled email reports | L |

### 3.6 Simulator (critical for build, test, and launch demo)

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-50 | N simulated devices with configurable device type, channels, send rate | M |
| FR-51 | Fault scenario generator: spike, drift, flatline, dropout (deliberate failure patterns) — validated against alerting | M |
| FR-52 | Scenario script: run "a fridge warming up over 40 min" as a launch Ready-to-Run event | M |

### 3.7 Platform Admin

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-60 | Platform Admin sees tenant list with usage (assets, devices, msgs/day) | M |
| FR-61 | Enable/disable a tenant | M |
| FR-62 | Per-tenant quotas: max devices, max storage, alert rate | S |
| FR-63 | Onboarding wizard (guided tenant setup) | L |

## 4. Non-Functional Requirements (NFR)

Numbers here are **initial targets** — they get sharpened in 09 (Testing) with measurement, not opinions.

| ID | Category | Requirement | Priority |
| --- | --- | --- | --- |
| NFR-01 | Throughput | Sustain **2,000 readings/sec** on one modest server (4 vCPU/8GB) without losses — a headroom target (~10× what the first ~10 customers need); the load test proves headroom, it does not justify extra MVP complexity | M |
| NFR-02 | Latency | Reading arrival → alert visible on dashboard: **p95 ≤ 5s** | M |
| NFR-03 | Dashboard | First meaningful paint ≤ 2s on normal broadband | S |
| NFR-04 | Durability | Zero loss across process/broker restarts: a reading acknowledged by the platform's consumer is already committed. The device→broker leg is at-least-once (QoS1 + device retry); on host/disk loss the loss window equals the backup RPO (08 §5) | M |
| NFR-05 | Availability | ≥ 99.5% monthly for MVP (NOT "mission-critical" language — honesty first) | M |
| NFR-06 | Isolation | No tenant can read or write another tenant's data through any API path — **tested automatically** | M |
| NFR-07 | Security | Device tokens stored only as a keyed fast hash (HMAC-SHA-256 — they are high-entropy secrets, so no slow password hash); TLS for MQTT/HTTP/API | M |
| NFR-08 | Secrets/config | Nothing hardcoded (12-factor) | M |
| NFR-09 | Backups | Daily automatic DB backup + a proven restore procedure (restore is tested, not assumed) | M |
| NFR-10 | Observability | Structured logs, key metrics (ingress rate, rules fired, error rate) + health endpoint | M |
| NFR-11 | Data retention | Default: raw readings kept 180 days; aggregates longer — configurable per tenant | S |
| NFR-12 | Scalability ceiling | One honest path out: the modular monolith so that scaling out is possible later without a rewrite (ADR in 03) | M |
| NFR-13 | Legal/privacy | Data belongs to the tenant; full export + delete on request | S |
| NFR-14 | Onboarding | Trial tenant can first alert in ≤ 60 minutes from signup using the simulator | M |

## 5. Permissions Matrix

| Capability | Viewer | Tenant Admin | Platform Admin |
| --- | --- | --- | --- |
| View dashboards/history | ✔ | ✔ | ✔ (all tenants) |
| Receive alerts | ✔ | ✔ | ✔ (all tenants) |
| Manage assets/devices | ✖ | ✔ | ✔ (all tenants) |
| Manage rules | ✖ | ✔ | ✔ (all tenants) |
| Manage users | ✖ | ✔ (own tenant) | ✔ (all tenants) |
| Monitor platform health | ✖ | ✖ | ✔ |

## 6. Acceptance Checks (Definition of Done for the MVP)

A release counts as "MVP done" only when **all** these pass:

1. Two tenants exist; tenant A cannot see tenant B's data through any API, including guessed IDs (isolation test suite).
2. Simulator run: a stock "cold chain failure" script (fridge warming) produces an alert on the dashboard within 5s of threshold crossing and an email within a minute.
3. Device-offline script: a sim device stops sending and the offline alert fires in ≤ the configured quiet window + small margin.
4. Restart the ingestion worker mid-stream: zero readings lost (ack implies stored).
5. Load test: 2,000 readings/sec for 10 sustained minutes: no errors, p95 alert latency ≤ 5s.
6. Restore yesterday's backup into a clean environment: platform serves yesterday's data.
7. A brand-new device with a fresh token produces real data on the dashboard in ≤ 15 minutes of work.
8. UI fully works in Arabic RTL and in English LTR.
9. **Proof of genericness:** a second template (e.g., "water tank: level + flow") can be added via config/seed data alone, its device sends readings, and its threshold rules alert — with no schema migration and no changes to core code.
10. **Starter Kit proof:** following only the written install guide, the reference sensor + gateway (05 §13) sends real readings through the production path and a real threshold alert (dashboard + Telegram) fires.

## 7. Explicitly NOT in MVP (out-of-scope checklist, from file 00)

Code changes from a previous unchecked extension; new hardware drivers; work orders; billing automation (invoicing handled manually); statistical/ML detection; SSO; mobile app; multi-protocol (Modbus/OPC UA); multi-region deployment; auto scaling in K8s; marketing site features beyond a landing page.

## 8. Next Step

- [ ] Review the numbers (NFR, thresholds, retention, quiet windows) — adjust what feels off
- [ ] Then → **03 — System Architecture** (the modular monolith map: modules, data flow, ADRs)
