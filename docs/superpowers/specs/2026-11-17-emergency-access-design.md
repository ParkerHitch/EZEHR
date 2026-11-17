# Emergency access (break-glass)

## Why

The spec picked a central record over patient-held keys partly because "if the patient is unconscious or their phone
is dead, nobody can open the record". Until now nothing used that: every clinician read needed the patient to tap
Approve. Emergency access is spec stretch goal 2, the ER scenario.

## Goals

- A verified clinician can open a record with no approval step, when the patient can't give one.
- It stays the patient's decision: off unless the patient turned it on.
- It is loud: the patient sees who, when and why; so does the clinician's organization.
- It goes through the same access gate and the same grant model as a shared code.

## Non-goals

- Finding a patient by name, date of birth or anything else a clinician could guess.
- Emergency access for apps or provider systems. People only.
- Review workflows for the clinician's organization. The event is in its audit; what it does with it is its business.

## Approaches considered

1. **An emergency code the patient opts in to (chosen).** The Access page has an Emergency access section. Turning it
   on makes an 8-character code, in the same format as a share code, that never expires. The patient keeps it where
   emergency staff look: the phone's medical ID, a wallet card. A signed-in clinician who enters it with a reason
   gets a one-hour grant at once. Consent is real, given ahead of time, and the patient can change the code or turn
   it off. The cost: a patient who never turned it on can't be helped this way.
2. **Always on; the clinician looks the patient up by their own organization's patient id.** This is how break-glass
   works inside one hospital's EHR. Rejected: it contradicts "nobody can read it by default", it only works at
   providers the patient is already linked to, and patient ids like `LUC-20417` are guessable by anyone at that
   provider.
3. **A second clinician co-signs.** Stronger against one bad actor, but it needs a second signed-in person at the
   worst moment, and a whole request-and-approve flow between clinicians. Too much for what it adds here.

## Design

- `patients.emergency_code` (nullable, unique when set). Null means off. `GrantManager.TurnOnEmergencyAccessAsync`
  sets a new code (also how a code is replaced); `TurnOffEmergencyAccessAsync` clears it.
- `GrantManager.OpenEmergencyAccessAsync(code, reason, clinician)` finds the patient by code and inserts an `Active`
  grant: scope `Full`, purpose `emergency`, expires one hour from now, `grants.emergency_reason` set to the stated
  reason (required, cut to 200 characters). In the same transaction it appends an audit event with action
  `OpenEmergencyAccess` whose claimed purpose is the reason.
- The gate is unchanged: it sees an active grant. Each read under an emergency grant is logged with claimed purpose
  `emergency`, so both audits can tell it from an approved read. The patient revokes it like any other grant, and
  the clinician's open record locks within a couple of seconds as usual.
- A wrong code names no patient, so nothing is logged, the same rule as any attempt on an unknown patient id.
- Pages: `/Clinician/Emergency` (code, reason, a warning about who will see it); the patient's Access page gets the
  Emergency access section and shows an open emergency grant highlighted with its reason; Activity highlights the
  line "Emergency access: Dr. Rivera, Riverside Primary Care, opened your record without your approval. Reason
  given: …". While emergency access is open, the patient's Record page shows a red alert
  with the same who, until when and why, and a link to the Access page; it appears and clears on the page's
  2-second poll.
- The provider's organization audit already returns every event by its clinicians, so `OpenEmergencyAccess` and its
  reason appear there with no change. `ezehr-tools provider audit` prints the reason.

## Honest limits

- The emergency code is not a secret that grants access by itself: it only works for a signed-in clinician whose
  organization EZEHR trusts. But anyone who has seen the patient's card and is such a clinician can use it. That is
  the trade the patient makes by turning it on, and why every use is shown to them with a name and a reason.
- The reason is the clinician's word. EZEHR records it; the clinician's organization is who can judge it.
- Turning emergency access off stops new emergency access. One already open runs out within the hour, or the
  patient revokes it.
