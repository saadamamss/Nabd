# CHANGELOG — Blueprint v0.1 → v0.2

*Result of the full blueprint review. Every change below is already applied in the files. Items marked **judgment call** are my recommendations — you can veto any of them and the file named next to it is where to revert.*

## A. Corrections (something was wrong or unsafe)

| # | Where | What changed | Why |
| --- | --- | --- | --- |
| A1 | 03 ADR-002, 08 §3 | .NET 8 → **.NET 10 LTS**, pinned via `global.json` | .NET 8 and .NET 9 reach end of support on 2026-11-10; .NET 10 is supported through Nov 2028 |
| A2 | 02 FR-22/NFR-04, 03 §6, 04 §11, 05 §1/§6/§7 | "ack ⇒ stored" replaced by precise wording: consumer acks only after DB commit; device→broker is at-least-once | A QoS1 PUBACK comes from the broker, not from the database |
| A3 | 02 NFR-07, 04 §5, 05 §11, 07 §4/§7/§10 | Device tokens: **HMAC-SHA-256 with a pepper**, not argon2id | Tokens are 32-byte random secrets; a slow hash adds CPU load and a reconnect-storm DoS vector without adding security |
| A4 | 02 FR-23, 03 §6/§8, 04 §6, 05 §6 | `COPY` → multi-row `INSERT ... ON CONFLICT DO NOTHING`; `seq` is `NOT NULL DEFAULT 0` | `COPY` cannot skip duplicates; NULLs in a unique key never conflict, so duplicates slipped through |
| A5 | 03 §4, 04 §5/§6, 05 §9, 06 §4 | New `telemetry.device_status` table (last-seen held in memory, flushed every 10–30 s); `devices.last_seen_at` removed | One UPDATE per reading is a hot-row write storm, and Ingestion was writing into the Assets room (breaking the room rule) |
| A6 | 04 §7, 06 §3.2/§3.4 | Rule state in memory, persisted on transitions only; `fired` flag stops repeated `CrossingDetected` | Per-reading state writes would be ~2,000 extra writes/s; the pseudo-code re-emitted on every violating reading |
| A7 | 04 §10, 03 §8 | Ingestion writer role + **RLS verification spike** (compressed chunks, continuous aggregates, writer role) | A batch can span tenants but `SET LOCAL` is per transaction; RLS coverage of aggregates must be tested, not assumed |
| A8 | 02 NFR-04, 08 §5 | "Zero loss" scoped to restarts; host/disk loss window = RPO; WAL archiving recommended in P6 | The old wording contradicted the 24 h RPO |
| A9 | 03 §6.3, 05 §4/§7 | HTTP 202 = durably queued (publisher confirm), not "stored" | Honest semantics for the HTTP path |
| A10 | 01 §5.2, 08 §8 | Pricing arithmetic fixed (graduated brackets, worked examples: $57 / $65 / $120 / $360); infra-cost ratio recomputed; USD/EGP note | Examples did not follow from the table; Starter is $65 max, not $45 |
| A11 | 03 §6.2 | Watchdog interval 1 min → 30 s | Matched 06 §4 |

## B. Additions (gaps in the product)

| # | Where | What changed | Why |
| --- | --- | --- | --- |
| B1 | 00, 01, 02 (FR-34, FR-38), 03, 04 §9, 06, 07, 10 P4, 11 P5 | **Telegram push** as the MVP's real-time alert channel (+ linking flow, `user_channels`, `message_templates`); WhatsApp/SMS stay V1 | Email-only alerts miss a 3 AM failure — the core promise |
| B2 | 05 §13, 02 (FR-17, acceptance #10), 09 §3, 10 P2/P6, 11 B6, 01 D13 | **Reference Starter Kit** (DIY ESP32 + probe, then one commercial kit) with install guide and acceptance check #10 | The activation metric needs a real sensor; trial users had no hardware path |
| B3 | 02 FR-44 | Basic CSV export promoted from L to S (PDF log stays V1) | Cold-chain buyers often need logs for inspections |
| B4 | 01 §7.4, §8 | Activation tracked as *demo* (simulator) vs *real* (own sensor) | A simulator alert is not proof of value |
| B5 | 01 §7.6, 10 P7 | **Acquisition plan** (weekly outreach cadence, with its assumptions labelled) | The traction gate assumed visitors without a plan to create them |
| B6 | 09 §4–§6, 07 §11 | New tests: duplicate flood, reconnect storm, EMQX mid-stream restart | Cover A2/A3/A4 |
| B7 | 11, 12 §8 | Risks T10–T12, B6, P5, S5; AI gotcha rows 8–11 | Capture the review findings so they are not repeated |

## C. Plan changes

| # | Where | Change |
| --- | --- | --- |
| C1 | 10 §1, §3, §6, README | Schedule re-based from 8–12 to **14–20 weeks** (central ≈ 17); phase week ranges and buffer (3 weeks) updated |
| C2 | 10 P0 | Two P0 spikes: RLS × Timescale, EMQX persistence behavior |
| C3 | 02 §6, 10 DoD/G3, README | Acceptance list is now **10** checks |

## D. Judgment calls you may veto

| Call | Where it lives | To revert |
| --- | --- | --- |
| RabbitMQ is **kept** but gets a 1-day spike at the start of P3 (direct batched insert vs publisher-confirm queue); keep it only if it earns its place | 03 ADR-005, 05 §4, 10 P3 | Remove the spike sentence and the P3 row prefix |
| Grafana/Alertmanager become **optional** at MVP | 08 §6, 10 P6 | Delete the v0.2 note in 08 §6 |
| Telegram (not WhatsApp) as the first push channel | 01 D14, 02 FR-34/38 | Swap the channel name; WhatsApp costs per message and needs business approval |
| Pulling WAL archiving into P6 | 08 §5 | Keep nightly dumps only |
| CSV export as an MVP *should* | 02 FR-44 | Change S back to L |

## E. Deliberately not changed

- **No pre-build interviews** — your decision stands. 01 §7.7 only *suggests* two cheap, optional additions (one real fridge as Design Partner #1, 3–5 short conversations).
- The room/module model, the generic template mechanism (acceptance #9), the traction gate numbers, and the overall phase order.

## F. Still to be verified by running things (not by reading)

1. EMQX session/message persistence behavior on restart for the edition and config you choose (decides the final wording of NFR-04).
2. RLS behavior with compressed chunks and continuous aggregates, and inserts into compressed chunks with the unique key.
3. Competitor prices in 01 §3 (captured 2026-10-26) — re-check before publishing any price sheet.
4. Domain/trademark availability for the name Nabd (01 D8).
