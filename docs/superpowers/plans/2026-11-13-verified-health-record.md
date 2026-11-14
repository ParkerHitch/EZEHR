# EZEHR Implementation Plan (C# / ASP.NET Core)

> **For agentic workers:** recommended: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** in the remaining hours of the event (2 PM Nov 13 → 2 PM Nov 14), build a central, patient-controlled health record.
- Nobody can read it by default.
- Verified clinicians, and apps the patient approves, read it only under a live grant.
- Every access attempt is shown to the patient.

**Architecture:** one ASP.NET Core Razor Pages site (Parker's existing `EZEHR.csproj` at the repo root) with three surfaces:
- **Patient pages** (phone): passkey sign-in, cookie session.
- **Clinician pages** (laptop): Entra ID sign-in via OpenID Connect with a physical security key, and a 10-hour cookie session.
- **A network API** (`/api/network/*`): organizations and apps authenticate with **EZCA client certificates over mutual TLS**.

Every read or write goes through **one access check** that writes one audit event. Records are FHIR R4 resources stored as JWS signed by their author's EZCA certificate, and encrypted per patient with AES-256-GCM. The per-patient key is wrapped by a Key Vault key.

**Tech Stack:**
- **Language and framework:** C#, ASP.NET Core Razor Pages, xUnit + `Microsoft.AspNetCore.Mvc.Testing`.
  - `TargetFramework`: keep `net11.0` only if App Service offers .NET 11; otherwise `net10.0` (LTS).
- **Packages:**
  - `Microsoft.Identity.Web` (clinician sign-in)
  - `Fido2.AspNet` 4.3.x (patient passkeys)
  - `Dapper` over `Microsoft.Data.SqlClient` 7.1.x, plus `Microsoft.Data.SqlClient.Extensions.Azure` for Entra sign-in to Azure SQL (both SqlClient packages are already in the csproj)
  - Tests: `Testcontainers.MsSql` (a throwaway SQL Server per test run)
  - `Azure.Identity` + `Azure.Security.KeyVault.Keys`
  - `EZCAClient` 1.0.x (Keytos's EZCA SDK)
  - `QRCoder`
  - `Microsoft.AspNetCore.Authentication.Certificate` (certificate forwarding)
- **Browser:** htmx (pinned, from a CDN) for polling. The only custom JavaScript is `wwwroot/js/passkey.js`.
- **Azure:** App Service (Linux, B1, managed identity), **Azure SQL Database** (Entra-only authentication, reached with the managed identity), Key Vault (RBAC), one Entra ID tenant. **Keytos:** EZCA free trial (approved by Keytos, the host).

**Spec:** `docs/superpowers/specs/2026-11-13-hackathon-design.md`

## Global Constraints

- No password anywhere on screen, including admin and seed accounts shown in the demo.
- Every security check happens on the server and is real. There are no hard-coded "verified" flags. Use .NET's built-in cryptography and standard protocols only: X.509, mutual TLS, OpenID Connect, WebAuthn, JWS (RFC 7515), AES-GCM.
- Copy never says "our servers can't read your data," never says that expiry erases what a clinician saw, never claims HIPAA compliance, and the Kidney Check note never diagnoses.
- Records are FHIR R4 JSON; export is a FHIR Bundle.
- Nothing reads a patient's record by default. The Kidney Check app's grant is off unless the patient turns it on at enrollment.
- Every access attempt, granted or denied, writes exactly one audit event and is shown to the patient.
- The share code is opaque and single-use (8 characters, shown as `XXXX-XXXX`). It expires 10 minutes after creation and contains no data or keys.
- The patient approves the named clinician before a grant becomes active.
- **Clinician shift:** a session lasts **10 hours** from sign-in, absolute, never sliding. This is enforced by both the cookie and the access check.
- Grant durations offered: **5 minutes (demo), 1 hour, 4 hours, end of day** (23:59:59 in `America/Indiana/Indianapolis`).
- **EZCA issues every organization and app certificate.** The broker's certificate is deliberately self-made. `LocalCertificateAuthorityService` is only for tests and the fallback.
- Cast:
  - patient **Sam Taylor**
  - clinician **Dr. Rivera** at **Riverside Primary Care**. Riverside is also a provider on the network: its system pushes the visit documentation.
  - providers **Lakeside Urgent Care**, **Northwind Workplace Health**, **Maple Street Family Medicine**, **Riverside Primary Care**
- **People read; organizations write, and sign what they write.** Clinician pages are read-only. See `docs/auth-flows.md` section 1, "Who writes, who reads".
  - app **Kidney Check**
- Exact copy lives in `Models/Copy.cs`:

  | Name | Text |
  |---|---|
  | `SignedBy(org)` | `Signed by {org} ✓` |
  | `WrittenBy(name, org)` | `Written by {name} · signed by {org} ✓` |
  | `ApprovePrompt(name, org, time)` | `Give {name} at {org} access until {time}?` |
  | `BrokerBlocked(purpose)` | `Blocked: unverified requester claimed {purpose}` |
  | `AppGrantLine(app, what, date)` | `{app} · {what} · you allowed this {date} · Revoke` |
  | `KidneyOptIn` | `Allow the Kidney Check app to read your lab results. It looks for slow changes no single clinic would see. You can turn it off any time.` |
  | `KidneyNote(percent, providers)` | `Kidney function (eGFR) dropped {percent}% across {providers} providers. No single result was flagged. Consider asking a doctor to review it.` |
  | `AccessEnded` | `Your access to this record has ended. Ask the patient to share again.` |
  | `NotAClinician` | `Your organization hasn't approved this account as a clinician.` |
  | `UntrustedOrg` | `This organization isn't connected to EZEHR yet.` |
  | `Pitch` | `Epic says it blocks data to protect patients. We make blocking unnecessary.` |

- **Feature freeze at 8:00 AM. Submit by 1:00 PM.**
- **Checkpoint at 11:00 PM**, with two independent fallbacks:
  - Entra sign-in isn't working on the deployed site → Task 8b, clinician passkeys in our own directory.
  - EZCA issuance isn't working → `CertificateAuthority:Mode=Local`.

## Review Focus

1. **Clock skew.** A certificate used within 2 minutes of its `notBefore` must still verify. *Test:* Task 2.
2. **The same share code entered twice** (a photo of the screen, or a double submit). The second redemption is rejected, and the patient sees exactly one pending request. *Test:* Task 6.
3. **The grant ends while the clinician has the record open.** The next page load shows `AccessEnded`, not an error page. *Tests:* Task 6 (server) and Task 12 (page).
4. **Re-running the provider push or the Kidney Check app** (a demo reset). No duplicate entries, and no duplicate notes. *Tests:* Tasks 5 and 10.
5. **A valid Entra user without the Clinician role, or from an untrusted tenant.** No access, and the page shows `NotAClinician` or `UntrustedOrg`. *Test:* Task 8.

---

## Team, timeline, ownership

The **Identity lead** is the teammate who built the EZCA CLI; the **App lead** is Parker. Times are local. Fit sleep in staggered blocks so someone is always working.

| Time | Identity lead | App lead |
|---|---|---|
| 6:00–7:30 PM | Task 0a (Entra, EZCA) | Task 1 (foundations), then Task 0b (Key Vault, App Service) |
| 7:30–9:00 PM | Task 2 (PKI core) | Task 4 (encryption, record store) |
| 9:00–10:00 PM | Task 3 (EZCA adapter, organization certificates) | Task 7 (patient passkeys) |
| 10:00–11:00 PM | Task 8 (clinician sign-in), then the **11 PM checkpoint** | Task 7 |
| 11:00 PM–1:00 AM | Task 5 (network API, client certificates) | Task 9 (provider simulator, seed data) |
| 1:00–3:00 AM | Task 6 (grants, access check, audit) | Task 10 (Kidney Check app), then Task 11 (patient UI) |
| 3:00–6:00 AM | Task 12 (clinician UI, share flow), Task 13 (broker) | Task 11, Task 14 (export) |
| 6:00–8:00 AM | Integrate, then the end-to-end share-flow test (together) | Same |
| 8:00 AM | **Feature freeze** | |
| 8:00 AM–1:00 PM | Task 15 (deploy, rehearse, record, submit) | Task 15 |

Stretch goals S1–S4 are taken only if the team is ahead at 6:00 AM.

## Solution layout

`EZEHR.csproj` stays at the repo root. Its default file globbing would also compile the other projects' files, so Task 1 adds `<Compile Remove="EZEHR.Tests/**;EZEHR.Tools/**" />`, with matching `Content`/`None` removes.

```
EZEHR.csproj  Program.cs  appsettings.json        web app; Program.cs only wires things up
Controllers/   NetworkController.cs, PatientPasskeyController.cs        ([ApiController]; HTTP only)
Pages/Patient/ Enroll, SignIn, Home, Share, Activity, Grants   (+ _ActivityPartial for htmx)
Pages/Clinician/ Index (sign in), Redeem, Wait, Record, AccessEnded     (PageModels follow the controller rules)
Managers/      CallerManager, AccessManager, GrantManager, AuditManager, InviteManager,
               RecordManager, EnrollmentManager                          (all business logic; query Db with Dapper)
Services/      external systems behind interfaces: IKeyWrapService (KeyVault…, Local…),
               ICertificateAuthorityService (Ezca…, Local…), IClientCertificateService, IFido2Service;
               static pure logic: CertificateValidator, OrgDirectory, RecordSigner, RecordVerifier,
               RecordCipher, KidneyRule
Models/        Party.cs, Grant.cs, ShareOffer.cs, AuditEvent.cs, Entries.cs, Copy.cs, Enums.cs,
               AppOptions.cs, Results.cs, NetworkModels.cs, Certificates.cs
Data/          Db.cs (Azure SQL connection factory + idempotent schema)
Security/Trust/ anchors.pem, intermediates.pem (public EZCA certificates)
wwwroot/js/passkey.js
EZEHR.Tests/   xUnit, references EZEHR.csproj
EZEHR.Tools/   console app, references EZEHR.csproj; commands: certs, provider, kidney-check, broker
EZEHR.Tools/seed/*.json   FHIR seed data per provider
docs/ezca-notes.md        what the EZCA probe showed
.pki/          (git-ignored) keys and certificates
```

The layers are described in `docs/style-guide.md` section 3. Expected failures (a used share code, a denied access) come back from managers as result records (`Models/Results.cs`), never as exceptions, so controllers and pages need no `try`/`catch`.

## Shared contracts (`Models/`, written in Task 1; every task uses these names)

- `enum PartyKind { Clinician, Provider, App, Platform }`
- `record Party(PartyKind Kind, string SubjectUri, string DisplayName, string OrgName)`. Values of `SubjectUri`:
  - `urn:entra:{tid}:{oid}` for clinicians
  - `urn:provider:{slug}` for providers
  - `urn:app:kidney-check` for the app
- `enum GrantScope { Full, LabsAndNotes }`. `Full` covers clinicians reading everything; it allows no writes. `LabsAndNotes` covers apps reading lab Observations and writing notes.
- `enum GrantStatus { Pending, Active, Denied, Revoked, Expired }`
- `record Grant(Guid Id, Guid PatientId, Party Grantee, GrantScope Scope, string Purpose, GrantStatus Status, int? DurationMinutes, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt)`. A `null` `ExpiresAt` means ongoing; only app grants are ongoing.
- `enum ShareDuration { FiveMinutes, OneHour, FourHours, EndOfDay }` and `record ShareOffer(Guid Id, Guid PatientId, string Code, ShareDuration Duration, DateTimeOffset OfferExpiresAt, Guid? RedeemedGrantId)`
- `enum DenyReason { NoCertificate, UntrustedCertificate, CertificateExpired, NotSignedIn, ShiftEnded, NoGrant, GrantPending, GrantExpired, GrantRevoked, ScopeDenied, WrongPartyKind }`. `WrongPartyKind` is a verified caller of the wrong kind (an app creating an invite).
- `enum AccessAction { ReadFullRecord, ReadEgfrLabs, WriteSignedNote }`, named after the network endpoints and logged as-is.
- `abstract record Caller` with two cases:
  - `sealed record VerifiedCaller(Party Party, DateTimeOffset? SessionStartedAt) : Caller`
  - `sealed record UnverifiedCaller(DenyReason Reason) : Caller`
- `record AuditEvent(long Id, Guid PatientId, DateTimeOffset At, Party? Actor /* null = unverified */, string Action, string? ClaimedPurpose, bool Granted, DenyReason? Reason)`
- `record VerifiedEntry(Guid Id, string ResourceType, JsonElement Resource, Party Signer, DateTimeOffset SignedAt, string? WrittenBy)`
- **Tables:**
  - `patients(id, display_name, wrapped_dek, created_at)`
  - `patient_credentials(credential_id, patient_id, public_key, sign_count, user_handle)`
  - `invites(code, provider_uri, provider_patient_id, expires_at, redeemed_at)`
  - `provider_links(patient_id, provider_uri, provider_patient_id)`, unique on (`provider_uri`, `provider_patient_id`)
  - `entries(id, patient_id, source_key, resource_type, iv, ciphertext, created_at)`, unique on (`patient_id`, `source_key`). `source_key` = `{signerUri}|{resourceType}/{resource.id}`.
  - `share_offers`, `grants` and `audit_events`, matching the records
  - `local_clinicians`, used by Task 8b only
- **Storage rules (Azure SQL + Dapper):**
  - Column types:
    - IDs: `UNIQUEIDENTIFIER`, except `audit_events.id`, which is `BIGINT IDENTITY`
    - times: `DATETIMEOFFSET`
    - keys, IVs and ciphertext: `VARBINARY(MAX)`
    - text: `NVARCHAR(n)`
    - enums: `NVARCHAR(32)` holding the enum's name. Pass `.ToString()` when writing, because Dapper writes enums as integers; on reads it parses the name back.
  - **Identifier columns use a binary collation** (`COLLATE Latin1_General_100_BIN2`): `source_key`, every `*_uri`, and `provider_patient_id`. `credential_id` and `user_handle` are `VARBINARY`, which already compares exactly. SQL Server's default collation is case-insensitive, which would make `Observation/a` and `Observation/A` collide. Share and invite codes are upper-cased before they're stored and before every lookup.
  - `source_key` is `NVARCHAR(400)`, so the (`patient_id`, `source_key`) unique index fits SQL Server's index key limit. Every patient-owned table has an index on `patient_id`.
  - Party columns are flattened: `grantee_kind`, `grantee_uri`, `grantee_name`, `grantee_org` on `grants`, and the same with the `actor_` prefix on `audit_events` (all `NULL` = unverified).
  - **Dapper mapping:** positional records map through their constructor, so `SELECT` with aliases that match the parameter names, in parameter order (`patient_id AS PatientId`). Rows that contain a `Party` are read into a private flat row record, then converted.
  - **Single-use operations are a conditional `UPDATE`, not a read followed by a write.** Redeeming a share offer runs `UPDATE share_offers SET redeemed_grant_id = @grantId WHERE code = @code AND redeemed_grant_id IS NULL AND offer_expires_at > @now`. It must affect exactly 1 row, in the same transaction as the grant insert. Invites work the same way. This is what makes Review Focus 2 hold under a double submit.
  - Open one connection per operation (`await using SqlConnection connection = await db.OpenAsync()`); pooling makes this cheap. Never share a `SqlConnection` between requests. An operation that must commit with its audit event passes its connection and transaction to `AuditManager.AppendAsync`.
  - Values a caller chooses are bounded before they reach a column: FHIR resource ids over 64 characters are refused (`MissingOrInvalidResourceId`), and a claimed purpose is cut to 200 characters.

---

### Task 0a: Entra and EZCA setup (Identity lead, 6:00–7:30 PM; portal work)

**Produces:**
- Configuration: `AzureAd:TenantId`, `AzureAd:ClientId`, `TrustedTenants`, `Ezca:CaId`, `Ezca:TemplateId`.
- `.pki/ezca-root.pem` and `.pki/ezca-issuing.pem`.
- Dr. Rivera signing in with a security key.
- `docs/ezca-notes.md`.

- [ ] **Azure account:** Azure for Students with a **personal Microsoft account**, verified with the Purdue email. Don't use the Purdue account itself, because its tenant belongs to Purdue. Invite the App lead (Entra → Users → Invite external user) as Contributor on the subscription.
- [ ] **Dr. Rivera:**
  - Create the user, with display name `Dr. Rivera`.
  - Under Authentication methods → Policies, enable **Passkey (FIDO2)** and **Temporary Access Pass**.
  - Issue a TAP, then at `https://mysignins.microsoft.com/security-info` register the **physical security key**. If TAP isn't available, use the initial password once, off camera.
  - Verify: in a private window, "Sign-in options → Face, fingerprint, PIN or security key" gets in with a tap and a PIN.
  - If this fails on the Free tier, activate the **Entra ID P2 30-day trial** and retry.
- [ ] **App registration** "EZEHR":
  - Single tenant, platform **Web**.
  - Redirect URIs: `https://localhost:{port}/signin-oidc` (from `Properties/launchSettings.json`) and `https://{app}.azurewebsites.net/signin-oidc`.
  - Under Authentication, enable **ID tokens**. Sign-in only, so no client secret.
  - App role `Clinician` (allowed member type Users). Under Enterprise applications → Users and groups, assign Dr. Rivera to it.
  - Set `TrustedTenants = { "{tenantId}": "Riverside Primary Care" }`.
- [ ] **EZCA:**
  - As Global Admin, open EZCA's "Register Keytos Applications" admin-consent link and accept.
  - At `https://portal.ezca.io`, register subscription `EZEHR` on the Basic plan, choosing **Skip for now** on payment.
  - Create a Root CA, then a Subordinate/Issuing SSL CA. On the issuing CA, under the advanced EKU settings, include **Client Authentication** (it can't be changed later), and set the maximum lifetime to at least 400 days.
  - Register these subject names:
    - `lakeside.providers.ezehr`
    - `northwind.providers.ezehr`
    - `maple.providers.ezehr`
    - `riverside.providers.ezehr`
    - `kidney-check.apps.ezehr`
  - Download the root and issuing CA certificates to `.pki/`.
  - Get `CAID` and `TemplateID` with `az login`, then `curl -H "Authorization: Bearer $(az account get-access-token --query accessToken -o tsv)" https://portal.ezca.io/api/CA/GetAvailableSSLCAs`.
  - All certificates are issued from a laptop with Task 3's tool. The server never calls EZCA at runtime, so the managed identity needs no EZCA role.
- [ ] **EZCA probe (20 min):** with the `EZCAClient` SDK (or curl), request a 1-day certificate for `lakeside.providers.ezehr` from an ECDSA P-256 CSR, with EKU `1.3.6.1.5.5.7.3.2`. Record in `docs/ezca-notes.md`:
  - whether EC was accepted (if not, use RSA 2048 everywhere)
  - the response fields
  - the subject EZCA produced

  Check it with `openssl verify -CAfile .pki/ezca-root.pem -untrusted .pki/ezca-issuing.pem probe.crt`, which should print `OK`.

### Task 0b: Hosting and Key Vault (App lead, after Task 1)

**Produces:** `KeyVault:KeyId`, `AZURE_SQL_CONNECTIONSTRING`, the site URL, and the managed identity's object ID (send it to the Identity lead).

- [ ] **Key Vault** (RBAC): create RSA 3072 key `records-kek` with wrap and unwrap permitted. Give yourself Key Vault Crypto Officer.
- [ ] **App Service** (Linux, **B1**; client certificates don't work on F1):
  - Runtime: .NET 11 if offered, otherwise .NET 10 (and set `TargetFramework` to `net10.0`).
  - Turn on the system-assigned managed identity and grant it **Key Vault Crypto User**.
  - Deploy with Rider's publish, or `dotnet publish -c Release` plus `az webapp deploy`.
- [ ] **Azure SQL Database.** It's already provisioned, and `AZURE_SQL_CONNECTIONSTRING` is set on App Service (`Pages/DbTest` reads it). Confirm the rest:
  - The server uses **Microsoft Entra-only authentication**, so there's no SQL password anywhere.
  - The connection string uses `Authentication=Active Directory Managed Identity` (or `Active Directory Default`) and contains no `Password=`.
  - The App Service managed identity is a database user: `CREATE USER [<app-name>] FROM EXTERNAL PROVIDER;`, then add it to `db_datareader`, `db_datawriter` and `db_ddladmin`. `db_ddladmin` is needed because the app creates its own tables at startup.
  - A second database, `ezehr-dev`, on the same server for local `dotnet run`. Its connection string goes in `appsettings.Development.json` with `Authentication=Active Directory Default`; it holds no secret, so it's safe to commit.
  - Both teammates are database users in both databases, and the server firewall allows Azure services plus each laptop's IP (for local runs and `demo reset`).
- [ ] **Client certificates:** Configuration → General settings → Incoming client certificates = **Optional Interactive User**, so browsers aren't prompted and programs can send certificates.
  - This mode uses TLS renegotiation, so set **HTTP version 1.1** (HTTP/2 off).
  - Requests over 100 KB fail under renegotiation. The tools send `Expect: 100-continue` and keep each push small.
- [ ] **Verify:** `https://{app}.azurewebsites.net` loads, `/DbTest` connects, and `az keyvault key show --id {KeyVault:KeyId}` succeeds. Delete `Pages/DbTest` once Task 1's schema is running; it shows server details and raw exception text.

### Task 1: Foundations (App lead, 6:00–7:00 PM)

**Files:**
- Modify `EZEHR.csproj` (packages, `Compile Remove`), `.gitignore`, `Program.cs`, `Pages/Shared/_Layout.cshtml` (add htmx).
- Create `Models/*`, `Data/Db.cs`, the `EZEHR.Tests` and `EZEHR.Tools` projects, and add both to `EZEHR.slnx`.
- Test: `EZEHR.Tests/Data/SqlServer.cs`, `EZEHR.Tests/Data/DbTests.cs`, `EZEHR.Tests/Models/CopyTests.cs`.

**Interfaces:**
- Produces:
  - `sealed class AppOptions`, bound from configuration, with:
    - `Origin`. The database connection string is not in `AppOptions`: `Db` reads the top-level `AZURE_SQL_CONNECTIONSTRING` key, the name App Service already uses.
    - `TrustedTenants: Dictionary<string,string>`, `AzureAd`
    - `CertificateAuthority.Mode: "Ezca" | "Local"`, `Ezca { CaId, TemplateId }`
    - `TrustAnchorsPemPath`, `IntermediatesPemPath`. These are public CA certificates, committed under `Security/Trust/`.
    - `KeyWrapper.Mode: "KeyVault" | "Local"`, `KeyVault.KeyId`, `LocalKekBase64`
    - `TimeZone`
  - `sealed class Db(IConfiguration configuration)`, registered as a singleton; it reads `AZURE_SQL_CONNECTIONSTRING` (`AppOptions.ConnectionStringKey`):
    - `Task<SqlConnection> OpenAsync(CancellationToken ct = default)` returns an open, pooled connection; callers dispose it.
    - `Task EnsureSchemaAsync(CancellationToken ct = default)` runs an idempotent script (`IF OBJECT_ID(N'dbo.entries') IS NULL CREATE TABLE ...`) that creates the tables, constraints and indexes above if they're missing. `Program.cs` calls it once at startup. There are no migrations; when the schema changes, edit the script and the table list in this plan in the same commit.
    - `static bool IsUniqueViolation(SqlException ex)` is true for SQL errors 2627 and 2601.
    - `Task DeleteAllDataAsync()` deletes every row, in foreign-key order. Only `demo reset` (Task 15) calls it; add it then.
  - `SqlServer`, a static test helper:
    - It starts one SQL Server container per test run with Testcontainers (`mcr.microsoft.com/mssql/server:2022-latest`). If `EZEHR_TEST_SQL` is set (a server connection string), it uses that server instead.
    - `Task<TestDatabase> CreateDatabaseAsync()` creates a fresh `ezehr_test_{guid:N}` database and runs `EnsureSchemaAsync`. `TestDatabase` has `Db` and `ConnectionString`, and dropping it drops the database. Each test creates one in its class's `IAsyncLifetime.InitializeAsync`, so tests never share state and classes still run in parallel. A full run takes about 1.5 minutes, most of it creating databases.
    - `WebApplicationFactory` tests (`EzehrFactory`) set `AZURE_SQL_CONNECTIONSTRING` to `TestDatabase.ConnectionString`.
    - It needs Docker or Podman (for Podman, point `DOCKER_HOST` at its socket). Tests that never touch storage (`Copy`, PKI, `RecordCipher`) don't use it.
  - The `Models` types above, and `static class Copy`.
  - `TimeProvider` is registered in DI, and every time-dependent class takes it, so tests can control the clock.

- [ ] Fix `.gitignore`: remove the four `/wwwroot/lib/*/dist/` lines; add `.pki/`.
- [ ] **Write failing tests:**
  - `DbTests.EnsureSchema_creates_all_tables`: every table above exists in a fresh test database.
  - `DbTests.EnsureSchema_twice_is_harmless`: a second run neither throws nor changes anything.
  - `DbTests.Entries_unique_per_patient_and_source_key`: a second insert throws a `SqlException` for which `Db.IsUniqueViolation` is true.
  - `DbTests.Source_key_is_case_sensitive`: `…|Observation/a` and `…|Observation/A` both insert.
  - `CopyTests.Exact_spec_strings`:
    - `Copy.SignedBy("Lakeside Urgent Care")` is `"Signed by Lakeside Urgent Care ✓"`
    - `Copy.ApprovePrompt("Dr. Rivera","Riverside Primary Care","5:00 PM")` is `"Give Dr. Rivera at Riverside Primary Care access until 5:00 PM?"`
    - `Copy.BrokerBlocked("treatment")` is `"Blocked: unverified requester claimed treatment"`
- [ ] Run `dotnet test`. Expected: FAIL (types missing).
- [ ] Implement, then run `dotnet test`. Expected: PASS. `dotnet run` serves the template home page and creates the tables in `ezehr-dev`.
- [ ] Commit `feat: foundations, domain contracts, schema`.

### Task 2: PKI core (Identity lead, 7:30–9:00 PM)

**Files:** Create `Services/ICertificateAuthorityService.cs`, `LocalCertificateAuthorityService.cs`, `CertificateValidator.cs`, `OrgDirectory.cs`, `RecordSigner.cs`, `RecordVerifier.cs`. Test: `EZEHR.Tests/Pki/*`.

**Interfaces:**
- Produces:
  - `interface ICertificateAuthorityService { Task<IssuedCertificate> IssueAsync(string csrPem, string subjectName, int validityDays, CancellationToken ct); }` with `record IssuedCertificate(X509Certificate2 Leaf, IReadOnlyList<X509Certificate2> Chain)`.
    - Every certificate carries the clientAuth EKU (`1.3.6.1.5.5.7.3.2`).
    - The CSR's own subject is ignored; `subjectName` is used.
  - `sealed class LocalCertificateAuthorityService(X509Certificate2 root, TimeProvider time) : ICertificateAuthorityService`, with `static X509Certificate2 CreateRoot(TimeProvider)`. ECDSA P-256 root; CN = `subjectName`; `notBefore` = now − 5 min. Built with `CertificateRequest.LoadSigningRequestPem` and `CertificateRequest.Create`.
  - `static class CertificateValidator` with `CertificateCheck Validate(X509Certificate2 leaf, IEnumerable<X509Certificate2>? extra, DateTimeOffset at, TrustAnchors trustAnchors)` and `TrustAnchors LoadTrustAnchors(AppOptions)`. `record TrustAnchors(IReadOnlyList<X509Certificate2> Roots, IReadOnlyList<X509Certificate2> Intermediates)`; the managers that need it load it into a field from `AppOptions`.
    - `CertificateCheck` is `(bool Valid, DenyReason? Reason)`.
    - Uses `X509Chain` with `ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust`, revocation mode `NoCheck`, and `VerificationTime = at + 2 min` so 2 minutes of skew are tolerated.
    - Requires the clientAuth EKU.
    - An unknown root is `UntrustedCertificate`; outside validity is `CertificateExpired`.
  - `static class OrgDirectory`, mapping certificate CN to `Party`. It is the only place that builds subject URIs (`ProviderUri`, `ClinicianUri`) and CNs (`ProviderCn`):

    | CN | Party |
    |---|---|
    | `lakeside.providers.ezehr` | Provider `urn:provider:lakeside` "Lakeside Urgent Care" |
    | `northwind.providers.ezehr` | Provider `urn:provider:northwind` "Northwind Workplace Health" |
    | `maple.providers.ezehr` | Provider `urn:provider:maple` "Maple Street Family Medicine" |
    | `riverside.providers.ezehr` | Provider `urn:provider:riverside` "Riverside Primary Care" |
    | `kidney-check.apps.ezehr` | App `urn:app:kidney-check` "Kidney Check" |

    `Party? FindByCertificate(X509Certificate2 leaf)` returns `null` for an unknown CN.
  - `static class RecordSigner`, with `string Sign(JsonElement resource, X509Certificate2 certWithKey, IReadOnlyList<X509Certificate2> chain, DateTimeOffset iat)`.
    - Produces a compact JWS (RFC 7515) with protected header `{ "alg": "ES256" | "RS256", "x5c": [...], "iat": unix }`.
    - The payload is the UTF-8 JSON of the resource.
    - ES256 signatures use `DSASignatureFormat.IeeeP1363FixedFieldConcatenation`.
  - `static class RecordVerifier`, with `VerifiedEntry Verify(Guid entryId, string jws, TrustAnchors trustAnchors)`.
    - Verifies the signature with the `x5c` leaf, then checks the chain **at the header `iat`**, then resolves the signer.
    - Throws `RecordSignatureException` on any failure.
    - `WrittenBy` = `sender.display` when the resource is a `Communication` (the author, as the signing organization states it).

- [ ] **Write failing tests:**
  - `LocalCa_uses_subjectName_not_csr_subject`: CSR `CN=Mallory` with subject name `lakeside.providers.ezehr` gives a leaf with CN `lakeside.providers.ezehr` and the clientAuth EKU.
  - `LocalCa_rejects_tampered_csr`: throws.
  - `Validator_ok_for_local_chain`, `Validator_untrusted_root`, `Validator_expired` (at = `notAfter` + 1 s gives `CertificateExpired`).
  - **(Review Focus 1)** `Validator_tolerates_2min_skew`: at = `notBefore` − 2 min gives Ok; − 3 min gives `CertificateExpired`.
  - `OrgDirectory_resolves_known_and_rejects_unknown`: `mallory.providers.ezehr` gives `null`.
  - `Signer_verifier_roundtrip_es256` and `_rs256`.
  - `Verifier_valid_after_cert_expiry_when_signed_inside_validity`.
  - `Verifier_rejects_tampered_payload`, and `Verifier_rejects_unknown_signer` (a known root but an unknown CN).
- [ ] Run `dotnet test --filter Pki`. Expected: FAIL. Implement, then run again. Expected: PASS.
- [ ] Commit `feat: PKI core (local CA, chain validation, signed records)`.

### Task 3: EZCA adapter and organization certificates (Identity lead, 9:00–10:00 PM)

**Files:** Create `Services/EzcaCertificateAuthorityService.cs` and `EZEHR.Tools/Commands/CertsCommand.cs`. Test: `EZEHR.Tests/Pki/EzcaCertificateAuthorityServiceTests.cs`.

**Interfaces:**
- Consumes: `ICertificateAuthorityService` (Task 2), `docs/ezca-notes.md` (Task 0a).
- Produces:
  - `sealed class EzcaCertificateAuthorityService(IEZCAClient client, AvailableCAModel ca) : ICertificateAuthorityService`. It calls `RequestCertificateWithChainV2Async(ca, csr, subjectName, validityDays, subjectAltNames)`, and maps the leaf and chain from `CertificatePEM`, `IssuingCACertificate` and `RootCertificate`.
  - The SDK authenticates with `DefaultAzureCredential`: the managed identity on App Service, `az login` on a laptop.
  - `CertsCommand`: `ezehr-tools certs issue --ca ezca|local`.
    - For each CN in `OrgDirectory`, it generates a P-256 key (or RSA 2048, per the probe) and requests a **365-day** certificate.
    - It writes `.pki/{slug}.pfx`, `.pki/anchors.pem` and `.pki/intermediates.pem`.
    - With `local`, it first creates `.pki/local-root.pfx`.
  - `certs broker` writes `.pki/broker.pfx`, a self-signed certificate (CN `exchange.broker.example`), on purpose.

- [ ] **Write failing tests** against a fake `IEZCAClient`:
  - `Ezca_passes_subject_validity_and_csr`.
  - `Ezca_maps_chain`: returns a leaf plus 2 chain certificates.
  - `Ezca_error_throws_with_message`.
- [ ] Run (FAIL), implement, run (PASS).
- [ ] Run `certs issue --ca ezca`, then check each organization certificate with `openssl verify`. Expected: `OK`.
- [ ] Copy `anchors.pem` and `intermediates.pem` to `Security/Trust/`. They're public, so commit them.
- [ ] Commit `feat: EZCA certificate authority and org certificate tool`.

### Task 4: Encryption at rest and the record store (App lead, 7:30–9:00 PM)

**Files:** Create `Services/IKeyWrapService.cs`, `KeyVaultKeyWrapService.cs`, `LocalKeyWrapService.cs`, `RecordCipher.cs`, `Managers/RecordManager.cs`. Test: `EZEHR.Tests/Crypto/*`, `EZEHR.Tests/Records/RecordManagerTests.cs`.

**Interfaces:**
- Produces:
  - `interface IKeyWrapService { Task<byte[]> WrapAsync(byte[] dek); Task<byte[]> UnwrapAsync(byte[] wrapped); }`
    - `KeyVaultKeyWrapService`: `CryptographyClient`, `KeyWrapAlgorithm.RsaOaep256`, `DefaultAzureCredential`.
    - `LocalKeyWrapService`: AES-256-GCM with a key-encryption key from configuration. Development and tests only.
  - `static class RecordCipher`, with `(byte[] Iv, byte[] Ciphertext) Encrypt(byte[] dek, string aad, string plaintext)` and `string Decrypt(byte[] dek, string aad, byte[] iv, byte[] ciphertext)`.
    - `AesGcm`, a 12-byte random IV, and a 16-byte tag appended to the ciphertext.
    - `aad` = `{patientId}:{entryId}`.
  - `sealed class RecordManager` (takes `Db`, `IKeyWrapService` and, from Task 6, `AccessManager`):
    - `Task<byte[]> CreatePatientKeyAsync()`: returns the wrapped key.
    - `Task<(bool Stored, Guid EntryId)> StoreEntryAsync(Guid patientId, string sourceKey, string resourceType, string jws)`: idempotent on `sourceKey`. It inserts with `WHERE NOT EXISTS`, and the unique index is the backstop: a race that hits `Db.IsUniqueViolation` also returns `Stored=false`.
    - `Task<IReadOnlyList<DecryptedEntry>> DecryptEntriesAsync(Guid patientId, Func<string,bool>? resourceTypeFilter)`: unwraps the patient key **on every call**, so every read is a Key Vault operation. Only `AccessManager`-guarded code in `RecordManager` and the patient's own pages call it.

- [ ] **Write failing tests** (`RecordManagerTests` use `SqlServer`):
  - `Cipher_roundtrip`, `Cipher_wrong_aad_throws`, `Cipher_tamper_throws`.
  - `LocalWrapper_roundtrip`.
  - **(Review Focus 4)** `Put_is_idempotent`: a second identical put returns `Stored=false`, with 1 row.
  - `Ciphertext_has_no_plaintext`: the raw column doesn't contain `"resourceType"`.
  - `Read_filters_by_type`.
- [ ] Run (FAIL), implement, run (PASS). Smoke-test `KeyVaultKeyWrapService` once against the real vault, from a scratch console command.
- [ ] Commit `feat: per-patient envelope encryption and record store`.

### Task 5: Network API and client certificates (Identity lead, 11:00 PM–1:00 AM)

**Files:** Create `Services/IClientCertificateService.cs` (+ `ConnectionClientCertificateService.cs`), `Managers/CallerManager.cs`, `Controllers/NetworkController.cs`; modify `Program.cs`. Test: `EZEHR.Tests/Api/NetworkIngestTests.cs` (WebApplicationFactory).

**Interfaces:**
- Consumes: `CertificateValidator`, `OrgDirectory`, `RecordVerifier` (Task 2); `RecordManager` (Task 4).
- Produces:
  - `interface IClientCertificateService { Task<X509Certificate2?> GetAsync(HttpContext httpContext); }`
    - Production uses `AddCertificateForwarding(o => o.CertificateHeader = "X-ARR-ClientCert")` plus `UseCertificateForwarding()`, then `ctx.Connection.GetClientCertificateAsync()`.
    - Tests register a source that reads a test-only header. That source is never registered outside tests.
  - `sealed class CallerManager(TimeProvider, IOptions<AppOptions>)`, with `Caller FromCertificate(X509Certificate2? certificate)`:
    - no certificate → `UnverifiedCaller(NoCertificate)`
    - validation failure → `UnverifiedCaller(reason)`
    - a valid chain but an unknown CN → `UnverifiedCaller(UntrustedCertificate)`
    - otherwise → `VerifiedCaller(party, null)`
  - Endpoints (`NetworkController`, route `/api/network`). Each action gets the certificate, turns it into a `Caller`, calls one manager method of the same name (`InviteManager.CreateEnrollmentInviteAsync` or `RecordManager.PushSignedRecordsAsync`), and maps the result:
    - `POST /create-enrollment-invite { providerPatientId }` (`CreateEnrollmentInviteAsync`) returns `201 { inviteCode, enrollmentUrl }`. Providers only (403 `WrongPartyKind` otherwise). Invites expire in 7 days.
    - `POST /push-signed-records { providerPatientId, signedEntries: string[] }` (`PushSignedRecordsAsync`) returns `{ stored, alreadyStored }`. Providers only.
      - No `provider_links` row returns 409.
      - Every JWS must verify (Task 2), and its signer must equal the caller; otherwise the request returns 400 and nothing is stored.
      - It writes one `PushSignedRecords` audit event for the patient.
    - Request models carry `[Required]`, so a missing field is the automatic 400. Unverified callers get **401 `{ error }`**, and **nothing is logged** for ingest, because there is no target patient yet. Patient-targeted endpoints log through the gate (Task 6).

- [ ] **Write failing tests:**
  - `Push_stores_signed_entries`: 2 entries give `stored=2`.
  - **(Review Focus 4)** `Push_twice_is_idempotent`: `{stored:0, alreadyStored:2}`.
  - `Ingest_rejects_entry_signed_by_other_provider`: 400, nothing stored.
  - `Ingest_unlinked_patient_409`.
  - `No_certificate_401` and `Self_signed_certificate_401` (`broker.pfx`).
  - `App_certificate_cannot_ingest_403`.
- [ ] Run (FAIL), implement, run (PASS).
- [ ] Deploy, then confirm on the real site: a provider push from Task 9 over mutual TLS succeeds, and the same request with no certificate returns 401.
- [ ] Commit `feat: network API with EZCA client certificates`.

### Task 6: Grants, the access check, the audit log (Identity lead, 1:00–3:00 AM)

**Files:** Create `Managers/GrantManager.cs`, `Managers/AccessManager.cs`, `Managers/AuditManager.cs`; modify `Managers/RecordManager.cs` and `Controllers/NetworkController.cs`. Test: `EZEHR.Tests/Access/*`.

**Interfaces:**
- Consumes: `Caller` (Models), `RecordManager`, `RecordVerifier`.
- Produces (all async, because each call goes to Azure SQL over the network; every class takes `Db`):
  - `AuditManager`: `Task<AuditEvent> AppendAsync(Guid patientId, Party? actor, string action, string? claimedPurpose, bool granted, DenyReason? reason)` and `Task<IReadOnlyList<AuditEvent>> ListAsync(Guid patientId, long afterId = 0)`.
  - `GrantManager`:
    - `Task<ShareOffer> CreateShareOfferAsync(Guid patientId, ShareDuration duration)`: an 8-character code, valid 10 minutes.
    - `Task<GrantResult> RedeemShareCodeAsync(string code, Party clinician)`: claims the offer with the conditional `UPDATE` (see Storage rules), creates a `Pending` grant and appends a `RedeemShareCode` event, all in one transaction. Fails with `GrantError.OfferUsed | OfferExpired | OfferUnknown`.
    - `Task<GrantResult> ApproveOrDenyAsync(Guid patientId, Guid grantId, bool approve)` (fails with `GrantUnknown` or `GrantNotPending`): approval sets `ApprovedAt` and `ExpiresAt` from the duration; `EndOfDay` is 23:59:59 in `AppOptions.TimeZone`.
    - `Task<GrantResult> RevokeAsync(Guid patientId, Guid grantId)`.
    - `static GenerateCode()`, `NormalizeCode(string)` and `FormatCode(string)` (`XXXX-XXXX`): the one place codes are made and shown. Invites use them too.
    - `record GrantResult(bool Succeeded, Grant? Grant, GrantError? Error)`.
    - `Task<Grant> CreateAppGrantAsync(Guid patientId, Party app)`: `LabsAndNotes`, ongoing, `Active`.
    - `Task<IReadOnlyList<Grant>> ListForPatientAsync(Guid patientId)` and `Task<Grant?> FindAsync(Guid grantId)`.
  - `AccessManager`: `Task<AccessDecision> AuthorizeAsync(Guid patientId, AccessAction action, Caller caller, string? claimedPurpose)`, where `AccessDecision` is `(bool Allowed, Grant? Grant, DenyReason? Reason)`.
    - It writes **exactly one** audit event, either way.
    - Check order:
      1. Unverified → that reason.
      2. A clinician whose `SessionStartedAt` is more than 10 h ago → `ShiftEnded`.
      3. No grant with this `Grantee.SubjectUri` for the patient → `NoGrant`.
      4. `Pending` → `GrantPending`.
      5. `Revoked` → `GrantRevoked`.
      6. Past `ExpiresAt` → mark `Expired`, then `GrantExpired`.
      7. The scope doesn't cover the action → `ScopeDenied`. `Full` allows `ReadFullRecord` and `ReadEgfrLabs` only (clinicians never write); `LabsAndNotes` allows `ReadEgfrLabs` and `WriteSignedNote`.
      8. Otherwise, allowed.
  - Network endpoints added here, all through the gate (`RecordManager.ReadEgfrLabsAsync`, `ReadFullRecordAsync`, `WriteSignedNoteAsync`, each returning a result record with `Allowed` and `Reason`):
    - `GET /patients/{patientId}/read-egfr-labs` (`ReadEgfrLabsAsync`), for apps (`ReadEgfrLabs`). Returns the verified eGFR Observations, LOINC `98979-8`.
    - `POST /patients/{patientId}/write-signed-note { jws }` (`WriteSignedNoteAsync`, `WriteSignedNote`). The signer must equal the caller. Idempotent by `sourceKey`.
    - `GET /patients/{patientId}/read-full-record?purpose=...` (`ReadFullRecordAsync`, `ReadFullRecord`), the broker's target.
    - Denials return 401 for certificate reasons, 403 for grant reasons. That mapping is one private method in `NetworkController`.

- [ ] **Write failing tests** (with a fake `TimeProvider`, and `SqlServer`):
  - `No_grant_denied_and_logged_once`.
  - `Pending_denied`.
  - `Active_allowed_and_logged`.
  - `Grant_for_other_clinician_is_NoGrant`.
  - **(Review Focus 3)** `Expired_marks_and_denies` and `Revoked_denies`.
  - **(Review Focus 2)** `Redeem_twice_second_is_offer_used`: exactly 1 pending grant. `Concurrent_redeems_create_one_grant`: 8 parallel redeems, 1 grant.
  - `Offer_expired_after_10_minutes`.
  - `Unverified_logged_with_claimed_purpose`: `Actor == null`, `ClaimedPurpose == "treatment"`.
  - `App_scope_cannot_read_record`.
  - `Clinician_shift_over_10h_denied`.
  - `EndOfDay_is_local_midnight_minus_1s`.
  - Integration: `Broker_no_cert_on_record_endpoint_logs_blocked_event`.
- [ ] Run (FAIL), implement, run (PASS).
- [ ] Commit `feat: grants, single access check, audit log`.

### Task 7: Patient passkeys, invites, sessions (App lead, 9:00–11:00 PM)

**Files:** Create `Managers/EnrollmentManager.cs`, `Services/IFido2Service.cs` (+ `Fido2Service.cs`), `Controllers/PatientPasskeyController.cs`, `wwwroot/js/passkey.js`, `Pages/Patient/Enroll.cshtml(.cs)`, `Pages/Patient/SignIn.cshtml(.cs)`; modify `Program.cs`. Test: `EZEHR.Tests/Auth/PatientPasskeyTests.cs`.

**Interfaces:**
- Consumes: `RecordManager.CreatePatientKeyAsync` (Task 4); `GrantManager.CreateAppGrantAsync` (Task 6; stub its signature until it merges).
- Produces:
  - Cookie scheme `"Patient"`: 12 h, HttpOnly, Secure, SameSite=Lax, and the claim `patient_id`. Policy `"Patient"`. `AuthorizeFolder("/Patient", "Patient")`, with Enroll and SignIn anonymous.
  - JSON endpoints (`PatientPasskeyController`, Fido2.AspNet). The Fido2 server is configured from `AppOptions.Origin`: `ServerDomain` is its host, and `Origins` is `{Origin}`. Each ceremony's options (its challenge) wait in the ASP.NET Core session (in-memory cache, 5 minutes) between its two calls and are removed when the second call reads them, so a challenge answers once.
    - `POST /api/patient/passkey/create-registration-options { inviteCode, displayName }` (`CreateRegistrationOptionsAsync`). Discoverable credential, `UserVerification = Required`, attestation `none`, a random 32-byte user handle. An invalid or used invite returns 400. Invites carry no name, so the Enroll page asks for it.
    - `POST /api/patient/passkey/complete-registration { attestation, kidneyCheckOptIn }` (`CompleteRegistrationAsync`). The invite code comes from the session, not the body. In one transaction it redeems the invite, creates the patient (with the wrapped key) and stores the credential; then it creates the Kidney Check grant **only if** opted in, and signs the patient in. A used invite creates nothing.
    - `POST /api/patient/passkey/create-sign-in-options` (usernameless, `CreateSignInOptions`) and `/complete-sign-in { assertion }` (`CompleteSignInAsync`).
    - A signed-in patient who opens another invite link gets "Add your records from {provider}?" on the Enroll page, which posts to its `Link` handler (`EnrollmentManager.LinkInviteAsync`). This replaces a separate `redeem-invite` endpoint.
  - `passkey.js`: buttons with `data-passkey="enroll" | "sign-in"`. It uses `PublicKeyCredential.parseCreationOptionsFromJSON` and `parseRequestOptionsFromJSON` when available (base64url helpers otherwise), and builds the response JSON itself.
  - `Pages/Patient/Home` is a placeholder (name and sign out) until Task 11.
- The Enroll page shows `Copy.KidneyOptIn` with a checkbox, plus one "Create passkey" button. The SignIn page has one "Sign in with passkey" button.

- [x] **Write failing tests** (Fido2 behind `IFido2Service`; `FakeFido2Service` makes real options and stubs verification, and `EzehrFactory` always registers it):
  - `PatientPasskeyTests`: `Invite_single_use`, `Invite_used_between_options_and_completion_creates_nothing`, `Invite_expired`, `Rejected_passkey_leaves_invite_usable`, `OptIn_creates_app_grant`, `No_optin_no_grant`, `Enroll_creates_wrapped_dek_and_link`, `Redeem_second_invite_adds_link`, and sign-in with a known, unknown and badly signed passkey.
  - `PatientPasskeyFlowTests` (through HTTP): enrollment signs the patient in, patient pages redirect to SignIn without a cookie, a challenge answers once, sign-in on a new browser.
- [x] Run (FAIL), implement, run (PASS).
- [ ] On the deployed site, on a phone: enroll from an invite, then sign in again. Expected: no password field ever appears.
- [ ] Commit `feat: patient passkeys, invites, sessions`.

### Task 8: Clinician sign-in (Identity lead, 10:00–11:00 PM, then the 11 PM checkpoint)

**Files:** Modify `Managers/CallerManager.cs`; create `Pages/Clinician/Index.cshtml(.cs)`; modify `Program.cs` and `appsettings.json` (the `AzureAd` section). Test: `EZEHR.Tests/Auth/ClinicianAuthTests.cs`.

**Interfaces:**
- Produces:
  - `AddMicrosoftIdentityWebApp(Configuration.GetSection("AzureAd"), openIdConnectScheme: "ClinicianOidc", cookieScheme: "Clinician")`. Sign-in only, ID token, no secret; if the library insists on a secret, set `ResponseType = "id_token"`.
  - On `OnTokenValidated`, add the claim `session_started_at`. The cookie is set to `ExpireTimeSpan = 10h`, `SlidingExpiration = false`, and the OIDC `UseTokenLifetime = false`.
  - `ClinicianCheck CallerManager.EvaluateClinician(ClaimsPrincipal user)` (trusted tenants come from `AppOptions`), returning `(bool Ok, Party? Party, DateTimeOffset? SessionStartedAt, string? ErrorCopy)`:
    - `tid` (`http://schemas.microsoft.com/identity/claims/tenantid`) not trusted → `Copy.UntrustedOrg`
    - no `Clinician` role (claim type `roles`) → `Copy.NotAClinician`
    - otherwise a `Party` with `OrgDirectory.ClinicianUri(tid, oid)` (`urn:entra:{tid}:{oid}`), display name from `name`, and the organization from `trustedTenants`
  - Policy `"Clinician"` uses scheme `"Clinician"`. `AuthorizeFolder("/Clinician", "Clinician")`, except `Index` and `AccessEnded`.
  - `Caller CallerManager.FromClinician(ClaimsPrincipal)` builds the `VerifiedCaller` for the gate.

- [ ] **Write failing tests** on `Evaluate`, with hand-built `ClaimsPrincipal`s:
  - `Valid_clinician`.
  - **(Review Focus 5)** `Missing_role_returns_NotAClinician` and `Untrusted_tenant_returns_UntrustedOrg`.
  - `Session_started_at_parsed`.
- [ ] Run (FAIL), implement, run (PASS). Deploy, then sign in as Dr. Rivera **with the security key**. Expected: the page shows "Dr. Rivera · Riverside Primary Care · signed in until {time}".
- [ ] Commit `feat: clinician Entra sign-in with 10-hour shift`.
- [ ] **11 PM CHECKPOINT:**
  - If deployed sign-in doesn't work, do Task 8b now.
  - If EZCA issuance doesn't work, set `CertificateAuthority:Mode=Local`, run `certs issue --ca local`, and keep going.

### Task 8b (only on checkpoint failure): Clinician passkeys in our own directory

- [ ] Reuse Task 7's passkey flow with the `local_clinicians` table, seeded with Dr. Rivera, Riverside Primary Care, `urn:local:rivera`. Register the security key as Dr. Rivera's passkey.
- [ ] `CallerManager.EvaluateClinician` reads the local cookie instead. Everything downstream is unchanged.
- [ ] Test `Local_clinician_yields_party`. Commit `feat: local clinician passkeys (fallback)`. The pitch then says "plugs into Entra ID; shown with our own directory."

### Task 9: Provider simulator and seed data (App lead, 11:00 PM–1:00 AM)

**Files:** Create `EZEHR.Tools/Commands/ProviderCommand.cs`, `EZEHR.Tools/Net/NetworkClient.cs`, `EZEHR.Tools/seed/{lakeside,northwind,maple,riverside}.json`.

**Interfaces:**
- Consumes: `RecordSigner` (Task 2); the network endpoints (Task 5). Build against the Interfaces block until Task 5 merges.
- Produces:
  - `NetworkClient.Create(string baseUrl, string pfxPath)`: an `HttpClient` with the client certificate, `SslProtocols = Tls12` (TLS renegotiation needs 1.2), HTTP/1.1, and `Expect100Continue = true`.
  - `ezehr-tools provider invite <lakeside|northwind|maple|riverside> <providerPatientId>` prints the enrollment URL.
  - `ezehr-tools provider push <provider> <providerPatientId>` signs each resource in `seed/<provider>.json` with `iat` = the resource's clinical date at 12:00 UTC. It POSTs one request per encounter date, so each request stays well under 100 KB.
- **Seed data** (FHIR R4). Patient Sam Taylor, born 1984-05-17. Provider patient IDs: `LUC-20417`, `NWH-88213`, `MSF-0932`, and `RPC-1107` for Riverside.

  | Provider | Date | Resources |
  |---|---|---|
  | Maple Street Family Medicine | 2024-09-12 | Encounter (annual physical); Condition "Essential hypertension"; MedicationStatement "amlodipine 5 mg daily"; Observation eGFR **96** |
  | Northwind Workplace Health | 2025-03-04 | Observation blood pressure 138/88; Observation eGFR **88** |
  | Lakeside Urgent Care | 2025-08-21 | Encounter (low back pain); MedicationStatement "ibuprofen 600 mg three times daily as needed"; Observation eGFR **81** |
  | Northwind Workplace Health | 2026-03-10 | Observation blood pressure 146/92; Observation eGFR **72** |
  | Lakeside Urgent Care | 2026-09-15 | Encounter (back pain follow-up); Observation eGFR **63** |

  - eGFR uses LOINC `98979-8`, unit `mL/min/1.73m2`, with a reference range whose low is 60. That's 13 resources in total.
  - **`seed/riverside.json`** is the visit documentation, pushed **after** Dr. Rivera's visit, not at setup. Dated the demo day:
    - Encounter (new-patient visit)
    - `Communication` with `sender.display` = "Dr. Rivera" and payload "Stop regular ibuprofen. Repeat kidney labs and urine albumin; refer to nephrology if the decline is confirmed."
    - `ServiceRequest` "Referral to nephrology", `requester.display` = "Dr. Rivera", status `active`
    - All 3 are signed with `riverside.pfx`. They are not among the 13 above.

- [ ] Manual check on the deployed site:
  1. Create three invites (plus a Riverside invite for the visit).
  2. Enroll on the phone with the first invite (opt in to Kidney Check), then redeem the other two.
  3. Push all three providers.
  4. Later, redeem the Riverside invite and push `riverside`: 16 entries, and the plan shows `Copy.WrittenBy("Dr. Rivera", "Riverside Primary Care")`.

  Expected: Sam's record holds 13 entries. **(Review Focus 4)** Pushing again gives `stored:0`.
- [ ] Commit `feat: provider simulator and seed data`.

### Task 10: Kidney Check example app (App lead, 1:00–2:00 AM)

**Files:** Create `EZEHR.Tools/Commands/KidneyCheckCommand.cs` and `Services/KidneyRule.cs`. Test: `EZEHR.Tests/Services/KidneyRuleTests.cs`.

**Interfaces:**
- Consumes: `NetworkClient` (Task 9); `GET read-egfr-labs` and `POST write-signed-note` (Task 6); `RecordSigner`.
- Produces:
  - `static KidneyResult KidneyRule.Evaluate(IReadOnlyList<(DateOnly Date, double Egfr, string Source)> points)`, returning `(bool Flag, int PercentDrop, int ProviderCount)`.
    - It flags when there are at least 2 points from at least 2 sources and the drop from the earliest to the latest point is more than 25%.
    - `PercentDrop` is rounded.
    - **That's the whole rule. No clinical guidelines.**
  - `ezehr-tools kidney-check run <patientId>` connects with `kidney-check.pfx` and GETs the labs.
    - When the rule flags, it POSTs a FHIR `Communication` (the endpoint accepts only that type): `payload[0].contentString` = `Copy.KidneyNote(...)`, `sender.display` = "Kidney Check", `id` = `kidney-check`, signed with the app's certificate.
    - On 403, it prints that access was revoked.

- [ ] **Write failing tests:**
  - `Seed_series_flags_34_percent_3_providers`.
  - `Single_source_no_flag`.
  - `Drop_25_exactly_no_flag`.
  - `One_point_no_flag`.
- [ ] Run (FAIL), implement, run (PASS).
- [ ] Manual check: run it twice; there is exactly 1 note **(Review Focus 4)**. Revoke the grant in Sam's app and run again; the result is 403, and Sam's activity shows the denial.
- [ ] Commit `feat: Kidney Check example app`.

### Task 11: Patient UI (App lead, 2:00–6:00 AM)

**Files:** Create `Pages/Patient/Home`, `Share`, `Activity`, `Grants`, `_ActivityPartial`.

These are checked manually on a phone. The UI isn't unit-tested, by choice; testing time goes to the access check and the crypto.
- [ ] **Home:** the combined timeline grouped by date.
  - Each entry shows `Copy.SignedBy(org)`, or `Copy.WrittenBy(...)` when the entry names an author (Dr. Rivera's plan, signed by Riverside).
  - The Kidney Check note shows as a card at the top.
  - An "Export my record" button (Task 14).
- [ ] **Share:**
  - Pick a duration (5 min, 1 h, 4 h, end of day). The page shows the code as `XXXX-XXXX` in large type, with a QRCoder PNG of the code.
  - The page polls `_ActivityPartial` every 2 s with htmx (`hx-get`, `hx-trigger="every 2s"`).
  - A pending request shows a full-screen `Copy.ApprovePrompt(...)` with Approve and Deny buttons, which post to `GrantManager.ApproveOrDenyAsync`.
- [ ] **Activity:** a live list, in plain language:
  - `"Dr. Rivera, Riverside Primary Care, viewed your record · 2:14 PM"`
  - `"Kidney Check read your lab results"`
  - `Copy.BrokerBlocked(purpose)`, in red
- [ ] **Grants:** each grant has a Revoke button. The app grant shows `Copy.AppGrantLine("Kidney Check", "lab results only", date)`. Clinician grants show "until 5:00 PM".
- [ ] **Layout:** works at 390 px width, with tap targets of at least 44 px.
- [ ] Commit `feat: patient home, share, activity, grants`.

### Task 12: Clinician UI and the share flow (Identity lead, 3:00–5:00 AM)

**Files:** Create `Pages/Clinician/Redeem`, `Wait`, `Record`, `AccessEnded`. Test: `EZEHR.Tests/Flows/ShareFlowTests.cs` (WebApplicationFactory with test authentication handlers for both schemes).

- [ ] **Write the failing integration test** `Full_share_flow`:
  1. The patient creates an offer (5 min).
  2. The clinician redeems it: pending.
  3. Loading the record page redirects to Wait (`GrantPending`).
  4. The patient approves.
  5. The record page returns 200, with 13 or more entries and the Kidney Check note.
  6. Activity contains Dr. Rivera's read.
  7. The patient revokes.
  8. The record page redirects to AccessEnded **(Review Focus 3)**.
  9. Activity contains the denial.
- [ ] Run (FAIL), implement, run (PASS).
- [ ] **Redeem:** a code field. On submit it calls `GrantManager.Redeem` with the clinician `Party` and goes to Wait.
- [ ] **Wait:** polls the grant status with htmx every 2 s, showing "Waiting for Sam to approve…", then goes to Record.
- [ ] **Record:**
  - Every load calls `RecordManager.ReadFullRecordAsync` (`AccessAction.ReadFullRecord`).
  - Header: "Sam Taylor · access until 5:00 PM".
  - The combined timeline with badges, and the Kidney Check note at the top.
  - **Read-only.** There is no note form: Dr. Rivera's plan and referral arrive from Riverside's system (`provider push riverside`, Task 9).
  - Any `GrantExpired`, `GrantRevoked`, `NoGrant` or `ShiftEnded` result redirects to AccessEnded.
- [ ] **AccessEnded:** shows `Copy.AccessEnded`. A `ShiftEnded` result also shows a "Sign in again" link.
- [ ] Commit `feat: clinician pages and share flow`.

### Task 13: Data broker, the Particle moment (Identity lead, 5:00–6:00 AM)

**Files:** Create `EZEHR.Tools/Commands/BrokerCommand.cs`.

- [ ] `ezehr-tools broker request <patientId> --cert none|self` calls `GET /api/network/patients/{id}/read-full-record?purpose=treatment`. With `self` it uses `.pki/broker.pfx`. It prints the status code and body verbatim, in large console text for the video.
- [ ] Manual check: run both modes. Expected: two 401s, and two red `Copy.BrokerBlocked("treatment")` lines appear live on Sam's phone. The integration test from Task 6 covers the server side.
- [ ] Commit `feat: unverified broker demo`.

### Task 14: Export my whole record (App lead, 5:00–6:00 AM)

**Files:** Modify `Pages/Patient/Home` (an export handler). Test: `EZEHR.Tests/Flows/ExportTests.cs`.

**Produces:** a FHIR R4 `Bundle` with `type: "collection"`, plus one `Provenance` per entry:
- `target` → the resource
- `recorded` = `SignedAt`
- `agent[0].who.display` = `"{DisplayName}, {OrgName}"`
- `signature[0]`: `sigFormat` `"application/jose"`, `data` = base64 of the JWS, `type` = the ASTM origin-signature code `1.2.840.10065.1.12.1.1`

The download is named `sam-taylor-record.json`.

- [ ] Test `Export_bundle_has_provenance_per_entry_and_signatures_verify`: Provenance count = resource count, and every `signature.data` decodes to a JWS that `RecordVerifier` accepts. Run (FAIL), implement, run (PASS).
- [ ] Commit `feat: FHIR export with signed provenance`.

### Task 15: Freeze, deploy, rehearse, record, submit (both, 8:00 AM–1:00 PM)

- [ ] **8:00 AM feature freeze.** `dotnet test` must be all green, then deploy. Have a fresh Claude Code session review the whole branch, focused on the access check, certificate handling and the copy rules. Fix only real defects.
- [ ] **Demo reset script** (`EZEHR.Tools`, command `demo reset`):
  1. Clear the production database with `Db.DeleteAllDataAsync()`. The tool connects straight to Azure SQL as the teammate running it (`az login`, then `Authentication=Active Directory Default`), so that laptop's IP must be in the server firewall. The site has no reset endpoint.
  2. Create 3 invites and print the URLs.
  3. After phone enrollment, push all 3 providers and run Kidney Check. Riverside's invite is redeemed and its push run live, after the visit scene.

  Rehearse it twice. It must take under 5 minutes.
- [ ] **Video plan (about 5:00):**

  | Time | Scene |
  |---|---|
  | 0:00–0:35 | The problem: records are scattered, so slow problems are missed, and Epic blocked "treatment" requests it couldn't trust (Particle, 2024). Pitch line. |
  | 0:35–1:20 | Sam's phone: passkey sign-in; the combined timeline with "Signed by" badges; the Kidney Check note and its revocable grant ("apps on an open platform"). |
  | 1:20–2:30 | Share: Sam's code; Dr. Rivera signs in to Entra by **tapping the security key**; enters the code; Sam approves; the combined record (read-only). Then Riverside's system pushes the plan and referral: "Written by Dr. Rivera · signed by Riverside Primary Care ✓" appears on Sam's phone. |
  | 2:30–3:10 | The broker: no certificate, then a self-made certificate; both blocked; red lines appear live on Sam's phone. |
  | 3:10–3:40 | Sam revokes; the doctor's page shows "access ended"; the export shows signed provenance. |
  | 3:40–4:40 | Architecture slide: people use passkeys and security keys; organizations and apps use **EZCA certificates** over mutual TLS; one access check; Key Vault envelope encryption; no standing access, every attempt logged. |
  | 4:40–5:00 | Close: pitch line; what's next (break-glass, Verified ID recovery, more hospitals as trusted tenants). |

- [ ] **Q&A prep:** rehearse the spec's section 10 "Honest limits."
- [ ] **Submission write-up:** problem, solution, an architecture diagram, how to run it, the honest limits, and what's next. **Submit by 1:00 PM.**

### Stretch goals (only if ahead at 6:00 AM; in this order; each must fit in the video)

- **S1 Conditional Access** (needs the P2 trial; about 30 minutes): a policy requiring the "phishing-resistant MFA" authentication strength for the EZEHR app. Show it on one slide.
- **S2 Break-glass:** an "Emergency access" button for a verified clinician creates a 1-hour grant without patient approval. It requires a reason and writes an audit event, and Sam's activity marks it "Emergency access."
- **S3 EZCA certificate on the security key for Entra certificate-based sign-in.** Generate the key on the device with `ykman piv`, have EZCA issue the certificate, upload EZCA's CA to Entra's certificate authorities, and enable certificate-based authentication. No app code changes.
- **S4 Verified ID recovery:** recover a lost phone with an Entra Verified ID presentation, then register a new passkey.
