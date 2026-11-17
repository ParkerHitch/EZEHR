# Where EZEHR is going after the hackathon

*Written 2026-11-17, from a design conversation after the hackathon. It records a decision and the reasoning behind
it, so later sessions don't reopen it by accident.*

## The decision

The hackathon is over, so the goal is no longer a five-minute demo.

**The goal now: a system that behaves the way healthcare organizations actually work.** A reviewer who knows how a
clinic or hospital operates should find the roles, the custody of records and the flow of an access believable.

**Not the goal, for now: a system real EHR vendors would integrate with.** We looked at what that would take
(below) and decided it is too much to take on right now. The section is kept because it is the honest answer to
"would this work in the real world?", and because it shows which parts of today's design are unrealistic for
reasons we are choosing to live with.

The first piece of work under the new goal is the organization launch
(`docs/superpowers/specs/2026-11-17-organization-launch-design.md`).

## What "realistic" means here

Use these as the test for new work:

- **An organization is responsible for its people.** A clinician acts for an organization, through that
  organization's system, and the organization can refuse.
- **The organization's own system is the record of the care it gave.** It writes what its clinicians document, and
  it knows what they looked at.
- **People read; organizations write, and sign what they write.** Unchanged from the spec.
- **The patient decides who sees the record, and sees every attempt.** Unchanged from the spec.

## The approach that could actually integrate (documented, not being built)

*This section was written from general knowledge of US health-data interoperability, without checking the current
status of each program. The network programs and the rules behind them change often. Check before relying on any
of it.*

### Why today's shape would not integrate

- **No hospital will push records into a custom API,** or sign each record with a certificate. Real source systems
  don't sign individual records at all.
- **No clinician will sign in to a separate website.** Outside records reach a clinician inside their own EHR,
  requested by the organization's system, with the clinician named in the request.
- **Enrollment by provider invite needs every provider's cooperation.** Real patient-facing services prove the
  patient's identity independently and then go and fetch the records.

### What it would look like instead

Stop being a new network with its own API. Become a standards-based, patient-directed record service: pull records
through the interfaces hospitals are already required to expose, and release them through the standards their EHRs
already speak.

| Part | Today | Realistic version |
|---|---|---|
| Getting records in | Providers push signed records | Pull, with the patient's authorization, from each EHR's patient-access FHIR API (certified EHRs must offer one) |
| Proof of origin | Per-record provider signature | A provenance record: which endpoint, when, under whose authorization |
| Releasing records | Custom endpoints | A standard FHIR API (US Core) behind SMART on FHIR authorization |
| Organization identity | Our certificate check and organizations table | UDAP, the certificate-based trust standard for healthcare APIs |
| Clinician identity | Entra sign-in to our pages | Named by their organization in the request; UDAP's tiered sign-in can still send them to their own identity provider |
| Sharing at a visit | Share code typed into our page | A SMART Health Link (QR) the clinic scans, or the clinic's EHR querying us |
| Patient identity | Provider invite | Identity proofing to the level the networks require, plus the passkey |
| Apps | Our scopes | SMART scopes |

### What would survive, and what would be lost

- **Survives, and is the real differentiator:** the consent model, the patient-visible log of every disclosure, the
  tamper-evident chain, and encryption at rest. Today's networks give patients almost no view of who queried them.
- **Survives and gets stronger:** the certificate work. UDAP is PKI for healthcare.
- **Lost:** "access ended locks the screen". Once released, data is copied into the requester's system; expiry only
  stops future requests.
- **Lost:** per-clinician verification by us in the common case. The organization vouches for its clinician, as it
  does everywhere in healthcare.

### What a two-person team could do toward it

Joining a national network is legal and commercial work (a company, agreements, security audits, fees), not code.
What can be done is to build to the standards and prove it with public conformance suites:

1. A patient-access client that pulls from the Epic and SMART public sandboxes.
2. A US Core FHIR API with SMART authorization that passes the Inferno conformance test kits.
3. UDAP registration and organization-to-organization access, with our own CA as the trust anchor.
4. SMART Health Links for the walk-in case.

### Why we are not doing it now

- **Aggregation is crowded.** Pulling records through patient-access APIs, through the national networks'
  individual-access route, or through a hybrid of both, is what established aggregators already do. We would not
  compete.
- **"A solved problem done better" has no buyer by itself.** Better consent, logging and encryption only matter
  where data is flowing. Patients don't pick a record app for its audit log, and hospitals already satisfy their
  auditors.
- **The niche that looked most promising** was the trust layer: an authorization gateway in front of any FHIR
  server that answers "who is asking, on whose behalf, with what consent", using UDAP certificates and SMART scopes,
  and that handles certificate issuance and rotation. It fits the team's PKI and Entra background. It was not
  validated: we don't know the demand, and an open-source UDAP implementation in .NET (Udap.Net) already exists, so
  the gap is more likely operations than the protocol. Other niches considered: consent for sensitive data (a real
  gap, but blocked by policy and by EHRs that can't tag data), and a patient-visible log of who queried them across
  networks (a real gap, but the data sits in the networks' logs).

If the goal changes back to real-world integration, start from this section, and start by checking what has
changed.

## Known gaps against the current goal

Places where today's system doesn't match how organizations work, most important first. The first is being fixed
now.

1. **Two doors for one organization.** A clinician signs in to EZEHR directly, so their organization lends them an
   identity and then isn't involved: it can't refuse an access, its own system doesn't know what the clinician
   opened, and it learns about its staff from EZEHR's audit. Fix: the organization launch.
2. **Grants are per person and per visit.** Real care is a team over months. Every nurse and covering doctor needs
   their own share code. A grant to a care team, with the organization naming its members, would follow naturally
   from the launch.
3. **Cutting off an organization erases history.** Records are verified against the current organizations table,
   so deleting a row for a leaked key hides every record that organization ever signed. It should stop new
   signatures and keep old ones.
4. **Corrections overwrite.** A provider's corrected record replaces the old one in place, so the patient can't see
   that something changed.
5. **One passkey, no recovery.** A lost phone locks the patient out for good, and there is no way to add a second
   device.
6. **No delegates.** A parent or caregiver can't act for a patient.
7. **Nobody vets organizations or apps.** Whoever holds the platform certificate decides, and nothing records why
   an organization was admitted.
