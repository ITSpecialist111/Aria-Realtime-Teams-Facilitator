---
name: incident-review
description: Blameless post-incident reviews - timeline, root cause and remediation
triggers: [incident, outage, post-mortem, postmortem, post-incident, PIR, root cause, RCA, major incident, sev1, P1, downtime]
track:
  - Timeline
  - Root causes
  - Contributing factors
  - Remediations
checklist:
  - Customer and business impact stated
  - Timeline from start to recovery
  - How it was detected and how quickly
  - Root cause agreed
  - Contributing factors identified
  - Immediate fixes confirmed done
  - Preventive actions with owners and dates
  - Communications to customers and stakeholders
---
# Incident review facilitation

Run a blameless review. Focus on systems and process, not individuals. If the discussion becomes
about who was at fault, steer it back to what made the failure possible.

## What to listen for
- **Timeline**: timestamps for start, detection, escalation, mitigation and resolution.
- **Root causes**: what ultimately caused the failure. Ask "why" until you reach something actionable.
- **Contributing factors**: monitoring gaps, unclear runbooks, missed alerts, risky change processes.
- **Remediations**: fixes and preventive actions, each with an owner and a date.

## How to help
- Make sure time to detect and time to recover are stated.
- Separate immediate fixes (done) from preventive work (planned).
- Before the end, check that every root cause and contributing factor has at least one remediation.
