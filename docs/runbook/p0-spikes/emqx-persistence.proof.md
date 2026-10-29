# P0 Spike — EMQX persistence (what the broker REALLY promises)

**Date:** 2026-10-29 · **EMQX:** 5.8 (container `nabd-emqx`, dev compose) · **Topic:** `spike/room` · **Subscriber id:** `spike-sub`
**Bet on record before the test:** 0/5 (RAM-only queues). **Executed by:** agent (user delegated execution; walkthrough taught after — A4 note: user to re-derive the verdict table from this file until fluent).
**Note:** dashboard access still pending (`admin/public` rejected by REST API; `EMQX_DASHBOARD_PASSWORD=` empty in `.env`) — moved to P3 checklist: set a real dashboard password. CLI `emqx ctl` used instead — better instrument anyway (scriptable, honest).

---

## Step 1 — subscriber registered (persistent session)

```
mosquitto_sub -h localhost -p 1883 -t 'spike/room' -q 1 -i spike-sub -c -v -W 600   # terminal 2, silence = connected

docker exec nabd-emqx emqx ctl clients list | head -20
Client(spike-sub, username=undefined, peername=172.18.0.1:46140, clean_start=false,
  keepalive=60, session_expiry_interval=7200, subscriptions=1, inflight=0, awaiting_rel=0,
  delivered_msgs=0, enqueued_msgs=0, dropped_msgs=0, connected=true,
  created_at=1793233551639, connected_at=1793233551639)
```

Key fields: `clean_start=false` (the `-c` flag, broker-side) · `session_expiry_interval=7200` (**"persistent" ≠ eternal: 2 h grace, then the broker deletes the session**) · `subscriptions=1` (the topic filter is stored WITH the session) · `enqueued_msgs` = the spike's counter.

## Step 2 — subscriber killed (device goes offline)

`Ctrl+C` in terminal 2 (graceful DISCONNECT; session survives — connection doesn't).

## Step 3 — publish 5 QoS1 messages while nobody listens

```
for i in 1 2 3 4 5; do mosquitto_pub -h localhost -p 1883 -t 'spike/room' -q 1 -m "msg-$i" && echo "published msg-$i (PUBACK'd)"; done
published msg-1 (PUBACK'd) … published msg-5 (PUBACK'd)   # broker RECEIVED all 5 — received ≠ stored (the premise, in evidence)

docker exec nabd-emqx emqx ctl clients list | grep -o 'enqueued_msgs=[0-9]*'
enqueued_msgs=5
```

## Step 4 — THE RESTART

```
docker compose --env-file .env -f docker-compose.dev.yml stop emqx ; sleep 3
docker logs nabd-emqx 2>&1 | tail -5
Stop listener http:dashboard on :18083 successfully.
Listener tcp:default on 0.0.0.0:1883 stopped.
Listener ssl:default on 0.0.0.0:8883 stopped.
Listener ws:default on 0.0.0.0:8083 stopped.
Listener wss:default on 0.0.0.0:8084 stopped.
docker compose --env-file .env -f docker-compose.dev.yml start emqx ; sleep 5
docker exec nabd-emqx emqx ctl clients list
No clients.
```

→ The session whose registration we PROVED (Step 1), holding 5 credited queued messages, **does not exist after restart.**

## Step 5 — reconnect & count (Q1+Q2 verdict)

```
mosquitto_sub -h localhost -p 1883 -t 'spike/room' -q 1 -i spike-sub -c -v -W 30
Timed out            # 0 of 5 arrived
docker exec nabd-emqx emqx ctl clients list | head -3
Client(spike-sub, …, clean_start=false, …, created_at=1793234058208, …)
```

→ `created_at` ~8.5 min AFTER the original (`1793233551639`): the broker built a **brand-new session** and attached it to the old client name. "Reconnected" ≠ "session restored".

## Step 6 — Q3: retained messages, with a positive control

```
# (user's first -r publish order was ambiguous; re-established cleanly:)
mosquitto_pub -h localhost -p 1883 -t 'spike/room' -q 1 -r -m "retained-baseline-check" && echo PUBACK ok
PUBACK ok
mosquitto_sub -h localhost -p 1883 -t 'spike/room' -q 1 -i fresh-eyes -c -v -W 8
spike/room retained-baseline-check
spike/room retained-baseline-check      # BASELINE: retained delivery WORKS (delivered twice — QoS1 duplicate
                                        #  on re-subscription, allowed; logged as microsurprise, explanation deferred)
Timed out

docker compose --env-file .env -f docker-compose.dev.yml stop emqx && sleep 3 \
  && docker compose --env-file .env -f docker-compose.dev.yml start emqx && sleep 5
mosquitto_sub -h localhost -p 1883 -t 'spike/room' -q 1 -i fresh-eyes2 -c -v -W 8
Timed out                               # RETAINED MESSAGE DID NOT SURVIVE
```

## Step 7 — why: is any persistence engine even running?

```
docker logs nabd-emqx 2>&1 | grep -i -E 'durable|persist|rocksdb|retainer' | head
(no output)
```

→ **Zero persistence machinery at startup.** Default EMQX in this container = RAM-only.

---

---

## Part 2 — The challenge battery (2026-10-29, user demanded replication; agent executed)

User challenged: "are you sure about this report!? can you make another experiment?" — correct instinct; a single-run result is preliminary. The battery attacks the report's own conclusion.

### E1 — replication (core test, independent rerun)

```
PUBACK e1-msg-1 … PUBACK e1-msg-5
enqueued_msgs=5
docker compose stop/start emqx
No clients.
mosquitto_sub -q 1 -i sla-sub -c -v -W 8 → Timed out   # 0/5
```

### E2 — CONTROL: no restart (the missing control)

```
subscriber connects (spike-sub, -c) → goes offline
PUBACK e2-msg-1 … e2-msg-5 ; retained-hello2 published (-r)
enqueued_msgs=6
reconnect WITHOUT restart → delivered:
spike/room e2-msg-1 … spike/room e2-msg-5 , spike/room retained-hello2   # 6/6
```

→ The queue works perfectly with the broker up. **Restart is the sole cause of loss.** Causal attribution now airtight.

### E3 — non-persistent session sanity (spec's step 6)

```
PUBACK e3-msg-1..3 ; fresh NON-persistent subscribe (no restart) → Timed out (0)
```

### E4 — build/config introspection

```
emqx ctl status → 5.8.6 started
grep durable etc/emqx.conf → no durable config
/opt/emqx/lib contains emqx_durable_storage-0.4.4  (machinery PRESENT, INACTIVE)
/opt/emqx/data has mnesia/ cluster metadata
```

### E5 — retained deep-probe (+ a genuine microsurprise)

```
fresh -r publish "ret-now-<ts>" → PUBACK; retainer info: 4; fresh subscribe → DELIVERED immediately ✅
restart → retainer info: 0 ; fresh subscribe → Timed out (0)
```

**Microsurprise (logged, deep-dive deferred to P1/P3):** after E1's restart, `retainer info` reported **3** retained messages while NOTHING was deliverable (`--retained-only` empty); one restart later the counter reconciled to **0**. Interpretation candidate: retainer metadata outlived the RAM payloads; books reconciled later. Recorded as observed — the *verdict-relevant* fact is untouched: the deliverable retained set was empty after every restart.

## Part 2 verdict: STRENGTHENED

All original conclusions held under replication + control + introspection:
Q1 ❌ Q2 ❌ (0/5, now with a no-restart control delivering 6/6) Q3 ❌ (+ metadata anomaly noted).
EMQX 5.8.6 OSS in our container: RAM-only by default; durable storage library present but disabled.

---

## VERDICT

**Three questions, three negatives, one root cause:**


| Q | Test | Count | Verdict |
|---|---|---|---|
| 1 | persistent session across restart | `No clients.` / fresh `created_at` | ❌ dies |
| 2 | 5 queued QoS1 msgs for offline subscriber | **0/5** | ❌ dies |
| 3 | retained message across restart (with positive control) | absent after restart | ❌ dies |

**Stress-tested:** challenged by the owner, re-verified with a 5-experiment battery (Part 2 above): E1 replication 0/5; E2 no-restart control 6/6 (queue works — restart is the sole cause); E3 sanity 0; E4 config introspection (durable machinery present-but-off); E5 retained re-probe (dies; metadata anomaly logged).

**Doctrine sentence:** the default EMQX 5.8 we run is a cork board in a rented room — the whole broker state is emptied on every restart. Broker-side durability is NOT default; it must be built device-side (retry/store-and-forward) or enabled explicitly (`durable_sessions`, P3 decision).

**Consequences (feed the paper `emqx-persistence.md`):**
1. NFR-04 wording must state: *broker restart = window of loss for offline-device messages under default config; durability = device-side buffering; QoS1's PUBACK = "broker received", nothing more.*
2. plan/05 §7 device-side doctrine **confirmed by measurement** (not weakened by this spike).
3. P3 decision: evaluate `durable_sessions` config with honest eyes (it exists; we measured its absence, not its cost) — dashboard access + real password needed for the P3 config work (moved to P3 checklist).
4. plan/09 §5 restart-chaos test: numbers updated to these counts (0/5 default).
