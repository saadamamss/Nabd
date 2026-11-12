# Install Guide — one page (≤ 15 minutes)

> **Scope honesty:** v0 draft (P2). Real firmware and the ≤ 15-minute test with a real reader = P6 (acceptance #10). This page describes the shape the user will follow — no ready flash commands yet.

## Before you start (done by the admin, not you)

The platform admin has already: created the account → the asset (the fridge) → **the device**, and handed you a slip with three values:
- `broker` — the broker address
- `deviceId` — your device's id
- `token` — **the password is shown ONCE** — if it's lost, your admin issues a fresh one (there is no "recover" — by design)

## Steps

1. **Wire the sensor:** DS18B20 cable (red = 3V3, black = GND, yellow/white = data) + a 4.7 kΩ resistor between data and 3V3. Seat it on the ESP32 per the wiring slip.
2. **Open the device's setup page:** first boot broadcasts a Wi-Fi hotspot named `Nabd-Setup`. Join it from your phone.
3. **Give the device your home Wi-Fi:** pick your network, type the password.
4. **Paste the three values** from the admin slip: `broker`, `deviceId`, `token`. Press save — the device reboots and connects.
5. **See the first reading:** the device reports "connected" and starts sending a reading every sample interval (default: every minute). The admin sees it in the Nabd platform at once.

## If it misbehaves (quick table)

| Symptom | First move |
|---|---|
| `Nabd-Setup` hotspot not visible | cut power 10 s, reboot |
| "connected" never settles + broker refuses | see the troubleshooting sheet (`troubleshooting.md`) |
| values lost / token mistyped | ask your admin for a reissue — a NEW token shows once, the old one dies immediately |

## What the device promises (05 §13)

It publishes payload v1 every sample interval, QoS 1, and reconnects with jittered backoff (never hammering the broker). If the network drops, the device buffers readings and sends them when it returns — durability is the device's duty, not the broker's.
