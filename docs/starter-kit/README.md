# Nabd Starter Kit — v0 draft

> **Scope honesty (read first):** this is the v0 *documentation* deliverable of P2 (plan/05 §13). The firmware, the physical kit, and the ≤ 15-minute install test against a real reader are **P6 work** (acceptance #10 lives there). Nothing in this folder is a claim — the numbers marked "verify" become real only when the owner records them.

## What's here

| Doc | Purpose |
|---|---|
| [bom.md](bom.md) | Bill of materials, retail prices (owner-verified column pending) |
| [install-guide-ar.md](install-guide-ar.md) | The one-page install guide, Arabic |
| [install-guide-en.md](install-guide-en.md) | The one-page install guide, English |
| [provisioning.md](provisioning.md) | Token-shown-once flow on the REAL platform doors + QR contents spec |
| [troubleshooting.md](troubleshooting.md) | Symptom → cause → fix, from real broker log lines |

## The kit in one sentence

An ESP32-class board + a waterproof DS18B20-class probe publishes **payload v1** over MQTT with per-device credentials — exactly what the Nabd simulator (P2 spec 06) does, which is why every demo and test stays honest.

## What the firmware promises (05 §13 — promises, not code yet)

- payload v1 on `t/{tenantId}/dev/{deviceId}/v1`, QoS 1
- per-device credentials (deviceId = MQTT username, token = password)
- jittered exponential reconnect backoff — never a hot reconnect loop
- configurable sample interval
- offline buffering is the **device's** durability duty (the broker queues RAM-only — measured, P0 spike 04)

## Deliberately NOT in v0

- Firmware source and flashing images (P6)
- QR generation tooling — the QR **contents** are specified in provisioning.md; the generator is a nicety
- TLS endpoint config with the real certificate — 8883 wiring is P6 hardening
- Commercial off-the-shelf track (B) evaluation — decision D13, later
