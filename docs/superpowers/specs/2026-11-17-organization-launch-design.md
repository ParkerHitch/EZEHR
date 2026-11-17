# The organization launch

## Why

`docs/direction.md` sets the goal: behave the way healthcare organizations work. The biggest gap against it was
the spec's "two doors for one organization". A clinician signed in to EZEHR directly, so their organization lent
them an identity and then wasn't involved:

- It couldn't refuse a particular access.
- Nothing tied an access to one of its patients or to a visit.
- It learned what its clinicians did only by reading EZEHR's audit afterwards.

In a real organization a clinician reaches outside records from inside the organization's own system, from a
patient's chart, during a visit. The launch makes EZEHR work that way.

## Goals

- A clinician can ask for a record only through a launch that their own organization's system made for them.
- The launch names the chart (the organization's patient id) and the visit, and EZEHR holds the clinician to it: a
  share code works only from the chart of the patient it belongs to.
- Every grant and audit line made under a launch carries the visit, so the organization can match each access to
  its own records.
- The patient's part doesn't change: they still show a code and approve the named clinician.

## Non-goals

- Changing who writes. Organizations still write through the network door; clinicians still only read.
- Ending a launch from the organization's side, or cutting off access the patient already gave. See Honest limits.
- A simulated EHR with screens. `ezehr-tools provider launch` stands in for the "open EZEHR" button in a chart.
- SMART on FHIR conformance. The shape is borrowed (the EHR hands the app an opaque launch id); the protocol isn't.

## Approaches considered

1. **A launch the organization's system registers over the network door (chosen).** With its certificate, over
   mutual TLS, the system calls `create-clinician-launch` and gets back a link carrying an opaque, short-lived code.
   Same door, same certificate check and same single-use code pattern as enrollment invites, so there is no new
   protocol to get wrong.
2. **A launch token the organization signs itself** (a JWS in the link, like a signed record). No call to EZEHR
   first. Rejected: it is a home-made token format, which the spec's anti-goals rule out where a simpler standard
   mechanism exists, and replay protection would need server state anyway.
3. **One door: the organization's system reads on the clinician's behalf.** The most realistic option, and the one
   real exchanges use. Rejected for now because the record would be copied into the organization's system, and
   "access ended locks the screen" would stop being something EZEHR can enforce. `docs/direction.md` records this
   as part of the larger shift we decided not to make yet.
4. **Keep direct sign-in and require the organization to register the visit first.** Smallest change, but the
   clinician would still work outside their own system and nothing would bind the browser session to the visit.

## Design

### The launch

`clinician_launches` holds one row per launch: the provider, the clinician (`urn:entra:{tenant}:{object id}`), the
provider's patient id, the provider's encounter id, and its times.

1. **The organization's system makes it.** `POST /api/network/create-clinician-launch` with the clinician's Entra
   object id, the provider's patient id and the encounter id. Only a verified provider may call it, and the
   clinician URI is built from the tenant on that provider's own row, so a provider can only ever launch its own
   clinicians. A provider with no tenant has no clinicians and gets 409. The response is a link,
   `/Clinician/Launch?launch=CODE`, which must be opened within 5 minutes.
2. **The clinician opens it.** `/Clinician/Launch` signs the clinician in if needed (the same Entra sign-in as
   before) and comes back to the link. It opens only for the clinician the organization named; anyone else sees
   "this link was made for a different clinician". Opening it again while it is running returns the same launch, so
   a reload isn't an error.
3. **It runs for one hour.** That bounds new requests only. Access the patient gives lasts as long as the patient
   chose.

### What a launch is needed for

- **The share-code form.** `/Clinician/Redeem` and every post from it carry the launch id.
  `GrantManager.RedeemShareCodeAsync` refuses without the clinician's own open launch (`LaunchNotOpen`).
- **The chart must match.** The code's patient must be linked to the launching provider under the launch's patient
  id (`provider_links`). If not, the request is refused (`OfferNotForLaunchedPatient`), the code stays unused, and
  the patient's log gets a blocked line: "asked from another patient's chart". This also means a patient has to
  connect an organization (accept its invite) before its clinicians can ask for the record.
- **Emergency access.** It needs a launch too, so the organization is part of a break-glass, but the chart is not
  matched: the patient may be unconscious and unknown to this organization.
- **Not needed** to keep reading under a grant the patient already approved, or to sign in. A signed-in clinician
  with no launch sees "open EZEHR from that patient's chart in your organization's system" and nothing else.

### The visit in the record of what happened

- `grants.encounter_id` is set from the launch. The access gate passes it to every audit event decided on that
  grant, so reads carry it too.
- `audit_events.encounter_id` is part of the hash chain, as a thirteenth value that is present only when there is
  a visit. Events from before this change, and events with no visit, keep the fingerprints they have. A test pins
  one such fingerprint to a value computed outside the code base.
- The organization audit returns `encounterId`, and `ezehr-tools provider audit` prints it.

### Pages

- New: `/Clinician/Launch`.
- `/Clinician/Redeem` and `/Clinician/Emergency` show "Opened from chart RPC-1107 · visit …" and redirect to the
  clinician home page without an open launch. A failed attempt redirects back to the form with the error, so each
  handler still makes one manager call.
- The clinician home page and the "access ended" page no longer link to the share-code form.

### Tools

- `ezehr-tools provider launch <provider> <chart> --clinician <Entra object id> [--encounter <id>]`
  prints the link.
- `stage` has a new step before the visit: Riverside's system opens EZEHR for Dr. Rivera from Sam's chart. It reads
  Dr. Rivera's object id from `EZEHR_CLINICIAN_OBJECT_ID`.

## What changes for the demo

- Sam must have accepted Riverside's invite before Dr. Rivera can use Sam's share code. `stage` already does the
  invite before the visit.
- Dr. Rivera starts from the launch link, not from the EZEHR home page.

## Honest limits

- **The organization can refuse to start an access, not stop one.** Once the patient has approved, the grant runs
  to its end without the organization. Letting the organization end its clinician's access early is the natural
  next step.
- **A departed clinician keeps a live session until the 10-hour shift ends.** They can read under grants they
  already hold, though they can't get a new launch.
- **The clinician still reads in EZEHR's pages, not in the organization's system.** The organization knows that an
  access happened and for which visit, from its own launch and from the audit, but not what was on the screen.
- **The visit id is the organization's word.** EZEHR records it and can't check it against anything.
- **A patient who hasn't connected the organization can't be asked,** except through emergency access. That is
  deliberate, but it is one more step at a first visit.
