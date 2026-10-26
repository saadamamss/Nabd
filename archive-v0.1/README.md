# Nabd — Asset Telemetry & Anomaly Detection SaaS

> ⚠️ **SUPERSEDED — v0.1 archive.** Do not read these files for current decisions: .NET 8, `COPY`, `devices.last_seen_at`, argon2id device tokens, email-only alerts and the 8–12 week schedule were all corrected in v0.2. The current Blueprint is the set at the repository root (see `CHANGELOG-v0.2.md`). Kept only for decision history (see `telemetry.md`).

**Project Blueprint — one product: tell an SMB that its asset is about to fail, before it fails.**

*Blueprint v0.1 — living document set. Every decision lands in the file that owns it.*

## The One-Paragraph Pitch

For small and medium companies whose asset failures are expensive: **Nabd** is a ready-made monitoring platform for a specific vertical (first template: cold/temperature storage, but the data model is fully generic), installed in days, sending early Arabic/English alerts before the failure — not a builder platform that needs engineers, and not competing with giants on breadth; competing on a working solution, local service, and honest pricing.

## The 13 Blueprint Files (read in order)

| # | File | Answers |
| --- | --- | --- |
| 00 | [Vision & Scope](00%20—%20Vision%20%26%20Scope.md) | What product, for whom, inside/outside scope, success metrics, decisions D1–D7 |
| 01 | [Market & Business](01%20—%20Market%20%26%20Business.md) | Competitors (real prices, 2026-10-26), pricing model, GTM, launch-based validation (build-first → publish → market, no interviews) |
| 02 | [Requirements](02%20—%20Requirements.md) | FR/NFR with numbers, 3-role permission matrix, 9 acceptance checks (Definition of Done) |
| 03 | [System Architecture](03%20—%20System%20Architecture.md) | Modular monolith: 9 rooms, 2 hosts, data flows, 10 ADRs with reasons |
| 04 | [Domain & Data Model](04%20—%20Domain%20%26%20Data%20Model.md) | Concrete tables/schemas, generic template mechanism, TimescaleDB policies, RLS two-lock design |
| 05 | [Device Connectivity & Ingestion](05%20—%20Device%20Connectivity%20%26%20Ingestion.md) | MQTT topic scheme, payload v1, validation pipeline, durability rules, clock discipline, simulator contract |
| 06 | [Processing & Detection Rules](06%20—%20Processing%20%26%20Detection%20Rules.md) | Rules engine state machine, offline watchdog, alert lifecycle, dedup/cooldown math, honest "what it does NOT do" |
| 07 | [Platform & Security](07%20—%20Platform%20%26%20Security.md) | Attack surface, human + device auth, RBAC, secrets, audit logging, the AI-code review checklist |
| 08 | [Infrastructure & DevOps](08%20—%20Infrastructure%20%26%20DevOps.md) | Environments, CI/CD, backups + *tested* restore, DR table, observability, ~$45–70/mo cost |
| 09 | [Testing & Performance](09%20—%20Testing%20%26%20Performance.md) | Load/soak/chaos plans, acceptance checks automated, flakiness rules, recorded-results honesty clause |
| 10 | [Roadmap & Phases](10%20—%20Roadmap%20%26%20Phases.md) | 8 phases over 8–12 weeks, gates G1–G4, slippage buffer, kill criteria |
| 11 | [Risks Catalog](11%20—%20Risks%20Catalog.md) | The failure dictionary: risk → early signal → countermeasure → which file holds the answer |
| 12 | [Build Workflow With AI](12%20—%20Build%20Workflow%20With%20AI.md) | The daily operating manual: slices, review protocol, prompts, guardrails, AI gotcha ledger |

Supporting material: `telemetry.md` (decisions history), `archive-ar/` (Arabic versions of 00/01).

## Non-Negotiables (decided, recorded, enforced)

1. **One vertical focus, generic model** — verticals are template config; a second template must work with zero code/schema changes (02 acceptance #9).
2. **Strictly minimal MVP** — the scope list in 00 §6; nothing else until launch traction says otherwise.
3. **Build first, publish, then market** (your decision) — validation happens through the launched product via the traction gate (01 §7.4).
4. **Zero data loss, idempotent ingestion** — ack-after-commit + unique key (05 §6–7).
5. **Tenant isolation is double-locked** — app filter + PostgreSQL RLS, tested by CI on every PR (04 §10, 07 §11).
6. **Simple before smart** — thresholds + watchdog before any ML; rules engine reads typed config, ML later is additive (06).
7. **AI writes, you own** — small slices, guarded reviews, merge needs your approval (12).
8. **Honesty over marketing claims** — numbers are "design targets" until a test row records them (09 §9).

## The Path From Here

1. Create repo + CI skeleton (10 P0) → 2. Phases P1–P8 at one slice/day → 3. Soft launch → 4. 4-weekly traction-gate reviews → 5. Build V1 only from launch data.
