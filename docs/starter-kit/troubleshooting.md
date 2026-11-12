# Troubleshooting — symptom → cause → fix

> **Scope honesty:** v0 draft (P2). Every log line quoted here is REAL — taken from the live ladders of 2026-11-12 (specs 05/06), not invented. P6 extends this table with TLS/certificate rows once the 8883 endpoint exists.

## The four guaranteed rows (05 §13)

| Symptom (what you see) | Cause (what's true) | Fix (what you do) |
|---|---|---|
| **Device never joins Wi-Fi** (`Nabd-Setup` hotspot absent, or it can't reach the router) | wrong band (5 GHz-only router vs 2.4 GHz device), weak signal at the install spot | put the phone next to the device during setup; move the router or use 2.4 GHz; reboot the device (power off 10 s) |
| **Broker refuses: `Connection Refused: not authorised`** (console / broker log: `authentication_failure … reason: not_authorized`) | wrong token, or the token belongs to a **revoked/inactive** device — the credential check is exact-match by hash | re-mint via the reissue door (`POST /api/v1/devices/{id}/token/reissue`), paste the NEW token — the old one is dead by design |
| **Connects, but messages vanish** (broker log: `authorization_permission_denied … source: http`) | wrong topic — the device may publish **only** `t/{tenantId}/dev/{its-own-deviceId}/v1`; a typo in any part (a stray `0` in the tenant id happened live) means a denied, silently-discarded publish | re-paste the slip values; remember QoS 0 denials are silent — test with QoS 1 / MQTT 5 to get the visible `Not authorized` |
| **Connects, then drops every few minutes** (`keepalive` timeouts in the broker log) | clock/TLS mismatch at TLS endpoints (future), or keepalive starvation: device asleep between publishes without a keepalive-safe interval | keep keepalive 60 s and sample interval ≥ 5 s; at P6 this row becomes the TLS/clock row (certificate expiry, RTC drift) |

## Rows earned from live wounds (kept — they will happen to users)

| Symptom | Cause | Fix |
|---|---|---|
| **"It says accepted but nothing arrives"** | QoS 0 publish denial is **silent** on MQTT 3.1.1 (no negative PUBACK — the broker discards and acknowledges); the *tool truth* from the live ladder | always publish QoS 1; on MQTT 5 the PUBACK carries `Not authorized` visibly |
| **Token works for the API test but the broker refuses it** | the simulator/`mosquitto_pub` sends **username = deviceId, password = token** — swapped fields deny uniformly | username = deviceId, password = token; no exceptions |
| **"Everything denies after the app restarts"** | the app (auth doors) is down and the wall is fail-shut BY DESIGN — `allow_anonymous=false` + `no_match=deny` means no door = closed broker | bring the API host up; this is the safety property, not a bug |

## Escalation line (for the support table later)

Device side → installer's quick table (this page). Platform side → admin's doors (provisioning.md). Broker internals → operator runbook (P6).
