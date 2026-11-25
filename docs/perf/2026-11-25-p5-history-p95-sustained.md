# P5 spec 04 — history p95 under the Sustained profile (the DoD run)

- Date: 2026-11-25
- Host: **the owner's dev laptop** (8 cores, 11 GB usable). This is the same machine
  that carried the 2026-11-17 P4 referee run (2,000.2 rows/s × 600 s), so the
  capability is not new. The run was executed on the owner's explicit instruction
  after he challenged the earlier "staging only" framing (RULES §5's last line).
- API host: `Nabd.ApiHost` (Debug, .NET 10) on `http://127.0.0.1:5109`, additionally
  bound to `172.17.0.1:5109` for the duration because the EMQX authn/authz webhook
  calls `host.docker.internal:5109`; nothing else listened outside the host.
- Processing host: `Nabd.ProcessingHost` (Debug), 4 consumer shards, `FlushSize 2000`,
  `FlushIntervalMs 100`, `ReceiveMaximum 200`, aggregate window 800.
- Broker: EMQX 5.8.6, addressed at its container IP `172.18.0.4:1883` (no docker-proxy,
  the P4 lesson).
- DB: `timescale/timescaledb:2.17.2-pg16` (`nabd-postgres`, 1 GiB cap).

## The rider (Sustained profile)

| Piece | Value |
|---|---|
| Fleet | 10 processes × 10 devices = **100 devices, 20 msg/s each = 2,000/s nominal** |
| Scenario | `steady`, channel `temperature`, grid anchored (`--start-epoch 1795579509.244012`) |
| Flight | **04:04:59.749Z → 04:14:59.257Z = 599.5 s** |
| Stored in flight | **1,180,100 rows → 1,968.4 rows/s** |
| `raw_rejects` during the flight | **0** (the 6 rows in the table are an earlier wrong-channel probe) |
| PH RSS at end | 75,408 kB |

Stored < nominal because the simulators themselves sat at load average ≈ 16 on 8 cores
and each device delivered ≈ 19.7 msg/s. The number recorded here is the **stored**
rate — what the database actually absorbed while the reads were timed.

## The read under that storm

Tenant `7a01fb00…`, asset `7ca7f3c1…`, channel `level`: **518,400 seeded raw rows
over 30 days**, materialized hourly cagg, read at `hourly / bucket_seconds=3600 /
719 buckets`. A dev-minted 30-minute HS256 token (no password used, none printed).
500 requests per sample, 10 concurrent, through the real door
`GET /api/v1/assets/{id}/history`.

| Sample (UTC) | Rows stored in the same 5 s | Requests | Failures | p50 | **p95** | p99 | max |
|---|---|---|---|---|---|---|---|
| 04:06:09 → 04:06:14 | 9,700 (≈1,940/s) | 500 | 0 | 70 ms | **101 ms** | 129 ms | 132 ms |
| 04:08:18 → 04:08:23 | 9,000 (≈1,800/s) | 500 | 0 | 62 ms | **99 ms** | 154 ms | 183 ms |

Single-request baseline before the fleet (same range, same door): 78 ms.

## Verdict

**spec 04 box 3 — p95 < 2 s under the query storm: MET, on the owner's machine,
measured with its data volume.** Worst p95 = **101 ms** (≈20× inside the bar), zero
failed requests in 1,000.

## What this is and is not

- It **is** the DoD measurement: a 2,000/s-class sustained ingest with 10 concurrent
  30-day history reads timed through the API door.
- It is **not** a re-run of the NFR-01 referee: no drain-tail reconciliation, no
  per-device ack audit, and the fleet ran 599.5 s at 1,968 rows/s stored rather than
  the strict 2,000.2/s flight. The 2026-11-17 referee remains the NFR-01 record.
- The 30-day read is served from the materialized hourly cagg of a **different
  tenant**, so it exercises the wall plus the cagg read while the storm tenant writes
  raw rows. It does not measure reads against rows still in flight.

Rig (kept, local-only): `/tmp/opencode/fleet-run.sh`, `/tmp/opencode/fleet-devices.tsv`
(100 minted device tokens), `mint-rig.sh`, `mint_jwt.py`, `storm-sustained*.txt`.
Storm data left in the dev DB per the owner's instruction.
