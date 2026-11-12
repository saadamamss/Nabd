# Provisioning — the token-shown-once flow

> **Scope honesty:** v0 draft (P2). This maps the flow onto the REAL platform doors as they exist today (specs 03/04/05). No fantasy endpoints. QR *generation* is a later nicety — the QR **contents** are specified here.

## The actors

- **Admin** (tenant_admin role) — owns the platform side: assets, devices, minting
- **Installer** — a non-engineer with the device in hand; receives exactly three values

## The flow, on real doors

```
Admin (logged in, Bearer JWT)
│
├─ 1. the device lives under an ASSET
│     POST /api/v1/assets/{assetId}/devices        {"name": "Fridge 1 — sensor"}
│     → 200 { device: {...}, token: "…" }          ← the ONLY moment the token exists on a wire
│
├─ 2. hand the installer the slip (paper or QR):
│        broker   = the broker address
│        deviceId = device.id from the response
│        token    = token from the response        (never again retrievable)
│
├─ 3. if the token was lost / leaked / mistyped — REISSUE, don't despair:
│     POST /api/v1/devices/{deviceId}/token/reissue
│     → 200 { device: {...}, token: "NEW" }        ← new token shown once,
│                                                      the OLD one is revoked at the same instant
│                                                      + two audit log lines (revoked, issued)
│
└─ 4. stolen device / end of life — KILL it:
      DELETE /api/v1/devices/{deviceId}
      → 204   the token dies everywhere within the measured SLA (62 ms in the suite, ≤ 60 s bound)
```

## Why "shown once" is a feature, not an inconvenience

The platform stores only an HMAC-SHA-256 hash of the token (NFR-07) — nobody, including us, can read a token back. A lost token is not a support call; it is one `reissue` door that instantly invalidates the old credential. This is FR-12's kill switch doing its job.

## QR contents (v0 spec — the generator is a later nicety)

A QR carrying the provisioning slip, one string, URL-encoded:

```
nabd://b=<broker>&d=<deviceId>&t=<token>
```

| Field | Meaning |
|---|---|
| `b` | broker host (and port when non-default) |
| `d` | deviceId — the MQTT username |
| `t` | the token — the MQTT password (shown once) |

P6 hardening will move `b` to the TLS endpoint (8883) and can extend the scheme — the fields above are the contract's floor, not its ceiling.

## What the installer NEVER sees

- Any other tenant's data (RLS wall), any admin credential, the platform's internal secret
- A "recovery" path for a lost token — there is none; reissue is the recovery
