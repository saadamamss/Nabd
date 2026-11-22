# P4 spec 10 — `warmup-demo` live MQTT leg

**2026-11-21 23:51–23:56 UTC · agent-run on the owner's request.** This is the
real-transport run of step 8 in `phases/P4/10-warmup-demo-e2e.md`: simulator →
EMQX → ProcessingHost → validation → store → RulesEngine → Alerting → email.
The 5 s / 60 s bars are E2E #2's in-process measurement; this run owns the
**MQTT leg** and the story on the wire.

## Rig

- Dev Postgres + EMQX containers (`nabd-postgres`, `nabd-emqx`, broker on
  `127.0.0.1:1883`), ApiHost on `:5109`.
- `Nabd.ProcessingHost` restarted on the spec-10 build (`AddP4Vertical`),
  4 MQTT consumer shards, flush trigger 2000 / 100 ms timer, watchdog 30 s.
- Throwaway tenant `b5dfae17-82f7-4a9b-a26d-08065a926392`, asset
  `ae58e005-…`, device `dc3c8ec3-…`, minted through the real doors.
  The mint seeded the fridge rule unchanged: **temperature 2–8 °C, 60 s, warn**
  (+ offline 30 min).
- Simulator: `--scenario warmup-demo --rate 1 --run-for 310 --channel temperature=4`,
  same profile the E2E feeds. Token passed only on the command line.

## The story (UTC)

| Event | Wall time |
| --- | --- |
| sim start / `phase=stable` | 23:51:22.405 |
| `phase=door` (9.5 °C) | 23:52:22.383 |
| `phase=ramp` — **0 alerts** at t≈70 s | 23:52:28.380 |
| `crossed 8.0 °C at seq=98` (t=97) | 23:52:59.382 |
| `phase=hold` (t=126) | 23:53:28.382 |
| t≈130 s mid-window snapshot — **0 alerts** | 23:53:32.383 |
| `60 s above 8.0 °C at seq=158` (t=157) | 23:53:59.381 |
| **alert visible door** (`GET /api/v1/alerts`) | **23:53:59.855** (+0.474 s vs marker, +0.611 s vs t=157) |
| clean stop `warmup-demo: stopped after 310 s, last seq=310`, exit 0 | 23:56:32.414 |

## Evidence

- **Readings:** 310 stored (max `time` 23:56:31.378), `raw_rejects` 0.
- **Alerts:** exactly one, `threshold | warn | open`, `opened_at`
  23:53:59.379, detail value **12.0** — the door beat opened nothing.
- **Events:** exactly one, `opened`.
- **Email:** `email | sent | attempts 1 | alert_raised | sent_at`
  23:54:03.708594 → **+4.33 s after the fire** (bar 60 s).
- **Reconcile guard (spec 02):** 0 `ALARM`/`reconcile` lines in the 630 PH log
  lines produced during the run.

Raw logs: `/tmp/opencode/drill-warmup.log` (orchestrator),
`/tmp/opencode/sim-warmup.log` (full simulator output), `/tmp/opencode/ph.log`
(PH). Throwaway tenant stays in the dev DB.

## Note

`alert_events.at` for `opened` equals `opened_at` by construction (the service
stamps the event with the reading's time), so the measured latency is the wall
clock line above, not the DB difference.
