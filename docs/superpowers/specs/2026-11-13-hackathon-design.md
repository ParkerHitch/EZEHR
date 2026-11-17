# Hackathon Design: Patient-Controlled Health Record with Verified Access

*Working name: EZEHR. Written 2026-11-13; updated the same evening with the C# stack, the identity design, and the platform-first framing. Updated 2026-11-14: clinicians only read; Riverside's own system writes the visit (section 8, "Where clinicians write").*

## 1. Context

- **Event:** 24-hour virtual hackathon hosted by Keytos, running **2:00 PM Nov 13 to 2:00 PM Nov 14** (team's local time). We are entering the **Health** track.
- **Team:** two Purdue Computer Engineering students who both know C#. One interned at Keytos and built the Keytos CLI client for EZCA and EZRADIUS.
- **Keytos:** about 12 people in Boston, employee-owned, founded by ex-Microsoft identity/PKI engineers. Products: EZCA (cloud PKI), EZRADIUS (cloud RADIUS), EZSSH (Entra ID sign-in turned into short-lived SSH certificates), EZCMS (passwordless smart card and YubiKey onboarding), EZMonitor, and Keytos Shield (bundle). Everything is built natively for Microsoft Entra ID and Intune, and their codebase is C#.
- **Keytos and healthcare:** their careers page mentions "our new healthcare branch," with no public detail. Our project's goal: replace closed, proprietary record systems with an open, patient-centered approach, so that diseases are detected earlier.
- **How Keytos runs itself (the model we are copying):** no standing access to production; just-in-time access that expires after a few hours; short-lived certificates instead of long-lived keys; managers alerted about who accessed what; support tools show no customer names. Their culture prizes "security that your team actually uses" and bold ideas.

## 2. Intent and the why

**Thesis:** health data stays closed partly because data holders cannot verify who is asking or why. In 2024 Epic stopped answering some record requests from companies connected through Particle Health. Those requests were labeled "treatment," the one purpose providers must answer, and one that the requester simply declares. Epic said at least one company was really looking for class-action plaintiffs. When every access carries a **verified identity, the patient's consent, and an expiry**, that excuse for blocking goes away.

**Pitch line:** *"Epic says it blocks data to protect patients. We make blocking unnecessary."*

**The product is a platform, not a clinical app.** It is one central, cloud-hosted health record per patient:
- It brings together records from every provider, in an open standard format.
- **Nobody can read it by default.**
- Verified clinicians, and apps the patient approves, read it only under a live grant from the patient.
- The patient sees every access attempt.

**How it should feel:** like an identity company entering healthcare. The security should be real, invisible to the user, and recognizably "the Keytos way": passwordless, phishing-resistant sign-in, certificates for every organization, no standing access, everything logged.

**Openness means:**
- Records follow the patient, not the organization.
- Any verified provider, and any app the patient approves, can get access with the patient's permission. Here the walled garden is gone: the platform has an open API that any certified organization or app can use.
- Data is stored in an open format (FHIR R4), and the patient can export all of it at any time.

**The health payoff, kept small:** because records from many providers come together in one place, an app can notice what no single provider could. The demo shows this with one tiny example app (section 7). It exists to prove the platform's app model, not as a clinical feature.

## 3. Goals

1. A demo that runs the story in section 7 end to end.
2. **Real identity and crypto, not mocked:**
   - patient passkeys
   - clinician Entra ID sign-in with a **physical security key**
   - **EZCA certificates** for every organization and app, used for mutual TLS and record signatures
   - encryption at rest with keys in Azure Key Vault
   - one server-side access check
3. **The platform's app model:** an example app that reads only what the patient granted, with that grant visible and revocable. *(Updated 2026-11-17: apps are now rows in the `organizations` table with a declared scope, the Access page lists all of them, and a second example app, Medication Check, has a wider scope. See `2026-11-17-app-platform-design.md`.)*
4. **Usable:** a patient or clinician never sees a password, a key, or a certificate dialog.
5. A design that holds up in Q&A with identity and PKI experts (see section 10).
6. **A small openness feature:** "Export my whole record" downloads a FHIR bundle.

## 4. Non-goals

- Clinical charting, orders, billing, scheduling. EZEHR has no charting UI: after the visit, Riverside's system (simulated) pushes Dr. Rivera's plan and one referral as signed FHIR resources. There are no order workflows.
- Real integrations with hospital EHRs, FHIR servers, or health information exchanges. Source records are **seeded fake data** in FHIR format.
- Clinical decision rules beyond the one-line example app. No medical guidelines, charts, or risk scores.
- Claims of HIPAA compliance.
- Native mobile apps. The patient side is a phone-friendly web app.
- Scanning QR codes with a camera. The clinician types the share code.
- A focus on homelessness (considered and dropped: too niche for the broader goal, and it adds hard side problems).
- Production-grade infrastructure, multi-region, scale.

## 5. Anti-goals (failure even if it "works")

- **A password anywhere on screen**, including admin or seed accounts shown in the demo.
- **Security that is faked**, such as a hard-coded "verified" flag or a check only in the browser.
- **Homemade crypto primitives or protocols** where a standard exists. Use .NET's built-in cryptography, WebAuthn, X.509, mutual TLS, and OpenID Connect.
- **Clumsy security UX.**
- **Overclaiming.** We do not say "our servers can't read your data" (they can, behind the access check) or "expiry erases what the doctor saw."

## 6. Constraints

- **Time:** the event started at 2:00 PM. As of this update (about 6 PM), roughly **20 hours remain**, and no setup is done yet.
  - Setup happens inside the event: the Azure account and Entra tenant, the app registration, the doctor's security-key sign-in, the EZCA trial and its certificate authorities, Key Vault, and hosting.
  - That setup is portal work that doesn't depend on code, so it should start immediately, in parallel with planning.
  - Code can be written now, since the event has started.
- **Team skills:** both know C#; neither knows Node or TypeScript. Browser JavaScript is limited to the passkey prompt.
- **Azure:** Azure for Students credit and a free Entra ID tenant. The Entra ID P2 30-day free trial is the fallback if security-key sign-in or Conditional Access needs it.
- **EZCA is in the core build.** Keytos, the host, approved using its products through their free trials. EZCA's free trial lasts a month and needs no credit card. **EZCA issues every certificate in the demo:**
  - the three source providers, plus Riverside Primary Care (Dr. Rivera's clinic)
  - the example app
  - the unverified broker, whose certificate is deliberately **not** from EZCA
  - Our own small `LocalCA` exists only for automated tests and as the fallback if EZCA setup stalls.
- **EZCA specifics that shape the design:**
  - Certificate lifetime is set in whole days.
  - Certificates are requested for registered subject names ("domains").
  - Applications call the API with an Entra token, for example App Service's managed identity.
  - Applications get domain-level roles; only users can be PKI admins.
  - Keytos publishes a .NET SDK (`EZCAClient` on NuGet).
- **Physical security keys:** the team has them. One is Dr. Rivera's Entra sign-in passkey.
- **Demo video is about 5 minutes.** That fits the full story with a short intro and close. A stretch goal is worth building only if it fits in the video.
- **No requirement** to use Keytos products, but using EZCA is explicitly allowed (see above).
- **Judging format:** a recorded video; live Q&A is likely but unconfirmed. If there is a live demo, grants need a short duration or a revoke button (which we have), and the phone-plus-laptop flow must be rehearsed.

## 7. Demo story

The patient is **Sam**. Sam's care is split across three providers on three different systems: Lakeside Urgent Care, Northwind Workplace Health, and Maple Street Family Medicine. Sam has just moved and is seeing a new primary care doctor, **Dr. Rivera at Riverside Primary Care**.

1. **Records arrive over the network.**
   - Each provider's system connects with its EZCA certificate and pushes Sam's records.
   - Each record is signed by the provider that wrote it, and the app shows "Signed by Lakeside Urgent Care ✓."
2. **One combined record, plus an app.**
   - Sam signs in on a phone with a passkey and sees everything in one timeline.
   - The **Kidney Check** example app, which Sam allowed at enrollment to read lab results only, has posted a note: kidney function dropped 34% across three providers, and no single result was flagged.
   - Sam's access list shows the app's grant ("Kidney Check · lab results only · you allowed this Oct 2 · Revoke").
3. **Verified sharing at the visit.**
   - Sam taps Share, picks a duration, and shows a short code (with a QR code of it).
   - Dr. Rivera signs in to the clinician page through the clinic's Entra ID by **tapping a physical security key**.
   - Dr. Rivera enters the code. Sam's phone asks "Give Dr. Rivera at Riverside Primary Care access until 5:00 PM?" and Sam approves.
   - Dr. Rivera sees the combined record and the app's note (and the regular ibuprofen from urgent care). The clinician view is read-only.
   - After the visit, Riverside's own system pushes Dr. Rivera's plan and a nephrology referral, like any other provider. Sam's timeline shows "Written by Dr. Rivera · signed by Riverside Primary Care ✓."
4. **The Particle moment.**
   - A data broker's system asks the network for Sam's record, claiming the purpose is "treatment."
   - First it has no certificate; then it has one that EZCA didn't issue. Both requests are refused.
   - Sam's activity shows: "Blocked: unverified requester claimed treatment."
5. **Visibility and expiry.** Sam's phone showed each read live. When the grant expires, or Sam revokes it, Dr. Rivera's open record locks itself within a couple of seconds and shows "access ended."

**Stretch goals, in priority order:**
1. Conditional Access requiring phishing-resistant sign-in for the clinician app (needs the P2 trial; about 30 minutes).
2. Break-glass emergency access (ER scenario). **Built 2026-11-17:** see `2026-11-17-emergency-access-design.md`. The patient opts in and gets an emergency code; a verified clinician who enters it with a reason gets one hour of access with no approval, and the patient and the clinician's organization both see it.
3. An EZCA certificate on the security key's smart-card slot, used to sign in to Entra (certificate-based authentication). This is Keytos's real product flow, but setup is heavy.
4. Recovery after a lost phone using a verifiable credential (Entra Verified ID).

## 8. Approaches considered

**How real the security is (decided first):** real security, built on Microsoft and Keytos identity. We rejected a fully mocked version, because it would not survive scrutiny from identity engineers. A standards-only build on SMART Health Links was also rejected: anyone holding the link can read the data, and that is the weakness we pitch against.

**Where the data lives:**

| Option | Trade-off |
|---|---|
| Patient-held keys (end-to-end encrypted, like 1Password) | Strongest privacy claim. Rejected: if the patient is unconscious or their phone is dead, nobody can open the record; lost keys mean lost history; hospitals need to write; and apps can't analyze it. It doesn't fit a system meant to replace hospital databases. |
| **One central record nobody can open by default (picked)** | Mirrors how Keytos runs production. We give up "our servers can't read your data" and claim instead "nobody reads your data without your permission and a verified identity, and you see every read." |

**How clinicians and organizations prove who they are (revised):**

| Option | Trade-off |
|---|---|
| A. Browser-held keys: Entra sign-in → per-clinician EZCA certificate bound to a WebCrypto key → every request signed | The strongest "EZSSH for health records" pitch. Rejected after review: it needs the most browser JavaScript (which the team doesn't know), a custom request-signing protocol, and a software-only key. It has the biggest debugging risk with 20 hours left. |
| **B. Certificates for organizations, sessions for people (picked)** | Clinicians sign in with Entra ID using a **physical security key**, then use a normal server session that ends after a 10-hour shift. Organizations and apps connect with **EZCA certificates over mutual TLS** and sign what they write. Everything is standard (OpenID Connect, mutual TLS, X.509) and all C#. We lose per-clinician certificates and per-request signatures; clinician requests ride on a session cookie. |
| EZCA certificate on the security key, used for Entra certificate-based sign-in | Very on-brand for Keytos and needs no change to our code. Kept as stretch goal 3, because setup (a user-certificate template, generating the key on the device, Entra's certificate sign-in configuration) could take hours. |
| Browser presents the security key's certificate to our app (mutual TLS for people) | Gives a hardware-backed per-clinician certificate on every request without custom JavaScript. Rejected: browser certificate pickers, App Service client-certificate configuration for browser pages, and revocation checks for long-lived certificates. |

**Where clinicians write (revised 2026-11-14):** the rule is *people read; organizations write, and sign what they write.*

| Option | Trade-off |
|---|---|
| A note form on the clinician page, signed by the platform's certificate on the clinician's behalf | The original plan. A live typing moment in the video, but inconsistent: the clinic's own chart never sees its doctor's plan, EZEHR vouches for clinical content it didn't write, and Riverside had no identity on the network. History and referrals would end up squeezed into a free-text note. Rejected. |
| **The clinician page is read-only; Riverside's system pushes the visit documentation (picked)** | Matches real custody: the clinic's system is the record of the care it gave, and it signs it. One write path, one signature model, no platform certificate. Costs the on-camera typing moment; the plan arrives from a console push instead. |
| SMART on FHIR EHR launch (clinician opens EZEHR from inside the clinic's EHR, carrying both identities) | How it would work in production. It needs a simulated EHR to launch from, so it's too big for the event. Mentioned in Q&A as the path forward. |

**Stack (revised):** C# / ASP.NET Core with Razor Pages, picked over TypeScript/Node.
- The team knows C#; Keytos's codebase and EZCA's .NET SDK are C#.
- .NET has certificates, mutual TLS, AES-GCM, Entra sign-in and Key Vault built in or first-party.

**The early-detection feature (revised):**

| Option | Trade-off |
|---|---|
| A clinical feature (guideline-based kidney rules, chart, flag card) | Over-weighted the medical details. Rejected. |
| **An example app on the open platform (picked)** | Proves the app model: scoped, revocable, logged access for software, which is the anti-Epic point. One simple rule, under an hour of work. |
| Cut it entirely | Saves an hour, but leaves the Health track story with only "the doctor sees the combined record." |

## 9. Design: what is decided

### Who proves identity, and how

The full, flow-by-flow breakdown with diagrams is in `docs/auth-flows.md`.

| Party | Proves identity with | Where the private key lives | Who vouches |
|---|---|---|---|
| Patient (Sam) | Passkey (WebAuthn) | The phone's secure hardware; never leaves it | Our server verifies the passkey |
| Clinician (Dr. Rivera) | Entra ID sign-in with a **physical security key** (FIDO2), through OpenID Connect | Inside the security key | Microsoft signs the sign-in token; our server checks the tenant and the Clinician app role |
| Providers (the 3 sources, plus Riverside) | **EZCA certificate**, presented over **mutual TLS**; records signed with the same key | The provider system's key file (the simulator's, for the demo) | EZCA; our server checks the chain to the EZCA root and matches the organization |
| Example app (Kidney Check) | **EZCA certificate** over mutual TLS | The app's key file | EZCA, plus Sam's grant |
| Data broker (demo) | Nothing, or a certificate EZCA didn't issue | n/a | Nobody, so it's refused |
| Records at rest | AES-256-GCM, one data key per patient, wrapped by a Key Vault key | Key Vault (the wrapping key never leaves it) | Azure, via App Service's managed identity (no secrets) |

### Components

1. **Web app** (ASP.NET Core Razor Pages, one site).
   - **Patient area** (phone):
     - passkey sign-up from a provider invite, and passkey sign-in
     - the combined timeline with "signed by" badges
     - Share: pick a duration, then show the code and QR
     - the approval prompt
     - live activity, including denied attempts
     - active grants with Revoke
     - connected clinics (provider links: they can add records, not read them), with Disconnect
     - Export
   - **Clinician area** (laptop):
     - sign in with Entra ID (security key)
     - enter a share code
     - wait for Sam's approval
     - view the record (read-only)
     - an "access ended" page
   - **Live updates** by simple polling (htmx, or a few lines of script).
   - **The only custom browser JavaScript is the passkey prompt.**
2. **Network API** (same site, under its own path, mutual TLS with EZCA certificates).
   - Providers push records.
   - Approved apps read what their grant allows and post notes back.
   - Every request's certificate is checked against the EZCA root and mapped to a known organization or app.
   - Requests with no certificate, or an untrusted one, are refused and logged to the patient they targeted.
   - App Service passes the client certificate to the app; the app does all the checking.
   - **Fallback**, if App Service's client-certificate settings interfere with the browser pages: certificate-signed requests, still all C#.
3. **Platform core.**
   - Grants and share codes.
   - **One access check** that every read and write goes through, and that writes exactly one audit event per attempt.
   - Per-patient envelope encryption.
   - Signature verification on every stored record.
4. **Tools** (C# console apps):
   - **certificate issuer** (EZCA via the `EZCAClient` SDK, or `LocalCA`)
   - **provider simulator** (invites and record pushes, for the three sources and for Riverside's post-visit documentation)
   - **Kidney Check app**
   - **data broker**

### Flows

- **Patient enrollment.**
  - A provider issues a single-use invite, like a patient-portal activation code. Redeeming it creates Sam's account with a passkey, or links another provider to an existing account.
  - At enrollment Sam can also allow the Kidney Check app to read lab results. **It is off unless Sam turns it on.**
  - The demo starts with Sam already enrolled.
- **Records arriving.** The provider simulator connects with its EZCA certificate and pushes signed FHIR records for its patient ID. The server checks the certificate, the link to Sam, and each record's signature, then encrypts and stores them. Re-sending the same records does nothing.
- **App access.** The Kidney Check app connects with its EZCA certificate and asks for Sam's lab results.
  - The access check finds Sam's grant (lab results only) and returns them, then logs the read.
  - The app's one rule: if eGFR fell more than 25% from the earliest to the latest result, across at least two providers, it posts a signed note.
  - If Sam revokes the grant, the next request is refused.
- **Clinician sharing.**
  1. Sam creates a share offer: an opaque, single-use, 8-character code that expires in 10 minutes.
  2. Dr. Rivera, signed in, enters it. This creates a pending grant bound to Dr. Rivera's Entra identity.
  3. Sam's phone shows the approval prompt with Dr. Rivera's verified name and organization.
  4. On approval, the grant is active until its end time.
  5. Every page load of the record passes the access check and is logged.
- **Visit documentation.** Dr. Rivera documents the visit in Riverside's own system, not in EZEHR. Riverside's system (the provider simulator) pushes the plan as a FHIR `Communication` and the referral as a `ServiceRequest`, signed with **Riverside's EZCA certificate**, with Dr. Rivera as the author. The badge reads "Written by Dr. Rivera · signed by Riverside Primary Care ✓." Riverside is linked to Sam the usual way, through an invite Sam redeems at the visit.
- **Shift limit.** The clinician's session is fixed at 10 hours from sign-in, with no sliding renewal. After that, the clinician signs in again with the security key.
- **The broker.** It calls the network API for Sam's record with purpose "treatment": first with no certificate, then with a self-made one. Both are refused, and Sam's activity shows each attempt.

### Stack and hosting (decided; open to correction)

- **Language and framework:** C# on .NET 10 (LTS), ASP.NET Core Razor Pages, xUnit for tests.
- **Libraries and services:**
  - Microsoft.Identity.Web for Entra sign-in
  - Fido2 (fido2-net-lib) for passkeys
  - Dapper over Microsoft.Data.SqlClient, against Azure SQL Database
  - Azure.Identity + Azure.Security.KeyVault.Keys
  - `EZCAClient` (Keytos's EZCA SDK)
  - QRCoder
  - htmx from a CDN
- **Hosting:**
  - Azure App Service (Linux, B1) with its managed identity
  - Azure SQL Database, with Entra-only authentication; App Service connects with its managed identity, so no database password exists
  - Azure Key Vault
  - one Entra ID tenant
  - the EZCA free trial (Basic plan): one root CA and one issuing CA that allows Client Authentication

## 10. Honest limits (prepare for Q&A)

- **The server can decrypt** behind the access check. An insider who changes the code could bypass it. In a real system, this is mitigated by reviewed infrastructure-as-code, audit, and HSM key-release policies. Not eliminated.
- **Expiry stops future reads**, not what a clinician already saw or copied. That is why every read is logged and shown to the patient.
- **Clinician requests use a session cookie.** It's limited to one shift, HttpOnly, and same-site, and the sign-in itself is phishing-resistant (a security key). A stolen cookie would still work until the shift ends. Per-request clinician certificates were considered (section 8) and deferred.
- **The author of a provider's record is the provider's word.** "Written by Dr. Rivera" comes from the record that Riverside signed; EZEHR verifies Riverside, not Dr. Rivera personally. That matches how custody works (the organization stands behind its chart), but no key only the clinician holds is involved.
- **Two doors for one organization.** Dr. Rivera reads through the clinician pages, while Riverside writes through the network API. In production these would merge into a SMART on FHIR EHR launch, so the clinician opens EZEHR from inside Riverside's system and both identities travel together. **Addressed 2026-11-17** by the organization launch (`2026-11-17-organization-launch-design.md`): a clinician can ask for a record only through a launch their organization's system made, from one chart, for one visit. The clinician still reads in EZEHR's own pages.
- **The trust roots are EZCA (for organizations and apps) and each hospital's Entra tenant (for clinicians).**
- **Source data is only as trustworthy as the provider's signature.**
- **A patient passkey proves "the same person who enrolled,"** and enrollment is anchored by a provider's invite. Stronger identity proofing (Verified ID) is a stretch goal.

## 11. Left to the builder

- The schema and the API shape. (Database access is decided: Dapper over Azure SQL Database.)
- Grant duration choices (suggested: 5 minutes for the demo, 1 hour, 4 hours, end of day). The shift length is decided: 10 hours.
- The signature format for records: JWS with the signing certificate attached, or CMS. It must be standard and verifiable outside our app, and it goes into the FHIR export as Provenance.
- The polling mechanism (htmx or a few lines of script).
- Visual design. Mockups can come later.
- Seed data content, and the broker tool's details.
- Whether the audit log is hash-chained (nice to have). **Built 2026-11-17:** a SHA-256 chain per patient, checked on every load of the Activity page. See `2026-11-17-audit-hash-chain-design.md` for what it does and doesn't prove.
- How the two team members split the work.
