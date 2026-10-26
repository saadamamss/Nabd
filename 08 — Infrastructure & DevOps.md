# 08 — Infrastructure & DevOps

**Environments, Deployment, Backups & DR, Observability, and Running Costs**

*Blueprint v0.2 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file defines **where the system runs, how changes get deployed, how disasters get healed, and what it costs to keep the lights on.** It operationalizes the initial targets from 02 (NFR-01…NFR-11) and the deployable map from 03 §5 on real infrastructure.

Ground rule inherited from ADR-007: **one managed VM + Docker Compose at MVP.** Everything here must keep working while we upgrade later (that's the growth-path honesty requirement of NFR-12).

## 2. Environments

| Environment | Where | Purpose | Data |
| --- | --- | --- | --- |
| **dev** | your machine: `docker compose up` | daily work, fast feedback loop | simulated/seeded |
| **staging** | the production-like VM (separate project dirs/ports) | PR validation, load tests, restore rehearsals | anonymized copy or seeded sim data |
| **production** | one managed VM (Hetzner/DO class, ADR-007) | paying users | real |

| Rule | Detail |
| --- | --- |
| Configuration | all diverging values via env files per environment (07 §7); **no config differences in code** |
| VM specs (initial) | 4 vCPU / 8 GB RAM / 80 GB SSD (NFR-01 design point) |
| DNS/TLS | `api.<domain>` + `app.<domain>` + `mqtt.<domain>` (MQTT listener) behind a reverse proxy (Caddy — auto-Let's-Encrypt) |
| Access | SSH keys only; password auth disabled; fail2ban; automatic security updates on |

## 3. CI/CD Pipeline (GitHub Actions)

```
PR  ─▶ build ─▶ unit tests ─▶ module arch tests (NetArchTest)
        │      └▶ integration tests (spin Postgres+RabbitMQ+EMQX containers)
        │      └▶ isolation suite (02 #1, 07 §11) — every PR
        │      └▶ code coverage gate (70% on modules, rising rule)
merge ─▶ build images (multi-arch not needed; linux/amd64)
        └▶ push to GHCR (tags: sha + latest)
        └▶ deploy to staging ─▶ migration dry-run ─▶ smoke tests (health, ingest 500 msgs, sim alert)
manual approval ─▶ deploy production (same images, same steps, health checks)
```

| Policy | Statement |
| --- | --- |
| Deploy cadence | whenever green; production deploys are manual-approved (small safety for solo dev) |
| Rollback | previous image tag redeploy = 2 minutes, documented in runbook |
| DB migrations | **expand/contract discipline**: new column added and backfilled before code reads it; no destructive migration in the same release as code that uses it — two releases minimum for drops |
| Zero-downtime | compose rolling restart of API Host first, then Processing Host (the ingest buffer absorbs the gap — the design reason the queue exists) |
| Versioning | CalVer-ish: `yy.mm.patch` + commit sha in `/health` response |
| Runtime | .NET 10 LTS pinned via `global.json`; Docker base images pinned to `mcr.microsoft.com/dotnet/*:10.0` digests (.NET 8/9 reach end of support on 2026-11-10) |

## 4. Provisioning (IaC honesty)

- **MVP level:** a single `docs/runbook/` page: exact console steps to rebuild the VM from scratch (user, firewall, docker, compose, secrets, DNS) — reproducible by following it twice. Plus one `provision.sh` script capturing those steps.
- **Terraform:** deferred to V1 (when a second VM/staging split makes scripted provisioning pay for itself). It's on the V1 list from 00; not skipped forever.
- Secrets per environment live only in the env files on that VM (07 §7); CI never carries production secrets — deploy step pulls from the VM's env store via a scoped deploy key.

## 5. Backups & Disaster Recovery (acceptance #6 territory)

| Item | MVP choice | Notes |
| --- | --- | --- |
| DB backups | **pg_dump full logical backup, nightly + after-deploy**, gzip, **encrypted (07 §8 key)**, pushed off-VM to object storage (S3-compatible / Hetzner Storage Box) | Timescale hypertables dump fine at MVP scale |
| Backup retention | 14 daily + 8 weekly | cycles off-VM |
| Restore rehearsal | **weekly automated job on staging: restore latest backup into a scratch DB, run smoke queries, report result** | the check that separates having backups from having *usable* backups — this is 02 acceptance #6 automated |
| Files/config | compose + env templates in git; secrets excluded — documented as the rebuild path | |
| Monitoring the backup job itself | success/failure metric + alert (a backup that silently stops running is the classic silent failure) | |

### DR targets & runbook (single honest table)

| Scenario | RPO | RTO | Path |
| --- | --- | --- | --- |
| Bad deploy | 0 | ~5 min | image rollback (§3) |
| DB corruption / delete accident | ≤ 24 h (nightly) | ≤ 2 h | restore latest backup on staging (rehearsed) → swap |
| VM loss / provider outage | ≤ 24 h | ≤ 4 h | new VM + runbook rebuild + latest backup restore |
| Broker/queue loss (RabbitMQ/EMQX) | 0 data | minutes | devices buffer via QoS1 (05 §7 doctrine); re-create from runbook |
| Region-wide cloud outage | ≤ 24 h | 1 day | the runbook paths assume a different provider region |

**Recommended earlier (v0.2):** WAL archiving with pgBackRest or WAL-G takes RPO from ≤ 24 h to minutes (PITR) for a few hours of setup — worth pulling into P6. A second host stays V1.

Note: "zero loss" in NFR-04 means loss-free across process/broker restarts. On host or disk loss the loss window equals the RPO above.

## 6. Observability Stack (NFR-10 operational)

| Layer | MVP implementation | Deferred |
| --- | --- | --- |
| **Metrics** | each host exposes Prometheus format `/metrics`; Prometheus + Grafana containers on the same VM; dashboards for the 11 metrics named in 06 §11 + host (CPU/RAM/disk/IO) + DB (connections, bloat, query latency) | managed stack |
| **Logs** | Serilog structured → per-host JSON file (rotation) → `docker logs` accessible; grep is the MVP alert | Loki / centralized (V1) |
| **Alerts to you** | Alertmanager → **your email**; rules: ingress rate anomaly (0 readings for 15 min on N active devices = pipeline dead), error-rate spikes, disk > 80%, backup job failed, cert expiry < 14 days | phone channel (V1) |
| **Health** | `/health` (liveness: process up; readiness: DB reachable, queue reachable, broker session ok) — used by deploy checks and the external monitor | per-module health detail |
| **External uptime** | free external monitor pinging `/health` + landing | public status page (V1, honesty-first) |
| **Tracing** | correlation IDs in logs (request id, device id, batch id) | full OpenTelemetry (V1; ADR-010) |

> **v0.2:** Grafana and Alertmanager are *optional at MVP*. The minimum that satisfies NFR-10 is: `/metrics` on each host, `/health`, an external uptime monitor, and simple email alerts for pipeline silence, backup failure, disk > 80% and certificate expiry. Adopt Grafana/Alertmanager the first time you actually feel the pain of not having them — they are one more set of containers to patch and secure.

Rule: **every alert must have a menu ("if this fires, open runbook X")** — alerts without runbook entries get deleted, not accumulated. These runbooks live in `docs/runbook/`.

## 7. Build Workflow With AI Agents (from `telemetry.md` working method — concrete here)

| Step | Who | Gate |
| --- | --- | --- |
| Task definition | you/agent writes the FR/NFR test slice, small | — |
| Implementation | AI agent, in one module's room only (solution filter) | arch tests must pass |
| Review | you read the diff against **06 §9 review points** and **07 §10 checklist** | unchecked item = rework |
| Tests | agent writes, you read the assertions not just the code | coverage gate |
| Merge | PR into main; CI runs the full suite | green or fail |

Loop cadence proposed: **one shippable slice per day** (with 6–8 h/day from 00 D2, that's realistic and keeps the roadmap in 10 honest).

## 8. Running Costs (planning budget, USD/month)

| Item | Estimate | Notes |
| --- | --- | --- |
| Production VM (4 vCPU/8 GB, 80 GB) | $25–45 | Hetzner/DO class |
| Staging VM (beefy only during load tests) | $10 (small box; scale-up during 09 tests) | |
| Object storage backups | ~$5 | 14 daily + 8 weekly compressed dumps |
| Domain + DNS | ~$1 | amortized |
| Email relay (transactional) | $0–15 | free tiers cover MVP traffic; vendor per 04 §9 |
| Monitor/uptime ping | $0 | free tier |
| GitHub Actions | $0 at this volume | free tier |
| **Total MVP** | **≈ $45–70/month** | comfortably inside 00's "small budget" constraint |

The cost model supports pricing analysis: with even 10 Starter tenants (≈ $25–65/mo each, roughly $450/mo in total), infrastructure is ≈ 10–15% of revenue — margin health noted in 01 §5.2.

## 9. Hardening Checklist for the VM (execute once, re-check on audit day)

- [ ] SSH keys only, root login disabled, fail2ban active
- [ ] Unattended security updates enabled
- [ ] Firewall: public ports = 443 (proxy), 8883 (MQTT TLS), SSH from your IP range only — everything else internal-only
- [ ] Docker: no root daemon socket exposure to any container; images pinned (07 §10)
- [ ] Backups encrypted + off-box + **restore rehearsal scheduled weekly** (§5)
- [ ] `docker compose` network internal for DB/queue/broker mgmt ports; no direct DB exposure
- [ ] Cert auto-renewal verified + expiry alert armed (§6)
- [ ] Disk usage alert armed at 80% (telemetry is a growth workload — disk is the first thing that fills)

## 10. Open Points in This File

| # | Point | Default |
| --- | --- | --- |
| O | Managed Postgres vs self-hosted at MVP | self-hosted (cost + Timescale extension availability); trigger to revisit: sustained disk >80% or ops fatigue |
| P | Grafana self-host vs Grafana Cloud free tier | self-host on same VM (one less external dependency) |
| Q | Multi-AZ / multi-host | V1 |
| R | Container CI runner | GitHub-hosted (simpler; self-hosted runner = later security trade-off) |

## 11. Next Step

- [ ] Confirm the VM hosting choice + budget line (Hetzner vs DO — either is fine; pick by where you can pay simply)
- [ ] Then → **09 — Testing & Performance** (the suite that implements 02's acceptance list: load, chaos, isolation, security)
