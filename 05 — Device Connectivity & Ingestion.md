# 05 — Device Connectivity & Ingestion

**MQTT spec, Device Identity, Payload Schema, Validation Pipeline, Offline Handling**

*Blueprint v0.2 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file defines **how readings actually travel from a physical/virtual device into the database** — the contract between any device and the platform. 03 drew the pipeline; this file pins down the wire-level details that code and tests (09) implement.

Inputs: data model (04 §5 devices, `channel_definitions`), ingestion requirements (02 §3.3), architecture (03 §6, ADR-004 EMQX, ADR-005 RabbitMQ).

Design goals, in order:

1. **No customers-side engineering:** a new device connects in minutes with one credential.
2. **No lost data:** per FR-22/NFR-04 — the consumer acknowledges only after the DB commit, so nothing is lost across restarts; end to end the guarantee is at-least-once delivery + idempotent storage.
3. **No duplicate data:** idempotency on `(device_id, channel_key, time, seq)`.
4. **Bounded blast radius:** one rogue device cannot starve another tenant's ingestion (per-device auth, per-tenant topic prefix, quotas later).

## 2. Device Lifecycle (the onboarding story)

```
Tenant Admin creates asset (assets table)
  → registers device, choosing a device_type_template
  → platform issues ONE credential pair (deviceToken shown once, stored hashed)
  → Admin pastes credential into the device config (or a gateway)
  → device connects (MQTT or HTTP), sends first reading
  → device_status.last_seen_at updated; asset status becomes "healthy"
  → first real alert arrives when a rule triggers — target ≤ 15 min total (02 #7)
```

Rules:

- Credential is **shown once** at creation; re-issue = new credential + revocation of the old (`device_credentials_log`).
- One device ↔ one asset at MVP. A multi-channel device (e.g., a sensor box reporting temp + humidity + door) is one device with several channels via its template.
- Deleting/revoking a device stops ingestion immediately (broker revocation + `is_active=false` double-check).

## 3. MQTT Connectivity Spec

| Item | Value | Notes |
| --- | --- | --- |
| Broker | EMQX single node (ADR-004) | container at MVP |
| Protocol | MQTT 3.1.1 minimum, 5 preferred | 5's session/properties kept for the future |
| Listener | port **8883, TLS 1.2+** only; plain 1883 disabled in prod | NFR-07 |
| Auth mode | username = `deviceId`, password = `deviceToken` | EMQX HTTP auth plugin validates against our `assets` service (HMAC-SHA-256 lookup, result cached ~60 s, so a reconnect storm after an outage does not melt the CPU or the API) |
| Authorization | device may **publish only** to its own topic (ACL via same plugin); **no subscribe** at MVP | least privilege; command channel is V1+ |
| QoS | 1 for readings | at-least-once from device; duplicates handled downstream by idempotency |
| Keepalive | 60 s | broker drops silent connections; `telemetry.device_status.last_seen_at` is the truth for offline rules |

### Topic scheme (ADR-001 open point A, resolved)

```
t/{tenantId}/dev/{deviceId}/v1          ← readings publish (the only topic)
```

- The auth plugin **binds the device credential to its tenant at connection time**; publishing to a topic whose `tenantId`/`deviceId` don't match the credential is refused by broker ACL — not just app-validated. Cross-tenant publish is impossible at the network layer.
- Version segment (`v1`) lets the schema evolve without a coordinated queue downtime (see §5).

## 4. HTTP Ingest Endpoint (for gateways & HTTP-capable devices)

```http
POST /ingest/v1
Authorization: Bearer {deviceToken}
Content-Type: application/json
```

| Rule | Value |
| --- | --- |
| Max batch | 500 readings per call |
| Max body | 1 MB |
| Response | **202 Accepted** (enqueued), or 4xx with the error catalog (§6) |
| Idempotency | same key as MQTT; re-POST of the same batch never duplicates |
| Rate limiting | per device-token: default 30 req/min at MVP (platform quotas are per-tenant in V1) |

Why 202 semantics: the API Host publishes to RabbitMQ (ADR-005) and returns 202 only after the **publisher confirm** (durably queued); the Processing Host does full validation + store. (If the ADR-005 spike removes the queue, the endpoint instead replies 200 only after the DB commit.) A 4xx is definitive (the call was rejected before enqueue); a network failure after enqueue is safe **because the DB unique key makes retries harmless**.

## 5. Payload Schema v1

```json
{
  "v": 1,
  "ts": "2026-10-26T14:03:11.250Z",
  "seq": 14872,
  "rr": [
    { "t": 1450523881.3, "c": "temperature" },
    { "t": 0.62,            "c": "humidity" }
  ]
}
```

| Field | Meaning | Rules |
| --- | --- | --- |
| `v` | schema version | unknown higher `v` tolerated: unknown fields ignored (forward compatible); unknown channel key → rejected (see §6) |
| `ts` | event time (RFC3339 UTC) | device clock; must be ∈ [now−30 days, now+5 min]; outside → timestamps are sanitized (see §8) |
| `seq` | per-device monotonically increasing sequence | required for MQTT path uniqueness; HTTP-only gateways may omit it (stored as 0, so uniqueness = `(deviceId, channel, ts)` — coarse but workable) |
| `rr` | readings: `t` numeric value (booleans as 0/1), `c` channel key | channel must exist on the device's template (checked against `channel_definitions`); max 32 channels per message |
| batch form | `ts` per-batch; each reading may override with own `ts` if needed | |

Unit policy: the device sends **raw values in the template's declared unit** — no client-side conversion. Mismatch debugging is a template/analysis bug, not a wire format job.

## 6. Validation Pipeline & Error Catalog

Processing stages (both paths converge here):

1. **Credential validity** — issued, unrevoked, device active (broker already did this for MQTT; HTTP re-checks).
2. **Identity match** — topic deviceId == credential deviceId (MQTT), token → device row (HTTP).
3. **Structural validation** — schema shape, size limits, timestamp sanity bounds, seq sanity.
4. **Channel validation** — `c` ∈ device's template channels; value type numeric; not NaN/±Inf; reject sentinel junk (-999 etc. — template may declare known sentinels later).
5. **Assign/normalize:** resolve `channel_key`, fill `tenant_id`, `device_id`, `received_at = now()`.
6. **Store:** multi-row `INSERT ... ON CONFLICT DO NOTHING` into `readings` (not `COPY` — it cannot skip duplicates) → commit → **only now ack the broker message / mark the queue item processed** (NFR-04).
7. **Emit internal event** `ReadingsAccepted(batch)` → rules engine (06).

| Code | Meaning | Where | Disposition |
| --- | --- | --- | --- |
| `E401_AUTH` | bad/revoked token | both | MQTT: connection refused; HTTP: 401. Logged as device security event |
| `E403_TOPIC` | topic/device mismatch | MQTT ACL | connection refused + flag |
| `E422_UNKNOWN_CHANNEL` | `c` not in template | 4 | HTTP 422; MQTT: log + DLQ (see below) |
| `E422_RANGE` | NaN/Inf, absurd values | 4 | same as above |
| `E422_TS` | timestamp outside sane window | 4 | sanitized per §8, not rejected |
| `E429_QUOTA` | per-device rate exceeded | HTTP | 429 + backoff hint |
| `DLQ_INVALID` | structurally unparseable batch | queue consumer | RabbitMQ dead-letter with reason; never silently dropped |

MQTT path has no HTTP response channel — malformed-but-authenticated payloads are logged + dead-lettered per tenant/device for later reprocessing (stored raw first when recovery is plausible).

## 7. Durability Rules (condensed from 03 §6)

| Guarantee | Mechanism |
| --- | --- |
| Device → broker | QoS1 PUBACK from EMQX means the *broker* received the message — not that it is in Timescale. The device retries until PUBACK; configure EMQX session/message persistence deliberately and test a broker restart (09 §5) |
| Broker → DB | the consumer acks only after the DB commit; the shared subscription redelivers unacked messages ⇒ no loss across consumer/process restarts |
| HTTP 202 ⇒ durably queued | 202 only after the RabbitMQ publisher confirm (durable quorum queue); the consumer retries with backoff indefinitely; the DB unique key makes retries idempotent |
| Broker outage | device QoS1 persists pending messages; on reconnect the backlog floods in once (bounded by device OS buffers) |
| Queue bounded | max in-flight per tenant; overflow → 429 / backpressure rather than unbounded memory (FR per 02 NFR and 03 §6.3) |
| Nothing silently dropped | DLQ + metrics for every rejection reason (§6) |

## 8. Clock Discipline (event time vs server time)

- `received_at` (server) is recorded on every row — never trust device clocks alone.
- Device `ts` in the future by **> 5 min** → clamp to received time and log `E422_TS` (real GPS-less devices do this after power loss).
- Very old `ts` (> 30 days) → accepted but flagged — late-flood from a store-and-forward gateway (V1 feature).
- **Rules and dashboards evaluate on event time** (so offline/replayed data lands in the right hour), with a 60-second watermark tolerance before a rule closes its evaluation window.

## 9. Offline Detection & Field Reality

- `telemetry.device_status.last_seen_at` is updated on any accepted message (any path) — held in memory and flushed every 10–30 s, never one UPDATE per reading (04 §6).
- The Offline Watchdog (Processing Host) scans devices whose `is_active=true` and fires `OfflineDetected` per the device's offline rule (default 30 min, 02 FR-31).
- Known field devices lie: NAT timeouts, cellular blackouts, power cycles that reset sensors' clocks. The design tolerates all three via §8 + watchdog; write your test scenarios from exactly these (09).
- **Store-and-forward Edge Gateway is V1** (00): MVP devices that buffer & resend on reconnect rely on QoS1 persistence + idempotency — acceptable for offline gaps ≤ a few hours.

## 10. Simulator Contract (dev/test/demo — 02 §3.6)

The simulator is **just another device** — it authenticates with real device tokens over real MQTT (same path as production gadgets). This keeps the demo honest.

| Scenario | What it does | Proves |
| --- | --- | --- |
| `steady` | N devices, K channels, S readings/sec, stable values | baseline ingestion + dashboards |
| `spike` | value jumps past threshold then returns | threshold rule + dedup |
| `drift` | slow temperature climb over 40 min | duration & threshold window behavior |
| `flatline` | constant value | statistical gap (later), template sanity for now |
| `dropout` | device stops sending | offline watchdog → OfflineDetected alert |
| `warmup-demo` | the fridge-warming launch story | the 15-minute "asset → alert" demo (02 #7) |

## 11. Security Checklist for This Layer (links to 07)

- TLS-only MQTT listener; HSTS on HTTP endpoints.
- Tokens: 32-byte random, shown once, stored only as an HMAC-SHA-256 hash (never argon2/bcrypt — the token is a high-entropy secret; slow hashes only add CPU load and a reconnect-storm DoS vector), rotated per-device on demand, all events logged.
- EMQX management API is on an internal network only — never publicly exposed.
- No wildcard topics granted; ACL denies by default.
- Rejection logs are themselves rate-limited (a malicious device vomiting garbage cannot flood our logs).

## 12. Open Points in This File

| # | Point | Default |
| --- | --- | --- |
| D | Future command/RPC channel (config push, OTA) | Not in MVP (V1+); topic design reserved so EMQX scheme won't need breaking changes |
| E | MQTT 5 vs 3.1.1 only | Accept both; prefer 5 |
| F | Compact binary payload (protobuf) for tiny devices | Not MVP; JSON is debuggable; noted for heavy gateways later |
| G | Per-device MQTT session expiry grace | 0 (reconnect = fresh) at MVP |

## 13. Reference Starter Kit (v0.2)

Why this exists: the activation metric (01 §7.4) needs a real sensor, and a trial user who has no hardware cannot reach a real alert. One tested kit beats "bring any sensor".

| Track | What | Used for |
| --- | --- | --- |
| **A. DIY reference** (P2) | ESP32-class board + waterproof digital temperature probe (DS18B20-class), optional door reed switch. Firmware publishes payload v1 over MQTT/TLS with per-device credentials | Development, demos, the real-fridge test and Design Partner #1. A few dollars per unit; we control the firmware, so protocol fidelity is perfect |
| **B. Commercial off-the-shelf** (decision D13, 01 §10) | A sold logger or gateway that can publish to a custom MQTT/HTTPS endpoint | What customers can buy and install without us flashing firmware |

Selection criteria for track B (check spec sheets before choosing):

| Criterion | Why |
| --- | --- |
| Custom MQTT or HTTPS endpoint with TLS | otherwise it cannot talk to us at all |
| Configurable payload or a stable documented one | if it cannot emit payload v1, add a small per-profile payload adapter in Ingestion — the profile id lives on `device_type_templates`, so this stays configuration, not core code |
| Configurable sample interval + offline buffering | matches 05 §7 and §9 |
| Probe accuracy and calibration certificate | cold-chain buyers may need it for inspections |
| Local availability, warranty, price | the kit must be buyable and supportable where the customers are |
| Mains/battery behavior and Wi-Fi/LTE options | determines real-world install effort |

Deliverables for the kit: bill of materials with retail price; a one-page install guide (AR/EN) a non-engineer can follow in ≤ 15 minutes; a provisioning flow (token shown once, ideally as a QR code carrying broker host, device id and token); a troubleshooting table (Wi-Fi, TLS/clock, wrong topic); jittered reconnect backoff in the firmware; and acceptance check #10 (02 §6).

## 14. Next Step

- [ ] Review: does the payload schema & topic scheme look deployable for tooling you'll build?
- [ ] Then → **06 — Processing & Detection Rules** (the rules engine internals, alert lifecycle state machine, dedup/cooldown math)
