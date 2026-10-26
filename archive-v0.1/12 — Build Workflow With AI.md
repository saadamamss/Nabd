# 12 — Build Workflow With AI

**How You + AI Agents Work Daily: Slices, Reviews, Guardrails, and the Rhythm**

*Blueprint v0.1 — final file of the set. A living document, updated with every decision.*

## 1. Purpose of This File

This file is the **operating manual for the thing that makes this project feasible at all: one person + AI agents**. It expands the method sketched in `telemetry.md` and 08 §7 into daily practice, and encodes the non-negotiables that protect quality and your understanding.

Ground principles (carried from `telemetry.md`):

- You write the architecture; agents write code. **You own every decision that ships.**
- You must be able to explain any code in the repo at the phase gate (11 A4 — early failure signal = you can't).
- Agents work in **small, bounded tasks**, never "build me the project".

## 2. The Roles

| Role | Who/what | Scope |
| --- | --- | --- |
| **Architect / Reviewer** | you | decides modules/contracts/tests-accepted; reads every diff; owns gates G1–G4 (10 §5) |
| **Builder agents** | AI sessions | implement one task inside ONE module room |
| **Reviewer agent** | a separate fresh session | re-reads the diff adversarially: contract breaches, missing tenant filters, secret hygiene, test honesty |
| **Plankeeper** | you (10 min/week) | move open points (D#) into decisions; keep the checklist honest |

## 3. The Unit of Work: The Slice

A **slice** is one PR: small enough to finish and review in a day, big enough to mean something visible.

| Slice anatomy | Description |
| --- | --- |
| Task statement | 3–10 lines: what + which FR/NFR/acceptance-check it serves (02 IDs) + which room it lives in |
| Boundaries | files/dirs in scope only — touching another room = slice rejected by definition |
| Tests required | the failing test first where possible (TDD-lite): at minimum one proving test |
| Done definition | acceptance-check reference + checklist items checked (07 §10) + CI green |
| Notes | the agent explains its design choices in the PR body — reading that explanation is part of your review |

Slice size calibration: a day's slice ≈ 300–800 lines *touched* including tests. Two consecutive slices over 1,000 lines = "my slices are too fat" — split the pattern (that's a process bug, not a discipline offense).

## 4. The Daily Rhythm (6–8 h/day, 00 D2)

```
- Plan (15m): pick the slice from the current phase backlog; confirm it's within the phase's DoD path
- Build (3–4h): agents write, you steer; run tests locally as they come in
- Review (60-90m): your adversarial pass + reviewer agent pass; both sign
- Merge + CI (30m): green pipeline; publishable-check: what did this slice make visibly demoable?
- Customers/learning (60m): dashboards, docs polish, community watching (per the ~15% line when launch is live)
- Log (10m): open-point register updates; tomorrow's slice pre-picked
- 1-2 rest days/week: original D2 commitment — the schedule includes them honestly
```

## 5. The Review Protocol (yours — scripted, not vibes)

Read every diff against this ladder, in order:

1. **Boundaries:** any file outside the slice's room? → reject (mechanized too, but the human eye first)
2. **Correctness of intent:** does the code do the FR it claims, not just *run*?
3. **Tenant filter on every query** (07 §10); permissions asserted in service layer
4. **Secrets/logging:** nothing sensitive passes through logs or errors
5. **SQL:** parameterized; no string building anywhere
6. **Tests:** do assertions actually prove the requirement? Cover negative cases (auth failures, invalid input)?
7. **Failure behavior:** what happens on the bad day (worker restart, queue drop, DB pause)? Consider it per room.
8. **Understandability:** could you re-implement this flow on a whiteboard? If no — ask the agent to explain until yes.

Then run the **reviewer agent** with: "read this diff as an adversarial security and correctness reviewer; name your top-5 concerns ranked by severity; check the 07 §10 items." Read its concerns; keep what's true, argue what's not.

## 6. Prompt Patterns That Earn Their Keep

| Pattern | When | Example shape |
| --- | --- | --- |
| **Task-bounded prompt** | every slice | "You are implementing FR-31 in Modules/Rules (Contracts unchanged). Constraints: 05 §6 catalog, 06 §3.2 machine. Produce: failing test list → implementation → tests pass. Do not touch other rooms." |
| **Explain-before-code** | new modules | "Summarize the room's contract and which events it exports before writing anything" |
| **Adversarial review** | every PR | §5 reviewer-agent pattern |
| **Whiteboard restatement** | any confusion | "Walk me through this code's flow as a sequence diagram with failure points, so I can re-implement it from memory" |
| **Gotcha ledger** | after each notable AI mistake | add to §8 table so future sessions inherit the pattern break |
| **Refactor-with-tests-only** | cleanup days | "Only behavioral-preserving changes; run the full suite; zero API surface change" |

## 7. Guardrails (non-negotiables — enforced by both you and CI)

1. **Rooms are sealed:** contracts only; NetArchTest fails breaches regardless of intent (03 §4).
2. **The AI never merges its own PR:** CI green + *your* explicit approval. No "self-review" passes.
3. **No decisions in the dark:** agent proposes options for anything architectural; you choose (ADR-required items, 00 §7.7 — never let an agent "just pick" a pattern that outlives the session).
4. **Tests are read, not trusted:** an agent-written test with a tautological assertion (= passing because it mimics the code, not the contract) is a failed PR.
5. **Migration discipline:** expand/contract only (08 §3); agents never invent schema changes beyond the slice's declared scope.
6. **"Later" list is sacred:** hinting an agent to "also add small X" mid-slice is scope creep with AI speed; note it in the register instead (00 §10 risk).
7. **Every dependency addition gets a why:** one line in the PR body, or it goes back.

## 8. AI Gotcha Ledger (grow this every time an agent bites you)

| # | Observed failure mode | Standing defense |
| --- | --- | --- |
| 1 | Invents config/db table behaviors that don't exist in our files | agents quote the Blueprint file/section they implemented from; unquoteable = suspect |
| 2 | Adds MediatR/CQRS ceremony uninvited | explicitly excluded: 03 §9; PR reads "no new abstractions unless slice declares one" |
| 3 | Writes happy-path-only tests | §5 item 6: negative cases required, reviewer agent specifically probes |
| 4 | Strips security checks "for test setup" | 07 §10; test auth uses fixtures, not omissions |
| 5 | Over-abstraction (repository ceremony for a 50-line module) | slice boundary + line budget (§3); prefer plain services (03 §9) |
| 6 | Drops bilingual strings, defaults to English only | RTL/i18n checklist; resource files required in same PR |
| 7 | Silently renames/moves shared types, breaking other rooms | arch tests + reviewer agent diff scan |

*(rows 1–7 seeded from the working-method warnings in `telemetry.md` and the pattern classes seen in modern agent code reviews — extend with your own.)*

## 9. Weekly and Phase Rhythms

| Cadence | Ritual |
| --- | --- |
| Weekly (30 min) | phase backlog read: on track for DoD? open points resolved or rolled identically documented |
| Phase gate (½ day) | 10 §5 gate question answered in writing; 11 risks catalog skimmed with fresh eyes; next-phase slices drafted |
| Post-launch (weekly, first 8 weeks) | funnel numbers vs traction gate (01 §7.4); decisions logged in 01; **no feature work while under gate review except fixes** |
| Monthly | dependency scan (07 §10), restore-rehearsal result read (08 §5), costs review (08 §8), glossary updates |

## 10. When to Work WITHOUT Agents

- Designing contracts/ADRs/data-model sections: you first, agents second (drafts invited, choices yours).
- Security-sensitive code (07 layers): you read it line by line against the doc.
- The 02 acceptance list — you personally run #7 and #8 walk-throughs.
- Anything where "understanding it" is the deliverable for the phase gate.

## 11. Open Points in This File

| # | Point | Default |
| --- | --- | --- |
| X | Agent tooling mix (best-of-feel vs single product) | whatever you currently have; the protocol is tool-agnostic |
| Y | Repo conventions (branch naming, PR template) | one short CONTRIBUTING.md at P0 — 10 lines, enforced by habit |

## 12. The Blueprint Is Complete — What Now

Files 00–12 form the whole Blueprint. The standing rule: **the Blueprint is the product's contract with itself** — every phase gate re-reads the files it touches, every decision updates the file that owns it, every conflict between files is a bug to fix in the source file, not an excuse in code.

Your immediate next actions live in 10 §3 P0 and the checklists upstream:

1. Pick the host (08 §11), create the repo + CI skeleton (P0 deliverable)
2. Confirm phase durations against your real calendar (10 §1)
3. First slice: "hello module" through the contracts — and the machine starts.
