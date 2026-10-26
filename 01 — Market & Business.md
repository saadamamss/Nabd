# 01 — Market & Business

**Asset Telemetry & Anomaly Detection SaaS (Nabd — provisional working name)**

*Blueprint v0.2 — draft for review. A living document, updated with every decision.*

## 1. Purpose of This File

This is the second Blueprint file. It answers: **who the competitors are, at what price we sell and how, who will pay and why, and how we validate all of this before any major code.**

Builds on decisions in file 00 (D1–D7):

- **D1 (first vertical)** is decided by default, since pre-build interviews were ruled out (your decision): **temperature/cold focus + generic data model** — validated after launch, not before.
- All numbers here (pricing, revenue targets) are **initial assumptions to be tested after launch**. Not a single number here is "final".
- **Strategy context:** we build first, publish, then market. Section 7 defines how we validate **through the launched product** instead of through interviews.

## 2. Geography (D9)

**Adopted default:** primary market **Egypt** first, then the Gulf (Saudi Arabia, UAE) once the product proves itself.

- Egypt has real **access advantages**: language, your network, understanding of local business reality. That's a top criterion from D1.
- Local trust builders: Arabic RTL interface, real WhatsApp/phone support, local invoicing, local payment without an international card.
- The Gulf: hard currency and higher prices — we enter after a successful Pilot in Egypt.
- ⏳ This is an assumption — if post-launch data shows your strongest market is elsewhere, we revisit.

## 3. Who Sells in This Market, and for How Much? — Competitor Research

Prices taken directly from competitor websites, **as of 2026-10-26**. Any number can change — verify before any final pricing decision.

### 3.1 General Cloud Platforms (Building Blocks for Developers)

| Competitor | Model | Price (as advertised) | Problem for our customer |
| --- | --- | --- | --- |
| **AWS IoT Core** | Pay-as-you-go per component | $1 per 1M messages; connectivity ≈ $0.042/device/year | Messages are cheap on paper, but there's **no ready dashboard for the end customer**: a team must build ingestion, storage, alerting, and support. These are "components", not a product |
| **Azure IoT Hub** | Subscription per unit | ≈ $25/month per unit (≈ 400K msgs/day) — verify on the pricing page | Same story: blocks that need engineers, no ready supervision UI |

> **Takeaway:** general platforms are **not direct competition** for us. Their role in analysis: they prove that the raw technical cost of moving/storing data is very low — so what actually gets sold is the **finished product and service**, not data ingestion itself.

### 3.2 Ready-Made General Platforms (Closest Direct Competitors)

| Competitor | Model | Price (as advertised) | Why they don't cover our need |
| --- | --- | --- | --- |
| **ThingsBoard Cloud** | Monthly per device | Free: 5 devices. Prototype: $49/mo (50 devices). Pilot: **$149/mo (100 devices)**. Startup: $399/mo (500 devices). Business: $749/mo (1,000 devices). Extra device: $0.30. Private Cloud: from $1,499/mo | Generic platform for all verticals — customers spend weeks building on it. **No Arabic/RTL**, no local support, Stripe payment (hard in Egypt). **This is our main benchmark** |
| **Ubidots** | Monthly per device | Professional: **$99/mo** (50 devices, 10M points/mo, 6-month retention). Extra device: +$2. Industrial/Enterprise: custom | Same gaps: generic, English only, no local presence |
| **ThingsBoard On-prem (PE)** | License | Pilot: **$99/mo** up to 100 devices + you run the infrastructure | Defines the floor of the cheapest option globally: DIY, but operations and maintenance are on you |

### 3.3 Vertical Solutions & the Local Market

- Globally: **Monnit, SensoScientific, Tive** and others in temperature/cold-chain monitoring — usually packaged pricing (device + subscription per sensor/month). Needs deeper verification only if the chosen vertical is cold chain.
- Locally: equipment vendors and integrators may already play in this space — **an assumption; monitor competitor actions after launch.**

> **Very important point:** the real first competitor is usually not a company — it's **"nothing"**: a paper notebook, a manual check every few hours, and someone walking between sites. Consider the pitch built around replacing that routine, not around "IoT".

## 4. Positioning & Why We Win

> **Positioning statement:** for small and medium companies whose asset failures are expensive, **Nabd** is a monitoring platform **ready for a specific vertical**, installed in days, sending early alerts — not a builder platform that needs engineers.

Why the customer pays us (assumptions, validated after launch):

1. **Ready, not building blocks:** real alerts within days, no IT team building the product.
2. **Fully Arabic + local support:** WhatsApp and a real phone, not a slow ticket with an English reply.
3. **Local-market pricing:** subscription in EGP or equivalent, local payment, official invoice.
4. **Vertical expertise:** rules and alerts tuned for this asset type, not empty generic tools.
5. **Cheaper than an in-house team:** a fraction of one extra maintenance employee's cost, running 24/7 without days off.

## 5. Pricing Model (D10)

### 5.1 Model

Composite monthly subscription (as agreed in file 00):

- **Base fee** per customer for the platform (covers onboarding and support).
- **+ price per asset/month in descending tiers**: the more assets, the lower the per-asset price.

Advantage: pricing grows with customer value, and is explained in one question: "How many assets do you have?"

### 5.2 Initial Numbers (hypotheses to be tested)

Reference: competitor prices above (ThingsBoard ≈ $0.3–1.5/device, Ubidots ≈ $2/device). We position as a small premium over self-host, still cheaper than managed global solutions, with the added value of local service.

Graduated per-asset pricing (like tax brackets): every customer pays the base fee of their plan plus the per-asset price of each bracket their assets fall into.

| Item | Price | Notes |
| --- | --- | --- |
| **Starter base fee** | $25/mo | Up to 10 assets, 2 users, dashboard + email + Telegram alerts |
| **Pro base fee** | $50/mo | Up to 50 assets, 5 users, all alert channels available |
| **Business base fee** | $100/mo | Up to 200 assets, 10 users, reports, priority support |
| Per asset, assets 1–10 | $4 | all plans |
| Per asset, assets 11–50 | $3 | Pro and Business |
| Per asset, assets 51–200 | $2 | Business |

Worked examples (v0.2 — arithmetic corrected):

- 8 assets on Starter: 25 + (8×4) = **$57/mo**. Starter maximum (10 assets): 25 + 40 = **$65/mo**.
- 20 assets on Pro: 50 + (10×4) + (10×3) = **$120/mo**.
- 100 assets on Business: 100 + (10×4) + (40×3) + (50×2) = 100 + 40 + 120 + 100 = **$360/mo**.

Practical notes:

- **First 2–3 customers = Design Partners: free POC for 60–90 days.** In exchange: regular feedback + permission to use them as a case study.
- The math is simple in the pitch: "How many assets do you have?" — no complexity.
- **Currency (v0.2):** USD is the reference currency in this table. Customers are billed in EGP from a fixed price list reviewed quarterly, so exchange-rate swings do not change a customer's bill mid-quarter.
- **Local payments:** Paymob, Fawry, or Kashier — Stripe isn't available in Egypt. Manual renewals at first; automated billing in V1.
- **Unit economics:** infrastructure cost per asset is very low → high margin; the real difficulty is **acquisition**, not cost.

### 5.3 How Are These Numbers Validated?

Initial numbers get adjusted after launch based on trial conversion and user feedback (section 7), not on paper.

## 6. Go-To-Market (GTM)

### 6.1 Phase 1 — Founder-led Sales

- Direct selling by you: visitor → simple online demo / trial → subscription (post-launch phase).
- Presentation anchor: "If the generator was down for a day, what did that cost you?" — one day of downtime pays for months of subscription.
- Messaging is written per vertical after D1 is decided.

### 6.2 Phase 2 — Expansion (after a successful Pilot)

- **Equipment and maintenance suppliers as Referral Partners** — they visit customers daily; we can incentivize them with a referral commission.
- Local integrators (PLC/Modbus people) as installation partners.
- Marketing content: a case study from the first Pilot beats any ad.

### 6.3 Required Sales Assets

| Asset | Language | When |
| --- | --- | --- |
| One-pager (problem + solution + price) | AR/EN | Before launch — as a pitch aid, not a final price sheet |
| Starter Kit sheet (supported sensor + gateway, price, install guide) | AR/EN | Before launch |
| Demo script (15 minutes) | AR | Before the first demo |
| Case study with numbers from the first Pilot | AR + EN | After the Pilot |

## 7. Launch-Based Validation — Replaces Pre-Build Interviews

You decided: **no customer interviews now — build first, publish, then market.** That's a legitimate strategy. The known cost is: we may build something nobody wants. This section is the answer to that cost — validation happens **through the launched product**, which needs no persuading anyone by phone, only real usage data.

### 7.1 Risk Containment (why the scope matters more now)

Because there's no pre-launch customer pull signal:

- The MVP is **strictly the list in file 00 §6** — nothing extra. Every extra feature is unpaid work if the market disagrees.
- The asset/sensor data model stays **generic** (asset → sensors → readings), with cold-chain stuff as **templates/presets**, not hard-coded logic.
- Pricing stays on the initial table (5.2) until real users react to it.

### 7.2 Assumption Register (accepted risks, revisited after launch)

| # | Assumption | Level of certainty | How we'll know |
| --- | --- | --- | --- |
| A1 | SMBs want early failure alerts | High (industry basics) | Trial → paid conversion |
| A2 | They'll pay monthly per asset | Medium | First 3 paying customers |
| A3 | Cold/temperature is the right wedge | Low (no interviews) | Which vertical trial users actually arrive from; usage patterns |
| A4 | Self-serve onboarding works without our help | Medium | Activation rate without support |
| A5 | Price point (5.2) is in range | Low | Trial conversion + exit feedback |

### 7.3 Launch Sequence

| Stage | What | Success check |
| --- | --- | --- |
| L1. Soft launch | Product live with a free trial; demo with **simulated cold-storage data** (the Device Simulator is part of the MVP anyway); post in relevant online communities (industry groups, Reddit/LinkedIn, Arabic SMB groups) | First 10 signups within 2–4 weeks |
| L2. Measure | Track the funnel: visitors → signups → activated (first real alert received) → trial → paid | See the traction gate below |
| L3. Learn | In-product feedback button + support conversations + a short optional exit survey | Qualitative notes per signup |
| L4. Decide | Every 4 weeks: keep / adjust pricing / adjust vertical / adjust onboarding — using the gate below | Documented decision, updated 00/01 |

### 7.4 Traction Gate (replaces the old interview decision gate)

Within **60 days of public launch**, look at:

| Signal | Keep going if | Pivot caution if |
| --- | --- | --- |
| Signups (trial starts) | ≥ 20 | < 10 → marketing story or landing is the problem |
| Activated (first alert received; track *demo* = simulator-fed and *real* = own sensor separately) | ≥ 40% of signups | < 20% → onboarding is too hard |
| Trial expressed purchase intent / ask for pricing | ≥ 25% | < 10% → value story unclear |
| Paying customers | ≥ 2 | 0 after 90 days → revisit ICP + pricing (D10) |

Pivot warnings trigger the same review loop: change the marketing wedge (e.g. cold → generators), change pricing, or change the buyer story — **not** a architecture rewrite (the generic model keeps a pivot cheap).

### 7.5 Feedback Channels

- In-product "feedback" button (one field, optional email) — built in MVP, cheap.
- Support email/WhatsApp listed in-app.
- Optional 3-question exit survey on trial cancel: what were you trying to do / why not continue / what price felt right.

### 7.6 Acquisition Plan (required for the traction gate — v0.2)

The gate in 7.4 assumes visitors exist. With no existing audience, they must be created on purpose, every week, from the first day of the launch:

| Channel | Weekly cadence | Notes |
| --- | --- | --- |
| Direct outreach to owners/operators in the chosen vertical (WhatsApp, phone, LinkedIn, walk-ins) | 25–50 targeted contacts | The largest lever. Offer the free Design Partner POC, not just a demo link |
| Posts in industry/SMB groups (Arabic first) | 2 | Show the `warmup-demo` story: a fridge warming up and the alert arriving |
| Referral asks to equipment suppliers and maintenance contractors | 3 conversations | The Phase 2 channel (6.2), started early at conversation level |
| Landing page + short demo video (AR) | continuous | One call to action: start the free trial |

Planning assumption (not a fact): a 5–10% positive response to direct outreach means roughly 200–400 targeted contacts over 8 weeks to reach the ≥ 20 signups of the gate. Replace this with real numbers after the first two weeks.

### 7.7 Optional Low-Cost De-Risking (recommended, not required)

Your decision stands: no pre-build interview phase. Two cheap additions fit inside it, because they cost hours rather than weeks and run in parallel with P0–P2:

1. **One real fridge, one real owner.** Put the DIY Starter Kit (05 §13) on a real cold-storage unit belonging to someone you can reach. It exercises the whole hardware path and becomes Design Partner #1.
2. **3–5 short conversations** with people who run cold storage: what they check today, how often, and what a failure has cost them. The goal is to test the pitch wording and the assumption register (7.2), not to run a research program.

## 8. Business Targets (KPIs) — initial

| Metric | Target | Why |
| --- | --- | --- |
| Signups in first 60 days after launch | ≥ 20 | Traction gate (7.4) |
| Activation rate (trial user receives their first alert; real-sensor activation tracked separately) | ≥ 40% of signups | Onboarding works without help |
| Pilot/Design Partner → paying conversion | ≥ 50% | **The real measure of success** (from file 00) |
| First real MRR | 3 paying customers | Proof the price isn't a favor |
| Referral per satisfied customer | ≥ 1 person | Strongest signal that value is real |

## 9. Glossary (this file)

- **Design Partner:** one of the first 2–3 customers on a free POC; the primary learning source and origin of the case study.
- **ICP:** Ideal Customer Profile (from file 00).
- **POC:** free trial on real assets.
- **Referral chain:** customers introducing us to peers — the strongest sales channel at this stage.

## 10. New Open Decisions (need your reply)

| # | Decision | Adopted default |
| --- | --- | --- |
| D8 | Confirm name **Nabd (نبض)** | Check domains (nabd.io / .com / .app) and search trademark databases before committing |
| D9 | Geography | Egypt first, then the Gulf — revisited if access proves different |
| D10 | Pricing numbers | Start with table 5.2, adjusted from post-launch data — not final |
| D11 | Launch traction gate | Signals and pivot criteria in 7.4 — reviewed every 4 weeks after launch |
| D12 | Payment gateway | Paymob / Fawry / Kashier — compare fees and terms before Billing (V1) |
| D13 | Reference Starter Kit hardware | DIY ESP32 + temperature probe for P2 and Design Partner #1; pick one commercial kit by the criteria in 05 §13 before launch |
| D14 | Real-time alert channel | Telegram bot in MVP (free, simple); WhatsApp/SMS in V1 once pricing supports their per-message cost |

## 11. Next Step

- [ ] Review this file: anything wrong or missing?
- [ ] Next: **02 — Requirements** (functional + non-functional, with numbers), then architecture files 03–05.
- [ ] After launch: run the launch-based validation loop (section 7) every 4 weeks.

> **Reminder:** since interviews are off the table, this file's numbers are assumptions until launch proves them. The strictly minimal MVP is what keeps that risk affordable.
