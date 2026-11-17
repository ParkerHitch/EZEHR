# Apps as data: a real app platform

## Why

The pitch is an open platform: "any app the patient approves". The code knew exactly one app. Kidney Check's URI
was hard-coded in the Access page, in `GrantManager` and in the one scope an app could have, so a second app meant
editing the site. This makes apps rows in the `organizations` table, each declaring what it may read, and proves it
with a second example app that has a different scope.

## Goals

- The patient's Access page lists every app on the network, each with what it does, what it could read, and its own
  on/off.
- An app declares a scope. A patient's grant gets that scope and nothing more.
- The platform operator adds an app over the network door, the same way it adds a provider.
- A second example app, Medication Check, with a wider scope than Kidney Check.

## Non-goals

- Scopes as a free-form list of resource types. Two named scopes are enough to show the model.
- An app store, reviews, or any vetting beyond "the platform operator registered it".
- Putting Medication Check into `stage` or `demo`.

## Approaches considered

1. **Scope as a named level, declared on the app's row (chosen).** `organizations` gets `app_scope` and
   `app_description`. `GrantScope` gains `MedicationsLabsAndNotes`; `AccessAction` gains `ReadMedications`. The gate
   stays one small switch from scope to allowed actions.
2. **Scope as a set of FHIR resource types per grant, with one generic read endpoint.** What a production platform
   (SMART scopes) does. Rejected for now: it replaces the gate's switch with set logic, adds a consent screen per
   resource type, and turns `read-egfr-labs` into a query language. Worth doing when a third app needs it.
3. **Keep one scope and add apps only.** Smallest, but every app would read the same thing, so nothing would show
   that scopes are enforced per app.

## Design

- **Table.** `organizations.app_scope` (a `GrantScope` name) and `organizations.app_description` (what the patient
  reads). An app with no scope gets `LabsAndNotes`, the narrowest. Migrations now run before the seed, so the seed
  can fill the new columns; Kidney Check's existing row gets its description from a migration.
- **Scopes.** `LabsAndNotes`: read eGFR labs, write notes. `MedicationsLabsAndNotes`: the same plus
  `ReadMedications` (`MedicationStatement` and `MedicationRequest`). `Full` (clinicians) also covers
  `ReadMedications`. A grant stores the scope it was made with, so changing an app's row later doesn't widen what a
  patient already agreed to.
- **Granting.** `GrantManager.TurnOnAppAsync(patientId, appUri)` replaces `TurnOnKidneyCheckAsync`. It grants only
  to a URI that is an app in the table. Turning off is the existing Revoke.
- **Access page.** `AccessOverview.Apps` lists every app by name with its description, scope in words and current
  grant. A grant for an app whose row was deleted stays visible under "Who can see your record", so it can still be
  revoked.
- **Enrollment.** The enrollment page has a checkbox for every app, all unticked, each with its description and
  scope in words. `complete-registration` takes `appUris` (the ticked ones) in place of `kidneyCheckOptIn`; a URI
  that isn't an app on the network is ignored.
- **Network.** `GET /api/network/patients/{id}/read-medications`, and `POST /api/network/register-app` (platform
  certificate only; slug, name, scope, description; `Full` is refused with 400; an existing row gets 409).
- **Tools.** `ezehr-tools certs issue --app <slug> --name … --scope … --description …` issues the app's certificate
  and registers it. `ezehr-tools medication-check run <patientId>` is the second example app: it reads medications
  and eGFR labs, verifies every signature itself, and when an anti-inflammatory painkiller is active and eGFR has
  dropped more than 25% it writes one signed note. Like Kidney Check's rule, it is deliberately not clinical advice.

## Deploying to a database that already has organizations

The seed only fills an empty table, so an existing database doesn't get Medication Check by itself. Register it
with the platform certificate:

```
ezehr-tools certs issue --app medication-check --name "Medication Check" \
  --scope MedicationsLabsAndNotes \
  --description "Looks at your medications next to your kidney lab results, and tells you when a medicine may need a second look."
```
