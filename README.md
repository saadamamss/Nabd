# Nabd — Asset Telemetry & Anomaly Detection SaaS

**One product: tell an SMB that its asset is about to fail, before it fails.**
Modular monolith on .NET 10 — 9 sealed "rooms" (03 §3) enforced by architecture tests, Postgres+Timescale, EMQX, one VM at MVP.

| P0 status | Done ✓ |
|---|---|
| Solution skeleton (9 rooms + contracts, 2 hosts, simulator) | `Nabd.slnx` |
| Room-boundary enforcement (fail the build on any wall breach) | `tests/Nabd.ArchTests` |
| "Hello module" DoD tests (contracts + Testcontainers row) | `tests/Nabd.HelloModule` |
| Dev compose (postgres default; brokers behind `--profile` in P3) | `docker-compose.dev.yml` |
| CI (build → arch → integration, < 15 min budget) | `.github/workflows/ci.yml` |

## Start here

- **Environment & P0 steps:** [docs/dev/SETUP.md](docs/dev/SETUP.md)
- **Working rules (the 10-line version):** [CONTRIBUTING.md](CONTRIBUTING.md)

## Layout

```
src/Hosts/          API Host (ASP.NET Core) · Processing Host (worker)
src/Modules/<Room>/ <Room> (implementation) + <Room>.Contracts (the only wall door)
src/SharedKernel/   integration events + Result (03 §8)
src/Tools/          simulator — a real device, never a shortcut (05 §10)
tests/              ArchTests (room walls) · HelloModule (P0 DoD)
docs/dev/           setup + handover guides
docs/runbook/       spike proofs + verdict papers (P0) — earlier: ops runbooks (P6)
phases/             FIELD MANUALS — per-phase small specs (P0 live now)
plan/               the v0.2 Blueprint (13 files) — local only, gitignored
db/migrations/      schema as code (P0 spec 02): 0001 identity · 0002 assets · 0003 telemetry
scripts/            environment provisioning
```

*The blueprint lives in `plan/` on this machine only — the code repo tracks code.*
