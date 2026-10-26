# 10 — Roadmap & Phases

**Build Order, Phase Definitions of Done, and the Launch Path**

*Blueprint v0.1 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file turns files 00–09 into an ordered, doable schedule: **what gets built in what order, how we know a phase is done, and what the launch path looks like.** Constraints honored: one person + AI agents at 6–8 h/day (00 D2), strictly minimal MVP scope (01 §7.1), Docker Compose on one VM (03 ADR-007).

Timeline honesty (from `telemetry.md` and the AI-speed assessment): a realistic Build → Publish path is **8–12 weeks** of focused daily work, not optimism-compressed. Phases below sum to that, with buffers.

## 2. The Order and Why

Dependency logic drives the order: **Auth before everything** (adding it late = painful rework — an old lesson recorded in `telemetry.md`), **ingestion before rules** (rules need data), **simulator early** (it is the test bench for everything else), **UI last-ish** (nothing visual is provable without the pipeline), and **hardening runs throughout, not at the end.**

```
P0 Setup ─▶ P1 Auth+Tenancy ─▶ P2 Assets+Templates ─▶ P3 Ingestion ─▶ P4 Rules+Alerts ─▶ P5 UI+Dashboards ─▶ P6 Hardening ─▶ P7 Launch ─▶ P8 Launch Loop
                └── (CI/CD live from P0; simulator arrives in P2, grows each phase)
```

## 3. Phases

### P0 — Bootstrap (Week 1)

| Deliverable | Detail |
| --- | --- |
| Repo + solution skeleton | module folders/contracts, ADR files seeded from 03 §7 |
| CI pipeline green | build + unit test + NetArchTest baseline + Testcontainers smoke (03 §4 rule 1 alive from day 1) |
| `docker compose up` dev env | Postgres+Timescale, EMQX, RabbitMQ, API host, processing host stubs — all healthy |
| Migrations framework chosen | expand/contract habits documented (08 §3) |
| 04 schemas created | identity + assets + telemetry (hypertable) — migrations as code |

**DoD:** a "hello module" test (any module writes/reads one row through contracts) passes in CI < 15 min.

### P1 — Identity, Tenancy & Auth (Week 2)

| Deliverable | Detail |
| --- | --- |
| Tenant + user CRUD | schemas from 04 §4; RLS policies live |
| Login | argon2id, JWT+refresh rotation, revocation, anti-enumeration (07 §3) |
| 3 roles enforced | API attributes + in-service assertions (07 §5), isolation suite skeleton from 09 §6 runs in CI |
| Admin module stub | tenant list/enable-disable (FR-61) with `BYPASSRLS` audited path |

**DoD:** isolation probes (02 #1 subset) pass; lacking-attribute endpoint tests pass; password/session tests from 09 §6 scripted.

### P2 — Assets, Devices & Templates (Week 3)

| Deliverable | Detail |
| --- | --- |
| Assets/devices APIs | CRUD, archive, device tokens (issue/revoke, hash-only, log) |
| Template mechanism | `asset_type_templates` + `channel_definitions` + **fridge + tank seeds** (04 §5) |
| Simulator v0 | console tool: real tokens over real MQTT, `steady` scenario |
| Token lifecycle tests | revocation ≤ 60s SLA test from 09 §6 scripted |

**DoD:** a simulated device authenticates to EMQX and publishes to its tenant topic; invalid token/topic attempts refused (05 §3 exercises).

### P3 — Ingestion (Weeks 4–5)

| Deliverable | Detail |
| --- | --- |
| MQTT consumer + HTTP ingest | both converge on the validation pipeline (05 §6), error catalog complete |
| Batch store | COPY into hypertable; idempotency key live; ack-after-commit (NFR-04) |
| RabbitMQ wiring | bounded queues, DLQ, backpressure (05 §7) |
| `received_at`, clock discipline | §8 of 05 — clamp/flag/watermark implemented |
| Simulator grows | `spike`, `dropout`, replay scenarios |

**DoD:** acceptance #4 (restart mid-stream, zero loss) and first Sustained load run fill **row 1 of the results table (09 §9)** with real numbers.

### P4 — Rules, Alerts & Notifications (Week 6)

| Deliverable | Detail |
| --- | --- |
| Threshold engine | per-rule state machine, `duration_seconds`, watermark behavior (06 §3.2) |
| Offline watchdog | loop + auto-resolve on return (06 §4) |
| Alert lifecycle | OPEN→ACK→RESOLVED, re-raise-upsert, dedup + cooldown (06 §5) |
| Email notifications | bilingual templates, SMTP config encrypted (04 §9) |
| Severity & mute windows | warn/critical/info recorded (06 §6, §5.4) |
| Simulator | `warmup-demo` scenario = the launch story |

**DoD:** acceptance #2 (fridge-warming alert ≤ 5s dash / ≤ 60s email) and #3 (offline detection) pass as automated E2E tests.

### P5 — Dashboards & UI (Weeks 7–8)

| Deliverable | Detail |
| --- | --- |
| Next.js app shell | AR/EN i18n + RTL from commit one (00 D3), both locales parity |
| Login + tenant admin flows | users, assets, devices, rules management |
| Live dashboard | asset status via SignalR; statuses healthy/warning/alert/offline |
| Asset history | time-range charts over hourly/daily aggregates (04 §6) |
| Alerts page | filter/list/ack/resolve |
| Feedback button | in-product, one field (01 §7.3 L3) |

**DoD:** acceptance #7 (new device ≤ 15 min on staging) and #8 (AR/EN+RTL) pass; p95 dashboard query < 2s under concurrent-load storm (09 §4.1 array).

### P6 — Hardening & Release Prep (Weeks 9–10)

| Deliverable | Detail |
| --- | --- |
| Chaos packet | full §5 catalog (09) executed on staging, results recorded |
| Security suite | full 09 §6, image secret grep, dependency scan |
| Backup/restore rehearsal | weekly job live (08 §5) — acceptance #6 automated |
| Prod VM hardened | 08 §9 checklist executed + signed off |
| Observability | 06 §11 metrics dashboards + alert-with-runbook rules (08 §6) |
| Costs & billing manual process | 01 §5.2 (renewals manual for now) |

**DoD:** 02 §6 acceptance list (all 9) fully green on staging, with the numbers page updated.

### P7 — Launch Prep (Week 10)

| Deliverable | Detail |
| --- | --- |
| Landing page + one-pager | AR/EN (01 §6.3), honest claims only (02 NFR-05 posture) |
| Demo environment | read-write demo tenant with simulator `warmup-demo` running live |
| Docs | quickstart (5 min), device-onboarding (15 min), FAQ |
| Soft launch channels picked | list of communities/groups from 01 §7.1 L1 stage — posted *after* DoD |
| Terms/privacy pages | short, honest, data-rights statement (07 §12) |

### P8 — Launch Loop (Week 11+ — per 01 §7)

Post to the channels → funnel dashboard (visitors → signups → activated → paid) → **4-week review against the traction gate (01 §7.4)** → decide: keep / adjust story / adjust pricing / adjust vertical. V1 features (billing automation, SMS/webhooks, statistical detection, work orders) are prioritized **from this loop's data**, not from this file's guesses.

## 4. Standing Habits (true from P0 to P8)

| Habit | Ref |
| --- | --- |
| One shippable slice per day; PR reviewed by you against 06 §9 + 07 §10 | 08 §7 |
| Isolation suite runs on every PR from P1 onward | 09 §6 |
| No "temporary" code merges to main — ever | working method |
| Every phase's new endpoints extend the cross-tenant probe automatically | 09 §6 |
| Every "later" marked D#/open point gets actually skimmed at the phase gate | open-point register |

## 5. Milestone Gates (stop-the-world checkpoints)

| Gate | When | Question | Bad answer |
| --- | --- | --- | --- |
| G1 | end P1 | "Would I trust this auth in front of paying users?" | no → fix before ingestion |
| G2 | end P3 | "Does the ingestion pipeline honestly guarantee 0 lost on chaos?" | re-run 09 §5 before P4 |
| G3 | end P6 | "All 9 acceptance checks green + costs < $70/mo?" | no → push P7 |
| G4 | week 8 post-launch | traction gate (01 §7.4) | below → loop, don't build |

## 6. Slippage Buffer & Kill Criteria

- Total buffer booked: **2 weeks** across phases (not attached to any single one).
- **Kill criteria (hard honesty):** if by end of P6 the MVP still fails 3+ acceptance checks after the buffer, the next step is **scope surgery** (cut to a demo-only product + landing), not more months. A publishable wedge beats a perfect closet.
- If launch validation (P8) flatlines below the traction gate at **week 12 post-launch**: stop building V1 features; run the pivot options in 01 §7.4 with fresh eyes.

## 7. Open Points in This File

| # | Point | Default |
| --- | --- | --- |
| V | Start date anchor | "Week 1" = first Monday after you commit; all durations are calendar weeks assuming 6–8 h/day per 00 D2 |
| W | Working language of code/comments | English everywhere in code; Arabic/English appear only in resources and UI strings |

## 8. Next Step

- [ ] Sanity-check the phase durations against your real weeks, flag any that feel optimistic
- [ ] Then → **11 — Risks Catalog** (the failure dictionary: each risk → early signal → countermeasure)
