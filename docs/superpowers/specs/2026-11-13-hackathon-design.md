# Hackathon Design: Patient-Controlled Health Record with Verified Access

*Working name: TBD. Written 2026-11-13 from a brainstorming session.*

## 1. Context

- **Event:** 24-hour virtual hackathon hosted by Keytos. We are entering the **Health** track.
- **Team:** two Purdue Computer Engineering students. One interned at Keytos and built the Keytos CLI client for EZCA and EZRADIUS.
- **Keytos:** about 12 people in Boston, employee-owned, founded by ex-Microsoft identity/PKI engineers. Products: EZCA (cloud PKI), EZRADIUS (cloud RADIUS), EZSSH (Entra ID sign-in turned into short-lived SSH certificates), EZCMS (passwordless smart card and YubiKey onboarding), EZMonitor, and Keytos Shield (bundle). Everything is built natively for Microsoft Entra ID and Intune.
- **Keytos and healthcare:** their careers page mentions "our new healthcare branch," with no public detail. Our project's goal: replace closed, proprietary record systems with an open, patient-centered approach, so that diseases are detected earlier.
- **How Keytos runs itself (the model we are copying):** no standing access to production; just-in-time access that expires after a few hours; short-lived certificates instead of long-lived keys; managers alerted about who accessed what; support tools show no customer names. Their culture prizes "security that your team actually uses" and bold ideas.

## 2. Intent and the why

**Thesis:** health data stays closed partly because data holders cannot verify who is asking or why. In 2024 Epic stopped answering some record requests from companies connected through Particle Health. Those requests were labeled "treatment," the one purpose providers must answer and one that the requester simply declares. Epic said at least one company was really looking for class-action plaintiffs. When every access carries a **verified identity, the patient's consent, and an expiry**, that excuse for blocking goes away.

**Pitch line:** *"Epic says it blocks data to protect patients. We make blocking unnecessary."*

**The product:** one central, cloud-hosted health record per patient. It brings together records from every provider, in an open standard format. **Nobody can read it by default.** A verified clinician reads it only with the patient's live permission, and the patient sees every read. Because the whole history is in one place, the platform can spot trends that no single provider can see. That is the early-detection payoff.

**How it should feel:** like an identity company entering healthcare, not a clinical app. The security should be real, invisible to the user, and recognizably "the Keytos way": passwordless, short-lived certificates, no standing access, everything logged.

**Openness means:** records follow the patient, not the organization. Any verified provider can get access with the patient's permission. Data is stored in an open format (FHIR R4) and the patient can export all of it at any time.

## 3. Goals

1. A demo that runs the story in section 7 end to end.
2. **Real identity and crypto, not mocked:** passkeys for patients, Entra ID sign-in for clinicians, short-lived X.509 certificates for clinicians, signed record entries, encryption at rest with keys in Azure Key Vault, and a server that unlocks a record only after it checks the request.
3. **An early-detection moment:** a kidney-function decline that is visible only when labs from three providers are combined.
4. **Usable:** a patient or clinician never sees a password, a key, or a certificate dialog.
5. A design that holds up in Q&A with identity and PKI experts (see section 10).
6. **A small openness feature:** "Export my whole record" downloads a FHIR bundle.

## 4. Non-goals

- Clinical charting, orders, billing, scheduling. The clinician writes one simple signed entry in the demo; no order workflows.
- Real integrations with hospital EHRs, FHIR servers, or health information exchanges. Source records are **seeded fake data** in FHIR format.
- Claims of HIPAA compliance.
- Native mobile apps. The patient side is a phone-friendly web app.
- A focus on homelessness (considered and dropped: too niche for the broader goal, and it adds hard side problems).
- Production-grade infrastructure, multi-region, scale.

## 5. Anti-goals (failure even if it "works")

- **A password anywhere on screen**, including admin or seed accounts shown in the demo.
- **Security that is faked**, such as a hard-coded "verified" flag or a check only in the frontend. Three identity engineers will look under the hood.
- **Homemade crypto primitives.** Use standard algorithms and libraries (WebCrypto, WebAuthn, X.509, AES-GCM, ECDSA).
- **Clumsy security UX.**
- **Overclaiming.** We do not say "our servers can't read your data" (they can, behind the check) or "expiry erases what the doctor saw."

## 6. Constraints

- 24 hours, two people, virtual.
- Azure for Students credit; a free Entra ID tenant.
- **EZCA is in the core build.** Keytos, the host, approved using its products through their free trials. EZCA's free trial lasts a month and needs no credit card. **EZCA issues every certificate in the demo:** clinicians, source providers, and the kidney-trend service. The certificate authority stays a swappable component; our own small `LocalCA` is used for automated tests and as the fallback if EZCA setup stalls.
- **EZCA specifics that shape the design:**
  - Certificate lifetime is set in whole days, with a one-day minimum.
  - Certificates are requested for registered subject names ("domains").
  - Applications call the API with an Entra token, such as App Service's managed identity.
  - Applications get domain-level roles; only users can be PKI admins.
- **The hackathon starts 2026-11-14. No setup is done beforehand.** Creating the Azure account and Entra tenant, the app registration, the demo doctor's passwordless sign-in, the EZCA trial and its certificate authorities, Key Vault, and hosting all happen inside the 24 hours. They are the slowest and riskiest part of approach A.
- **Fallback to approach C:** if Entra sign-in for the clinician does not work end to end by a fixed checkpoint early in the event (set in the build plan), clinicians switch to signing in with a passkey against our own directory. Everything after sign-in stays the same: the short-lived certificate, signed requests, grants, and the audit log.
- **No product code before the clock starts.** Most hackathons ban pre-written code, so the design and plan are written as prose, steps, and interfaces only.
- **Demo video is about 5 minutes.** That fits the full five-step story with a short intro and close. A stretch goal is worth building only if it fits in the video.
- **No requirement** to use Keytos products, but using EZCA is explicitly allowed (see above).
- **Judging format:** a recorded video; live Q&A is likely but unconfirmed. If there is a live demo, grants need a short timer or an "expire now" control, and the phone-plus-laptop flow must be rehearsed.

## 7. Demo story

The patient is **Sam**. Sam's care is split across three providers on three different systems: an urgent care clinic, a health screening at work, and a former primary care doctor. Sam has just moved and is seeing a new primary care doctor.

1. **Records arrive.** Sam's history from all three providers is in Sam's record. Each entry is signed by the provider that wrote it, and the app shows "Signed by Lakeside Urgent Care ✓."
2. **The trend is flagged.** Sam, already enrolled, signs in on a phone with a passkey. Each provider's kidney lab (eGFR) was only slightly off, so nobody flagged it. Combined, they show a steady two-year decline. The kidney-trend check flags it and suggests sharing it with the new doctor. Sam turned the check on at enrollment. It appears in Sam's access list as an ongoing grant ("you turned this on Oct 2 · Revoke").
3. **Verified sharing at the visit.**
   - Sam taps Share, picks a duration, and shows a QR code.
   - The doctor signs in with the clinic's Entra ID and, without noticing, receives a certificate good for one shift.
   - The doctor scans the QR code. Sam's phone asks "Give Dr. Rivera at Riverside Primary Care access until 5:00 PM?" and Sam approves.
   - The doctor sees the combined history and the trend, then adds a signed entry (for example, "repeat kidney labs; refer to nephrology").
4. **The Particle moment.** A "data broker" requests Sam's record, claiming the purpose is "treatment," but cannot present a certificate from a trusted issuer. The server refuses. Sam's log shows: "Blocked: unverified requester claimed treatment."
5. **Visibility and expiry.** Sam's phone showed each read live. When the grant expires, or Sam revokes it, the doctor's next request fails.

**Stretch goals, in priority order:**
1. Break-glass emergency access (ER scenario).
2. Recovery after a lost phone using a verifiable credential (Entra Verified ID).
3. A verifiable credential for a clinician's license.

(Real EZCA issuance moved from stretch to core once Keytos approved using the free trial.)

## 8. Approaches considered

**How real the security is (main decision):**

| Approach | What it is | Trade-off |
|---|---|---|
| **A. The EZSSH pattern (picked)** | Clinicians sign in with their hospital's Entra ID and receive a short-lived certificate bound to a key in their browser. Patients use passkeys. Access requires a valid certificate plus a live patient grant. | Strongest identity story, and directly mirrors EZSSH. Most setup work (Azure tenant, small CA), and the crypto has to be right. |
| B. Standards-first | Build on SMART Health Links and SMART on FHIR, the standards the CMS "Kill the Clipboard" effort points to. | Credible to healthcare people, with less crypto to write. But SMART Health Links let anyone holding the link read the data, which is the weakness we pitch against, so the Keytos part shrinks to an add-on. |
| C. Self-contained | The same as A, but with our own logins and CA instead of Entra ID. | Most reliable to demo, no Azure setup. Gives up "built for Entra ID," the platform Keytos builds on. |
| (Dropped) Fully mocked | Fake logins, checks only on the server. | Would not survive scrutiny from identity engineers. |

**Where the data lives (sub-decision):**

| Option | Trade-off |
|---|---|
| Patient-held keys (end-to-end encrypted, like 1Password) | Strongest privacy claim. Rejected: if the patient is unconscious or their phone is dead, nobody can open the record; lost keys mean lost history; hospitals need to write; and cross-record early detection is blocked. Does not fit a system meant to replace hospital databases. |
| **One central record nobody can open by default (picked)** | Mirrors how Keytos runs production. We give up "our servers can't read your data" and claim instead "nobody reads your data without your permission and a verified identity, and you see every read." Simpler to build in 24 hours. |

## 9. Design: what is decided

### Actors

- **Patient (Sam):** uses the phone web app.
- **Clinician:** belongs to an organization that has an Entra ID tenant. The demo clinic is "Riverside Primary Care."
- **Source providers:** three fake organizations. Their seeded entries are signed with certificates EZCA issued to them.
- **Unverified requester:** a "data broker" client (script or simple page) for the Particle moment.
- **Kidney-trend check:** a server-side job that holds a standing, patient-visible, revocable grant.

### Patient enrollment and consent (decided after review; open to correction)

- **Enrollment is by provider invite**, similar to a patient-portal activation code. A provider that has Sam as a patient issues a single-use invite. Redeeming it creates Sam's account (passkey registration) or adds to an existing one. It also links that provider's FHIR Patient ID to Sam's record, so that provider's records flow into the right record. Each of the three source providers links this way, which happens before the demo.
- **Nothing reads the record by default.** The kidney-trend check is **opt-in**: Sam turns it on during enrollment, and it shows in the access list as a grant with the date it was granted. This keeps "nobody can read it by default" literally true.
- **The demo starts with Sam already enrolled.** Passkey sign-up is built, but it is not shown on camera unless there is time.
- **Identity proofing** (a government ID check and selfie through Entra Verified ID, the way EZCMS onboards Keytos employees) is not in the core build. It pairs with stretch goal 3, recovery after a lost phone.

### Components

1. **Patient web app (phone).**
   - Passkey sign-up and sign-in (WebAuthn).
   - Timeline of records with "signed by" badges.
   - Kidney trend chart and flag.
   - Share flow: pick a duration, show the QR code, approve the named clinician.
   - Live access log, including denied attempts.
   - Active grants with a Revoke button.
   - Export as a FHIR bundle.
2. **Clinician web app (laptop).**
   - Entra ID sign-in (OIDC).
   - On sign-in, the browser generates a **non-extractable** key pair (WebCrypto) and sends its public key with the Entra token to the API, which returns a certificate valid for one shift.
   - Scans or enters the patient's QR code.
   - Every API request is signed with the private key (proof of possession, with a nonce or timestamp against replay).
   - Views the combined record and writes a signed entry.
3. **API server.**
   - **Patient auth:** verifies WebAuthn.
   - **Certificate issuance:** validates the Entra ID token. The issuer must be a configured trusted hospital tenant, and the user must hold a Clinician app role.
     - It then requests an X.509 certificate from **EZCA**, through the CA interface, for the clinic's registered subject name (for example `riverside.clinicians.ezehr`). EZCA's minimum lifetime is one day.
     - The server records the certificate's serial number with the clinician's identity (name, organization, Entra IDs) and a **10-hour policy window**, then enforces that window on every request. So the clinician's access lasts **one shift**, even though the certificate itself is valid for a day.
     - This record also means the design doesn't depend on which identity fields EZCA puts in the certificate.
   - **CA interface:** takes a signed certificate signing request (CSR) built in the browser from the WebCrypto key pair, plus the subject name, validity in days, and the Client Authentication usage.
     - Implementations: `EzcaCA` (the demo; called with App Service's managed identity, so there are no secrets) and `LocalCA` (automated tests and the fallback).
     - Provider and service certificates are issued once, through the same interface, by a small command-line tool.
     - The pitch can say: **"every certificate in this demo was issued by EZCA."**
   - **Grants:** a grant records the patient, the grantee (a specific clinician bound at approval time, or a service), the scope, the purpose, the start and expiry, and whether it was revoked.
   - **Access check on every read:**
     1. Certificate chains to our trusted root and is not expired.
     2. The request signature verifies against the certificate's key and is fresh.
     3. An active grant matches the certificate's subject.

     Only then is the record decrypted and returned. Anything else is denied and logged.
   - **QR code contents:** an opaque, single-use grant offer, never data or keys. A verified clinician redeems it, and **the patient confirms the named clinician** on their phone before access starts. A photographed QR code is useless to anyone without a valid certificate, and the patient would see the name and decline.
   - **Audit log:** append-only. Records every attempt, granted or denied, with who, what, when, and the claimed purpose. Pushed live to the patient app.
   - **Kidney-trend check:** reads eGFR lab results under its grant and flags a sustained decline across sources.
4. **Storage.**
   - SQLite.
   - Records are FHIR R4 JSON resources (Observation, DiagnosticReport, Condition, MedicationStatement, Encounter).
   - Each patient's records are encrypted with a per-patient data key (AES-256-GCM). That key is wrapped by a key-encryption key in **Azure Key Vault**, with a local fallback for development.
   - Each entry carries a signature from its author's certificate.

### Stack and hosting (calls made in the session; open to correction)

- TypeScript throughout. Phone-friendly web app; no native app.
- SQLite. Azure App Service on Azure for Students credit, plus Azure Key Vault and one Entra ID tenant. That is enough cloud to honestly say "cloud-native" without spending a day on infrastructure.
- **EZCA free trial** (Basic plan): one root CA and one issuing CA that allows Client Authentication.

## 10. Honest limits (prepare for Q&A)

- **The server can decrypt** behind the access check. An insider who changes the code could bypass it. In a real system, this is mitigated by reviewed infrastructure-as-code, audit, and HSM key-release policies. Not eliminated.
- **Expiry stops future reads**, not what a clinician already saw or copied. That is why every read is logged and shown to the patient.
- **The trust root is each hospital's Entra tenant.** The certificate means "this tenant says this person is a clinician here."
- **Source data is only as trustworthy as the provider's signature.**
- **Clinician certificates are valid for a day**, EZCA's minimum. The one-shift limit is our server's policy, enforced on every request; a different relying party would see a one-day certificate.
- **A patient passkey proves "the same person who enrolled,"** and enrollment is anchored by a provider's invite. Stronger identity proofing (Verified ID) is the stretch goal.

## 11. Left to the builder

- Frameworks and libraries (web framework, WebAuthn library, X.509/PKI library), database schema, and API shape.
- Grant duration choices (suggested 1 hour, 4 hours, end of day). The shift length is decided: 10 hours, enforced by the server.
- The exact trend rule. It must be clinically plausible; KDIGO's definition of significant decline (a drop of 25% or more from baseline together with a change of GFR category) is a reasonable reference. Seed data should show values that each look only mildly off.
- The notification mechanism (server-sent events, WebSocket, or polling).
- Visual design. Mockups can come later.
- Seed data content, fake provider names, and the data broker client.
- Whether the audit log is hash-chained (nice to have).
- How the two team members split the work.
