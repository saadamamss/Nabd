# 09 — Testing & Performance

**Test Strategy, Load & Chaos Plans, and the Suite That Implements the Acceptance List**

*Blueprint v0.2 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file defines **how we know the system is actually good** — turning the acceptance list (02 §6) and NFRs (02 §4) into concrete, automatable tests, and adding the scenarios 02 didn't enumerate but reality will (chaos, flood, outage).

Ground rule (from the working method): a feature isn't "done" when the code exists — it's done when **the test that proves it exists and passes in CI** (02 Definition of Done).

## 2. Test Stack & Where Each Kind Lives

| Kind | Tooling | Where | When |
| --- | --- | --- | --- |
| Unit (pure logic: rules engine math, dedup, cooldown, validation) | xUnit | per module project | every commit, sub-second |
| Module architecture tests | **NetArchTest** | one project watching all rooms | every commit — module boundary breaches fail the build (03 §4 rule 1 mechanized) |
| Integration (module + real DB/queue/broker) | xUnit + **Testcontainers** (Postgres/Timescale, RabbitMQ, EMQX images) | per module + a shared suite | every PR |
| End-to-end flows | xUnit against a compose stack | one project | every PR (staging)**and before each deploy** |
| Load & soak | **k6** (JS scenarios, good TS ecosystem) + the Simulator (05 §10) | staging | scheduled weekly + before release + during tuning |
| Chaos/failure injection | bash + compose: kill containers, drop nets with `tc`, fill disks on clones | staging | before each release |
| Security tests | custom (07 §11 catalog): auth fuzz, cross-tenant probe, header scan, image secret grep | CI (fast subset) + weekly deep scan | policy |
| Manual | checklist only (UI languages AR/EN, RTL, mobile-responsiveness spot checks) | before each release | small, honest |

Coverage gates: **70% minimum on module code**, with the rising rule (each release can't lower it). Coverage is a floor, not a religion — we track *which* paths are covered, not the number.

## 3. The Acceptance-List Test Implementations (02 §6 → concrete)

| # (02) | Test | Automated as |
| --- | --- | --- |
| 1 Isolation | tenant A probes tenant B (and guessed UUIDs) through every endpoint | CI suite: generated from the API route list — new endpoint ⇒ new probe (07 §11) |
| 2 Fridge-warming alert | simulator `warmup-demo` → alert on dash ≤ 5s from crossing, email ≤ 60s | E2E with correlated IDs; assertions include the p95 claim |
| 3 Offline watchdog | simulator `dropout`; measure detection time = config window + margin ≤ 45s (margin covers the ≤ 30 s watchdog loop; freshness comes from in-memory last-seen, not the flushed `device_status` table — 04 §6) | E2E |
| 4 Restart mid-stream | kill Processing Host while 1k msg/sec flows; count accepted vs stored after recovery | chaos, staged: **0 lost, 0 duplicated** (DB unique key as referee) |
| 5 Load | k6: 2,000 readings/sec × 10 min sustained | weekly + pre-release; assertions NFR-01/02 + error rate 0 + queue lag bounded |
| 6 Backup restore | restore latest prod backup to scratch DB in staging; smoke queries; app boots against it | weekly automation (08 §5) |
| 7 New device in ≤ 15 min | timed walkthrough on staging: issue token → connect → first data in dash | manual checklist each release (small honesty cost, high demo value) |
| 8 AR/EN + RTL | scripted UI checklist both locales | manual, documented |
| 9 Generic template | add "tank" template via seed → device → rules → alert, zero schema/code changes | CI: a test that literally does the SQL inserts from 04 §5 then runs the flow |
| 10 Starter Kit | follow only the written install guide with the reference sensor + gateway: real readings, real alert (dashboard + Telegram) | manual per release, timed, with a short recording |

## 4. Load Test Plan (the NFR-01/02 proofs)

### 4.1 Scenarios (k6 + simulator)

| Scenario | Shape | Success criteria |
| --- | --- | --- |
| **Sustained** | 2,000 readings/sec for 10 min (mix of 100 devices × 20/10s + 20 devices × 1/s) | 0 rejected (except legit E429), ingested == stored count, no DLQ growth |
| **Burst** | 30s at 6,000/sec (3× steady) | queue depth grows, then drains < 2 min; no OOM; no alert latency death |
| **Soak** | 1,000/sec × 24 h (schedule over weekend weekly during build) | flat memory; no unbounded disk in queue/logs; aggregates keep up |
| **Latency probe** | during Sustained: timestamp crossing → alert visible in dash | **p95 ≤ 5s, p99 ≤ 15s** (NFR-02; the p99 is new honesty here) |
| **Query storms** | 10 users pulling 30-day histories concurrently during Sustained | dash p95 < 2s (NFR-03), reading path unaffected > 10% loss |
| **Duplicate flood** | replay each batch 10× during Sustained (idempotency under load) | stored row count == unique readings; no throughput collapse; the `ON CONFLICT` path is measured |

### 4.2 Tuning levers (measured knobs, not vibes)

| Lever | Where | Expected effect |
| --- | --- | --- |
| Batch size (ingest → multi-row INSERT) | Processing Host config | largest cheap win for insert/sec |
| Chunk interval | Timescale (04 §6) | fewer chunks = faster inserts, slower pruning |
| Rules fan-out vs batch eval | 06 implementation order | latency vs RSS for thousands of rules |
| Async slots / parallel ingestion shards | Processing worker count | CPU saturation curve |
| Queue prefetch per consumer | RabbitMQ consumer | latency under burst |
| Compression/index check | `readings` index & compression policy | affects history queries far more than writes |

Discipline: **one lever at a time, one measured scenario at a time, results recorded in `docs/perf/<date>.md`** — micro-decisions get a page, not a mythology.

## 5. Chaos Plan (releases ship after the "bad day" packet)

Catalog, executed on staging before each release; each entry asserts defined recovery:

| Injection | Assert |
| --- | --- |
| Kill Processing Host (mid-stream) | 0 lost / 0 dup; alerting resumes ≤ 60s (02 #4) |
| Block broker 60s → restore | devices buffer; on reconnect drain without loss (05 §7) |
| Restart EMQX mid-stream | devices reconnect and re-send unacked QoS1 messages; **observe what the chosen EMQX edition/config actually persists** — the result decides the exact wording of NFR-04 (02) |
| Kill RabbitMQ | API keeps 202ing? **NO** — it must 503-reject with clear error (bounded queue honesty); drain after restore |
| DB failover simulation: pause Postgres 30s | workers retry with backoff; no crash loops; no poison messages |
| Fill disk to 90% (clone box) | DB survives; watchdog alert fires; ingest stalls gracefully with no silent loss in any local ring buffer |
| Slow network (tc 300ms 20% loss) between worker ↔ broker | Bounded queue holds; redelivery races resolved by unique key |
| DB password rotation mid-flight | platform alerts, worker reconnects; no credential leaks to logs (07 §9 rule verified) |

## 6. Security Test Suite (from 07 §11 — implementations)

| Test | CI or scheduled | Pass condition |
| --- | --- | --- |
| Cross-tenant probe over every route (07 §11) | every CI run | 0 leaks, incl. guessed IDs & URL manipulation |
| Refresh-token replay | CI | replayed token → all sessions revoked + security event |
| Revocation TTL | CI (device token) | dead after ≤ 60s measured |
| Reconnect storm (1,000 devices within 30 s) | pre-release | auth CPU stays bounded (fast keyed hash, cached), 0 connection failures after warm-up |
| Login anti-enumeration (status+timing) | CI | statistically indistinguishable over 100 samples |
| Ingest flood (10k req burst, 1 device) | pre-release | 429s only that device; other devices unaffected |
| Fuzz: search/filter params, JSON payloads deep-nest | pre-release | no 500 leaks, no crash loops |
| Image secret grep (07 §11) | CI | zero secret-class patterns in built images |
| Dependency CVE scan | weekly | no Critical open > 7 days |

## 7. Test Data Management

| Need | Source |
| --- | --- |
| Dev seeded tenants/devices | `nabd seed:demo` command (templates from 04 §5, realistic time patterns incl. day/night cycles, weekly seasonality) |
| Staging realistic-ish data | 7-day anonymized export from prod, or "sim week" replay — better: generated via Simulator steady 50 devices |
| Load tests | simulator at scale (05 §10) — validated to produce exactly the shapes 4.1 wants |
| Never | production customer data on dev machines. No exceptions, no "just this once" |

## 8. Flakiness & CI Health Rules

- Any test failing 2× without a product change in its area = quarantined within 24h and fixed or deleted within a week — flaky suites breed ignored suites.
- E2E timing assertions use the products' correlated IDs (batch IDs → alert IDs), never sleep-and-hope sleeps over 15s.
- Containers wait-for-ready, not sleep(30) rituals.
- CI total time budget: **< 15 min for PR suite** — tests that take longer will not be run, so they bisect into parallel shards by module.

## 9. Performance Benchmarks Recorded (to fill as results come in)

| Date | Scenario | Result | Decision |
| --- | --- | --- | --- |
| — (placeholder — fills during build) | Sustained 2k/sec × 10 min | ... | ... |

Honesty clause: **no numbers in this file are validated until row 1 of that table exists.** Until then, they are design targets only (same discipline as 02 NFR labels).

## 10. What We Deliberately Do NOT Test at MVP

- Multi-region / failover beyond the DR table (08 §5) — single-host honesty.
- Browser matrix corners: Chrome/Firefox/Safari latest + common Android/iOS viewport only.
- Pen test by professionals (paid test is a V1 spend, per the honesty of 01/07; community review + §6 suite covers now).
- Cross-DB consistency with an external analytics store — there is no second store (ADR-003 pays off).

## 11. Open Points in This File

| # | Point | Default |
| --- | --- | --- |
| S | p99 target for alert latency | ≤ 15s (new, formalized in 4.1; revisit after first Sustained run) |
| T | Weekly load test schedule vs manual | automated weekly nightly during build, pre-release before big merges |
| U | k6 vs NBomber (stay-in-C# vs best ecosystem) | k6 (ecosystem + cloud-free tier) |

## 12. Next Step

- [ ] Review the chaos catalog — which "bad day" would hurt *your* launch most? (it goes in §5 first)
- [ ] Then → **10 — Roadmap & Phases** (the build schedule with Definition-of-Done per phase)
