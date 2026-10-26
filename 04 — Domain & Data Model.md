# 04 — Domain & Data Model

**Tables, Schemas, the Generic Template Mechanism, and Multi-Tenancy (RLS) Design**

*Blueprint v0.2 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file turns the module map (03) into **concrete database design** owned by the TelemetryStore. It is the contract between the modules and the database; files 05–09 implement and test against it.

Two golden constraints:

1. **Type-agnostic model** (02 §3.2): no vertical concepts in the schema. "Refrigerator" is app-level config, not tables.
2. **Room ownership** (03 §4): each module owns its database schema. No cross-schema joins by any module's application code — cross-module data flows through contracts/events (the platform admin's read-only views are the one exception, built as contract-declared views).

## 2. Schema Layout (one database, four module schemas + telemetry)

```
nabd_db (PostgreSQL + TimescaleDB)
├── identity     → owned by Identity & Tenancy module
├── assets       → owned by Assets module
├── telemetry    → owned by TelemetryStore module (time-series)
├── rules        → owned by Rules module
├── alerting     → owned by Alerting module
└── notifications→ owned by Notifications module
```

ID strategy: **UUID v7** (time-ordered, app-generated) on all persistent entities — no hot auto-increment blocks on hypertables, no GUID v4 write pattern problems.

## 3. ER Overview

```
assets (assets, devices, templates)
     │  tenant_id on every tenant-scoped row (RLS key)
     ▼
assets ── devices ── device_types ── device_type_channels (the template mechanism)
  │          │
  │          └── readings (hypertable: time-series, kept in telemetry schema)
  └── rules ── alerts ── alert_events
```

## 4. Identity & Tenancy Schema

| Table | Key columns | Notes |
| --- | --- | --- |
| `tenants` | id, name, slug, status(active/disabled), plan ref, created_at | plan ref is a string at MVP ("starter"/"pro"/"business") — real billing plan FK arrives in V1 |
| `users` | id, tenant_id (FK), email (unique), password_hash, display_name, preferred_locale(**ar/en**), status | one tenant per user at MVP |
| `roles` | code (**viewer/tenant_admin/platform_admin** — only 3, check constraint), description | static seed |
| `user_roles` | user_id, role_code, tenant_id | composite PK |
| `refresh_tokens` | user_id, token_hash, expires_at, revoked_at | plain revocable sessions |

## 5. Assets Schema — including the Template Mechanism

This is the heart of the "generic vertical" design. Nothing here knows what a fridge is.

| Table | Key columns | Notes |
| --- | --- | --- |
| `assets` | id, tenant_id, name, location, notes, status, archived_at | type is display + template ref |
| `asset_type_templates` | id, tenant_id NULL, key (e.g., **fridge**, generator, tank), name_en, name_ar | **NULL tenant_id = global seed (built-in templates)**; tenant rows = custom overrides |
| `channel_definitions` | id, template_id FK, channel_key (e.g., 'temperature'), data_type (numeric/boolean), unit (°C, %, rpm...), name_en, name_ar, default_min, default_max | **the allowed channels for a template** — reading keys not listed here get rejected at ingestion |
| `device_type_templates` | id, template_id FK, name, mqtt_topic_pattern, sample_rate_hint | what makes a physical device a "type" |
| `devices` | id, tenant_id, asset_id FK, device_type_template_id FK, name, **token_hash**, token_issued_at, **token_revoked_at**, is_active | token never stored plain — only a keyed fast hash (HMAC-SHA-256 with a server-side pepper; the token is a 32-byte random secret, so a slow password hash adds CPU cost and a DoS vector, not security). Revocation = one timestamp. Last-seen is **not** stored here — see `telemetry.device_status` (§6) |
| `device_credentials_log` | device_id, action (issued/revoked), actor, at | audit trail for security reviews |

**How a new vertical happens (the acceptance proof, 02 #9):**

```sql
-- 1. template
INSERT INTO asset_type_templates (tenant_id, key, name_en, name_ar) VALUES (NULL,'tank','Water Tank','خزان ماء');
-- 2. channels
INSERT INTO channel_definitions (template_id, channel_key, data_type, unit, ...)
VALUES (:template_id, 'level', 'numeric', '%'), (:template_id, 'flow', 'numeric', 'L/min');
-- 3. sensible default rule presets (seeded per template, editable per asset)
-- DONE. No migration ran. No core code changed.
```

## 6. Telemetry Schema — the Time-Series

| Table | Type | Columns | Notes |
| --- | --- | --- | --- |
| `readings` | **hypertable**, chunked by 1 day | `time` (event time UTC), `received_at`, `tenant_id`, `device_id`, `channel_key` (text, FK conceptually to channel_definitions — no physical FK on hypertables), `value` (double precision; booleans stored as 0/1), `seq` (device sequence, **NOT NULL DEFAULT 0** — 0 means the device sent none; NULLs would defeat the unique key because NULL ≠ NULL) | the one big table |
| `readings_hourly` | **continuous aggregate** | min/max/avg/count per (tenant, device, channel, hour) | dashboards' long-range reads |
| `readings_daily` | continuous aggregate | same, daily | month/year views at negligible cost |
| `device_status` | regular table (telemetry schema, owned by TelemetryStore) | device_id PK, tenant_id, last_seen_at, last_event_time | last-seen is held in memory and flushed every 10–30 s — avoids one hot-row UPDATE per reading; the offline watchdog decides freshness from the engine's **in-memory** last-seen (authoritative between flushes), and reads this table only after a restart — flush delay must not count against the detection-time budget (09 §3) |

**Key policies (Timescale features, configured not code):**

| Policy | Setting (initial, sharpened by 09 tests) |
| --- | --- |
| Unique constraint | `(device_id, channel_key, time, seq)` — the idempotency key (FR-23). Duplicate insert = stored once. The key must contain `time` (hypertable rule). Writes use `INSERT ... ON CONFLICT DO NOTHING` — `COPY` cannot skip duplicates. P0 spike: confirm behavior on compressed chunks |
| Primary index | `(tenant_id, device_id, channel_key, time DESC)` — all history queries filter these four |
| Compression | enabled after 7 days (~90% size cut typical) |
| Retention | raw readings kept **180 days**, then dropped automatically. Aggregates survive forever |

**Sizing sanity check** (why 2,000 msg/sec is modest): one customer with 100 assets × 3 channels × one reading/15s ≈ **1.7M readings/day ≈ 20/sec**. Their entire year ≈ 620M rows, ≈ **under 20 GB compressed** on Timescale. The MVP box handles dozens of such tenants.

## 7. Rules Schema

| Table | Key columns | Notes |
| --- | --- | --- |
| `rules` | id, tenant_id, device_id FK, channel_key, **rule_type**: `above` / `below` / `outside_range` / `offline`, threshold_low, threshold_high, **duration_seconds** (must persist X sec to fire — kills spike noise), **cooldown_minutes** (default 60), severity (info/warn/critical), enabled | user-editable, seeded from template defaults (FR-15) |
| `rule_eval_state` | rule_id PK, last_state (normal/violating), violating_since, last_evaluated_at | the engine's memory across restarts; written on state transitions only (NORMAL↔VIOLATING, fired) plus a periodic checkpoint — never once per reading (06 §3.4) |

> Offline rules have no channel; they hang off the device, using `offline_after_minutes` and the `telemetry.device_status.last_seen_at` clock.

## 8. Alerting Schema

| Table | Key columns | Notes |
| --- | --- | --- |
| `alerts` | id, tenant_id, asset_id (denormalized from rule), rule_id FK, **type** (threshold/offline), severity, **status**: open → acknowledged → resolved, opened_at, resolved_at, title (JSONB, per-locale strings), detail (JSONB: the reading value, etc.) | **alerts belong to the asset, not the reading** (FR-33) |
| `alert_events` | alert_id FK, event (opened/ack_by/ack_at/resolved/resolved_by), at | full lifecycle audit trail |
| `notification_log` | alert_id, channel (email/telegram/dashboard), recipient, status (sent/failed), sent_at | retry bookkeeping |

**Dedup + cooldown logic (FR-32, in the Alerting module, not the DB):** before raising, look up any open alert with same `(asset_id, rule_id)`; if the last alert was closed less than `cooldown_minutes` ago, update it instead of creating a new one (or suppress silently, per severity). This is a small state machine — code owns it.

## 9. Notifications Schema

| Table | Columns | Notes |
| --- | --- | --- |
| `channel_config` | platform-level SMTP host/port/user and Telegram bot token (encrypted), sender name | MVP: one platform SMTP. Per-tenant config in V1 |
| `message_templates` | key (alert_raised/offline...), channel (email/telegram), locale (ar/en), subject (email only), body | bilingual, per 00 D3 |
| `user_channels` | user_id, tenant_id, channel (telegram), external_id (chat id), verified_at, severities (JSON) | opt-in; the one-time link code is stored hashed and expires in 10 min (FR-38) |

## 10. Multi-Tenancy Enforcement (RLS Design)

Defense in depth, per NFR-06 and 02 acceptance #1:

**Layer 1 — Application:** every module's repository adds `WHERE tenant_id = :current`. No exceptions, checked by code review + tests.

**Layer 2 — PostgreSQL RLS (the safety net):**

```sql
ALTER TABLE assets ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON assets
  USING (tenant_id = current_setting('app.current_tenant')::uuid);
-- same pattern on every tenant-scoped table in every schema
```

- The app sets `SET LOCAL app.current_tenant = '<uuid>'` at the start of each transaction (per-request, works with pooling).
- The database **rejects** any query that forgets the filter — even a raw SQL mistake in the app cannot cross tenants.
- **Platform Admin** connects with a separate `admin` DB role that holds `BYPASSRLS`. That connection is logged separately and only used by the Admin module — this is the audited exception, not a backdoor.
- **Ingestion writer role (v0.2):** one batch can contain several tenants, and `SET LOCAL app.current_tenant` is per transaction. Either group each batch by tenant (one transaction per tenant) or write through a dedicated `ingest_writer` role that can only INSERT into `telemetry.readings` / `device_status` and bypasses RLS — with `tenant_id` taken from the authenticated device row, never from the payload. State plainly that RLS does not protect this one write path; the device-credential binding does.
- **RLS verification spike (P0, v0.2):** before relying on the policies, test them against (a) compressed hypertable chunks, (b) continuous aggregates — policies on the base table do not automatically cover materialized aggregate data, so give the aggregates their own tenant filter (a policy or a tenant-filtered view) and test it — and (c) batch inserts under the writer role. Treat any gap as a P0 blocker.
- Isolation test suite (CI + staging): a viewer token of tenant A tries to read device/readings/alerts of tenant B via every endpoint — must fail, including guessed IDs.

**Why this beats "just be careful in SQL":** one forgotten WHERE clause in one report endpoint is how tenant data leaks in real SaaS products. The database makes that bug impossible instead of unlikely.

## 11. Data Lifecycle

| Stage | What happens |
| --- | --- |
| Arrival | validated at ingestion → batch-inserted to `readings` → the consumer acks the broker/queue message only after the commit |
| 0–7 days | uncompressed hot chunks, raw queries, aggregates refreshing |
| 7+ days | chunk compressed (≈90% smaller) |
| 180 days | raw chunk dropped; aggregates remain |
| Exit | tenant requests export → CSV per module tables; delete → tenant row + all tenant-scoped rows (FK cascade) + readings cleanup job |

## 12. Design Notes & Rejections (short)

- **Why `channel_key` text instead of a channel_id FK on the hypertable:** a reading row shouldn't need a join to validate its channel at write time; ingestion verifies against the template in memory, and keeping the row self-contained keeps batch writes fast. A broken key is rejected at ingestion, so the table can't contain orphans.
- **Why JSONB for alert title/detail:** bilingual text + variable shape per alert type beats a fixed-column explosion for MVP; vote to normalize if V1 analytics ever needs it.
- **Why no foreign keys from `readings` to devices:** hypertable join constraints are heavy; integrity is enforced at the API/validation layer, plus periodic integrity checks. This is the timeseries trade-off worth accepting.
- **Why device tokens are hash-only:** database dumps (backups) must never leak usable credentials (NFR-07).

## 13. Next Step

- [ ] Review the tables — especially whether the template mechanism (§5) clears acceptance test #9
- [ ] Then → **05 — Device Connectivity & Ingestion** (MQTT topics, payload schema, device onboarding, offline handling)
