# Bill of Materials — DIY reference kit (Track A, plan/05 §13)

> **Prices honesty:** the prices below are **market estimates in EGP** for sizing only. The `owner-verified` column becomes real numbers when the owner confirms local retail availability — no published number before that. Two vendor options where possible.

| # | Item | Spec | Qty | Est. price (EGP) | Vendor option 1 | Vendor option 2 | Owner-verified price |
|---|---|---|---|---|---|---|---|
| 1 | ESP32-class dev board | Wi-Fi + BLE, USB, 3V3 logic (e.g. ESP32-DevKitC / ESP32-WROOM class) | 1 | 250–400 | FUT-Electronics | Ram Electronics | ⬜ |
| 2 | Waterproof temperature probe | DS18B20-class, stainless tip, ~1 m cable, 3–5 V | 1 | 80–150 | FUT-Electronics | Ram Electronics | ⬜ |
| 3 | 4.7 kΩ resistor | DS18B20 pull-up (data → 3V3) | 1 | ~1 | any electronics shop | — | ⬜ |
| 4 | Breadboard + jumpers | prototyping only — production kit uses soldered connections | 1 set | 60–100 | FUT-Electronics | Ram Electronics | ⬜ |
| 5 | USB power supply | 5 V ≥ 1 A (phone charger is fine) | 1 | 0–100 (usually on hand) | any | — | ⬜ |
| 6 | (optional) Door reed switch | for the fridge door channel (booleans-as-0/1 in later scenarios) | 1 | 30–60 | FUT-Electronics | Ram Electronics | ⬜ |

**Kit total (without reed switch): roughly 400–750 EGP** — "a few dollars per unit" at the plan's scale, but *verify, don't trust* this line.

## Selection notes (why these classes)

- **ESP32-class, not ESP8266:** TLS (needed at P6) is comfortable on the ESP32's crypto hardware; the 8266 squeezes.
- **DS18B20-class waterproof probe:** the cold-chain demo target is a real fridge (2–8 °C, template `fridge-v1`). Stainless waterproof tip survives the condensation that kills bare boards.
- **Every part buyable locally** — the kit must be supportable where customers are (plan/05 §13, selection criterion). Both vendors are Cairo walk-in + delivery.

## Deliberately excluded from v0

- Power-over-mains enclosures, DIN mounts, IP-rated boxes — P6 production-kit decisions
- Battery sizing — the reference kit is mains-powered; battery duty cycles are a commercial-kit question
- Any LTE variant — Wi-Fi covers development and Design Partner #1
