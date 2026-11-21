# Crisis Management Live Demo Script

This 4-minute script demonstrates Aria facilitating a high-pressure incident **without anyone @mentioning her or asking her to load a skill**. As the conversation shifts across triage, ransomware recovery, regulatory exposure and board strategy, Aria autonomously activates specialist skills on screen and grounds the room with policies and playbooks from Microsoft 365 via **Work IQ**.

## Setup

- **Meeting subject:** `Northwind Health — Urgent Operations Check-in`
- **Agenda in invite:**
  1. Clinical systems alert (5 min)
  2. Containment & clean recovery (5 min)
  3. Regulatory & insurer position (5 min)
  4. Board decision & next checkpoint (5 min)
- **At join (`00:00`):** Aria joins automatically from her own AI Teammate calendar, introduces herself briefly, and starts with **one** baseline skill active (`Crisis Manager`). Keep her video tile pinned (or open the companion workspace link she posts in the meeting chat).

---

## Act 1 — Triage & Ransomware Response (do not address Aria)

**Say naturally to the room:**

> "Thanks everyone for jumping on. Ten minutes ago our Epic EHR and Pathology lab system locked up across the main hospital — Ward 4 sent a photo of a ransomware note on screen, and A&E wants to know whether we divert ambulances now or wait to see how fast IT can restore from our immutable backups."

**Pause ~15 seconds and watch the screen:**
- **Skill auto-activated:** `✦ Ransomware Incident Response` lights up on the **Skills** bar.
- **Specialist insight:** flags human safety / clinical continuity ahead of uptime and the need for a named Incident Commander.
- **Work IQ evidence cards:**
  - *OPEL 4 Clinical Diversion Protocol* — divert acute trauma and stroke to **St Jude's Regional**, switch Wards 1–8 to paper downtime packs, dual-clinician check while Pathology LIS is offline.
  - *Immutable Backup Verification Report* — clean `02:15 UTC` Rubrik air-gapped snapshot verified; **6 hours** for Pathology LIS and **14 hours** for Epic EHR after Tier-0 Active Directory rebuild.

---

## Act 2 — Legal & Regulatory Exposure (still do not address Aria)

**Say naturally to the room:**

> "Okay, we're agreed: activate OPEL 4 ambulance diversion to St Jude's immediately, and Marcus will isolate Tier-0 Active Directory before touching the backups by five o'clock. Let's also email NHS England, the ICO and our private clinic partners right now and guarantee them that no patient data was taken so nobody panics."

**Pause ~15 seconds and watch the screen:**
- **Outcomes captured:**
  - **Decision:** *Activate OPEL 4 diversion to St Jude's immediately.*
  - **Action:** *Marcus — Isolate Tier-0 AD before touching backups (due 17:00).*
- **Skill auto-activated:** `✦ Legal Compliance` lights up on the **Skills** bar.
- **`HIGH` Risk / Skill alert:** warns against stating *"no data was taken"* before forensic containment verifies egress logs (regulatory misrepresentation and privilege risk).
- **Work IQ evidence card:**
  - *Cyber Incident Regulatory Notification Playbook* — factual initial notice to **NHS Cyber CSIRT within 2 hours**, **ICO within 72 hours**, and **12-hour SLA** on the Private Diagnostics contract via Legal + Account Lead only.

---

## Act 3 — Board Trade-offs & Cyber Insurance (still do not address Aria)

**Say naturally to the room:**

> "Good catch — hold that press line. Helen, draft a factual two-hour NHS CSIRT notice instead by half four. Now the strategic choice for the Board: the attackers want 18 Bitcoin for a decryptor versus taking a 14-hour outage for a clean rebuild, and Mandiant needs £120k to start containment. How do we frame our options and who has authority to sign off?"

**Pause ~15 seconds and watch the screen:**
- **Skill auto-activated:** `✦ Strategy Consultant` lights up alongside the other three skills.
- **Specialist insight:** frames strategic options and trade-offs (clean verified rebuild vs unverified extortion payment).
- **Work IQ evidence cards:**
  - *Cyber Insurance Policy #NW-CYB-88412* — Mandiant must be engaged before wiping or restoring; unapproved ransom payments are excluded, and attacker decryptors fail or corrupt databases in **46%** of healthcare cases.
  - *Board Crisis Authority Matrix* — On-Call Gold Commander can authorise network isolation, St Jude's diversion and Mandiant up to **£150k** immediately; CEO + CFO + General Counsel required above £150k or for external media statements.

---

## Act 4 — Direct Voice Synthesis & Vision Capture (address Aria by name)

**Ask Aria out loud:**

> "Aria, based on our playbooks and insurance policy on screen, give us a crisp twenty-second recommendation for the Board."

- Aria answers **out loud** in the meeting, synthesising the verified backup RPO/RTO, insurance rules, authority threshold and regulatory deadlines.

**Optional vision beat:** hold a sketched recovery timeline or isolation diagram up to your camera and say:

> "Aria, capture this sketch for the incident log."

- Aria grabs a still frame, displays the annotated capture on her tile, extracts any written actions/decisions into **Outcomes**, and embeds the image in the HTML recap emailed from her own AI Teammate mailbox when the call ends.
