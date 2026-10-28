# P0 Spike Verdict — RLS × TimescaleDB

**Date:** 2026-10-28 · **Spec:** `phases/P0/03-spike-rls.md` · **Evidence:** `docs/runbook/p0-spikes/rls-timescale.sql` (all SQL + result comments)
**Authority tested:** plan/04 §10 (double lock, NFR-06, 02 acceptance #1)
**Environment:** PostgreSQL 16 / TimescaleDB 2.17.2 (`nabd-postgres`, dev)

## Hypothesis

The DB-level second lock (RLS by `tenant_id = current_setting('app.current_tenant')`) survives TimescaleDB machinery: compression, continuous aggregates, and the restricted `ingest_writer` role.

## What we ran

Seeded 2 tenants × 1 asset × 1 device × 24 hourly readings (evidence PART 1); armed RLS + policy; test role `app_user` (NOT owner); compressed yesterday's chunk; built a 1-hour continuous aggregate; created `ingest_writer` NOLOGIN role.

## The four questions

### Q1 — Does RLS filter `readings` for a normal user? ✅ PASS

`app_user` + `app.current_tenant = A`: grouped count → **only tenant A (24)**; tenant B mirrored. Owner control (`nabd`, same query): **both tenants (48)** — proving the actor is the *policy*, not the data.

### Q2 — Does the policy survive COMPRESSION of a chunk? ⚠️ FAIL original construction → remediated

**Finding → the engine refuses the combination in BOTH orders:**
- RLS enabled → `ALTER TABLE ... SET (timescaledb.compress)`:
  `ERROR: compression cannot be used on table with row security`
- Compression enabled → `ALTER TABLE ... ENABLE ROW LEVEL SECURITY`:
  `ERROR: operation not supported on hypertables that have compression enabled`

Known upstream limitation (timescaledb issue #6425, open since 2023; matches our 2.17.2/pg16). A created-but-inert policy (RLS disabled) gave no warning at all — silent dead code. Leak demo: with RLS off and direct grants alive, `app_user` saw **48 rows / both tenants**.

**Working construction (verdict):** RLS **off** on the hypertable; the second wall = **tenant-filtered definer views**:
- `readings_per_tenant` (WHERE `tenant_id = current_setting(...)::uuid`)
- direct `SELECT` on `telemetry.readings` **revoked**; grant on the view only
- verified over the **compressed** chunk: denied direct (`permission denied`), 24/24 mirrored through the view.

### Q3 — Do continuous aggregates respect tenant scoping? ⚠️ LEAK confirmed → remediated (as plan/04 §10 predicted)

`app_user` + tenant-A setting reading `readings_hourly` **directly**: saw **both tenants (24+24)** — walls on the base table don't reach agg materialization: exactly the hazard plan/04 §10 predicted ("policies do not automatically cover materialized aggregate data").

**Working construction:** wall *views on the agg* — `readings_hourly_per_tenant`; direct agg grant revoked; denied/24/24 verified. **Dashboard read-path rule for plan/06:** tenants read only `*_per_tenant` views, never raw tables/aggs.

### Q4 — `ingest_writer` bulk-inserts across tenants (nothing else)? ✅ PASS

`CREATE ROLE ingest_writer NOLOGIN`; grants: USAGE(`telemetry`) + INSERT(`readings`, `device_status`) only.
- Cross-tenant two-row batch INSERT: **`INSERT 0 2`** ✅
- `SELECT` / `UPDATE` / `DELETE`: **`permission denied for table readings`** ×3 ✅
- Architectural bonus (accidental but load-bearing): the first probe made the writer *look up* device ids in `assets.devices` → **denied for schema `assets`**. That error IS the trust boundary spoken by the DB itself: the writer cannot resolve tenant/device — resolution belongs ABOVE the writer (ingest service: authenticated device row → tenant + device ids as literals, never payload-trusted). `tenant_id` & `device_id` must arrive as values resolved by trusted code.

## Verdict table

| Q | Construction tested | Result | Working construction |
|---|---|---|---|
| 1 | RLS policy on readings | ✅ PASS | RLS (baseline works) |
| 2 | RLS × compression coexistence | ❌ mutually exclusive (issue #6425) | **tenant-filtered definer views + revokes** |
| 3 | base walls → agg storage | ❌ leak shown | **views on the agg (`*_per_tenant`)** |
| 4 | `ingest_writer` grant profile | ✅ PASS | grants-only; tenant resolved app-side |

**⚠️_microsurprises_ recorded:** `refresh_continuous_aggregate` is a **procedure** → `CALL`, not `SELECT`; its "already up-to-date" NOTICE surfaced oddly after the failed first attempt (data present) — accepted, deeper check deferred to P1. Compression defaults picked `segmentby = tenant_id` (good for tenant-batched reads) with orderby `(device_id, channel_key, time DESC, seq)`.

## VERDICT: **PASS-WITH-NOTES**

None of the four questions is a blocker: every gap has a verified working construction. The notes are *permanent architectural truths*, not warnings to revisit later.

## Consequences for the Blueprint (plan/04 §10 amended accordingly)

1. **Hypertable `readings` (all time-series):** RLS is **unavailable with compression** — the second lock on this one table = tenant-filtered **definer views** + zero direct grants on base tables. Declared, not accidental.
2. **Plain tenant-scoped tables** (`device_status`, later `assets`-schema dashboards' targets): RLS **does** apply — normal policy pattern from §10; consider `FORCE ROW LEVEL SECURITY` since the app connects as owner-ish roles.
3. **Aggregates:** own per-tenant views; never expose aggs directly to tenant readers.
4. **Writer path unchanged but with evidence:** `ingest_writer` INSERT-only; tenant/device resolved by ingest service from device credentials; the spike *demonstrated the writer cannot self-resolve* (`assets` denied).
5. **Naming/pattern rule for P1+:** `*_per_tenant` views as the canonical tenant-exposure surface; migrations own policies; RLS enabled wherever tables aren't Timescale-machined.
6. Defer: version-upgrade retest of RLS × compression (issue #6425 fix) — a future spike candidate, not a P0 item.

## Lab residue (disposable)

Spike artifacts live in the dev DB only (roles `app_user`/`ingest_writer`, views, compressed chunk, cagg). Production schema comes from `db/migrations/` — **nothing spike-made ships to P1**; the *decisions* above do.

## Verdict per plan/10

**PASS-WITH-NOTES** — P0 continues to spec 04 (EMQX persistence).
