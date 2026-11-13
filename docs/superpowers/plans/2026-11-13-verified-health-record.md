# Verified Health Record Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Hackathon rule:** this plan was written the night before the event. It contains decisions, interfaces, and test cases, but **no implementation code**: most hackathons ban code written before the clock starts. Tests are given as named cases with exact inputs and expected results, to be written as Vitest tests during the event.

**Goal:** a 24-hour build of a central, patient-controlled health record. Nobody can read it by default; verified clinicians read it with the patient's live permission; every access attempt is shown to the patient; and a cross-provider kidney-trend check produces the early-detection moment.

**Architecture:** one TypeScript repo. An Express API serves a Vite/React web app with three areas: `/patient` (phone), `/clinician` (laptop) and `/broker` (the Particle moment).
- **Clinicians** sign in with Entra ID. They trade the token for an **EZCA-issued** X.509 certificate bound to a non-extractable WebCrypto key. The server limits that certificate to a 10-hour shift. Clinicians then sign every request with a DPoP-style proof carrying the certificate.
- **Source providers and the kidney-trend service** also hold EZCA certificates, so every certificate in the demo comes from EZCA.
- **Patients** use passkeys.
- **Records** are FHIR R4 resources, stored as signed JWS and encrypted per patient (AES-256-GCM). The per-patient key is wrapped by an Azure Key Vault key, and every read passes one access check that logs every attempt.

**Tech Stack:**
- **Runtime and web:** Node 24 LTS (22 LTS is acceptable), TypeScript, Express 5, Vite + React, Vitest + supertest. SQLite via built-in `node:sqlite` (`DatabaseSync`), which avoids native module builds on App Service.
- **Libraries:** `@simplewebauthn/server` + `@simplewebauthn/browser`, `@azure/msal-browser`, `jose`, `@peculiar/x509`, `canonicalize` (RFC 8785 JSON canonicalization), `@azure/keyvault-keys` + `@azure/identity`, `qrcode`, `@zxing/browser`, `recharts`, `tsx`.
- **Azure:** App Service (Linux, B1), Key Vault (RBAC mode), one Entra ID tenant.
- **Keytos:** EZCA free trial (Basic plan, a root CA and an issuing CA), approved by Keytos, the host. The API is called with App Service's managed identity.

**Spec:** `docs/superpowers/specs/2026-11-13-hackathon-design.md`

## Global Constraints

- No password anywhere on screen, including admin and seed accounts shown in the demo.
- Every security check happens on the server and is real. No hard-coded "verified" flags; standard algorithms and libraries only (WebCrypto, WebAuthn, X.509, AES-GCM, ECDSA P-256 / ES256; RSA 2048 / RS256 only if the EZCA probe requires it).
- Copy never says "our servers can't read your data," never says that expiry erases what a clinician saw, never claims HIPAA compliance, and the kidney flag never diagnoses.
- Records are FHIR R4 JSON; export is a FHIR Bundle.
- Nothing reads a patient's record by default. The kidney-trend check is opt-in at enrollment.
- Every access attempt, granted or denied, is written to the audit log and shown to the patient.
- The QR code contains only an opaque, single-use share-offer code: no data, no keys.
- The patient confirms the named clinician before a grant becomes active.
- **Every certificate in the demo is issued by EZCA** (`CA_MODE=ezca`). `LocalCA` is used only in automated tests and as the fallback.
- Clinician certificates are issued for **1 day**, EZCA's minimum. The server enforces a **10-hour policy window** per certificate (`issued_certs.policy_not_after`), so access lasts one shift. Certificate verification tolerates **2 minutes** of clock skew on `notBefore`.
- Grant durations offered to the patient: **5 minutes (demo), 1 hour, 4 hours, end of day**.
- Cast: patient **Sam Taylor**; clinician **Dr. Rivera** at **Riverside Primary Care**; source providers **Lakeside Urgent Care**, **Northwind Workplace Health**, **Maple Street Family Medicine**.
- Exact copy, defined once in `src/shared/copy.ts`:
  - signed badge: `Signed by ${orgName} ✓`
  - approval prompt: `Give ${displayName} at ${orgName} access until ${time}?`
  - broker denial log line: `Blocked: unverified requester claimed ${purpose}`
  - opt-in grant line: `You turned this on ${date} · Revoke`
  - pitch line: `Epic says it blocks data to protect patients. We make blocking unnecessary.`
- Video is about 5 minutes. **Feature freeze at hour 17.**
- **Checkpoint at hour 7**, with two independent fallbacks:
  - If the deployed clinician page can't get a Clinician-role Entra token that the server accepts, switch to Task 4b (the approach C fallback) immediately.
  - If EZCA issuance isn't working, set `CA_MODE=local` and continue (Task 3b).

## Review Focus

1. **Clock skew between devices and the server.** A request made right after a certificate is issued, from a laptop whose clock is up to 2 minutes off, must succeed. *Tests:* Task 3 (2-minute tolerance on `notBefore`) and Task 5 (`iat` window).
2. **The same QR code redeemed twice** (a photo of the screen, or a double scan). The second redemption is rejected, and the patient sees exactly one pending request. *Test:* Task 7.
3. **The grant expires or is revoked while the clinician has the record open.** The next request is denied with `grant_expired` or `grant_revoked`, and the clinician sees the "access ended" screen, not a blank page. *Tests:* Task 7 (server) and Task 13 (acceptance).
4. **The provider simulator runs twice** (a demo reset). No duplicate entries or duplicate kidney flags. *Tests:* Tasks 9 and 10.
5. **A valid Entra user without the Clinician role, or from an untrusted tenant.** No certificate is issued, and the page shows a clear message. *Test:* Task 4.

---

## Team, timeline, and ownership

The two people work in parallel tracks:
- **Identity lead:** the teammate who built the EZCA CLI.
- **App lead:** the other teammate.

Hours are counted from the start of the event. If you sleep, stagger it after hour 17 so one person is always awake for the video.

| Hours | Identity lead | App lead |
|---|---|---|
| 0–1.5 | Task 2a (Entra and EZCA setup) | Task 1 (scaffold and contracts) |
| 1.5–3 | Task 2a | Task 2b (hosting and Key Vault), then Task 6 (encryption and record store) |
| 3–4.5 | Task 3 (PKI core) | Task 6, then Task 8 (patient passkeys and enrollment) |
| 4.5–5.5 | Task 3b (EZCA adapter, organization certificates) | Task 8 |
| 5.5–7 | Task 4 (Entra and certificate endpoint), then the **hour-7 checkpoint** | Task 8, then Task 9 (provider API, simulator, seed data) |
| 7–9 | Task 5 (signed requests) | Task 9, then Task 10 (kidney trend check) |
| 9–11.5 | Task 7 (grants, access check, audit) | Task 10, then Task 12 (patient UI) |
| 11.5–15 | Task 13 (clinician UI) | Task 12 |
| 15–17 | Task 14 (broker), then Task 11 (share flow, together) | Task 15 (export), then Task 11 |
| 17–24 | Task 16 (freeze, deploy, rehearse, record, submit) | Task 16 |

Stretch tasks S1–S4 are taken only if the team is ahead at hour 15.

## File structure

```
package.json  tsconfig.json  vite.config.ts  vitest.config.ts  .env.example
src/shared/types.ts        contracts shared by server and web (all types named in this plan)
src/shared/copy.ts         every user-facing string listed in Global Constraints and tasks
src/shared/entryJws.ts     signEntry (used by browser, simulator CLI, server)
src/shared/proof.ts        createProof (used by browser signedFetch and simulator CLI)
src/server/index.ts        Express wiring, static hosting of the built web app
src/server/config.ts       environment config (see Task 1)
src/server/db.ts           node:sqlite connection and schema migration
src/server/pki/ca.ts       CertificateAuthority interface and LocalCA
src/server/pki/ezca.ts     EzcaCA adapter (Task 3b)
src/server/pki/verify.ts   certificate chain and validity verification
src/server/pki/entryJws.ts verifyEntry (record entries as JWS)
src/server/auth/entra.ts   Entra access-token verification
src/server/auth/localClinician.ts  approach C fallback (Task 4b only)
src/server/auth/proof.ts   certificate-bound proof verification, replay cache
src/server/auth/patient.ts patient WebAuthn ceremonies, invites, sessions
src/server/crypto/keyWrapper.ts   KeyWrapper, KeyVaultWrapper, LocalWrapper
src/server/crypto/recordCipher.ts per-patient AES-256-GCM
src/server/records/store.ts      encrypted entry storage
src/server/access/grants.ts      share offers and grants
src/server/access/gate.ts        the one access check
src/server/audit/log.ts          audit events
src/server/checks/kidneyTrend.ts trend rule (pure) and runner
src/server/routes/{patient,clinician,provider}.ts
src/web/main.tsx  src/web/App.tsx (routes /patient/*, /clinician/*, /broker)
src/web/patient/*      Enroll, SignIn, Home, Share, Activity pages
src/web/clinician/*    SignIn, Redeem, Record pages
src/web/broker/BrokerPage.tsx
src/web/lib/clinicianKey.ts  key generation, CSR, IndexedDB persistence
src/web/lib/signedFetch.ts   attaches proofs to clinician requests
tools/issue-org-certs.ts  issues the provider and service certificates (EZCA or local)
docs/ezca-probe.md      what the EZCA probe (Task 2a) showed
tools/provider-sim.ts   CLI: create invites, push signed seed records
tools/seed/*.json       FHIR seed data per provider
tests/server/**         mirrors src/server
```

## Shared contracts (`src/shared/types.ts`, written in Task 1)

Every task uses these names exactly.

- `type OrgKind = 'clinician' | 'provider' | 'service'`
- `type FhirResource = { resourceType: string; id: string; [k: string]: unknown }`
- `type CertIdentity = { kind: OrgKind; subjectUri: string; displayName: string; orgName: string; serial: string; notBefore: Date; notAfter: Date }`. `subjectUri` comes from the server's issuance record (clinicians) or the `knownOrgs` config (providers and the service), never from the CSR. See `resolveIdentity` in Task 3:
  - `urn:entra:<tenantId>:<objectId>` for Entra clinicians
  - `urn:local:<clinicianId>` for the approach C fallback
  - `urn:provider:<providerId>` for source providers
  - `urn:service:kidney-trend` for the trend check
- `type ClinicianIdentity = { subjectUri: string; displayName: string; orgName: string }`
- `type ClinicianCredential = { keyPair: CryptoKeyPair; certPem: string; chainPem: string[]; notAfter: Date }`
- `type GranteeRef = { kind: 'clinician'; subjectUri: string; displayName: string; orgName: string } | { kind: 'service'; subjectUri: 'urn:service:kidney-trend'; displayName: 'Kidney trend check' }`. Grants always match on `subjectUri`.
- `type GrantStatus = 'pending' | 'active' | 'denied' | 'revoked' | 'expired'`
- `type Grant = { id: string; patientId: string; grantee: GranteeRef; scope: 'read_append' | 'read_labs'; purpose: 'treatment' | 'health_check'; status: GrantStatus; durationMinutes: number | null; createdAt: Date; approvedAt: Date | null; expiresAt: Date | null; revokedAt: Date | null }`. A `null` `expiresAt` means ongoing; only service grants are ongoing.
- `type ShareOffer = { id: string; patientId: string; code: string; durationMinutes: 5 | 60 | 240 | 'end_of_day'; offerExpiresAt: Date; redeemedGrantId: string | null }`. `code` is 8 characters from an unambiguous alphabet, displayed as `XXXX-XXXX`. The offer itself expires 10 minutes after creation.
- `type DenyReason = 'no_certificate' | 'untrusted_certificate' | 'certificate_expired' | 'bad_proof' | 'stale_proof' | 'replayed_proof' | 'no_grant' | 'grant_pending' | 'grant_expired' | 'grant_revoked' | 'scope_denied'`
- `type AuditActor = { kind: 'clinician' | 'provider' | 'service'; displayName: string; orgName: string; serial?: string } | { kind: 'unverified' }`
- `type AuditEvent = { id: number; patientId: string; at: Date; actor: AuditActor; action: 'read_record' | 'append_entry' | 'redeem_offer' | 'kidney_check' | 'ingest'; claimedPurpose: string | null; outcome: 'granted' | 'denied'; reason: DenyReason | null }`
- `type StoredEntry = { id: string; patientId: string; sourceKey: string; createdAt: Date }`, the plaintext columns only. `sourceKey` = `<signer subjectUri>|<resourceType>/<resource.id>` and is unique per patient, which makes ingestion idempotent.
- `type VerifiedEntry = { id: string; resource: FhirResource; signer: { displayName: string; orgName: string; serial: string }; signedAt: Date }`
- `type TrendResult = { flagged: boolean; reason: 'category_drop_25pct' | 'rapid_decline' | null; baseline: EgfrPoint | null; latest: EgfrPoint | null; percentDrop: number; slopePerYear: number; sourceCount: number }`
- `type EgfrPoint = { date: Date; value: number; source: string }`

---

### Task 1: Scaffold, contracts, schema (App lead, hours 0–1.5)

**Files:**
- Create: `package.json`, `tsconfig.json`, `vite.config.ts`, `vitest.config.ts`, `.env.example`, `src/shared/types.ts`, `src/shared/copy.ts`, `src/server/config.ts`, `src/server/db.ts`, `src/server/index.ts`, `src/web/main.tsx`, `src/web/App.tsx`
- Test: `tests/server/db.test.ts`, `tests/shared/copy.test.ts`

**Interfaces:**
- Produces:
  - `loadConfig(): Config`. Fields:
    - `origin`, `rpId` (the hostname of `origin`)
    - `trustedTenants: Record<tenantId, { orgName: string; certSubject: string }>`, `entraClientId`
    - `clinicianAuth: 'entra' | 'local'`
    - `caMode: 'local' | 'ezca'`
    - `keyWrapper: 'keyvault' | 'local'`, `keyVaultKeyId`, `localKekB64`
    - `ezca: { baseUrl, caId, templateId }` (`baseUrl` defaults to `https://portal.ezca.io`), `trustAnchorsPem: string[]`
    - `localCaCertPem`, `localCaKeyPem` (tests and fallback only), `serviceCertPem`, `serviceKeyPem`
    - `knownOrgs: Record<cn, KnownOrg>` (values in Task 3), `clinicianKeyAlg: 'ES256' | 'RS256'` (default `ES256`)
    - `timeZone` (default `America/Indiana/Indianapolis`)
    - `dbPath`
  - `openDb(path: string): DatabaseSync`. Runs migrations.
  - The shared types above.
  - `copy`, the object of exact strings.
- Tables:
  - `patients(id, display_name, wrapped_dek, created_at)`
  - `patient_credentials(id, patient_id, public_key, counter, transports)`
  - `sessions(token_hash, patient_id, expires_at)`
  - `invites(code, provider_uri, provider_patient_id, expires_at, redeemed_at)`
  - `provider_links(patient_id, provider_uri, provider_patient_id)`, unique on (`provider_uri`, `provider_patient_id`)
  - `entries(id, patient_id, source_key, iv, ciphertext, created_at)`, unique on (`patient_id`, `source_key`)
  - `share_offers(...)` and `grants(...)`, matching the types
  - `audit_events(...)`, matching the type
  - `clinicians(...)`, used by Task 4b only
  - `issued_certs(serial PRIMARY KEY, subject_uri, display_name, org_name, kind, issued_at, policy_not_after)`

- [ ] **Step 1: Write the failing tests.**
  - `db.test.ts › migrates an empty file`: after `openDb(':memory:')`, every table above exists.
  - `db.test.ts › entries are unique per patient and source_key`: inserting the same (`patient_id`, `source_key`) twice throws a constraint error.
  - `copy.test.ts › exact spec strings`: `copy.signedBy('Lakeside Urgent Care')` returns `"Signed by Lakeside Urgent Care ✓"`. `copy.approvePrompt('Dr. Rivera','Riverside Primary Care','5:00 PM')` returns `"Give Dr. Rivera at Riverside Primary Care access until 5:00 PM?"`. `copy.brokerBlocked('treatment')` returns `"Blocked: unverified requester claimed treatment"`.
- [ ] **Step 2:** Run `npx vitest run tests/server/db.test.ts tests/shared/copy.test.ts`. Expected: FAIL (modules missing).
- [ ] **Step 3:** Implement `openDb`, `loadConfig`, `copy` and the types. The Express app serves `dist/web` and, in development, `/` through the Vite dev server proxy. Scripts:
  - `dev` runs server and Vite together
  - `build` runs `vite build`
  - `start` runs `tsx src/server/index.ts`
  - `test` runs `vitest run`
- [ ] **Step 4:** Run the same tests. Expected: PASS. Also run `npm run build && npm start` and confirm `http://localhost:3000/patient` renders a placeholder page.
- [ ] **Step 5:** `git init`; commit `feat: scaffold, shared contracts, schema`. Push to GitHub so both people share it.

### Task 2a: Identity and EZCA setup (Identity lead, hours 0–3; portal work, no app code)

**Produces:**
- Values for `.env` and App Service settings: `ORIGIN`, `ENTRA_CLIENT_ID`, `TRUSTED_TENANTS`, `EZCA_CA_ID`, `EZCA_TEMPLATE_ID`.
- `.pki/ezca-root.pem` and `.pki/ezca-issuing.pem`.
- A demo doctor who signs in with a passkey only.
- `docs/ezca-probe.md`, recording what EZCA accepts.

- [ ] **Step 1: Azure account.** Sign up for Azure for Students with a **personal Microsoft account**, verifying student status with the Purdue email. Do not use the Purdue Entra account: its subscription would live in Purdue's tenant, where you can't create users or assign roles. If Azure for Students refuses the personal account, use the standard Azure free account instead.
  - Then invite the App lead (Entra admin center → Users → Invite external user) and give them Contributor on the subscription, so Task 2b can run in parallel.
- [ ] **Step 2: Tenant and demo user.**
  - In the tenant's Default Directory, create the user **Dr. Rivera**, with UPN `rivera@<tenant>.onmicrosoft.com` and display name `Dr. Rivera`.
  - Under Authentication methods → Policies, enable **Passkey (FIDO2)** and **Temporary Access Pass**.
  - Issue a TAP for Dr. Rivera. Sign in at `https://mysignins.microsoft.com/security-info` with the TAP and register a passkey: Microsoft Authenticator on a phone, or a YubiKey if you have one.
  - If TAP isn't available on the Free tier, sign in once with the initial password, **off camera**, only to register the passkey.
  - Verify: in a fresh private window, sign in as Dr. Rivera choosing "Sign-in options → Face, fingerprint, PIN or security key." There should be no password prompt.
  - **If passkey sign-in fails on the Free tier** (reported online as AADSTS135016), activate the **Entra ID P2 30-day free trial** (Entra admin center → Billing → Licenses → All products → Try/Buy), assign it to Dr. Rivera, and retry.
  - Timebox: if it still fails at **hour 4.5**, tell the App lead you're going to Task 4b at the hour-7 checkpoint.
- [ ] **Step 3: App registration** "Verified Health Record":
  - SPA redirect URIs: `http://localhost:5173/clinician` and `https://<app>.azurewebsites.net/clinician`.
  - Expose an API with Application ID URI `api://<clientId>` and scope `Records.Access`.
  - Add an app role `Clinician` (allowed member type: Users).
  - In the manifest, set access token version 2 (`requestedAccessTokenVersion: 2`).
  - In Enterprise applications → Users and groups, assign Dr. Rivera to `Clinician`. Assign the user directly; group assignment needs P1.
  - Record the tenant ID and client ID. Set `TRUSTED_TENANTS={"<tenantId>":{"orgName":"Riverside Primary Care","certSubject":"riverside.clinicians.ezehr"}}`.
- [ ] **Step 4: EZCA free trial** (approved by Keytos, the host).
  - As the tenant's Global Administrator (the Step 1 account), open the "Register Keytos Applications" admin-consent link from EZCA's docs (Getting Started → Register the EZCA App in your Azure Tenant) and accept.
  - At `https://portal.ezca.io`, register a subscription named `EZEHR` on the Basic plan, and choose **Skip for now** on payment. That starts the 1-month free trial.
  - Create a Root CA, then a Subordinate/Issuing SSL CA chained to it. On the issuing CA:
    - In the EKU advanced settings, include **Client Authentication**. EKUs can't be changed after the CA is created.
    - Set the maximum certificate lifetime to at least 400 days, because provider certificates last 365.
    - Leave CRL on and the cryptography at its defaults.
  - Register these subject names ("domains") on the issuing CA:
    - `riverside.clinicians.ezehr`
    - `lakeside.providers.ezehr`
    - `northwind.providers.ezehr`
    - `maple.providers.ezehr`
    - `kidney-trend.services.ezehr`
  - Download the root and issuing CA certificates as PEM to `.pki/ezca-root.pem` and `.pki/ezca-issuing.pem`.
  - Get the IDs: run `az login`, then `curl -s -H "Authorization: Bearer $(az account get-access-token --query accessToken -o tsv)" https://portal.ezca.io/api/CA/GetAvailableSSLCAs`. Copy `CAID` and `TemplateID`.
  - When Task 2b reports the App Service managed identity, give it a **domain certificate requester** role on `riverside.clinicians.ezehr`, in EZCA's domain management. Managed identities can't be PKI admins, and don't need to be.
- [ ] **Step 5: EZCA probe** (20 minutes, from a terminal with your az token). Send `POST https://portal.ezca.io/api/CA/RequestSSLCertificateV2` with:
  - `CSR`: an ECDSA P-256 CSR made with `openssl`
  - `SubjectName`: `riverside.clinicians.ezehr`
  - `CAID` and `TemplateID` from Step 4
  - `ValidityInDays`: `1`
  - `EKUs`: `["1.3.6.1.5.5.7.3.2"]`
  - `SubjectAltNames`: `[{"ValueSTR":"urn:entra:probe:probe","SubjectAltType":6}]`. Type 6 is the X.509 URI type; if it's rejected, check the Swagger page at `https://portal.ezca.io/swagger/index.html`.

  Record in `docs/ezca-probe.md`:
  1. whether EC P-256 was accepted
  2. the exact response JSON, and which fields hold the leaf and the chain (EZCA's docs show `CertificatePEM`, `IssuingCACertificate`, `RootCertificate` for this endpoint)
  3. whether the URI SAN appears in the issued certificate (`openssl x509 -noout -text`)
  4. the subject format EZCA produced

  **Decision:** if EC is rejected, set `CLINICIAN_KEY_ALG=RS256` (RSA 2048); Tasks 3–5 support both. Nothing depends on item 3, because clinician identity comes from the server's issuance record.
- [ ] **Step 6: Verify.**
  - Dr. Rivera's passwordless sign-in works.
  - `openssl verify -CAfile .pki/ezca-root.pem -untrusted .pki/ezca-issuing.pem probe.crt` prints `OK`.

### Task 2b: Hosting and Key Vault (App lead, hours 1.5–2.5, after Task 1)

**Produces:** `KEY_VAULT_KEY_ID`, the deployed site's URL, and the App Service managed identity's object ID. Send the object ID to the Identity lead for Task 2a Step 4.

- [ ] **Step 1: Key Vault** (RBAC permission model). Create an RSA 3072 key `records-kek` with wrapKey and unwrapKey permitted. Give yourself "Key Vault Crypto Officer" for setup.
- [ ] **Step 2: App Service.** Linux, B1. B1 rather than F1, because the free tier's daily CPU quota will run out during development.
  - Runtime: Node 24 LTS, or 22 LTS if 24 isn't offered.
  - Turn on the system-assigned managed identity and grant it **Key Vault Crypto User** on the vault. No client secrets anywhere.
  - Set the startup command to `npm start`, plus app settings for the `.env` values.
  - Set `DB_PATH=/home/data/app.db`, since `/home` persists across restarts.
  - Deploy Task 1's placeholder with `az webapp up` or zip deploy.
- [ ] **Step 3: Verify.**
  - `https://<app>.azurewebsites.net/patient` loads over HTTPS. WebAuthn needs HTTPS, and `rpId` is this hostname.
  - `az keyvault key show --id <KEY_VAULT_KEY_ID>` succeeds.

### Task 3: PKI core (Identity lead, hours 3–4.5)

**Files:** Create `src/server/pki/ca.ts`, `src/server/pki/verify.ts`, `src/server/pki/entryJws.ts`, `src/shared/entryJws.ts`, `tools/issue-org-certs.ts`. Test: `tests/server/pki/*.test.ts`.

**Interfaces:**
- Produces:
  - `interface CertificateAuthority { issue(req: { csrPem: string; subjectName: string; validityDays: number }): Promise<{ certPem: string; chainPem: string[] }>; trustAnchorsPem(): string[] }`.
    - Every certificate carries the Client Authentication EKU.
    - `subjectName` is a registered subject name such as `riverside.clinicians.ezehr`. **The CSR's own subject is ignored**; the CSR only proves possession of the key.
  - `class LocalCA implements CertificateAuthority`. Mirrors EZCA so there is one code path:
    - CN = `subjectName`
    - validity in whole days
    - `notBefore` = now − 5 min
    - ECDSA P-256 root

    Used by tests and as the fallback (`CA_MODE=local`).
  - `type ChainResult = { cn: string; serial: string; notBefore: Date; notAfter: Date; publicKey: CryptoKey }` and `verifyCertificateChain(x5c: string[] /* base64 DER, leaf first */, at: Date, anchors: string[]): ChainResult`.
    - Accepts `at` up to 2 minutes before `notBefore`, for clock skew.
    - Throws `PkiError` with `reason: 'untrusted_certificate' | 'certificate_expired'`.
  - `type IssuedCert = { serial: string; subjectUri: string; displayName: string; orgName: string; kind: 'clinician'; issuedAt: Date; policyNotAfter: Date }` (a row of `issued_certs`) and `type KnownOrg = { kind: 'provider' | 'service'; subjectUri: string; displayName: string; orgName: string }`.
  - `resolveIdentity(chain: ChainResult, at: Date, deps: { findIssuedCert(serial: string): IssuedCert | null; knownOrgs: Record<string /* cn */, KnownOrg> }): CertIdentity`:
    1. If the serial is in `issued_certs`, return that clinician identity, but throw `certificate_expired` if `at` is after `policyNotAfter` (the 10-hour shift).
    2. Otherwise, if the CN is in `knownOrgs`, return that organization's identity.
    3. Otherwise, throw `untrusted_certificate`.
  - `signEntry(resource: FhirResource, key: CryptoKey, x5c: string[], iat: Date): Promise<string>`, in `src/shared/entryJws.ts`.
    - Produces a compact JWS with protected header `{ alg, x5c, iat }` and payload `canonicalize(resource)`.
    - `alg` is ES256, or RS256 when `CLINICIAN_KEY_ALG=RS256`.
  - `verifyEntry(jws: string, deps: { anchors: string[]; findIssuedCert; knownOrgs }): VerifiedEntry`. Checks the signature, then the chain and identity **at the header `iat`**, so old entries stay valid after their author's certificate or shift ends.
- `knownOrgs`, in config:

  | CN | kind | subjectUri | Name |
  |---|---|---|---|
  | `lakeside.providers.ezehr` | provider | `urn:provider:lakeside` | Lakeside Urgent Care |
  | `northwind.providers.ezehr` | provider | `urn:provider:northwind` | Northwind Workplace Health |
  | `maple.providers.ezehr` | provider | `urn:provider:maple` | Maple Street Family Medicine |
  | `kidney-trend.services.ezehr` | service | `urn:service:kidney-trend` | Kidney trend check |

- `tools/issue-org-certs.ts --ca ezca|local`:
  - For each known organization, it generates a P-256 key, builds a CSR, and requests a 365-day certificate through the chosen CA.
  - `ezca` uses your `az account get-access-token` token. `local` first mints `.pki/local-root.{crt,key}.pem`.
  - It writes `.pki/<slug>.{crt,key}.pem` and `.pki/anchors.pem`.
  - In deployment, the service key is loaded from an App Service setting that references a Key Vault secret.

- [ ] **Step 1: Write the failing tests.**
  - `LocalCA sets CN to subjectName, ignoring the CSR`: a CSR with subject `CN=Mallory` and `subjectName:'riverside.clinicians.ezehr'` yields a certificate with CN `riverside.clinicians.ezehr`, whose extended key usage includes clientAuth.
  - `rejects CSR with invalid self-signature`: flipped signature bytes throw.
  - `LocalCA validity`: `validityDays:1` gives `notAfter − notBefore` = 24h 5m.
  - `chain verifies against anchor`: leaf plus local root returns a `ChainResult` with the matching serial and CN.
  - `untrusted root` throws `untrusted_certificate`; `expired` (`at` = `notAfter` + 1s) throws `certificate_expired`.
  - **(Review Focus 1)** `skew tolerance`: `at` = `notBefore` − 2 min verifies; `notBefore` − 3 min throws `certificate_expired`.
  - `resolveIdentity clinician`: an issued row for the serial returns `{kind:'clinician', subjectUri:'urn:entra:T:O', displayName:'Dr. Rivera', orgName:'Riverside Primary Care'}`; `at` = `policyNotAfter` + 1s throws `certificate_expired`.
  - `resolveIdentity provider`: CN `lakeside.providers.ezehr` returns `kind:'provider'`, `displayName:'Lakeside Urgent Care'`.
  - `unknown subject`: CN `mallory.clinicians.ezehr` with no issued row throws `untrusted_certificate`.
  - `entry JWS verifies after the shift ends`: sign with `iat` inside the policy window, then verify one day later. It passes, and `signedAt` equals `iat`.
  - `tampered entry`: changing one character of the JWS payload throws.
  - `RS256 path`: an entry signed and verified with an RSA 2048 key passes.
- [ ] **Step 2:** `npx vitest run tests/server/pki`. Expected: FAIL.
- [ ] **Step 3:** Implement with `@peculiar/x509` (`Pkcs10CertificateRequest`, `X509CertificateGenerator`, `X509Certificate.verify`) and `jose` (`CompactSign`, `compactVerify`), then implement `tools/issue-org-certs.ts`.
- [ ] **Step 4:** Run the tests again. Expected: PASS. Then run `npx tsx tools/issue-org-certs.ts --ca local`. Expected: `.pki/` holds the local root pair, four organization certificate and key pairs, and `anchors.pem`. Add `.pki/` to `.gitignore`.
- [ ] **Step 5:** Commit `feat: CA interface, local CA, identity resolution, signed entries`.

### Task 3b: EZCA adapter and organization certificates (Identity lead, hours 4.5–5.5)

**Files:** Create `src/server/pki/ezca.ts`. Test: `tests/server/pki/ezca.test.ts`.

**Interfaces:**
- Consumes: `CertificateAuthority` (Task 3); the probe results in `docs/ezca-probe.md` (Task 2a).
- Produces: `class EzcaCA implements CertificateAuthority`, constructed with `{ baseUrl: string /* https://portal.ezca.io */; caId: string; templateId: string; anchorsPem: string[]; getToken(): Promise<string> }`.
  - Production `getToken`: `DefaultAzureCredential.getToken('https://management.core.windows.net/.default')`. On App Service this is the managed identity; locally it's your `az login`. Tokens for `https://management.azure.com` are **not** accepted.
  - `issue` calls `POST {baseUrl}/api/CA/RequestSSLCertificateV2` with `{ SubjectName, SubjectAltNames: [], CAID, TemplateID, CSR, ValidityInDays, EKUs: ['1.3.6.1.5.5.7.3.2'] }`. It maps the response to `{ certPem, chainPem }` using the field names recorded in the probe.
  - An error response throws `CaError` carrying EZCA's message.

- [ ] **Step 1: Write the failing tests** (with `fetch` stubbed).
  - `sends bearer token and request body`: the stub sees `Authorization: Bearer <token>`, the CA and template IDs, `ValidityInDays:1`, and the client-auth EKU.
  - `maps the probe's response shape`: with the recorded probe response as the fixture, `issue` returns a leaf PEM and a chain of length 1 or more.
  - `EZCA error`: an error response, in the shape the probe showed (for example `{ Success:false, Message:'Domain not registered' }`), throws `CaError('Domain not registered')`.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement. Then run `npx tsx tools/issue-org-certs.ts --ca ezca`, and check each certificate with `openssl verify -CAfile .pki/ezca-root.pem -untrusted .pki/ezca-issuing.pem .pki/lakeside.crt.pem`. Expected: prints `OK`.
- [ ] **Step 4:** Run the tests again. Expected: PASS.
- [ ] **Step 5:** Commit `feat: EZCA certificate authority`.
- [ ] **If EZCA still fails at the hour-7 checkpoint:** set `CA_MODE=local`, re-run `issue-org-certs --ca local`, and keep going. Retry EZCA after hour 15 only if the demo is otherwise done. Without EZCA, the pitch says "built for EZCA; shown with a local CA."

### Task 4: Entra token verification and the certificate endpoint (Identity lead, hours 5.5–7, then the hour-7 checkpoint)

**Files:** Create `src/server/auth/entra.ts`; modify `src/server/routes/clinician.ts`; create `src/web/lib/clinicianKey.ts` and the `src/web/clinician/SignIn.tsx` page. Test: `tests/server/auth/entra.test.ts`, `tests/server/routes/certificate.test.ts`.

**Interfaces:**
- Consumes: `CertificateAuthority` (Tasks 3 and 3b); `loadConfig().trustedTenants` and `entraClientId`.
- Produces:
  - `verifyEntraToken(token: string, deps: { getKey: JWTVerifyGetKey; trustedTenants: Config['trustedTenants']; audience: string }): Promise<ClinicianIdentity & { certSubject: string }>`.
    - Checks: `iss` = `https://login.microsoftonline.com/<tid>/v2.0`, with `tid` in `trustedTenants`; `aud` = `audience`; not expired; `roles` includes `Clinician`.
    - `subjectUri` = `urn:entra:<tid>:<oid>`, `displayName` = the `name` claim, `orgName` and `certSubject` from `trustedTenants[tid]`.
    - Throws `AuthError('untrusted_tenant' | 'missing_role' | 'invalid_token')`.
    - Production `getKey`: `createRemoteJWKSet` on `https://login.microsoftonline.com/<tid>/discovery/v2.0/keys`.
  - `POST /api/clinician/certificate`, with `Authorization: Bearer <access token>` and body `{ csrPem }`.
    - Calls `ca.issue({ csrPem, subjectName: certSubject, validityDays: 1 })`.
    - Writes an `issued_certs` row: the serial, the identity from the token, `kind:'clinician'`, `issuedAt` = now, `policyNotAfter` = now + 10h.
    - Returns `200 { certPem, chainPem, notAfter }`, where `notAfter` is the **policy** end, the end of the shift.
    - A bad token returns `401 { error }`; a missing role returns `403 { error: 'missing_role' }`.
  - Web:
    - `getClinicianCredential(): Promise<ClinicianCredential | null>`. Reads from IndexedDB and returns `null` when the certificate has expired.
    - `obtainCertificate(accessToken: string): Promise<ClinicianCredential>`. Generates a key pair with `extractable: false`: ECDSA P-256, or RSA 2048 if `clinicianKeyAlg` is `RS256`. Builds a CSR, POSTs it, and stores the key pair, certificate and chain in IndexedDB.
- MSAL: scope `api://<clientId>/Records.Access`, `loginRedirect`, `acquireTokenSilent`.
- Copy:
  - `copy.notAClinician`: `"Your organization hasn't approved this account as a clinician."`
  - `copy.untrustedOrg`: `"This organization isn't connected yet."`

- [ ] **Step 1: Write the failing tests.** Use a local `jose` key pair and a fake `getKey`.
  - `valid token`: returns `{subjectUri:'urn:entra:T:O', displayName:'Dr. Rivera', orgName:'Riverside Primary Care'}`.
  - **(Review Focus 5)** `missing Clinician role` throws `missing_role`, and the route returns 403 without calling `ca.issue` (spy).
  - **(Review Focus 5)** `untrusted tenant` throws `untrusted_tenant`, and the route returns 401.
  - `wrong audience` and `expired token` both throw `invalid_token`.
  - `subject comes from tenant config, not the CSR`: a CSR with subject `CN=Mallory` makes the route call `ca.issue` with `subjectName:'riverside.clinicians.ezehr'` (spy on a `LocalCA`).
  - `records the shift`: after issuance, `issued_certs` holds the serial with `subjectUri:'urn:entra:T:O'`, and `policyNotAfter − issuedAt` = 10h. The response's `notAfter` equals `policyNotAfter`.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement `verifyEntraToken`, the route, `clinicianKey.ts`, and the sign-in page. The page shows the user's name and organization and the certificate expiry (`"Signed in until 7:42 PM"`), and never shows a key or certificate dialog.
- [ ] **Step 4:** Run the tests again. Expected: PASS. Deploy, then sign in on the deployed `/clinician` page as Dr. Rivera with the passkey. Expected: a certificate is stored, and the page shows the expiry.
- [ ] **Step 5:** Commit `feat: Entra sign-in to short-lived clinician certificate`.
- [ ] **Step 6: HOUR-7 CHECKPOINT.** If Step 4 doesn't pass on the deployed site, do Task 4b now. Don't keep debugging Entra. Check EZCA separately: if the deployed certificate endpoint can't get an EZCA certificate, set `CA_MODE=local` (see Task 3b).

### Task 4b (only on checkpoint failure): Approach C fallback

**Files:** Create `src/server/auth/localClinician.ts` and a `clinicians` seed row (Dr. Rivera, Riverside Primary Care, `urn:local:rivera`). Test: `tests/server/auth/localClinician.test.ts`.

**Produces:** `authenticateClinician(req): Promise<ClinicianIdentity & { certSubject: string }>`, chosen by `config.clinicianAuth`. The local clinician row carries `certSubject` `riverside.clinicians.ezehr`, so EZCA still issues the certificate. The local mode accepts a clinician session from a WebAuthn sign-in, reusing Task 8's ceremonies with the `clinicians` tables. The certificate endpoint and everything downstream stay unchanged.

- [ ] Test `local session yields identity urn:local:rivera`; test that a missing session returns 401. Run (FAIL), implement, run (PASS), commit `feat: local clinician passkeys (fallback)`. Then update the pitch: "plugs into Entra ID; shown here with our own directory."

### Task 5: Certificate-bound request proofs (Identity lead, hours 7–9)

**Files:** Create `src/server/auth/proof.ts`, `src/shared/proof.ts` and `src/web/lib/signedFetch.ts`. Test: `tests/server/auth/proof.test.ts`.

**Interfaces:**
- Consumes: `verifyCertificateChain` and `resolveIdentity` (Task 3). Identity always comes from `resolveIdentity`, which also enforces the clinician's 10-hour shift.
- Proof format. This is DPoP-style (RFC 9449), but bound to a certificate instead of a bare key. Tell judges exactly that.
  - Header `X-Proof: <compact JWS>`.
  - Protected header: `{ typ: 'cpop+jwt', alg: 'ES256' | 'RS256', x5c: [leafB64, ...intermediates] }`.
  - Payload:
    - `htm`: the request method
    - `htu`: the origin plus path, without the query string
    - `iat`: seconds since epoch
    - `jti`: a random UUID
    - `bdh`: base64url SHA-256 of the raw body, omitted when there is no body
    - `pur`: the claimed purpose, e.g. `'treatment'`
- Produces:
  - `createProof(req: { method: string; url: string; body: Uint8Array | null; purpose: string }, key: CryptoKey, x5c: string[], now?: Date): Promise<string>`, in `src/shared/proof.ts`. Used by `signedFetch` and the provider simulator; tests use it to build proofs.
  - `verifyProof(input: { method: string; url: string; rawBody: Buffer | null; proofHeader: string | undefined; now: Date }, deps: { anchors: string[]; replay: ReplayCache }): { ok: true; identity: CertIdentity; claimedPurpose: string } | { ok: false; reason: DenyReason; claimedPurpose: string | null }`
  - `class ReplayCache { seen(jti: string, now: Date): boolean }`. In memory, with a 10-minute TTL.
  - Express middleware `requireProof`: sets `req.actor` and `req.claimedPurpose`. On failure it does **not** respond itself; it sets `req.proofFailure`, so the gate (Task 7) can log the denial against the patient.
  - Web: `signedFetch(input: string, init?: RequestInit & { purpose?: string }): Promise<Response>`. Uses the stored key pair and certificate (Task 4).
- Freshness window: `iat` within [now − 120s, now + 60s].

- [ ] **Step 1: Write the failing tests.**
  - `valid proof` returns `ok` with `identity.subjectUri` and `claimedPurpose:'treatment'`.
  - A missing header returns `no_certificate`; a missing header with request header `X-Purpose: treatment` still reports `claimedPurpose:'treatment'` (the broker case).
  - A certificate from an untrusted root returns `untrusted_certificate`; an expired certificate returns `certificate_expired`.
  - A clinician certificate that is still valid, but past its `policyNotAfter` (10 hours and 1 second after issuance), returns `certificate_expired`.
  - Wrong `htm`, wrong `htu`, a body changed after signing, and a bad signature all return `bad_proof`.
  - **(Review Focus 1)** `iat` = now − 119s returns `ok`; `iat` = now − 121s returns `stale_proof`; `iat` = now + 61s returns `stale_proof`.
  - Using the same `jti` twice returns `replayed_proof` the second time.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement with `jose.compactVerify`, using the leaf certificate's public key imported from `x5c[0]`. Capture the raw body with `express.raw` on clinician and provider routes.
- [ ] **Step 4:** Run the tests again. Expected: PASS.
- [ ] **Step 5:** Commit `feat: certificate-bound request proofs`.

### Task 6: Encryption at rest and the record store (App lead, hours 2.5–3.5)

**Files:** Create `src/server/crypto/keyWrapper.ts`, `src/server/crypto/recordCipher.ts`, `src/server/records/store.ts`. Test: `tests/server/crypto/*.test.ts`, `tests/server/records/store.test.ts`.

**Interfaces:**
- Produces:
  - `interface KeyWrapper { wrap(dek: Uint8Array): Promise<string>; unwrap(wrapped: string): Promise<Uint8Array> }`
    - `KeyVaultWrapper(keyId: string)`: `CryptographyClient` with `DefaultAzureCredential` and `RSA-OAEP-256`.
    - `LocalWrapper(kekB64: string)`: AES-KW. For development and tests only.
  - `encryptEntry(dek: Uint8Array, aad: string, plaintext: string): { iv: Uint8Array; ciphertext: Uint8Array }` and `decryptEntry(dek, aad, iv, ciphertext): string`. AES-256-GCM with a 12-byte random IV; `aad` = `<patientId>:<entryId>`.
  - `createPatientKey(wrapper: KeyWrapper): Promise<string /* wrapped */>`
  - `putEntry(db, wrapper, args: { patientId: string; sourceKey: string; jws: string }): Promise<{ inserted: boolean; id: string }>`. Idempotent on (`patientId`, `sourceKey`): it returns `inserted: false` without writing.
  - `readEntries(db, wrapper, patientId: string): Promise<{ id: string; jws: string; createdAt: Date }[]>`. Unwraps the patient key **on every call** (no cache), so every read is a Key Vault operation visible in Key Vault's logs. **This function is never called except from the gate (Task 7) or the patient's own session.**

- [ ] **Step 1: Write the failing tests.**
  - `round trip`: encrypting and then decrypting gives back the plaintext.
  - `wrong AAD fails`: decrypting with `aad` of another entry ID throws.
  - `tamper fails`: flipping one ciphertext byte makes decryption throw.
  - `LocalWrapper round trip`: unwrapping a wrapped key gives back the same 32 bytes.
  - `putEntry idempotent`: a second call with the same `sourceKey` returns `inserted:false`, and the row count stays 1.
  - `ciphertext at rest`: the raw `entries.ciphertext` doesn't contain the substring `"resourceType"`.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement. Then, once Task 2b Step 1 is done, run one manual smoke test against the real vault: `npx tsx -e` with a wrap and unwrap through `KeyVaultWrapper`.
- [ ] **Step 4:** Run the tests again. Expected: PASS.
- [ ] **Step 5:** Commit `feat: per-patient envelope encryption`.

### Task 7: Grants, the access check, the audit log (Identity lead, hours 9–11.5)

**Files:** Create `src/server/access/grants.ts`, `src/server/access/gate.ts`, `src/server/audit/log.ts`; modify `src/server/routes/clinician.ts` and `src/server/routes/patient.ts`. Test: `tests/server/access/*.test.ts`.

**Interfaces:**
- Consumes: the `requireProof` results (Task 5); `readEntries` and `putEntry` (Task 6); `verifyEntry` (Task 3).
- Produces:
  - `appendAudit(db, e: Omit<AuditEvent,'id'|'at'>, now: Date): AuditEvent` and `listAudit(db, patientId: string, afterId?: number): AuditEvent[]`
  - `createOffer(db, patientId, durationMinutes, now): ShareOffer`
  - `redeemOffer(db, code, clinician: CertIdentity, now): Grant`. Creates a grant with status `pending` and records a `redeem_offer` audit event. Throws `GrantError('offer_used' | 'offer_expired' | 'offer_unknown')`.
  - `decideGrant(db, patientId, grantId, decision: 'approve' | 'deny', now): Grant`. Approving sets `approvedAt` and `expiresAt` (now + duration; `end_of_day` means 23:59:59 local time in the configured zone, default America/Indiana/Indianapolis).
  - `revokeGrant(db, patientId, grantId, now): Grant`
  - `createServiceGrant(db, patientId, now): Grant`, for the kidney trend check: `scope:'read_labs'`, `purpose:'health_check'`, `expiresAt:null`, `status:'active'`.
  - `authorize(db, ctx: { patientId: string; action: 'read' | 'append'; actor: { verified: true; identity: CertIdentity } | { verified: false; reason: DenyReason }; claimedPurpose: string | null; now: Date }): { allowed: true; grant: Grant } | { allowed: false; reason: DenyReason }`. Writes **exactly one** audit event either way. Order of checks:
    1. Unverified: deny with the proof's reason.
    2. Find the grant whose grantee `subjectUri` matches; if there is none, `no_grant`.
    3. `pending`: `grant_pending`.
    4. `revoked`: `grant_revoked`.
    5. Now past `expiresAt`: mark it `expired` and deny with `grant_expired`.
    6. `append` with a `read_labs` scope: `scope_denied`.
    7. Otherwise, allow.
  - Routes, each behind `requireProof` and then `authorize`:
    - `POST /api/clinician/share-offers/redeem { code }` returns `201 { grantId, status:'pending' }`.
    - `GET /api/clinician/grants/:id` returns `{ status, patientId, expiresAt }`. Clinicians may only see their own grants.
    - `GET /api/clinician/patients/:patientId/record` returns `{ entries: VerifiedEntry[], trend: TrendResult }`.
    - `POST /api/clinician/patients/:patientId/entries { jws }`: the entry signer's `subjectUri` must equal the proof's `subjectUri`, or it's `bad_proof`.
    - Denials return 401 (no, untrusted, or expired certificate; bad, stale, or replayed proof) or 403 (grant reasons), with `{ error: DenyReason }`.
    - Patient (session): `POST /api/patient/share-offers`, `POST /api/patient/grants/:id/approve|deny|revoke`.

- [ ] **Step 1: Write the failing tests** (unit tests on `authorize` and the grant functions, using a fixed `now`).
  - `no grant` returns `no_grant`, with one audit event (`outcome:'denied'`).
  - `pending` returns `grant_pending`.
  - `active` is allowed, with one audit event (`outcome:'granted'`, actor display "Dr. Rivera").
  - A grant bound to `urn:entra:T:other` returns `no_grant` for `urn:entra:T:O`.
  - **(Review Focus 3)** now = `expiresAt` + 1s returns `grant_expired`, and the grant's status becomes `expired`; after `revokeGrant`, the result is `grant_revoked`.
  - **(Review Focus 2)** `redeemOffer` twice with the same code: the second call throws `offer_used`, and `listAudit` plus the grants table show exactly one pending grant.
  - An offer redeemed 10m01s after creation throws `offer_expired`.
  - An unverified actor with `claimedPurpose:'treatment'` returns `no_certificate`. The audit event has `actor.kind:'unverified'` and `claimedPurpose:'treatment'`, and `copy.brokerBlocked` renders `"Blocked: unverified requester claimed treatment"`.
  - A service grant with `append` returns `scope_denied`.
  - `end_of_day` approval at 14:00 local time gives `expiresAt` 23:59:59 local time.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the grant functions, `authorize`, the audit log, and the routes. The record route decrypts only after `allowed: true`.
- [ ] **Step 4:** Run the tests again. Expected: PASS.
- [ ] **Step 5:** Commit `feat: grants, single access check, audit log`.

### Task 8: Patient passkeys, invites, sessions (App lead, hours 3.5–6)

**Files:** Create `src/server/auth/patient.ts`; modify `src/server/routes/patient.ts`; create the `src/web/patient/Enroll.tsx` and `src/web/patient/SignIn.tsx` pages. Test: `tests/server/auth/patient.test.ts`.

**Interfaces:**
- Consumes: `createPatientKey` (Task 6); `createServiceGrant` (Task 7; write a stub against its signature if Task 7 isn't merged yet).
- Produces:
  - `POST /api/patient/enroll/options { inviteCode }` returns WebAuthn registration options. `rpID` = `config.rpId`, `residentKey:'required'`, `userVerification:'required'`, `attestationType:'none'`. An unknown, used, or expired invite returns 400 `{ error:'invite_invalid' }`.
  - `POST /api/patient/enroll/verify { inviteCode, response, kidneyCheckOptIn: boolean }`. On success:
    - creates the patient (with its wrapped key), the credential, and the `provider_links` row
    - marks the invite redeemed
    - creates the service grant **only if** `kidneyCheckOptIn`
    - sets the session cookie `sid` (httpOnly, Secure, SameSite=Lax, 12h; only a hash is stored)
  - `POST /api/patient/invites/redeem { code }` (session required): adds a `provider_links` row to the current patient.
  - `POST /api/patient/signin/options` (usernameless) and `POST /api/patient/signin/verify { response }`: set the session.
  - `requirePatient` middleware: sets `req.patientId`.
- Copy: `copy.kidneyOptIn`: `"Turn on the kidney trend check. It reads your lab results to spot slow changes no single clinic would see. You can turn it off any time."`

- [ ] **Step 1: Write the failing tests.** Inject the SimpleWebAuthn verify functions as dependencies so tests can stub them.
  - `invite is single-use`: a second `enroll/verify` with the same code returns 400 `invite_invalid`.
  - `expired invite`: returns 400 `invite_invalid`.
  - `opt-in creates service grant`: with `kidneyCheckOptIn:true`, one active grant has `grantee.subjectUri:'urn:service:kidney-trend'`; with `false`, there are zero grants.
  - `enroll creates wrapped DEK and link`: the patient row has a non-empty `wrapped_dek`, and `provider_links` has one row.
  - `redeem while signed in adds a link`: two invites give two links on the same patient.
  - `session expiry`: a session older than 12h makes `requirePatient` return 401.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the server side, then the Enroll page (invite code from `?invite=`, opt-in checkbox, "Create passkey" button) and the SignIn page (a single "Sign in with passkey" button).
- [ ] **Step 4:** Run the tests again. Expected: PASS. On the deployed site, on a phone, enroll with an invite and sign in again. Expected: no password field is ever shown.
- [ ] **Step 5:** Commit `feat: patient passkeys, invites, sessions`.

### Task 9: Provider API, simulator, seed data (App lead, hours 6–8.5)

**Files:** Create `src/server/routes/provider.ts`, `tools/provider-sim.ts`, `tools/seed/lakeside.json`, `tools/seed/northwind.json`, `tools/seed/maple.json`. Test: `tests/server/routes/provider.test.ts`.

**Interfaces:**
- Consumes: `requireProof` and `createProof` (Task 5); `verifyEntry`, `signEntry` (Task 3); `putEntry` (Task 6); `runKidneyCheck` (Task 10; call it when present).
  - Task 5 lands at hour 9, so code against its Interfaces block and stub `requireProof` in tests until it merges.
  - Provider routes reject any proof whose `identity.kind` isn't `'provider'` with 403.
- Produces:
  - `POST /api/provider/invites { providerPatientId }` (proof by a provider certificate) returns `{ code, url }`, where `url` = `<origin>/patient/enroll?invite=<code>`. Invites expire in 7 days.
  - `POST /api/provider/records { providerPatientId, entries: string[] /* JWS */ }` returns `{ inserted: number, skipped: number }`.
    - If no `provider_links` row exists, it returns 409 `{ error:'patient_not_linked' }`.
    - Each JWS must verify, and its signer `subjectUri` must equal the proof's; otherwise the whole request returns 400.
    - It records one `ingest` audit event per request, then calls `runKidneyCheck(patientId)`.
  - The CLI `tools/provider-sim.ts`:
    - `invite <lakeside|northwind|maple> <providerPatientId>` prints the URL.
    - `push <provider> <providerPatientId>` signs each resource in `tools/seed/<provider>.json` with that provider's key, with `iat` = the resource's clinical date at 12:00 UTC, and POSTs it with a proof.
- **Seed data** (FHIR R4). Patient Sam Taylor, born 1984-05-17. Provider patient IDs: `LUC-20417`, `NWH-88213`, `MSF-0932`.

| Provider | Date | Resources |
|---|---|---|
| Maple Street Family Medicine | 2024-09-12 | Encounter (annual physical); Condition "Essential hypertension"; MedicationStatement "amlodipine 5 mg daily"; Observation eGFR **96** |
| Northwind Workplace Health | 2025-03-04 | Observation blood pressure 138/88; Observation eGFR **88** |
| Lakeside Urgent Care | 2025-08-21 | Encounter (low back pain); MedicationStatement "ibuprofen 600 mg three times daily as needed"; Observation eGFR **81** |
| Northwind Workplace Health | 2026-03-10 | Observation blood pressure 146/92; Observation eGFR **72** |
| Lakeside Urgent Care | 2026-09-15 | Encounter (back pain follow-up); Observation eGFR **63** |

  - eGFR Observations use LOINC `98979-8` (CKD-EPI 2021), unit `mL/min/1.73m2`, and a reference range with low = 60.
  - No result falls below 60, so no lab would flag any single one. Combined, they show a 34% drop and a fall from category G1 to G2.
  - The ibuprofen gives Dr. Rivera something concrete to act on.

- [ ] **Step 1: Write the failing tests.**
  - `ingest stores encrypted entries`: pushing 2 JWS gives `inserted:2`, and `readEntries` returns 2.
  - **(Review Focus 4)** `re-push is idempotent`: the same push again gives `{ inserted:0, skipped:2 }`, and the entry count stays 2.
  - `entry signed by another provider`: a request proven as Lakeside, carrying a JWS signed by Northwind, returns 400, and nothing is stored.
  - `tampered JWS`: returns 400.
  - `unlinked patient`: returns 409 `patient_not_linked`.
  - `clinician certificate on provider route`: returns 403.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the routes, the CLI, and the three seed files.
- [ ] **Step 4:** Run the tests again. Expected: PASS. Then on the deployed site:
  1. `invite` three times.
  2. Enroll on the phone with the first invite, then redeem the other two.
  3. `push` all three providers.

  Expected: the patient's record holds the 13 seed entries, plus the kidney Flag if Sam opted in.
- [ ] **Step 5:** Commit `feat: provider ingestion, simulator, seed data`.

### Task 10: Kidney trend check (App lead, hours 8.5–10)

**Files:** Create `src/server/checks/kidneyTrend.ts`. Test: `tests/server/checks/kidneyTrend.test.ts`.

**Interfaces:**
- Consumes: `authorize` with the service identity (Task 7), which is built from `config.serviceCertPem` via `verifyCertificateChain` and `resolveIdentity`; `readEntries`, `putEntry` (Task 6); `signEntry` (Task 3), using the service key. The service certificate comes from Task 3's `issue-org-certs.ts`, issued by EZCA.
- Produces:
  - `evaluateKidneyTrend(points: EgfrPoint[]): TrendResult`. Pure.
  - `runKidneyCheck(db, wrapper, patientId: string, now: Date): Promise<TrendResult | null>`. Returns `null` when there is no active service grant. Otherwise it reads only Observations with LOINC `98979-8`, records a `kidney_check` audit event, and, when flagged, upserts one FHIR `Flag` entry with `sourceKey` `urn:service:kidney-trend|Flag/kidney-trend`.
- The rule (KDIGO 2012 definitions):
  - **Eligibility:** at least 3 points, spanning at least 365 days, from at least 2 distinct sources. Otherwise `flagged:false`.
  - **Baseline:** the earliest point; **latest:** the most recent.
  - **Categories:** G1 ≥ 90, G2 60–89, G3a 45–59, G3b 30–44, G4 15–29, G5 < 15.
  - **`category_drop_25pct`:** the latest category is lower than the baseline category, and `percentDrop` ≥ 25.
  - **`rapid_decline`:** otherwise, the least-squares slope is ≤ −5 per year.
  - `percentDrop` is rounded to the nearest integer.
- Flag copy (`copy.kidneyFlag(percent, years, sources)`): `"Your kidney function (eGFR) dropped ${percent}% over ${years} years across ${sources} providers. No single result was flagged. Consider asking a doctor to review it."`

- [ ] **Step 1: Write the failing tests.**
  - `seed series flags`: the 5 seed values give `flagged:true`, `reason:'category_drop_25pct'`, `percentDrop:34`, `sourceCount:3`, and `slopePerYear` < −5.
  - `two points` give `flagged:false`.
  - A 300-day span gives `flagged:false`.
  - `single source` (all points from Lakeside) gives `flagged:false`.
  - `stable` 95, 93, 96, 94 over 2 years gives `flagged:false`.
  - `rapid without category change` (88, 80, 72, 64 over 2 years, all G2) gives `flagged:true`, `reason:'rapid_decline'`.
  - `runner without grant` returns `null`, with no audit event and no Flag entry.
  - **(Review Focus 4)** `runner twice`: exactly one Flag entry, and two `kidney_check` audit events.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the tests again. Expected: PASS.
- [ ] **Step 5:** Commit `feat: kidney trend check`.

### Task 11: Share flow end to end (both people, hours 15–17)

**Files:** Modify `src/web/patient/Share.tsx`, `src/web/patient/Activity.tsx`, `src/web/clinician/Redeem.tsx`. Test: `tests/server/routes/shareFlow.test.ts` (supertest integration).

**Interfaces:**
- Consumes: the Task 7 routes; `GET /api/patient/activity?after=<auditId>`, which returns `{ events: AuditEvent[], pendingRequests: Grant[], grants: Grant[] }`. Add this route in this task.
- Polling: the patient app polls activity every 2 seconds. The clinician app polls `GET /api/clinician/grants/:id` every 2 seconds while the grant is `pending`.
- **Patient side:**
  - Share page: pick a duration (5 min, 1 h, 4 h, end of day), then show the QR code (`qrcode`) encoding `<origin>/clinician/redeem?code=XXXX-XXXX`, with the code printed underneath.
  - When a pending request arrives, a full-screen prompt shows `copy.approvePrompt(...)` with Approve and Deny buttons.
- **Clinician side:** the Redeem page scans with the webcam (`@zxing/browser`) or accepts a typed code. It shows "Waiting for Sam to approve…" until the grant is active, then routes to the record.

- [ ] **Step 1: Write the failing integration test** `full flow`:
  1. Enroll and seed a patient (helpers from Tasks 8 and 9).
  2. The patient creates an offer (5 min).
  3. The clinician redeems it (proof): 201, pending.
  4. A clinician record read returns 403 `grant_pending`.
  5. The patient approves.
  6. A clinician read returns 200, with 13 or more entries and a flagged trend.
  7. Activity contains a granted `read_record` by "Dr. Rivera".
  8. The patient revokes.
  9. A clinician read returns 403 `grant_revoked`.
  10. Activity contains that denial.
- [ ] **Step 2:** Run the test. Expected: FAIL (the activity route is missing).
- [ ] **Step 3:** Implement the activity route and the three pages.
- [ ] **Step 4:** Run the test again. Expected: PASS. Then run the whole flow on the deployed site with the phone and the laptop.
- [ ] **Step 5:** Commit `feat: QR share with patient approval`.

### Task 12: Patient UI (App lead, hours 10–15)

**Files:** Create `src/web/patient/Home.tsx` and `src/web/patient/Activity.tsx`, plus components `Timeline`, `TrendChart`, `FlagCard`, `GrantList`. Add `GET /api/patient/record`, the patient's own read (session; not logged as a third-party access). It returns `{ entries: VerifiedEntry[], trend: TrendResult }`, with `trend` from `evaluateKidneyTrend` (Task 10) over the patient's own eGFR points.

The acceptance checks are manual on a phone. The UI isn't unit-tested, by choice: time goes to the server tests that protect the security claims.
- [ ] **Home:**
  - The timeline is grouped by date. Each entry shows `copy.signedBy(orgName)`.
  - `TrendChart` (recharts) plots the 5 eGFR points with a dashed line at 60, colored by source.
  - `FlagCard` shows `copy.kidneyFlag(...)` and a "Share with a doctor" button that opens the Share page.
- [ ] **Activity:**
  - A live list of audit events in plain language: `"Dr. Rivera, Riverside Primary Care, viewed your record · 2:14 PM"`, `copy.brokerBlocked(purpose)`, `"Kidney trend check read your lab results"`.
  - Denied events are shown in red.
- [ ] **Active grants:** each has a Revoke button. The service grant shows `copy.optInLine(date)`. Clinician grants show `"until 5:00 PM"`.
- [ ] **Layout:** works at 390px width; no horizontal scroll; tap targets at least 44px.
- [ ] Commit `feat: patient home, trend, activity`.

### Task 13: Clinician UI (Identity lead, hours 11.5–15)

**Files:** Create `src/web/clinician/Record.tsx`, plus a "write plan" form component.

- [ ] **Record page:** the combined timeline with `copy.signedBy` badges, the same `TrendChart`, and the `Flag` at the top. Header: `"Sam Taylor · access until 5:00 PM"`.
- [ ] **Write plan:** a form that creates a FHIR `ServiceRequest`. Default text: code text "Repeat kidney function panel and urine albumin-to-creatinine ratio"; note "Stop regular ibuprofen. Refer to nephrology if decline is confirmed." The browser signs it with `signEntry` and the clinician's key, then POSTs it via `signedFetch`. After saving, it shows up in Sam's timeline as `"Signed by Riverside Primary Care ✓"`.
- [ ] **(Review Focus 3) Access-ended screen:** any 403 with `grant_expired`, `grant_revoked` or `no_grant` replaces the record with `copy.accessEnded`: `"Sam's access for you has ended. Ask Sam to share again."` Manual check: revoke from the phone while the record is open. The next refresh (or the next 10-second background poll on this page) shows the screen, with no blank page and no console error.
- [ ] **Certificate expired** (Task 4's `getClinicianCredential` returns `null`): redirect to sign-in.
- [ ] Commit `feat: clinician record view and signed plan`.

### Task 14: Data broker page, the Particle moment (Identity lead, hours 15–16)

**Files:** Create `src/web/broker/BrokerPage.tsx`. Test: `tests/server/routes/broker.test.ts`.

- [ ] **Step 1: Write the failing tests.**
  - `no certificate`: GET of the record with `X-Purpose: treatment` and no proof returns 401 `no_certificate`. The patient activity contains `copy.brokerBlocked('treatment')`.
  - `self-signed certificate`: a proof signed by a certificate from a broker-minted root returns 401 `untrusted_certificate`, and is logged the same way.
- [ ] **Step 2:** Run the tests. Expected: PASS, because Task 7 already logs proof failures. These tests pin the demo path. If either fails, the bug is in how Task 7's routes pass `req.proofFailure` to `authorize`; fix it there.
- [ ] **Step 3:** Build the page.
  - It's styled as a generic "HealthData Exchange API console": a patient ID field prefilled from `?patient=`, and purpose "treatment."
  - Two buttons, "Request record" and "Request with our own certificate." The second mints a throwaway root and leaf in the browser.
  - It shows the server's JSON response verbatim.
- [ ] **Step 4:** Run the tests again. Expected: PASS. Manual: press both buttons and watch two red lines appear on Sam's phone.
- [ ] **Step 5:** Commit `feat: unverified requester demo`.

### Task 15: Export my whole record (App lead, hours 15–16)

**Files:** Modify `src/server/routes/patient.ts` to add `GET /api/patient/export`. Test: `tests/server/routes/export.test.ts`.

**Produces:** a FHIR R4 `Bundle`:
- `type:'collection'`, with each verified resource as an entry
- plus one `Provenance` per entry:
  - `target` → the resource
  - `recorded` = `signedAt`
  - `agent[0].who.display` = `"<displayName>, <orgName>"`
  - `signature[0].sigFormat` = `'application/jose'`, `signature[0].data` = base64 of the JWS, `signature[0].type` = the ASTM origin signature code `1.2.840.10065.1.12.1.1`
- served as a download named `sam-taylor-record.json`

- [ ] Test `export bundle`: the bundle has 13 or more resources, an equal number of Provenance entries, and every `Provenance.signature[0].data` decodes to a JWS that `verifyEntry` accepts. Run (FAIL), implement, run (PASS). Add an "Export my record" button on Home. Commit `feat: FHIR export with signed provenance`.

### Task 16: Freeze, deploy, rehearse, record, submit (both, hours 17–24)

- [ ] **Hour 17: feature freeze.** Run `npm test`; everything must pass. Deploy. Ask a fresh Claude Code session for a whole-branch review focused on the access check, proofs, and copy rules. Fix only real defects.
- [ ] **Demo reset script** `tools/demo-reset.ts`:
  1. Back up and clear `app.db`.
  2. Create three invites.
  3. Print the enrollment URLs.
  4. After phone enrollment, push the three seed files.

  Rehearse the reset twice. It must take under 5 minutes.
- [ ] **Video plan (about 5:00):**

  | Time | Scene |
  |---|---|
  | 0:00–0:35 | Problem: records scattered, so slow problems are missed. Epic blocked "treatment" requests it couldn't trust (Particle, 2024). Pitch line. |
  | 0:35–1:20 | Sam's phone: passkey sign-in; timeline with "Signed by" badges; the trend chart and flag ("no single result was flagged"). |
  | 1:20–2:30 | Share: Sam's QR code; Dr. Rivera's passkey Entra sign-in; scan; Sam approves; combined record; Dr. Rivera writes a signed plan (stop ibuprofen, repeat labs). |
  | 2:30–3:10 | The broker page: both attempts blocked; the red lines appear live on Sam's phone. |
  | 3:10–3:40 | Sam revokes; the doctor's screen shows "access ended"; export shows signed provenance. |
  | 3:40–4:40 | Architecture slide: "EZSSH for health records." Entra sign-in → EZCA-issued certificate, limited to a 10-hour shift → certificate-bound proofs → one access check → Key Vault envelope encryption; no standing access, every read logged. **Every certificate in the demo was issued by EZCA.** |
  | 4:40–5:00 | Close: pitch line; what's next (break-glass, Verified ID recovery, more hospitals as trusted tenants). |
- [ ] **Q&A prep.** Rehearse answers to the "Honest limits" in the spec's section 10. Add one more: "Why no revocation checks? Clinician access is cut off after a 10-hour shift by our server policy, and the certificate itself expires after a day. Short lifetimes are the revocation strategy, the same as EZSSH. EZCA also publishes a CRL if a relying party wants one."
- [ ] **Submission write-up:** problem, solution, the architecture diagram, how to run it, the honest limits, and what's next. **Submit with at least 1 hour of buffer.**

### Stretch goals (only if ahead at hour 15, in this order; each must fit in the video)

- **S1 Break-glass:**
  - `POST /api/clinician/patients/:id/break-glass { reason }` (proof required) creates a 1-hour grant without approval. It records an audit event with `action:'read_record'`, and the patient's activity shows it marked `"Emergency access"`.
  - Test: the grant is created without approval; the audit reason is recorded.
- **S2 Verified ID recovery** (the spec's stretch goal 2): a lost phone is recovered via an Entra Verified ID presentation, followed by registering a new passkey.
- **S3 Verified ID for a clinician's license** (the spec's stretch goal 3): the clinic issues a "licensed clinician" credential, and the certificate endpoint requires it.
- **S4 Conditional Access** (added during planning; needs the P2 trial; under 30 minutes): a policy requiring the phishing-resistant MFA authentication strength for this app. Show it in one slide.

(The EZCA adapter used to be stretch S2. It is now core Task 3b.)
