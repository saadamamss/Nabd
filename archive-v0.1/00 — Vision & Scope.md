# 00 — Vision & Scope

**Asset Telemetry & Anomaly Detection SaaS (Nabd — provisional working name)**

*Blueprint v0.1 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This is the first file in the Blueprint. It defines **what the product is, who it's for, and what's inside and outside the scope**. Every file after this builds on it, so any change to a decision here means we must re-review the later files.

Reading rules:

- **Open decision (D#)** = needs a reply from you, with the proposed default next to it.
- Any technical number here is an **initial target** — confirmed or adjusted in file 02 (Requirements) and file 09 (Testing).
- Anything labeled a "hypothesis" must be validated with real customer interviews, not by thinking about it.

## 2. The Problem

- Many companies own expensive assets: generators, pumps, refrigeration units and cold storage, heavy equipment, fleets.
- Maintenance is usually **scheduled** (e.g. monthly) or **after the failure** — neither is enough, because failures happen at any moment between scheduled visits.
- Failure is expensive: production stops, emergency repairs, spoiled goods, broken commitments.
- The data that would raise the alarm usually **already exists** (sensors, meters, PLCs) but is never collected or analyzed; off-the-shelf solutions are expensive and complex; public cloud IoT platforms require an engineering team to build on top of them.
- Result: people discover problems **too late**.

## 3. The Vision

> Let any small or medium company know that its asset is about to fail **before** it fails — without building an IoT platform itself.

**Positioning:** not a generic IoT platform. A **ready-made product for a single vertical**, installed and working in days not months, delivering clean alerts — not complicated dashboards.

**Difference from the big competitors:** they sell "building blocks" and the customer builds. We sell a **working solution to a specific problem**, at a price and with a service closer to the customer.

## 4. Target Customer

### 4.1 Choosing the First Vertical (D1)

The rule: **one vertical at the start.** "Any asset in any industry" means a product that suits no one.

Selection criteria:

| Criterion | Why it matters |
| --- | --- |
| Cost of downtime to the customer | The higher it is, the more willing they are to pay |
| Ease of data collection | A ready sensor and simple protocol = faster MVP |
| Number of assets per customer | Determines subscription size |
| Customer's ability to pay | Determines pricing |
| Competition strength in the vertical | |
| Your actual access to customers | Your network matters more than market size |

Candidate verticals (assessed here by reasoning, re-ranked after launch from real usage data):

| Vertical | Initial hypothesis |
| --- | --- |
| Cold chains (refrigerators / cold storage) | Simple temperature sensor, clear alert rule, high spoilage cost. Probably the easiest MVP |
| Generators & pumps | High asset value, but connection is usually through controllers (Modbus) — technically harder |
| Heavy equipment & fleets | Needs GPS and engine data, more competition |
| Building HVAC | Many distributed assets, data sometimes available from BMS |

**Decision (set by default, since pre-build interviews were ruled out):** the primary design and demo focus is **temperature / cold storage** — the simplest sensor (temperature), the clearest alert rule (crossing a limit), and an easy-to-explain loss story. There were **no pre-launch customer interviews** (your explicit decision), so this is an assumption we accept risk on. Mitigation: a **strictly minimal MVP** (nothing beyond section 6) and a **generic asset/sensor data model**, so switching focus to generators, pumps, or fleets after launch needs only configuration and templates — no architectural rewrite. See file 01, section 7, for the launch-based validation plan.

### 4.2 Ideal Customer Profile (ICP) — initial assumption

- A small or medium company with 20 to 500 assets.
- Has a maintenance or operations person, but no big IT team.
- Willing to pay a monthly subscription if they see value quickly.

### 4.3 Personas

| Role | Needs | Measures success by |
| --- | --- | --- |
| **Maintenance engineer / technician** | Early, clear alert on mobile and email; asset history | Number of sudden failures prevented |
| **Operations manager** | Overview of asset status and reports | Downtime |
| **Owner / management** | Lower costs, simple reports | Financial savings |
| **Tenant Admin** | Adds assets and users, configures rules | Ease of setup |
| **You (Platform Admin)** | Monitors all customers and the system | System stability and pipeline health |

## 5. Value & Use Cases

| # | Use case | Phase |
| --- | --- | --- |
| UC-01 | See all asset status live on one screen | MVP |
| UC-02 | Receive an alert when a reading crosses a threshold | MVP |
| UC-03 | Receive an alert when **the device itself goes silent** (offline) | MVP |
| UC-04 | View asset history and compare periods | MVP |
| UC-05 | Add an asset or device easily and link it | MVP |
| UC-06 | Define alert rules without code | MVP (simple) |
| UC-07 | Manage users and permissions | MVP (3 roles) |
| UC-08 | Early warning on an abnormal trend (not just a threshold) | V1 |
| UC-09 | Turn an alert into a work order | V1 |
| UC-10 | Reports and data export | V1 |

> UC-03 is critical: a silent device can be more dangerous than a high reading.

## 6. Scope

### MVP — Goal: prove value with a real Pilot customer

- Tenants + Users + basic Auth (login + 3 roles).
- Assets & sensors management.
- Data ingestion via **MQTT** and HTTP.
- Per-device identity (revocable token per device).
- Time-series storage.
- Fixed threshold rules + device-offline detection.
- Alerts (dashboard + email) with deduplication and cooldown.
- Live dashboard + historical charts.
- Device Simulator generating intentional fault patterns (Spike, Drift, Flatline, Dropout).
- Basic observability + backups.
- Customer isolation: TenantId + Row-Level Security.

### V1 — after a successful Pilot

Statistical detection (Z-score, EWMA), work orders, webhooks & SMS, reports & export, billing, self-service onboarding, Edge Gateway (store-and-forward), audit logs, per-tenant rate limiting & quotas.

### V2 and beyond

SSO (OIDC/SAML), ML.NET, industrial protocols (OPC UA, Modbus), predictive maintenance (remaining useful life), mobile app, public API & SDK, dedicated database option for large customers, compliance certifications.

### Out of Scope (deliberate decisions)

- Manufacturing hardware or custom sensors.
- A full CMMS (spare parts inventory, technician scheduling).
- Video or image analysis.
- Microservices from day one.
- More than one mobile app.
- Supporting every protocol in existence.
- Competing as a generic IoT platform.

## 7. Governing Principles

1. **Value before tech:** no feature without the question "who uses it?".
2. **One vertical** until we succeed in it.
3. **Modular Monolith**, not Microservices.
4. **Data is not lost:** any reading whose receipt is confirmed must be stored.
5. **Security and customer isolation from day one** — retrofitting them later is painful.
6. **Measure before you optimize.**
7. **Every major decision gets an ADR** (reason + alternatives comparison).
8. **Simple before smart:** rules before ML.

## 8. Success Metrics (initial targets)

### Product

| Metric | Target |
| --- | --- |
| Time from first device shipped to first real alert | under 1 hour |
| Share of assets reporting regularly | above 95% |
| False alerts (per asset) | decrease week over week after tuning |

### Technical (confirmed in 02 and 09)

| Metric | Initial target |
| --- | --- |
| Ingestion capacity on one server | 2,000 msgs/sec sustained |
| Reading arrival → alert appearance | p95 under 5 seconds |
| Dashboard load | under 2 seconds |
| Loss of confirmed-received readings | zero |

### Business

- **Validation:** deferred to post-launch (your decision: build → publish → market). Run the launch-based validation plan in file 01 §7 — it needs no interviews, only published product data.
- **Pilot:** 1–2 customers on real devices.
- **Converting the Pilot into a paying customer:** that is the real measure of success. Revenue numbers are set after file 01.

## 9. Assumptions & Constraints

- Team: **one person + AI agents**, significant daily hours.
- MVP infrastructure budget is small (one or two managed servers).
- No special hardware: off-the-shelf sensors and gateways.
- **Core unvalidated assumption:** customers will pay a subscription. That's the first thing to test.

## 10. Top Risks (Vision Level)

| Risk | Why dangerous | Mitigation |
| --- | --- | --- |
| No customer willing to pay | Building a product nobody wants | Interviews ruled out by decision → strictly minimal MVP, cheap generic data model, launch-based validation with clear pivot criteria (file 01 §7) |
| Device integration harder than expected | The hardest part of this market | Off-the-shelf sensors & gateways; narrow the protocols |
| Scope creep | Project never ships | "Out of scope" section + ADR for any addition |
| Single-person dependency | Burnout or stall | Small, shippable milestones |
| Data leak between customers | Trust ends immediately | RLS + automated isolation tests |
| Competition from big platforms | More features at greater scale | Vertical specialization and closer service |
| Over-reliance on AI-generated code | Bugs and technical debt | Review every diff, tests, manual security review |

## 11. Open Decisions (need your reply)

| # | Decision | Default | Status |
| --- | --- | --- | --- |
| D1 | First vertical | Decided after interviews in 2–3 verticals | ✅ By default: **temperature/cold focus + generic data model** — interviews skipped (your build-first decision); validated post-launch |
| D2 | Your weekly working hours | ? | ✅ **6–8 hours/day**, with 1–2 rest days per week; split ~70% build / 15% customers / 15% learning & review |
| D3 | UI language | Arabic + English (i18n from day one) | ✅ **Arabic + English**, RTL & localization built in from day 1 |
| D4 | Product name | Temporary until chosen | ⏳ **Nabd (نبض)** as provisional working name — domain/trademark check = D8 in file 01 |
| D5 | Pricing model | Per asset/month (detailed in 01) | ✅ Composite subscription: base fee + per-asset tiered price; details and numbers in file 01 |
| D6 | Deployment | Cloud first, on-prem later if a large customer asks | ✅ Cloud first |
| D7 | Your current .NET / Docker / SQL level | Determines depth of execution files | ✅ **Less than one year of experience** — execution files written at higher detail; every new term explained once in the Glossary |

## 12. Glossary

- **Asset:** the monitored item (generator, fridge, ...).
- **Sensor:** a device measuring a value (temperature, vibration, ...).
- **Telemetry:** readings sent from devices.
- **Tenant:** a customer company with its own isolated data.
- **Anomaly:** abnormal behavior in readings.
- **Threshold:** a fixed limit that triggers an alert.
- **Alert:** a notification produced by a rule.
- **Work Order:** a maintenance task.
- **Gateway:** an intermediate device collecting readings and sending them upstream.
- **ICP:** Ideal Customer Profile.
- **MVP / Pilot:** smallest version proving value / trial on a real customer.
- **Design Partner:** one of the first 2–3 customers on a free POC; the primary learning source and future case study.
- **ADR:** Architecture Decision Record — a short document explaining a major decision and its alternatives.

## 13. Next Step

- [x] File 01 (Market & Business) written — competitors, pricing, GTM, launch-based validation.
- [x] D1 default set: temperature/cold focus + generic data model. No pre-build interviews (your decision).
- [ ] Move to **02 — Requirements**, then the architecture files.
- [ ] After launch: run the launch-based validation plan (file 01 §7) and re-check D1.
