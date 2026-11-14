# EZEHR Authentication and Trust Flows

This document explains, party by party, **how everyone proves who they are**, which component checks it, and how those components hand results to each other. It complements the spec (`docs/superpowers/specs/2026-11-13-hackathon-design.md`) and the build plan (`docs/superpowers/plans/2026-11-13-verified-health-record.md`). The diagrams are Mermaid, which GitHub draws automatically.

---

## 1. The one idea to hold onto

EZEHR has **three front doors**, one for each kind of party. Each door checks a different kind of credential, but **every door produces the same thing: a `Caller`**. Behind the doors sits **one access gate**, which only understands a `Caller`, a patient's **grant**, and the action being attempted. It decides yes or no, and it always writes exactly one audit event.

| Door | Who comes through it | Credential | Checked by | Result |
|---|---|---|---|---|
| **Patient pages** (`/patient/*`) | Sam, on a phone | **Passkey** on the phone | Our server (Fido2 library) | A patient cookie session (`patient_id`) |
| **Clinician pages** (`/clinician/*`) | Dr. Rivera, on a laptop | **Security key**, used to sign in to **Microsoft Entra ID** | Entra checks the key. Our server checks Entra's signed **ID token** | A clinician cookie session, fixed at 10 hours, which becomes `VerifiedCaller(clinician)` |
| **Network API** (`/api/network/*`) | Provider systems, the Kidney Check app, and the data broker | **EZCA client certificate**, presented in the TLS handshake (**mutual TLS**) | Our server checks that the certificate chains to the EZCA root and maps it to a known organization | `VerifiedCaller(provider or app)`, or `UnverifiedCaller(reason)` |

A sign-in only proves **who** someone is. Whether they may read Sam's record is a separate question, answered by **Sam's grant**. Even a perfectly signed-in doctor gets nothing until Sam approves.

### Who writes, who reads

**People read; organizations write, and sign what they write.**

| | Reads | Writes |
|---|---|---|
| **Clinician** (clinician pages) | the combined record, under Sam's grant | **nothing** |
| **Provider system** (network API) | nothing | records for its own linked patients, each signed with its EZCA key. The patient sees these links under "Connected clinics" on the Access page and can disconnect one; linking again takes a new invite |
| **App** (network API) | what its grant allows (Kidney Check: lab results) | signed notes, if its grant allows |

This mirrors how care is really documented. A clinician charts in **their own organization's system**, which is the legal custodian of that record: history taken at the visit, diagnoses, medication changes, referrals. That system then pushes the result to EZEHR like any other provider. So Dr. Rivera's plan and the nephrology referral reach Sam's record from **Riverside's** system, signed by Riverside, with Dr. Rivera as author. The clinician pages exist for the gap no single system can fill: seeing what *other* organizations hold, with the patient's consent.

The production form of this is a SMART on FHIR EHR launch: the clinician opens EZEHR from inside their own EHR, and the request carries both the organization's identity and the clinician's. The demo keeps the two doors separate instead.

---

## 2. The big picture

```mermaid
flowchart LR
  subgraph People
    P["Sam's phone<br/>passkey"]
    C["Dr. Rivera's laptop<br/>+ security key"]
  end
  subgraph Machines["Organizations and apps"]
    PR["3 provider systems<br/>EZCA certificates"]
    KC["Kidney Check app<br/>EZCA certificate"]
    BR["Data broker<br/>no trusted certificate"]
  end
  subgraph EZEHR["EZEHR on Azure App Service"]
    PP["Patient pages"]
    CP["Clinician pages"]
    NA["Network API"]
    G{{"Access gate"}}
    A[("Audit log")]
    S[("Encrypted record store")]
  end
  ENTRA["Microsoft Entra ID"]
  EZCA["EZCA by Keytos"]
  KV["Azure Key Vault"]

  P -- "passkey sign-in" --> PP
  C -- "sign in with security key" --> ENTRA
  ENTRA -- "signed ID token" --> CP
  PR -- "mutual TLS" --> NA
  KC -- "mutual TLS" --> NA
  BR -. "refused" .-> NA
  PP --> G
  CP --> G
  NA --> G
  G --> A
  G --> S
  S -- "wrap and unwrap data keys<br/>via managed identity" --> KV
  EZCA -. "certificates issued at setup" .-> PR
  EZCA -. "certificates issued at setup" .-> KC
```

**What EZEHR trusts, and nothing else:**
1. **Microsoft Entra ID**, but only for tenants that a provider's row in the `organizations` table names (for the demo, our tenant, set on Riverside Primary Care's row).
2. **The EZCA root CA**, whose public certificate is committed in `Security/Trust/`, and only for certificate CNs that have a row in `organizations`.
3. **Its own passkey table**, for patients.

---

## 3. Every party at a glance

| Party | Identity on the platform (`Party.SubjectUri`) | Credential | Where the private key lives | Who vouches | What our code checks |
|---|---|---|---|---|---|
| **Sam (patient)** | patient ID (GUID) | Passkey | The phone's secure hardware; it never leaves | Nobody else; our server stored Sam's public key at enrollment | WebAuthn signature against the stored public key |
| **Dr. Rivera (clinician)** | `urn:entra:{tenantId}:{objectId}` | Security key (FIDO2) used for **Entra** sign-in | Inside the security key | **Microsoft Entra** signs the ID token | Token signature, issuer, and audience (library); tenant trusted; app role `Clinician`; session under 10 h |
| **Lakeside, Northwind, Maple, Riverside (providers)** | `urn:provider:lakeside` and so on | EZCA client certificate (CN `lakeside.providers.ezehr`) | `.pki/lakeside.pfx` on the simulator's machine | **EZCA** | Chain to the EZCA root; not expired; Client Authentication usage; CN found in `organizations` |
| **Kidney Check (app)** | `urn:app:kidney-check` | EZCA client certificate (CN `kidney-check.apps.ezehr`) | `.pki/kidney-check.pfx` | **EZCA**, plus Sam's grant | The same certificate checks, plus a `LabsAndNotes` grant from Sam |
| **Data broker** | none | No certificate, or a self-made one | Its own machine | Nobody | Fails the certificate checks, so `UnverifiedCaller` and refused |
| **EZEHR server → Azure** | App Service **managed identity** | Azure-issued token, automatic | Azure manages it; we store no secret | Azure | Key Vault role: Crypto User (wrap and unwrap). Azure SQL: a database user that can read, write and create tables |

---

## 4. Setup-time flows (done once, before the demo)

### 4a. Issuing certificates with EZCA

The private keys are created on the identity lead's laptop. EZCA only sees **certificate requests** (CSRs), which contain the public key and are signed by the private key to prove possession. EZCA signs the certificate; it never sees a private key.

```mermaid
sequenceDiagram
  autonumber
  participant Dev as Identity lead laptop
  participant EZ as EZCA
  Dev->>Dev: az login as the EZCA PKI admin
  loop lakeside, northwind, maple, riverside, kidney-check
    Dev->>Dev: generate key pair and CSR
    Dev->>EZ: RequestSSLCertificateV2 with CSR, subject name, 365 days, Client Authentication
    EZ-->>Dev: certificate, issuing CA, root CA
  end
  Dev->>Dev: save .pki/NAME.pfx, private and never committed
  Dev->>Dev: commit root and issuing CA certificates, public, to Security/Trust
```

The broker's certificate is made by the same tool **without EZCA** (`certs broker`), on purpose.

### 4b. Entra setup (portal)

- **App registration "EZEHR":**
  - single tenant, platform **Web**, ID tokens on, no client secret
  - redirect URI `/signin-oidc`
  - app role **Clinician**, assigned to Dr. Rivera
- **Dr. Rivera's sign-in methods:** passkey (FIDO2) enabled, and **the physical security key registered** as Dr. Rivera's passkey.
- **EZEHR's organizations table:** Riverside's row carries the tenant id. This is how a tenant id turns into a provider identity; the organization's name comes from the same row. A sysadmin sets it once per environment:

  ```sql
  UPDATE organizations SET entra_tenant_id = '<tenant id>' WHERE uri = 'urn:provider:riverside';
  ```

### 4b-2. The organizations table

`organizations` is the one list of who each organization is: `uri` (its identity on the platform), `kind` (`Provider` or `App`), `display_name`, `certificate_cn` (the CN on its EZCA certificate, compared without case), and `entra_tenant_id` (providers only; a tenant belongs to at most one provider). The server reads it on every request that needs it, so an edit takes effect immediately.

- A new database starts with the five demo organizations: the app seeds them when the table is empty. Once the table has any rows the app never touches it, so a sysadmin's edits and deletions survive restarts, and `ezehr-tools demo reset` leaves the table alone.
- **Cutting off an organization** (for example, a leaked key): delete its row. EZEHR doesn't check certificate revocation, so this is the kill switch, and it takes effect on the next request. Its certificate is then refused at the network door, its clinicians can't sign in, and records it signed no longer verify, so they drop out of every view until the row is restored.
- There is no registration endpoint yet. To add an organization, a sysadmin inserts its row, and EZCA must also issue a certificate with that CN:

  ```sql
  INSERT INTO organizations(uri, kind, display_name, certificate_cn)
  VALUES ('urn:provider:harbor', 'Provider', N'Harbor Clinic', 'harbor.providers.ezehr');
  ```
- The tools have no database; they use the demo list in `OrgDirectory` (`OrgDirectory.Demo`).

**Deployment note (moving off `App:TrustedTenants`):** before deploying, copy the tenant id out of the App Service setting `App__TrustedTenants__…` (the id is in the setting's name, with underscores where the hyphens go). Once the deployed app has started and seeded the table, run the `UPDATE` above against prod, then delete the old setting. Dr. Rivera is refused between the deploy and the `UPDATE`.

### 4c. Azure plumbing

- App Service has a **system-assigned managed identity**. Key Vault grants it:
  - **Key Vault Crypto User** on `records-kek`, to wrap and unwrap patients' data keys. The server holds no certificate of its own and needs no other secret.
- Azure SQL uses **Entra-only authentication**. The same managed identity is a database user (`db_datareader`, `db_datawriter`, `db_ddladmin`), so the connection string holds no password.
- App Service **client certificates = "Optional Interactive User"**:
  - browsers aren't asked for a certificate
  - programs (our tools) can present one
  - App Service passes it to our code in the `X-ARR-ClientCert` header
  - **App Service does not validate it; our code does**

---

## 5. Runtime flows

### Flow 1: Patient enrollment (invite → passkey)

A provider creates an invite, like a patient-portal activation code. Sam opens it on a phone and creates a passkey. Nothing is shared with anyone yet, apart from the optional Kidney Check grant if Sam ticks the box.

```mermaid
sequenceDiagram
  autonumber
  participant Prov as Provider system
  participant Net as Network API
  participant Ph as Sam's phone
  participant Web as Patient pages
  participant KV as Key Vault
  Prov->>Net: POST /create-enrollment-invite over mutual TLS
  Net->>Net: certificate check, caller must be a provider
  Net-->>Prov: invite code and enrollment URL
  Note over Prov,Ph: the clinic gives Sam the link
  Ph->>Web: open /patient/enroll?invite=CODE
  Ph->>Web: register/options with the invite code
  Web-->>Ph: WebAuthn challenge
  Ph->>Ph: Face ID or fingerprint, phone creates the passkey
  Ph->>Web: register/verify with attestation and kidneyCheckOptIn
  Web->>Web: Fido2 verifies, invite marked used
  Web->>KV: wrap a new data key for Sam
  Web->>Web: save patient, passkey public key, provider link, app grant if opted in
  Web-->>Ph: patient cookie, 12 hours
```

**Proven:** the person holding this phone is the person the provider invited. **Stored:** only the passkey's *public* key.

### Flow 2: Patient sign-in

```mermaid
sequenceDiagram
  autonumber
  participant Ph as Sam's phone
  participant Web as Patient pages
  Ph->>Web: signin/options
  Web-->>Ph: WebAuthn challenge, no username needed
  Ph->>Ph: Face ID or fingerprint
  Ph->>Web: signin/verify with the signed challenge
  Web->>Web: Fido2 checks the signature against the stored public key
  Web-->>Ph: patient cookie with patient_id
```

### Flow 3: Clinician sign-in (Entra + security key)

**Our app never talks to the security key.** Microsoft does. Our app only checks Microsoft's signed token, then applies its own rules.

```mermaid
sequenceDiagram
  autonumber
  participant Br as Clinic laptop browser
  participant Key as Security key
  participant Web as Clinician pages
  participant En as Microsoft Entra ID
  Br->>Web: GET /clinician
  Web-->>Br: redirect to Entra, OpenID Connect, prompt=login
  Br->>En: Entra sign-in page, never skipped for a live Entra session
  En-->>Br: sign in with your passkey
  Br->>Key: WebAuthn challenge from Entra
  Key-->>Br: tap and PIN, signed response
  Br->>En: signed response
  En->>En: verify the security key
  En-->>Br: ID token signed by Microsoft
  Br->>Web: POST /signin-oidc with the ID token
  Web->>Web: Microsoft.Identity.Web checks signature, issuer, audience
  Web->>Web: CallerManager checks trusted tenant and Clinician role
  Web-->>Br: clinician cookie, 10 hours, no renewal
```

**Proven:** Microsoft vouches that this is Dr. Rivera from the Riverside tenant, signed in just now, and holding the Clinician role. `prompt=login` makes Entra ask again on every sign-in, so signing out of EZEHR, or the end of the 10-hour shift, really ends access, and the key tap happens each time.

**What makes it the security key:** our app doesn't check *how* Dr. Rivera authenticated (v2 ID tokens carry no `amr`). Entra enforces it: a Conditional Access policy requiring the "phishing-resistant MFA" authentication strength for the EZEHR app. Without that policy, a password sign-in is accepted.

**Not yet proven:** that Dr. Rivera may see Sam's record. That is Flow 5.

### Flow 4: A provider pushes records (mutual TLS + signed records)

Riverside's system uses this same flow after the visit to push Dr. Rivera's plan and referral (see section 1, "Who writes, who reads").

Two separate proofs are at work:
- **The TLS client certificate** proves *who is connected right now*.
- **The signature on each record** proves *who wrote this record*, and keeps proving it later, in exports and in other systems.

```mermaid
sequenceDiagram
  autonumber
  participant Tool as Provider simulator
  participant FE as App Service front end
  participant App as Network API
  participant KV as Key Vault
  Tool->>Tool: sign each FHIR record as a JWS with the Lakeside key
  Tool->>FE: TLS handshake, presents the Lakeside certificate
  FE->>App: request plus X-ARR-ClientCert header
  App->>App: CertificateValidator, chains to EZCA root, valid, Client Authentication
  App->>App: organizations table, CN lakeside.providers.ezehr is Lakeside Urgent Care
  App->>App: RecordVerifier, every record signed by Lakeside
  App->>App: provider_links, LUC-20417 belongs to Sam
  App->>KV: unwrap Sam's data key
  App->>App: encrypt, store, skip duplicates, replace corrected records, audit PushSignedRecords
  App-->>Tool: stored, alreadyStored and replaced counts
```

### Flow 5: Sharing with a clinician (code → approval → grant)

A grant is the only thing that lets a signed-in clinician into a record. Sam creates it, and it is bound to one verified identity.

```mermaid
sequenceDiagram
  autonumber
  participant Ph as Sam's phone
  participant Web as EZEHR
  participant Doc as Dr. Rivera's laptop
  Ph->>Web: create share offer, for example 4 hours
  Web-->>Ph: code ABCD-EFGH, single use, valid 10 minutes
  Ph-->>Doc: Sam shows or reads out the code
  Doc->>Web: redeem the code, with the clinician cookie
  Web->>Web: Pending grant bound to urn:entra:tenant:object
  Web-->>Doc: waiting page, polling
  Web-->>Ph: approval prompt via polling, Give Dr. Rivera at Riverside Primary Care access until 5:00 PM?
  Ph->>Web: Approve, with the patient cookie
  Web->>Web: grant Active until 5:00 PM
  Web-->>Doc: waiting page moves on to the record
```

The name in the prompt comes from **Microsoft's token**, not from anything the doctor typed. A photo of the code doesn't help anyone else: the code works once, and Sam sees exactly who redeemed it.

### Flow 6: The clinician reads the record

```mermaid
sequenceDiagram
  autonumber
  participant Doc as Dr. Rivera's laptop
  participant Pg as Record page
  participant G as Access gate
  participant KV as Key Vault
  participant Ph as Sam's phone
  Doc->>Pg: GET record, with the clinician cookie
  Pg->>Pg: CallerManager turns the cookie into VerifiedCaller with session start time
  Pg->>G: Authorize ReadFullRecord for Sam
  G->>G: shift under 10 h, grant found, Active, not expired, scope Full
  G->>G: audit, Dr. Rivera viewed your record
  G-->>Pg: allowed
  Pg->>KV: unwrap Sam's data key
  Pg->>Pg: decrypt entries, RecordVerifier checks every signature
  Pg-->>Doc: combined record with Signed by badges
  Ph->>Pg: activity polling
  Pg-->>Ph: Dr. Rivera, Riverside Primary Care, viewed your record
```

The clinician pages are read-only. A clinician grant (scope `Full`) allows `ReadFullRecord` and `ReadEgfrLabs`, and the gate answers `ScopeDenied` to any write. Dr. Rivera documents the visit in Riverside's own system, which pushes it through Flow 4.

Every page load repeats the gate check. That's how revocation and expiry take effect immediately (Flow 9).

### Flow 7: The Kidney Check app (an app on the open platform)

The app is just another organization at the network door. What makes it special is its **narrow grant**: lab results only, plus permission to post notes.

```mermaid
sequenceDiagram
  autonumber
  participant App as Kidney Check app
  participant Net as Network API
  participant G as Access gate
  App->>Net: GET /patients/ID/read-egfr-labs over mutual TLS with the kidney-check certificate
  Net->>Net: certificate check, CN kidney-check.apps.ezehr is the Kidney Check app
  Net->>G: Authorize ReadEgfrLabs
  G->>G: LabsAndNotes grant from Sam, Active
  G-->>Net: allowed, audit Kidney Check read your lab results
  Net-->>App: eGFR results
  App->>App: rule, more than 25 percent drop across 2 or more providers
  App->>Net: POST /patients/ID/write-signed-note with a Communication signed by the app certificate
  Net->>G: Authorize WriteSignedNote
  G-->>Net: allowed
```

If the app tries `GET /patients/ID/read-full-record` (everything), the gate answers `ScopeDenied`. If Sam revokes the grant, the next call gets `GrantRevoked`.

### Flow 8: The data broker is refused (the Particle moment)

```mermaid
sequenceDiagram
  autonumber
  participant Br as Data broker
  participant Net as Network API
  participant G as Access gate
  participant Ph as Sam's phone
  Br->>Net: GET /patients/ID/read-full-record?purpose=treatment with no certificate
  Net->>G: UnverifiedCaller NoCertificate, claimed purpose treatment
  G->>G: audit denial with the claimed purpose
  Net-->>Br: 401
  Br->>Net: same request with a self-made certificate
  Net->>Net: chain does not reach the EZCA root
  Net->>G: UnverifiedCaller UntrustedCertificate
  G->>G: audit denial
  Net-->>Br: 401
  Ph->>Net: activity polling
  Note over Ph: Blocked, unverified requester claimed treatment, shown twice
```

The broker **claims** "treatment", just as the companies did in the Particle–Epic dispute. EZEHR records the claim but ignores it: a purpose isn't a credential.

### Flow 9: How access ends

| Event | What happens on the next request | Patient sees |
|---|---|---|
| Sam taps **Revoke** | Gate: `GrantRevoked`. The clinician is sent to "access ended" | The denied attempt, if the clinician tries again |
| The grant reaches its **end time** | Gate marks it `Expired`, then `GrantExpired` | The same |
| The clinician's **10-hour shift** ends | Cookie expired (sign in again with the key); the gate also checks `ShiftEnded` | n/a |
| Sam revokes **Kidney Check** | The app gets 403 `GrantRevoked` | The denial in activity |

Expiry stops **future** reads. It can't erase what someone already saw, which is why every read is logged.

### Flow 10: A provider audits its clinicians

Riverside may need to review what its own clinicians did on EZEHR, the way a hospital audits chart access. Its system asks over the same network door it already uses, with its EZCA certificate.

```mermaid
sequenceDiagram
  autonumber
  participant Sys as Riverside system
  participant Net as Network API
  participant OA as OrganizationAuditManager
  participant DB as audit_events
  Sys->>Net: GET /list-organization-clinicians over mutual TLS
  Net->>Net: certificate check, CN found in organizations, caller is urn:provider:riverside
  Net->>OA: list clinicians for urn:provider:riverside
  OA->>DB: clinician events where actor_org_uri is urn:provider:riverside
  OA-->>Sys: Dr. Rivera, first and last seen, attempts and refusals
  Sys->>Net: GET /read-organization-audit?clinicianUri=...&providerPatientId=RPC-1107
  Net->>OA: same certificate check, then the filtered audit
  OA->>DB: events where actor_org_uri is urn:provider:riverside, filtered, paged by id
  OA-->>Sys: events, oldest first, with nextAfterId when more follow
```

- **What it sees:** every attempt by someone it vouches for: its clinicians' reads and share-code redemptions, granted or refused, and its own system's calls (pushes, invites). Each audit row records the actor's organization (`actor_org_uri`) when the event happens, so a clinician who later leaves stays in the history of the organization they acted for.
- **What it never sees:** anything done by another organization, its clinicians, an app, or an unverified caller, even for the same patient.
- **How patients appear:** by EZEHR patient id, plus the provider's own patient id when the patient is linked to it (`provider_links`). No names.
- **Filters:** `clinicianUri`, `patientId`, `providerPatientId`, `since` (inclusive), `until` (exclusive), and paging by `afterId` with `limit` (1 to 500, default 100). `ezehr-tools provider audit <slug>` and `provider clinicians <slug>` call both endpoints.

---

## 6. How the components fit together

```mermaid
flowchart TB
  subgraph Doors
    CS["IClientCertificateService<br/>reads X-ARR-ClientCert"]
    CR["CallerManager.FromCertificate"]
    PK["EnrollmentManager<br/>IFido2Service"]
    CA["CallerManager.FromClinician<br/>Entra claims"]
  end
  subgraph Trust
    CV["CertificateValidator<br/>EZCA root and intermediates"]
    OD["OrganizationDirectory<br/>organizations table<br/>CN or tenant to Party"]
  end
  subgraph Core
    G{{"AccessManager"}}
    GS["GrantManager"]
    AL[("AuditManager")]
  end
  subgraph Data
    RS["RecordManager"]
    KW["IKeyWrapService<br/>Key Vault"]
    RV["RecordVerifier"]
    RSG["RecordSigner"]
  end
  CS --> CR
  CR --> CV
  CR --> OD
  CR -- "Caller" --> G
  CA -- "Caller" --> G
  PK -- "patient cookie" --> GS
  G --> GS
  G --> AL
  G -- "allowed" --> RS
  RS --> KW
  RS --> RV
  RV --> CV
  RV --> OD
  RSG -. "used by the tools" .-> RS
```

| Component | Called by | Takes | Gives back |
|---|---|---|---|
| `IClientCertificateService` | `NetworkController` | the HTTP request | the caller's certificate, or nothing |
| `CertificateValidator` | `CallerManager`, `RecordVerifier` | a certificate (+ chain) and a time | OK, `UntrustedCertificate`, or `CertificateExpired` (2-minute clock-skew tolerance) |
| `OrganizationManager` / `OrganizationDirectory` | `CallerManager`, `RecordManager` (for `RecordVerifier`), `EnrollmentManager` | the `organizations` table; then a certificate's CN, a platform URI or an Entra tenant id | a `Party` (provider or app), or nothing |
| `CallerManager.FromCertificateAsync` | `NetworkController` | a certificate or nothing | `VerifiedCaller(party)` or `UnverifiedCaller(reason)` |
| `EnrollmentManager` | patient pages, `PatientPasskeyController` | WebAuthn responses | a patient cookie (`patient_id`) |
| `CallerManager.FromClinicianAsync` | clinician pages | Entra claims from the cookie; the tenant is looked up in `organizations` | `VerifiedCaller(clinician, sessionStartedAt)`, or the `NotAClinician` / `UntrustedOrg` message |
| `GrantManager` | patient pages, clinician pages, gate | offers, redemptions, approvals, revocations | grants |
| **`AccessManager`** | **every read and write** | patient, action, `Caller`, claimed purpose | allowed + grant, or a `DenyReason`. **Always exactly one audit event** |
| `AuditManager` | gate; patient activity page | events | the patient's activity list |
| `OrganizationAuditManager` | `NetworkController` (`read-organization-audit`, `list-organization-clinicians`) | a `Caller` and a filter | the caller organization's own events or clinicians, or a `DenyReason` |
| `RecordManager` | `NetworkController`, clinician and patient pages | patient ID and `Caller` | verified signed entries, after asking the gate; unwraps the key through Key Vault on every call |
| `IKeyWrapService` | `RecordManager` | a patient data key | the wrapped or unwrapped key (Key Vault, `RSA-OAEP-256`) |
| `RecordSigner` | the tools (provider simulator, Kidney Check) | a FHIR resource and a certificate with its key | a JWS with the certificate attached |
| `RecordVerifier` | ingest, reads, export | a JWS | `VerifiedEntry` with the signer `Party` and the signing time |
| `ICertificateAuthorityService` (`EzcaCertificateAuthorityService`, `LocalCertificateAuthorityService`) | **only the `certs` tool** (setup time) | a CSR and a subject name | a certificate and chain |

---

## 7. What each mechanism proves (and what it doesn't)

| Mechanism | Proves | Does **not** prove |
|---|---|---|
| HTTPS server certificate (`*.azurewebsites.net`) | You're talking to the real EZEHR site | Anything about the client |
| Patient passkey | The same person who enrolled with that provider invite is back | Their legal identity (identity proofing is a stretch goal) |
| Entra ID token + security key | Microsoft vouches: this is Dr. Rivera from a trusted tenant, holding the Clinician role, signed in with a phishing-resistant key | That Sam agreed to share; that comes from the grant |
| Clinician cookie | That sign-in happened within this 10-hour shift | That it hasn't been stolen; it's limited by HttpOnly, same-site, and the 10-hour cap |
| EZCA client certificate (mutual TLS) | EZCA vouches that this connection is Lakeside's (or the app's) system | That the organization may read Sam's data; that comes from links and grants |
| Record signature (JWS) | This exact record was written by Lakeside (or the app; or Riverside, for a note authored by Dr. Rivera), and hasn't changed since | That its medical content is correct |
| Grant | Sam agreed: this party, this scope, until this time | Anything after it's revoked or expired |
| Audit event | That an attempt happened, by whom, with what outcome and claimed purpose, and which organization vouched for the actor | Nothing beyond that; it's evidence, not prevention |
| Key Vault envelope encryption | Data at rest is unreadable without EZEHR's managed identity calling Key Vault | That EZEHR itself can't read it (it can, behind the gate; see the spec's honest limits) |

---

## 8. Denial reference

| `DenyReason` | Where it comes from | HTTP or page | Logged to the patient? |
|---|---|---|---|
| `NoCertificate` | Network door: nothing presented | 401 | Yes, when the request targets a patient |
| `UntrustedCertificate` | Network door: chain doesn't reach EZCA, or the CN isn't in `organizations` | 401 | Yes, when the request targets a patient |
| `CertificateExpired` | Network door, or record verification | 401 / record rejected | Yes, when the request targets a patient |
| `ShiftEnded` | Gate: clinician session over 10 h | "Sign in again" page | Yes |
| `NoGrant` | Gate: no grant for this identity | "Access ended" page / 403 | Yes |
| `GrantPending` | Gate: Sam hasn't approved yet | Waiting page / 403 | Yes |
| `GrantRevoked` / `GrantExpired` | Gate | "Access ended" page / 403 | Yes |
| `ScopeDenied` | Gate: an app asked for more than lab results, or a clinician tried to write | 403 | Yes |

---

## 9. Glossary

- **Passkey / FIDO2 / WebAuthn:** a sign-in method where the device holds a private key and signs a challenge. There's no password, and a passkey only works on the real site, so it can't be phished. A **security key** (for example a YubiKey) is a passkey on dedicated hardware.
- **OpenID Connect (OIDC):** the standard way a website asks Microsoft Entra to sign someone in. Entra returns a signed **ID token** saying who they are.
- **App role:** a label (here, `Clinician`) that Entra puts in the token for users the tenant admin assigned to it.
- **X.509 certificate:** a public key plus identity details, signed by a certificate authority (CA). **EZCA** is ours.
- **Chain / root:** our server trusts the EZCA root. A certificate is trusted if it traces back to that root through EZCA's issuing CA.
- **CSR:** a certificate signing request: the public key plus requested details, signed by the private key. The CA signs it into a certificate without seeing the private key.
- **Mutual TLS:** normal HTTPS where the *client* also presents a certificate during the handshake.
- **JWS:** a standard signed-data format (RFC 7515). Our records are JWS with the signer's certificate attached (the `x5c` header).
- **Envelope encryption:** each patient's records are encrypted with that patient's own data key, and that key is stored only in wrapped (encrypted) form by a Key Vault key that never leaves Key Vault.
- **Managed identity:** an identity Azure gives our App Service automatically, so it can call Key Vault without any stored password or secret.
