# 11 — Risks Catalog

**The Failure Dictionary — Every Risk, Its Early Signal, and Its Countermeasure**

*Blueprint v0.2 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

The other files made promises. This file catalogs **every way those promises can break**, with the earliest visible signal and the pre-agreed countermeasure. It exists so bad news triggers a *planned response* instead of an improvisation, and so nothing on this list is a surprise twice.

Conventions:

- Probability: **H**igh / **M**edium / **L**ow — judgment calls, revised at each phase gate (10 §5).
- Impact: H/M/L on *launch readiness*, not on an imaginary enterprise scale.
- Each entry names where its countermeasure lives in the Blueprint (so a risk with no file backing = a blueprint gap to fix, not a private worry).
- Review cadence: skim the whole catalog at every phase gate; add anything new that bit you in the previous phase.

## 2. Business & Market Risks

| ID | Risk | P/I | Early signal | Countermeasure (where) |
| --- | --- | --- | --- | --- |
| B1 | Nobody wants the product as designed | H/H | Zero trial signups after 4 weeks live; visitors bounce < 30s | Strict minimal scope + cheap generic model (01 §7.1); traction gate loop (01 §7.4); pivot menu pre-written |
| B2 | Wrong vertical first (temperature ≠ demand) | M/H | Signups arrive but ask for other asset kinds; or demos interest nobody | Templates are config only (04 §5); marketing story switchable without code (02 #9 proof) |
| B3 | Price rejected (too high/low) | M/M | Exit surveys say "price"; very short trials | Price is a table row (01 §5.2); 4-week loop adjusts (01 §7.4) |
| B4 | A big platform copies the wedge to MENA | L/H | Their pricing/localization announcements | Your moats are local service + speed, not features (01 §4); stay fast |
| B5 | Payment/collection friction (Egypt) | M/M | Manual renewals slipping; invoices chased | Local gateway decision D12 (01 §10); manual renewals documented as MVP-acceptable |
| B6 | No reliable sensor/gateway that customers can actually buy, install and keep online | M/H | Trial users cannot reach a real alert; installs need hand-holding | One reference Starter Kit (05 §13), acceptance #10, decision D13 (01 §10) |

## 3. Technical Risks

| ID | Risk | P/I | Early signal | Countermeasure (where) |
| --- | --- | --- | --- | --- |
| T1 | TimescaleDB under-scales before design target | L/H | First Sustained run under 2k msg/s (09 §9 row 1) | Tuning levers (09 §4.2); ADR-003 falsification criteria written (03 §7) |
| T2 | EMQX auth plugin brittleness | M/H | Auth-cache misbehaves; revocation TTL tests slip | Revocation SLA test in CI (09 §6); fallback = Mosquitto+auth service documented (ADR-004 alternative path) |
| T3 | Out-of-order/duplicate storm on flood-reconnect | M/H | Idempotency-key tests; replay scenario failing | Watermark + unique key design (05 §8, 06 §3.3); simulator replay scenarios (05 §10) |
| T4 | Alert storms from one bad rule | M/H | `notified_total` spikes; user complaints | Cooldown/dedup math (06 §5.2); tenant rate counters (06 §7) |
| T5 | Data loss during deploys/restarts | L/H | Acceptance #4 fails | Ack-after-commit design (05 §6.6); chaos packet (09 §5) |
| T6 | One-VM ceiling arrives early | L/M | Disk/CPU > 80% sustained; load-test drain > 2 min | Growth path built-in: rooms → separate hosts without rewrite (03 §4 rule 5; ADR-007 revisit trigger) |
| T7 | Migration breaks running devices on release day | L/H | Expand/contract violations in PR review | Two-release rule (08 §3); no destructive migration with code that uses it |
| T8 | Secrets leak (log/dump/image) | L/H | §10 checklist misses; image grep hits | Grep built images in CI (09 §6); hash-only tokens (04); logger masking (07 §9) |
| T9 | Cross-tenant leak via one missed query | L/H | Isolation suite failure — the *designed* catch | RLS double-lock (04 §10) + CI probes every route (07 §11) |
| T10 | Broker durability assumed, not verified (QoS1 PUBACK = broker received, not DB committed) | M/H | EMQX restart test loses or duplicates messages | Wording fixed in 02 NFR-04 and 05 §7; restart-EMQX chaos test (09 §5); configure session/message persistence deliberately |
| T11 | RLS misbehaves with compressed chunks, continuous aggregates or the ingestion writer role | M/H | P0 spike shows rows visible across tenants or writes failing | P0 RLS spike (04 §10); dedicated writer role; tenant-filtered aggregates; isolation suite covers aggregate endpoints |
| T12 | Reconnect storm after an outage overloads auth | M/M | CPU spike and connect failures when many devices return at once | Fast keyed token hash + 60 s auth cache (05 §3, 07 §4); reconnect-storm tests (07 §11, 09 §6); jittered backoff in the kit firmware |

## 4. Operational Risks (running it solo)

| ID | Risk | P/I | Early signal | Countermeasure (where) |
| --- | --- | --- | --- | --- |
| O1 | Backup job silently stops | M/H | Weekly rehearsal fails / backup metric alert | Restore rehearsal automation (08 §5); the alert-on-backup-failure rule |
| O2 | Disk full = platform death | M/H | Disk > 80% alert (08 §9) | Compression + retention (04 §6); disk alert armed; log rotation |
| O3 | Provider outage / region loss | L/M | DR rehearsal path stale | Runbook rebuild drill once per phase (08 §5 DR table) |
| O4 | Cert expiry surprise | L/H | < 14-day alert | Auto-renew + expiry alert (08 §6) |
| O5 | Alert fatigue → ignored system alerts | M/H | You start muting your own alerts | "No runbook → delete" rule (08 §6); minimal alert set |

## 5. Product & UX Risks

| ID | Risk | P/I | Early signal | Countermeasure (where) |
| --- | --- | --- | --- | --- |
| P1 | Onboarding too hard solo (no help) | M/H | Activation < 40% (traction gate) | Simulator-based first-alert walkthrough ≤ 60 min (NFR-14); demo tenant with `warmup-demo` running |
| P2 | AR/EN + RTL quality slips | M/M | Locale-parity checklist fails (02 #8) | RTL from commit one; parity checklist each release; Arabic copy reviewed by you |
| P3 | False alerts burn first users' trust | H/H | Dedup-suppressed ratio low; complaints | Duration/cooldown tuning loop (06 §8); "false alerts decrease weekly" metric (00 §8) |
| P4 | Demo ≠ reality (sim skin over broken pipes) | M/H | Chaos tests fail after a demo worked | Simulator is *just another device* (05 §10) — demos cannot diverge from the tested path |
| P5 | Email-only alerting misses incidents at night — the core promise fails exactly when it matters | H/H | Alerts acknowledged hours late; owners say they never saw the email | Telegram push in the MVP (FR-34, FR-38); WhatsApp/SMS in V1; measure time-to-acknowledge |

## 6. Process & AI-Agent Risks

| ID | Risk | P/I | Early signal | Countermeasure (where) |
| --- | --- | --- | --- | --- |
| A1 | AI-generated security hole ships | M/H | 07 §10 checklist misses; §11 suite finds late | Checklist as merge gate (07 §10); security suite in CI (09 §6) |
| A2 | Architectural drift: agents bypass room contracts | M/H | NetArchTest failure (the specific catch) | Arch tests enforcing rule 1 from P0 (03 §4; 09 §2) |
| A3 | Tech debt snowball (working-but-rotten code) | M/H | Coverage gate slipping; review time per PR growing | 70% floor + rising rule (09 §2); you read diffs (08 §7) |
| A4 | You stop understanding code you ship | M/H | You can't explain a module's flow at the phase gate | 00 warning honored: review protocol + glossary habit; agents explain before merging |
| A5 | Scope creep via "while we're here" | M/H | PR diff touching 2+ rooms with no ADR | One-slice-per-day rule; out-of-scope lists (02 §7); ADR required (00 §7.7) |

## 7. Personal Risks (the honest section)

| ID | Risk | P/I | Early signal | Countermeasure |
| --- | --- | --- | --- | --- |
| S1 | Burnout from 6–8 h/day solo build | M/H | Skipping rest days; slice quality dropping (catch it in PR reviews) | Rest days booked in D2 (00 §11); gates G1-G4 throttle pace (10 §5) |
| S2 | Motivation stall mid-build (weeks 4–7 dip) | M/H | Phases stretch; slices shrink; enthusiasm gone | Milestone visibility: P3's first load table (> 8/10) is designed to be a *proof-of-life* moment; demo tenant visible from P5 |
| S3 | Single point of failure (you, sick/life) | M/H | Any 2+ day unplanned gap | Daily slice ≤ 1 day WIP (10 §4); everything in git + compose — cold-start cost is one runbook page |
| S4 | Posting fear delaying launch (perfectionism) | M/H | P7 slips repeatedly with small deltas | Kill criteria + G3 gate ("push P7" only for missing checks, not for polish) |
| S5 | Schedule optimism — the first 10-week plan was too tight for a learner | H/M | P3 not done by the end of week 10; DoD slipping | Re-based to 14–20 weeks (10 §1); gate G1 re-check; scope-surgery rule (10 §6) |

## 8. Top-5 Tripwires (print-these-in-your-head)

1. **Silence is the worst signal** — no readings, no backups, no errors is *not* health. Every background job has a metric + alert (08 §6). Silent-stopped beats loud-broken in damage.
2. **First load-test numbers outrank every opinion** — including mine and the other AI's (ADR-003 note, 09 §9).
3. **False alerts die first or trust dies** — the tuning loop after launch is a *product feature*, not maintenance (06 §8).
4. **A demo can never be a shortcut path** — simulator or real device, same doors (05 §10).
5. **The traction gate is the boss, not the roadmap** — after launch, 10's phases are subordinated to 01 §7.4 decisions.

## 9. Next Step

- [ ] Add any risks this catalog missed (you know your context best) → then file 12 completes the Blueprint
