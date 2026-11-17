# P4 spec 01 — the Sustained bar, CLOSED (2026-11-17)

**Official referee: GREEN — 2,000.2 rows/s sustained × 600 s, 100.0000% delivery, zero loss.**
NFR-01 (plan/02): stored ≥ 2,000 rows/s at the official profile (100×20/s + 20×1/s, no rider).
Recorded as row 1 in plan/09 §9/H.

## Files

| File | What it is |
|---|---|
| `referee-run1-NOT-MET.log` | Official run 1: 1,998.5/s (exit 3) — the harness undershooting its own profile by 905/1,200,000 (per-device deadline grid anchored at connect time). Pipeline delivered 100.0000%. |
| `referee-run2-GREEN.log` | Official run 2 after `--start-epoch` grid anchoring: **2,000.2/s (exit 0)**. Stored 1,200,119 == delivered; 22 delivered-but-unacked at kill (QoS1 redelivery covers), reconciled per-device. |
| `gate3-pidstat-forensics.log` | The instrumented 60 s gate that convicted the HARNESS: 120 sim processes = 427% of 8 cores while EMQX idled at 71% of one core; docker-proxy (single thread) 41%. |
| `gate4-pidstat.log` | Post-fix gate: 12 processes × 10 devices + container-IP broker → sims 156%, proxy 22%, still broker-clean. |
| `referee-run2-pidstat.log` | The official run's CPU split: sims 130% · EMQX 84% · PG 40% · PH 28% · proxy 13%. |

## The lever chain (from the 2026-11-15 row's 14.7× gap to green)

1. **Batching sink** (P4 spec 01 core): FlushBuffer → FlushPump — ONE resolution, ONE multi-row INSERT, commit-synchronized acks (2026-11-15 run measured 136/s serial).
2. **Sharding**: 4 consumer connections × Receive Maximum 200 (aggregate window 800) — killed the broker connection kills.
3. **Overlapping ack window**: launch-and-return after commit, 4 chunks × 64 sliding — flush cycle 685–1,139 ms → 10–31 ms.
4. **Harness honesty** (owner-ruled): fleet mode (one process × 10 devices), EMQX container-IP direct (no docker-proxy), `--start-epoch` anchored grid + anchored kill, referee window law (drain after END), stale device a-1 removed → profile = exactly 2,000/s.

Margins are knife-edge BY DESIGN: stored == delivered means any real loss fails the bar.
