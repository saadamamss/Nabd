# 07 — Platform & Security

**Authentication, Authorization, Isolation Enforcement, Secrets, and the Security Review Checklist**

*Blueprint v0.1 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This file consolidates **all security-facing decisions** into one place: how users log in, how devices authenticate, what each role may do, how secrets live and rotate, what gets logged for audit, and — critically — the **AI-code review checklist** that operationalizes 00's risk item ("over-reliance on AI-generated code").

It pulls together threads scattered across 04 (RLS, token hashing), 05 (device credentials, TLS) and 02 (NFR-06/07) and completes them.

## 2. The Public Attack Surface (know it, guard it)

| Surface | Protocol | Who touches it | Main threats | Defenses |
| --- | --- | --- | --- | --- |
| Web app + login API | HTTPS | humans | credential stuffing, session theft | rate limiting, argon2id hashing, HttpOnly cookies / short JWT, lockout backoff |
| HTTP ingest `POST /ingest/v1` | HTTPS | devices/gateways | garbage floods, token guessing, quota abuse | Bearer device tokens, per-device rate limit, bounded queue, validation catalog (05 §6) |
| MQTT 8883 | TLS | devices | rogue publish, topic squatting | per-device ACL bound at connect (05 §3); publish-only |
| SignalR hub | WSS | logged-in users | impersonation | same JWT auth as REST; tenant scoping per connection |
| Marketing site | HTTPS | everyone | static — no dynamic surface beyond a contact form | form rate-limited |

Everything else (Postgres, RabbitMQ, EMQX mgmt, backups) lives on the **internal network only**, firewalled off from direct internet access.

## 3. Human Authentication

| Aspect | Decision | Notes |
| --- | --- | --- |
| Password hashing | **argon2id**, per-user salt | bcrypt acceptable, argon2id the modern default |
| Login flow | email + password → JWT access token (15 min) + refresh token (7 days, stored hashed in `refresh_tokens`) | refresh rotation on each use; revoke = delete row |
| Session revocation | logout = refresh revoked; **password change revokes all user sessions** | security events real users understand |
| Reset flow | emailed signed link (15 min single-use) | same bilingual templates (04 §9) |
| Brute force | per-IP + per-account backoff: 5 fails → escalating delay | no permanent lockouts (frustration complaints cost more than they save at MVP) |
| Headers | HSTS, X-Content-Type-Options, Content-Security-Policy (even from day 1 — costs one line, prevents XSS classes) | |
| User enumeration | identical responses/timing for wrong-email vs wrong-password | AI-generated auth code habitually fails this test — checklist item |

MFA: **not in MVP** (target users are SMB staff; risk disproportionate to drop-off). Listed in 00 as V1 candidate (TOTP first).

## 4. Device Authentication (recap from 05, decisions formalized)

- One credential per device: `deviceId` (public) + 32-byte random `deviceToken` (secret), **shown once, stored only as argon2id hash**.
- MQTT: EMQX HTTP auth plugin → our `assets` service check (issued + unrevoked + active + tenant match) → connection-level ACL binds the tenant/device topic.
- HTTP: `Bearer` token → same check per request.
- Revocation propagates within the auth-cache TTL (60s default) — acceptable window, logged as security event.
- Spring rotation: re-issue invalidates instantly; no global secret rolls needed per device.

## 5. Authorization (RBAC)

One source of truth = the matrix in 02 §5. Enforcement is **two-layered** (defense in depth):

1. **API layer:** every endpoint carries an authorization attribute/query (`Viewer`, `TenantAdmin`, `PlatformAdmin`) + implicit tenant scoping from the token.
2. **Service layer:** functions doing cross-tenant reads/writes assert permissions again — the API layer alone is not trusted (one naive refactor is all it takes to lose an attribute).

| Rule | Detail |
| --- | --- |
| Verb check | Viewer = read-only **everywhere**, enforced in services too |
| Cross-tenant rule | any API path taking an `assetId`, `deviceId`, `alertId` resolves the entity then asserts `entity.tenant_id == caller.tenant_id` — including PlatformAdmin logging that it crossed |
| Admin module | bypasses tenant scoping via the dedicated `BYPASSRLS` DB role — every query executed through it is audit-logged (04 §10) |

## 6. Tenant Isolation (summary — full design in 04 §10)

- App-level tenant filter on every repository call (test: every endpoint).
- DB RLS policies as the belt-and-suspenders net; application sets `app.current_tenant` per transaction.
- CI runs the **isolation guardrails suite** on every pull request: tenant A user probes tenant B's resources through every endpoint (both IDs and guessed IDs) — 0 leaks allowed, or build fails.
- Reading a duplicate-asset story: device tokens can never publish into another tenant's topic (broker ACL, 05 §3).

## 7. Secrets & Key Management

| Secret class | Where it lives | How it moves |
| --- | --- | --- |
| Environment config (DB passwords, queue creds, SMTP) | `.env`-style per environment, git-ignored; injected via compose secrets/env | never in code, never in logs (checklist §10) |
| SMTP creds in `notifications.channel_config` | encrypted at rest with an app key (`SECRET_KEY` env; AES-GCM) | can be re-encrypted on key rotation |
| Device tokens | argon2id hash only (04 §5) | backup DB dumps expose nothing usable |
| Signing keys (JWT) | env var, 256-bit random, rotatable (kid header) | rotation invalidates sessions — acceptable; announced |
| TLS certs | Let's Encrypt via reverse proxy | auto-renewing |

**Never on the roadmap for MVP secret storage:** Vault/KMS — overkill for one VM; revisit if 08's infra grows to multi-host (recorded, not religious).

## 8. Data Protection

| State | Protection | Notes |
| --- | --- | --- |
| In transit (external) | TLS 1.2+ on everything public | HSTS on |
| In transit (internal) | MQTT listener TLS-only; internal service traffic inside the compose network — LAN-trust documented as an accepted MVP simplification | revisit if hosts split |
| At rest | disk/provider-level encryption (VM provider) + DB dumps encrypted before leaving the server | note honestly: no native Postgres TDE in community edition — provider disk encryption is the mitigation |
| PII collected | email, display name, tenant name — **that's it** | no customer PII in telemetry paths (readings are machine data) → small footprint = small risk |
| Data rights (NFR-13) | export (CSV per module) + full tenant deletion | deletion = FK-cascade + readings cleanup job; a scripted "tenant erasure" command with dry-run |

## 9. Auditing & Security Logging

| Log | Content | Retention |
| --- | --- | --- |
| Auth events | login ok/fail, password change, reset, logout | 90 days |
| Device events | token issued/revoked/first-seen, auth failures per device | 90 days |
| Admin actions | tenant enable/disable, token ops, every admin-module query | 400 days |
| Alert lifecycle | already in `alert_events` (06 §9) | with alerts |

Rules: structured logs, **no secrets/tokens/passwords ever logged (masking enforced by logger config)** — security logs are themselves rate-limited so an attacker can't flood our log storage (extends 05 §11).

## 10. The AI-Code Security Review Checklist (00 risk #7 → operational)

Every pull request that touches the layers above gets checked against:

- [ ] **Authz on every endpoint** — no "obviously fine" exceptions (both API-layer and service-layer checks, §5)
- [ ] **Tenant filter present** in every new query — RLS is the safety net, not an excuse; the app layer must stand on its own
- [ ] **Parameterized SQL only** — no string-built queries anywhere, including the migrations
- [ ] **Input validation at 05 §6 catalog** — new endpoints conform, not "mostly"
- [ ] **No secrets in logs / exceptions / stack traces** (logger masking config active)
- [ ] **Error responses uniform** (no stack traces to clients; problem-details format)
- [ ] **Timing-safe comparisons** for anything holding a secret — never naive string equality
- [ ] **Dependencies scanned** (`dotnet list package --vulnerable` in CI; dependabot-like updates monthly)
- [ ] **Docker images** non-root user, pinned base image digests, no root daemon sockets in compose
- [ ] **Rate limits** on every public I/O endpoint (including the contact form)
- [ ] **CORS locked** to the app's own origins
- [ ] **New permission/role change** → update the 02 §5 matrix in the same PR (docs-as-code discipline)

Items not checked = the PR doesn't merge. This is the operationalization of "you are the standing quality control," per 00 §10 (risks) and the working-method section in `telemetry.md`.

## 11. Security Test Hooks (consume by 09)

| Test class | Proves |
| --- | --- |
| Auth: wrong email vs wrong password indistinguishable (status + timing) | no user enumeration leak |
| Auth: refresh-token replay (reuse after rotation) | theft detection: replay = full session revoke |
| Authz: Viewer attempts 5 write mutations | 403 everywhere |
| Isolation: the full cross-tenant probing suite (04 §10) | zero leaks through any surface |
| Device: revoked token still connects within TTL window === exactly ≤ 60s | revocation SLA measured, not assumed |
| Ingest: oversized batch / 10k-request flood | 4 locked: E429, no degradation to other devices |
| SQLi: fuzz the search/filter parameters | no injection paths |
| Config: grep the built images (not source) for secrets patterns | nothing baked |

## 12. Compliance Posture (honesty per 00 §6/7)

At MVP we claim **nothing** in the certification space (no ISO 27001, no SOC 2 — these follow revenue, per the earlier telemetry discussion). What we do claim, and can prove in the files:

- Tenant isolation design + automated test evidence (04/02)
- Data export & delete on request (NFR-13)
- Security event auditability (§9)
- Known-limitations document (§8 at-rest scope) — reviewed per release

## 13. Open Points in This File

| # | Point | Default |
| --- | --- | --- |
| K | TOTP MFA | V1 (00 lists SSO there; MFA rides the same Identity work) |
| L | Session/device list page for users ("my devices, revoke") | V1; MVP = password change revokes all |
| M | WAF / bot filtering | reverse proxy + nginx rate limits only; Cloudflare considered at launch (cost/latency trade-off) |
| N | Internal-network microsegmentation | one flat compose network at MVP |

## 14. Next Step

- [ ] Review the checklist in §10 — that's *your* standing role with AI agents
- [ ] Then → **08 — Infrastructure & DevOps** (environments, deployment, backups/DR, observability stack, cost)
