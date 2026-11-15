# P3 Spec 09 — First Sustained Load Run (plan/09 §4.1 → §9/H row 1)

**Date:** 2026-11-15 · **Executed by:** agent, on the owner's recorded delegation
**Environment (ruling A):** `env: dev-laptop (11 GB RAM, containers capped: postgres 1 GiB, emqx 1 GiB)`
**Scenario:** Sustained — 100 devices × 20/s + 20 devices × 1/s (target 2,000 readings/s), 600 s, NO rider (row 1 pure; the duplicate-flood rider is row 6, a separate scaled run).

## Row 1 — Sustained, official run #2 (run #1 voided by the boot-race bug, see §Fix below)

| Metric | Value |
| --- | --- |
| Window | 631 s (600 s fleet + 31 s settle) |
| Published (PUBACKed, unique — no rider) | **1,169,003** (≈ 1,852 msg/s offered) |
| Fleet sim errors | **0** (ingress clean end to end) |
| **Stored rows** | **85,730 → ≈ 136 rows/s** |
| Ingested == stored | ✅ 85,730 == 85,730 (the pipeline loses nothing internally) |
| raw_rejects | 0 (zero E422/E429 — validation clean under load) |
| DLQ growth | 0 |
| Lost unique (broker-side) | **1,083,273 — 92.7% of published** (EMQX consumer-session queue drops: 976,376 counted + 106,897 counter-attribution gap, see §Counters) |
| Latency of STORED rows (received_at − time) | p50 0.69 s · p95 0.78 s · p99 2.45 s |
| ProcessingHost RSS | 60,392 → 93,664 kB (+33.3 MB / 10 min — grew, modest; watch in Soak) |
| Health `/health/ready` | up, `rejected:{}` empty |

### Verdict vs NFR-01 (2,000 readings/s end-to-end): **NOT MET**

Measured sustained consumer capacity: **≈ 136 rows/s — a 14.7× gap** to the 2,000/s target.
The capacity law (drill 2, plan/09/P6 ledger) was live at scale: the broker absorbs offered load up to
`max_mqueue_len / rate`, then drops overflow — 92.7% of the offered fleet never reached the pipeline.

**Honesty notes on the numbers above:**
- Latency percentiles are **collapse-censored**: they describe only the 7.3% that got through; the dropped 92.7% have no latency.
- The pipeline's own §4.1 criteria (0 rejected; ingested == stored; no DLQ growth) are all ✅ — the failure is **pure consumer capacity vs broker queue overflow**, not validation or storage.

## The bottleneck (design-level finding, confirmed twice: pilot 90 s + official 600 s)

The consumer processes messages **sequentially, one MQTT delivery at a time**:
per message ≈ 2 door lookups (0014/0016 SECURITY DEFINER) + 1-row INSERT + manual ack ≈ **~7.3 ms**
(= 1/136 s). EMQX offers ~1,852 msg/s; the session mqueue (cap 1,000) fills in ~0.6 s and drops the rest.

## The nominated lever — ONE (plan/09 §4.2 lever 1)

**Cross-message batching in the sink with commit-synchronized acks.**
Accumulate deliveries into batches (the batch store already chunks at 500 — spec 04 measured 1.57 ms
per conflict-free batch vs 7.65 ms with conflicts), INSERT per chunk, and **ack only after commit**.
Per the ack law this is a redesign of the delivery path, not a knob: it becomes its own spec
(P4 candidate) — never a mid-run tune. Row 1 stays exactly as measured.

## §Counters — attribution honesty

`delivered_msgs + dropped_msgs` (consumer session) = 1,062,106 vs 1,169,003 published → **106,897 (9%) unattributed**.
One silent boot-window reconnect is in the run's own log (the re-ask fix working: `DENIED … re-asking` → subscribed ×2);
EMQX per-connection counters reset on resume, and route-level `no_subscriber` drops are global, not session-attributed.
Global drop counters could not be read post-mortem (EMQX restarted for the rider run). Verdict unaffected: stored rate and the 14.7× gap are DB-side facts.

## Row 6 — Duplicate-flood rider (separate scaled run, 15:08–15:36)

**Shape:** 10 slow devices @ 1 unique/s each, `--dup 10` → 10 copies/s per device (~100 copies/s offered ≈ 73% of the row-1 unique-message capacity). Fleet duration ran ~27 min instead of the planned 10 (rider orchestrator lacked its kill step — script bug, fixed for future runs); the run reached a clean steady-state equilibrium, so the extra duration strengthens rather than invalidates it.

| Metric | Value |
| --- | --- |
| Published (PUBACKed) | **80,980 copies / 8,098 unique** (10:1 exactly — `--dup` semantics verified under load) |
| **Stored rows** | **7,498 — count(*) == count(DISTINCT identity key) == 7,498 — ZERO duplicate rows** |
| EMQX session drops | **0** (global `delivery.dropped.*` also all zero) |
| Copy throughput (consumer) | ≈ **40 copies/s sustained** vs 136/s for all-unique messages (row 1) |
| Latency of stored rows | p50 0.06 s · p95 0.23 s · p99 0.37 s · max 0.7 s |
| raw_rejects / DLQ | 0 / 0 |

### Row 6 verdicts (plan/09 §4.1)

- **Idempotency: MET.** 10 copies of every message; exactly ONE row per unique reading in the DB. The identity key held under 27 minutes of continuous 10× replay.
- **No throughput collapse: MET.** The consumer held ~40 copies/s without degradation across the whole run.
- **The ON CONFLICT path is measured:** conflict-heavy copies drain at **≈ 40/s vs 136/s all-unique — the conflict path costs ≈ 3.4× per copy (~25 ms vs ~7.3 ms)**, consistent with spec 04's batch-level ratio (7.65 vs 1.57 ms). This strengthens the batching lever: batching amortizes BOTH the lookups AND the conflict cost.
- **Zero drops, and the mechanism is now understood:** EMQX does not drop below its acceptance rate — it throttles publishers by delaying QoS1 PUBACKs (the sims' effective rate fell to match the consumer's drain pace). Drops (row 1: 976,376) occur only when the offered burst exceeds the session's `max_inflight(32) + max_mqueue_len(1000)` window faster than the consumer drains — exactly the capacity law from drill 2.

**Honesty notes:** (a) latency percentiles are again collapse-censored — the queueing showed up as sim-side publish pacing, not stored-row latency. (b) 600 unique (7.4%; the final minute, in flight at fleet kill) were PUBACKed but never dispatched: delivered froze at 74,980 == stored×10 exactly, mqueue empty, inflight 0, zero drop counters. Broker-side PUBACK→dispatch attribution phantom — same family as row 1's 106,897 gap (9%), smaller and cleanly bounded. Flagged for a future EMQX-semantics drill; it does not touch the pipeline's own guarantees (our ack law covers PUBACK→stored only in the consumer→DB direction).

## §Fix — what run #1 caught (worth the void)

Run #1 (14:35–14:46) stored **zero**: a docker-daemon restart brought EMQX up before the API host;
the authz webhook door was `resource not_connected` when the consumer subscribed → EMQX answered
`NotAuthorized` → the old code logged "will retry" and **held a connected-but-unsubscribed client** —
EMQX dropped all 1,169,003 messages as unroutable. A subscribe denial does not kill the connection,
so spec-07's `IsConnected` fix could not see it.
**Fix (third sibling of the spec-07 family):** the grant is re-asked until given (`774c1a1`), live-witnessed:
EMQX-first boot order → `subscribed` → 11 published → 11 delivered → 11 stored. The orchestrator also
gained a **subscribed-before-fleet pre-flight gate** (run #2's own log shows the re-ask working).
