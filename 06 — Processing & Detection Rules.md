# 06 — Processing & Detection Rules

**The Rules Engine, Alert Lifecycle, Deduplication & Cooldown**

*Blueprint v0.2 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file defines **how readings become alerts** — the Rules module plus the Alerting module internals. The wire contract (05 §5–6) ends with the internal event `ReadingsAccepted(batch)`; everything downstream of that is spec'd here.

Inputs: requirements (02 §3.4 FR-30…37), data model (04 §7 rules, §8 alerts), architecture (03 §6.1–6.2, ADR-006 in-process events).

The philosophy from 00 applies in full: **simple before smart.** The MVP engine is: fixed thresholds + offline detection, both well-tuned. Z-score/EWMA statistical detection is V1 (FR-36); ML is V2. Nothing in this file depends on them, and nothing here closes the door on them (rules engine reads a typed config, new rule types are additive).

## 2. The Processing Pipeline (inside the Processing Host)

```
ReadingsAccepted(batch)
      │
      ▼
┌─────────────────────┐
│ Rules Engine        │  per (device, channel): evaluate open rules
│  - stream answers   │──▶ CrossingDetected(rule, reading)
│  - duration check   │
│  - auto-resolve     │──▶ ConditionRestored(rule)
└─────────────────────┘
      + (separate loop)
┌─────────────────────┐
│ Offline Watchdog    │  every 30s: devices past their quiet window
└─────────┬───────────┘──▶ OfflineDetected(device)
          ▼
┌─────────────────────┐
│ Alerting (state     │  dedup + cooldown + state machine
│ machine)            │──▶ AlertRaised (or AlertUpdated)
└─────────┬───────────┘
          ▼
   Notifications (email + Telegram)  +  Realtime (SignalR)  +  dashboard
```

Everything above runs in **one Processing Host at MVP** (ADR-006). Static modules communicate via in-process events — but the *architecture* allows 06's two loops to become separate workers later without changing their logic (they only consume the same events).

## 3. Threshold Rule Evaluation (FR-30, FR-31)

### 3.1 The state machine per rule

Each rule carries a persisted evaluation state (`rule_eval_state`, 04 §7):

```
states: NORMAL ⇄ VIOLATING(violating_since)
```

### 3.2 Evaluation algorithm (per batch, per rule within scope)

```text
for each reading r in batch where r.channel == rule.channel:
    if r.time > state.last_evaluated_time:          # ignore out-of-order stragglers for now
        state.last_evaluated_time = r.time

    violating = match(rule.rule_type, r.value):     # above / below / outside_range
    if violating and state is NORMAL:
        state → VIOLATING, violating_since = r.time  # firing is deferred to duration check
    if violating and state is VIOLATING:
        if (r.time − violating_since) ≥ rule.duration_seconds:
             emit CrossingDetected(rule, r)          # only when not state.fired (once per episode); Alerting applies dedup/cooldown
             state.fired = true   # persist this transition (see 3.4)
    if not violating and state is VIOLATING:
        if (r.time − violating_since) ≥ duration_seconds:
             emit ConditionRestored(rule, r)         # may auto-resolve, see §5.3
             state → NORMAL
        else:
             state → NORMAL            # brief spike: never fired, silently cleared
```

Design consequences — worth understanding, they explain half the tuning knobs:

- **`duration_seconds` (default 60) is the noise killer.** One packet of hot readings from a door opening never fires; a fridge *staying* over 8°C does. This is exactly the "false alert rate drops weekly after tuning" metric from 00.
- **A spike that clears before duration expires is not an alert** — it silently resets. No email for door-opening events.

### 3.3 Out-of-order readings

Devices reconnecting resend batches (QoS1, clock drift). Out-of-order readings are **stored** (history correctness) but **do not re-trigger fired rules** (no alert thrash), except the final-resolution case in §3.2. Watermark: a rule's evaluation window closes after `max(time) − 60s` (05 §8); older stragglers only update history and aggregates.

This behavior is deterministic and tested with the simulator's replay scenarios (09).

### 3.4 State Persistence & Write Rate (v0.2)

At 2,000 readings/sec, writing `rule_eval_state` on every reading would add 2,000 DB writes per second for no benefit. The rules:

- Rule state lives **in memory** (loaded from `rule_eval_state` at startup).
- It is persisted **only on transitions** (NORMAL → VIOLATING, fired, VIOLATING → NORMAL) plus a periodic checkpoint (every 60 s).
- `last_evaluated_time` is *not* persisted per reading. After a restart the engine may re-evaluate readings inside the watermark window (05 §8); because Alerting dedups on `(asset, rule)`, a re-evaluation cannot create a duplicate alert.
- The `fired` flag is what stops the engine from emitting `CrossingDetected` on every violating reading once the duration has elapsed: one emission per violation episode, re-armed only when the state returns to NORMAL.

## 4. Offline Detection (FR-31, 05 §9)

- **Watchdog loop (every 30s)** over `devices where is_active`:
  - `now − telemetry.device_status.last_seen_at > rule.offline_after_minutes` and rule enabled → emit `OfflineDetected`.
  - Device sends again → emit `OnlineRestored`.
- **Offline alerts auto-resolve** when the device returns — no human needs to close them. Resolution event carries the true gap duration (`last seen → first seen again`).
- Offline rules live per device (not per channel). Default `offline_after_minutes = 30`, Tenant Admin editable per device. A fridge default of 15 min and a weekly-report gateway of 6 h are both legitimate — no uniformity demanded.

## 5. The Alert Lifecycle (Alerting module — the state machine)

### 5.1 States

```
            Acknowledge            Resolve (manual or auto)
  OPEN ─────────────────▶ ACK ─────────────────────▶ RESOLVED
    ▲                                                    │
    └────────────── re-raise (same key, cooldown spent) ◀─┘
```

| Transition | Trigger | Effects |
| --- | --- | --- |
| OPEN | `AlertRaised` | notification channels fire (email + SignalR + dashboard) |
| OPEN→ACK | user action (Viewer+) | `alert_events` records who/when |
| ACK→resolved or OPEN→resolved | user action, or auto (§5.3) | resolved reason documented |
| CLOSED→re-OPEN | `AlertRaised` for same key after cooldown spent | **upserted on the same alert row** (04 dedup design) with a new `alert_event`, so one asset+rule = one story thread, not N fragments |

### 5.2 Deduplication & cooldown math (FR-32)

Key = `(tenant_id, asset_id, rule_id)`. Before publishing a new alert:

```text
existing = open-or-recent(key, lookback = cooldown_minutes)
if none            → create OPEN alert, notify
if open            → update detail (latest value), DO NOT re-notify
if just-resolved within cooldown → attach event to that alert, DO NOT re-notify
if cooldown spent  → re-raise per table above
```

Defaults: `cooldown_minutes = 60`, severity `critical` = cooldown × 0.5 (critical problems nag sooner), Tenant Admin editable. The point is: **one asset cannot spam a maintenance engineer 30 emails per hour; the engineer sets the risk appetite.**

### 5.3 Auto-resolution

| Rule kind | Auto-resolve condition |
| --- | --- |
| Threshold | `ConditionRestored` while alert is open → resolve automatically ("value back under limit") |
| Offline | `OnlineRestored` on the device → resolve automatically, gap quantified |
| Manual close is always possible regardless |

### 5.4 Mute windows (FR-35)

Per rule or per asset: tenant-scheduled quiet ranges (e.g., planned maintenance Sat 14:00–18:00). Inside a window, threshold crossings are still **recorded as muted events (visible in history), but no notifications are sent**. At MVP, mute applies to threshold rules only — offline detection always runs, so a faked maintenance window cannot hide a dead device. Simple and honest.

## 6. Severity & Prioritization

| Severity | Default source | Notification treatment |
| --- | --- | --- |
| `info` | rule author choice | dashboard only |
| `warn` | most threshold rules | dashboard + email + Telegram |
| `critical` | rule author choice (e.g., freezer line) | email + Telegram, cooldown halved |

V1 adds per-channel severity routing (SMS/WhatsApp/webhook) and digest emails. MVP keeps exactly this table — 3 severities, 3 channels (dashboard, email, Telegram).

## 7. Alert Storm & Platform Protection (from the risks table in 01/telemetry notes)

| Threat | Sent defense |
| --- | --- |
| Broken rule spams thousands of alerts | dedup key + cooldown caps notifications at `1/cooldown` per key; per-tenant alert-rate counter in Admin module flags anomalies (NFR-62 quota hook) |
| Nothing silently dropped | even suppressed crossings are logged in `alert_events` with reason = dedup/cooldown — auditability over politeness |
| Engine lag under flood | bounded queues between stages (03); shed excess with counters, never block ingestion critical path (NFR-01) |

## 8. Configuration Defaults (single source of truth)

| Config | Default | Where editable |
| --- | --- | --- |
| Threshold `duration_seconds` | 60 | per rule |
| Threshold `cooldown_minutes` | 60 (critical: 30) | per rule |
| Offline `offline_after_minutes` | 30 | per device-offline rule |
| Auto-resolve | on | global (V1: per-tenant) |
| Mute windows | none | per rule (threshold only, MVP) |
| Severity default | `warn` | per rule |
| Watermark tolerance | 60 s | platform constant (not exposed) |

## 9. Pseudo-Code Skeletons (for 07 implementation & review)

```csharp
// RulesEngine (Processing Host) — concept only, code in repo will be cleaner
async Task OnBatch(ReadingsBatch batch)
{
    foreach (var r in batch.Readings)
    {
        foreach (var rule in rules[r.DeviceId].OnChannel(r.ChannelKey))
        {
            var outcome = Evaluate(rule, r, state(rule));   // §3.2 machine
            if (outcome == Crossing)  _alerting.HandleAsync(new CrossingDetected(rule, r));
            if (outcome == Restored)  _alerting.HandleAsync(new ConditionRestored(rule, r));
        }
    }
}

// Offline watchdog — independent loop, every 30s
async Task WatchdogTick()
{
    var now = _clock.UtcNow;
    foreach (var (device, rule) in _offlineRules.All())
    {
        var gap = now - device.LastSeenAt;
        if (gap > rule.AfterMinutes) _alerting.HandleAsync(new OfflineDetected(device, gap));
        if (device.SeenRecently())   _alerting.HandleAsync(new OnlineRestored(device));

    }
}
```

Review points for the maintainer (you), not the agent: deterministic replay, no hidden I/O inside `Evaluate`, state persists in DB (`rule_eval_state`) not RAM — restart mid-flood must not lose the VIOLATING-since state.

## 10. What This Engine Deliberately Does NOT Do (MVP)

- No machine learning, no baseline statistical models (V1 Z-score/EWMA per FR-36).
- No multi-condition rules ("temp high AND humidity high") — V1 candidate if usage data demands.
- No per-device anomaly fingerprinting.
- No SMS/WhatsApp/webhooks (V1) — the MVP channels are dashboard, email and Telegram — and no escalation chains.
- No cross-asset correlation ("all fridges failing together = HVAC problem") — noted as a future feature candidate, not claimed anywhere in the MVP.

## 11. Observability Hooks (feeds 09 & 08)

| Metric | Why |
| --- | --- |
| `rules.evaluated_total`, `rules.crossing_detected` per rule_type/tenant | false-alert-rate tracking (00 target) |
| `alerting.notified_total`, dedup-suppressed, cooldown-suppressed | proves the noise-killer works |
| `watchdog.device_offline`, `device_online_restored` | heaviest loop of the MVP, needs its own watchdog |
| `processing.batch_lag_seconds` | p95 ≤ 5s alert latency proof (NFR-02) |

## 12. Open Points in This File

| # | Point | Default |
| --- | --- | --- |
| H | Alert re-notification cadence for never-acked critical alerts | 24h digest only (V1 candidate) |
| I | Alert retention (`alerts` rows) | unresolved issues in archive; alerts kept 400 days (aligns with 180-day raw data retention) |
| J | EventBus in-process implementation | plain channel/MediatR-free event buses — no framework |

## 13. Next Step

- [ ] Review the state machines — this is the part where marketing demo and real users collide; argue with the defaults in §8
- [ ] Then → **07 — Platform & Security** (auth surfaces, token/secret handling, encryption, admin isolation, security review checklist)
