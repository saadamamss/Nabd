# P0 Spike Verdict — EMQX persistence (what the broker REALLY promises)

**Date:** 2026-10-29 · **Spec:** `phases/P0/04-spike-emqx.md` · **Evidence:** `docs/runbook/p0-spikes/emqx-persistence.proof.md` (Parts 1 + 2, all outputs)
**Authority tested:** NFR-04 (plan/02), durability rules (plan/05 §7), risk T10 (plan/11)
**Environment:** EMQX **5.8.6** OSS (container `nabd-emqx`, dev compose) · topic `spike/room` · client `spike-sub`
**Method note:** the result was **challenged by the owner and re-verified with a five-experiment battery** (Part 2): replication, a no-restart control, sanity, config introspection, retained re-probe. The verdict below survived the battery and cites it.

## Hypothesis (bet on record before the test)

0/5: with default config, EMQX keeps queued messages for offline persistent subscribers **in RAM only**; a broker restart destroys session + queue + retained memories. (Owner's thesis, in the owner's words after the fact: *"QoS2 controls the transit, not the store."*)

## What we ran (15 min + the challenge session)

Registered subscriber `spike-sub` with a persistent session (`-q 1 -c`, verified broker-side: `clean_start=false`, `session_expiry_interval=7200`, `subscription=spike/room`), disconnected it, published 5 QoS1 messages (all PUBACK'd), watched `enqueued_msgs=5`, then `stop`/`start` of the container, then reconnected and counted. Positive control added for retained messages (delivered before any restart).

## The answers

### Q1 — Do persistent sessions survive restart? ❌ dies

After restart: `emqx ctl clients list` → `No clients.` On reconnect the broker issued a **brand-new session** for the old client id (`created_at` ~8.5 min later than the original registration). "Reconnected" ≠ "session restored". (Also measured: "persistent" is bounded — `session_expiry_interval=7200` = 2 h grace, after which the broker deletes the session anyway.)

### Q2 — Do queued QoS1 messages for an offline subscriber survive? ❌ dies — **0/5**, with the decisive control

- 5 messages PUBACK'd while offline, `enqueued_msgs=5` counted before restart.
- Reconnect after restart → **0 of 5 arrived** (30 s QoS1 listening window, nothing).
- **The control (E2):** identical flow *without* the restart → **6/6 delivered on reconnect** (the 5 offline-queued + 1 retained), `enqueued_msgs=6` arithmetic closing exactly.
- **Inference:** the queue mechanism works perfectly while the broker lives; the restart is the *sole* cause of loss. Replication (E1) with a different client id: 0/5 again. Non-persistent sanity (E3): 0 without restart, as expected.

### Q3 — Do retained messages survive? ❌ dies (positive control included)

Fresh retained publish → delivered immediately on subscribe (baseline ✅) → restart → fresh subscribe → nothing. Bonus anomaly logged (not verdict-changing): after one restart `retainer info` counter reported 3 while nothing was deliverable; the counter reconciled to 0 after the next restart — retainer metadata apparently outlived the RAM payloads; deep-dive deferred (P1/P3).

### Q4 (added) — Why: is persistence machinery even running? — present but OFF

Startup logs: zero hits for `durable|persist|rocksdb|retainer`. `grep durable etc/emqx.conf` → none. The durable-storage library (`emqx_durable_storage-0.4.4`) **is present in the build** — meaning the capability exists and is switched off, not absent. We measured the *absence of configuration*, not the *cost of enabling it*.

## VERDICT — the NFR-04 wording, old → new

**OLD (plan/02 NFR-04):**
> Zero loss across process/broker restarts: a reading acknowledged by the platform's consumer is already committed. The device→broker leg is at-least-once (QoS1 + device retry); on host/disk loss the loss window equals the backup RPO (08 §5)

**NEW — amend to:**
> Zero loss across process/broker restarts: a reading acknowledged by the platform's consumer is already committed. The device→broker leg is at-least-once (QoS1 + device retry); **measured (P0 spike, `docs/runbook/p0-spikes/emqx-persistence.md` on EMQX 5.8.6 OSS default): the broker keeps queued offline-subscriber messages in RAM only — a broker restart loses them (0/5) while a control without restart delivered 6/6. Durability across broker restarts is therefore provided by the DEVICE (retry/store-and-forward), not the broker, under default config; enabling EMQX `durable_sessions` is a deliberate P3 config decision to be evaluated (exists in the build, off by default), never assumed.** On host/disk loss the loss window equals the backup RPO (08 §5)

## Consequences

1. **Device-side doctrine (plan/05 §7) CONFIRMED by measurement** — row 1 "configure EMQX session/message persistence deliberately" stays, now with the measured default: RAM-only, restart = loss window for offline-device messages.
2. **P3 config decision recorded:** evaluate `durable_sessions` honestly (capability present in 5.8.6 build, disabled; we did not measure its cost over-head/RPO). Prerequisite discovered: dashboard REST basic-auth rejected; `.env` dashboard password empty — **set a real dashboard password (P3 checklist)**; CLI `emqx ctl` used throughout this spike.
3. **plan/09 §5 restart-chaos test:** expected numbers updated to measured counts — offline-queued loss default 0/5; the test's PASS criteria must follow the new NFR-04 (device-side durability), not broker-side.
4. **Risk T10 (plan/11)** downgraded from "unmeasured assumption" to "measured, doctrine adjusted": QoS1 PUBACK = "broker received", nothing more.

## Lab residue (disposable)

Spike-only topic `spike/room`, clients `spike-sub`/`sla-sub`/`fresh-eyes*`/`ret-probe*`, retained leftovers — all in the dev broker's RAM; **any broker wipe clears them.** Nothing ships to P1 except the decisions above.
