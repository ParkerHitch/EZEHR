# Organization Registry and Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move organizations (providers and apps) into an `organizations` table that maps each platform URI to its certificate CN, display name and Entra tenant, then let a provider pull the audit trail of what its own clinicians and system did, filtered by clinician, patient or time.

**Architecture:** `organizations` replaces both the hard-coded `OrgDirectory.KnownOrganizations` and the `App:TrustedTenants` setting. `OrganizationManager` loads it into an in-memory `OrganizationDirectory`, which the request's code uses for certificate, URI and tenant lookups. Every `Party` gains an `OrgUri`, the organization that vouches for it. Every audit row stores it as `actor_org_uri`, and two network-door endpoints (mutual TLS, the provider's EZCA certificate) return events where `actor_org_uri = caller`.

**Tech Stack:** .NET 10, ASP.NET Core controllers and Razor Pages, Dapper over Azure SQL, xUnit + Testcontainers SQL Server.

**Spec:** No separate spec. The "Design decisions" section below is the spec; background is in `docs/auth-flows.md` and `docs/superpowers/specs/2026-11-13-hackathon-design.md`.

## Design decisions

- **`organizations` is the only source of truth** for who an organization is: `uri` (key, e.g. `urn:provider:riverside`), `kind` (`Provider` or `App`), `display_name`, `certificate_cn`, `entra_tenant_id` (nullable, providers only, unique). There is no registration endpoint yet: sysadmins add or edit rows with SQL.
- **The five demo organizations are seeded by `Db.EnsureSchemaAsync` into an empty table only** (a new database). Once the table has rows it is never touched, so a sysadmin's edits (such as Riverside's tenant id) and deletions survive restarts; deleting a row is how an organization is cut off, since EZEHR doesn't check revocation. *(Revised after the final review: the first version re-added any missing demo org on every start, which undid that kill switch and could stop startup on a reused CN.)* Tenant ids are never seeded; whoever sets up an environment runs the `UPDATE`. `demo reset` leaves the table alone.
- **`OrgDirectory` (static) keeps building URIs and CNs** from slugs, as the style guide requires, and holds the demo list that is both the seed and the tools' offline directory (the tools have no database).
- **No cache.** The server loads the directory (a handful of rows) once per request that needs it, so a sysadmin's edit takes effect on the next request.
- **The audit records each actor's organization when the event happens** (`actor_org_uri`) and never recomputes it. A clinician who later leaves, or a tenant that is remapped, keeps their history under the org they acted for.
- **The auditor is the organization's system**, using its EZCA certificate at the existing network door. There is no human auditor role.
- **An organization sees only events whose actor it vouches for:** its clinicians (granted and refused) and its own system's calls. It never sees other parties' events for the same patient.
- **Patients are reported by EZEHR id, plus the caller's own patient id** (from `provider_links`) when the patient is linked. No patient names.

## Global Constraints

- Follow `docs/style-guide.md`: no `var`, block bodies only, primary-constructor DI, `Id` not `ID`, file-scoped namespaces, `dotnet csharpier format .` before each commit.
- Endpoints are kebab-case verb phrases; action = endpoint in PascalCase + `Async`; manager method shares the name; result type = name + `Result`, response = name + `Response`.
- Controllers and PageModels: get raw identity → `CallerManager` → one manager call → map result. Network denials go through the existing `NetworkController.DenialResult`.
- Managers take a `Caller` and plain values (or a `Models/` record), never a request object; they query `Db` with Dapper.
- SQL is parameterized raw-string literals. Identifier columns use `COLLATE Latin1_General_100_BIN2`; the one exception is `certificate_cn`, which is case-insensitive like the DNS name it is. `SELECT` aliases match the record's parameter names.
- New columns and indexes on **existing** tables go only in `Db.Migrations` (Task 2), never in the `CREATE TABLE` text, so fresh and deployed databases take the same path.
- Tests need Docker (or `EZEHR_TEST_SQL`) for `SqlServer.CreateDatabaseAsync()`.

## Review Focus

1. **Sysadmin edits must survive.** A restart (re-running `EnsureSchemaAsync`) must not overwrite Riverside's tenant id or display name, nor duplicate a seeded row, and `demo reset` must not empty `organizations`. (Revised 2026-11-14: `demo reset` deletes Northwind's row only, so `stage` can onboard Northwind through `register-provider` on camera; every other row, and Riverside's tenant, stays.) → Task 1 `EnsureSchema_seeds_demo_organizations_once_and_keeps_edits`.
2. **The deployed database already has `audit_events` and `grants`.** `EnsureSchemaAsync` must add the new columns and index to those existing tables and stay safe to run on every start. → Tasks 2–3 `EnsureSchema_adds_org_columns_to_existing_tables`.
3. **Cross-organization leakage.** Lakeside filtering by a patient that Riverside's clinician read must get none of Riverside's events, and vice versa. → Task 4 `Provider_sees_only_its_own_clinicians_and_systems`.
4. **A patient with two patient ids at the same provider** must appear once per event, not once per link. → Task 4 `Patient_with_two_ids_at_one_provider_appears_once`.
5. **Paging boundary.** A page that exactly fills `limit` must return `NextAfterId = null` when nothing follows, and walking pages must neither skip nor repeat events. → Task 4 `Pages_by_after_id`.

---

### Task 1: The `organizations` table replaces `KnownOrganizations` and `TrustedTenants`

Changes where organizations come from, not what they are: every existing test should still pass, with Riverside's tenant now set in the database.

**Files:**
- Create: `Models/Organization.cs`, `Models/OrganizationDirectory.cs`, `Managers/OrganizationManager.cs`
- Modify: `Services/OrgDirectory.cs` (drop `KnownOrganizations`, `FindByCertificate`, `FindBySubjectUri`; add the demo list)
- Modify: `Data/Db.cs` (`organizations` in `Schema`; seeding in `EnsureSchemaAsync`)
- Modify: `Managers/CallerManager.cs` (async; tenant lookup from the table)
- Modify: `Services/RecordVerifier.cs` (`Verify` takes the directory)
- Modify: `Managers/RecordManager.cs` (~lines 384, 418), `Managers/EnrollmentManager.cs:199` (`ProviderName`)
- Modify: `Controllers/NetworkController.cs` (5 actions), `Pages/Clinician/Index.cshtml.cs`, `Record.cshtml.cs`, `Redeem.cshtml.cs`, `Wait.cshtml.cs`
- Modify: `Models/AppOptions.cs` (remove `TrustedTenants`), `Program.cs` (remove the `TrustedTenants` `PostConfigure`; `AddScoped<OrganizationManager>()`)
- Modify: `EZEHR.Tools/Commands/CertsCommand.cs:73`, `ProviderCommand.cs:126`, `KidneyCheckCommand.cs:127`
- Modify (tests): `TestServices.cs`, `Data/SqlServer.cs`, `Auth/TestClinicianAuthHandler.cs`, `Auth/ClinicianAuthTests.cs`, `Auth/ClinicianSignInFlowTests.cs`, `Flows/ShareFlowTests.cs`, `Flows/PatientPagesTests.cs`, `Auth/PatientPasskeyFlowTests.cs`, `Auth/PatientPasskeyTests.cs`, `Pki/PkiTests.cs`, `Services/TimelineBuilderTests.cs`, `Data/DbTests.cs`, `Api/NetworkAccessTests.cs`
- Create (test): `EZEHR.Tests/Models/OrganizationDirectoryTests.cs`
- Docs: `docs/auth-flows.md` §2, §3, §4b, §6

**Interfaces:**
- Produces:
  - `record Organization(string Uri, PartyKind Kind, string DisplayName, string CertificateCommonName, Guid? EntraTenantId)` with `Party ToParty()` (Kind, Uri, DisplayName, DisplayName).
  - `sealed class OrganizationDirectory(IEnumerable<Organization> organizations)` with `Party? FindByCertificate(X509Certificate2 leaf)` (CN, case-insensitive), `Party? FindByUri(string uri)`, `Party? FindProviderByTenant(Guid tenantId)`.
  - `OrgDirectory.DemoOrganizations : IReadOnlyList<Organization>` (the five current orgs, no tenants) and `OrgDirectory.Demo : OrganizationDirectory` built from it.
  - `OrganizationManager(Db db)` with `Task<OrganizationDirectory> LoadDirectoryAsync()`.
  - `CallerManager(TimeProvider time, IOptions<AppOptions> options, OrganizationManager organizationManager)` with `Task<Caller> FromCertificateAsync(X509Certificate2?)`, `Task<ClinicianCheck> EvaluateClinicianAsync(ClaimsPrincipal)`, `Task<Caller> FromClinicianAsync(ClaimsPrincipal)`; the sync versions are removed.
  - `RecordVerifier.Verify(Guid entryId, string jws, TrustAnchors trustAnchors, OrganizationDirectory directory)`.
  - `TestDatabase.SetEntraTenantAsync(string organizationUri, Guid tenantId)`; `TestClinicianAuthHandler.InstallAsync(EzehrFactory app) -> Task<WebApplicationFactory<Program>>`.

- [ ] **Step 1: Write the failing tests**

`OrganizationDirectoryTests` (pure, over an explicit list containing a provider with tenant `T` and an app):
- `FindByCertificate_matches_cn_case_insensitively`: a cert issued for `"Riverside.Providers.EZEHR"` finds `urn:provider:riverside`.
- `FindByCertificate_unknown_cn_is_null`.
- `FindProviderByTenant_finds_only_the_mapped_provider`: `T` → riverside; another GUID → null.
- `FindByUri_round_trips`: returns a `Party` equal to `organization.ToParty()`.

`DbTests`:
- `EnsureSchema_creates_all_tables`: add `"organizations"` to `expected`.
- `EnsureSchema_seeds_demo_organizations_once_and_keeps_edits`: 5 rows after creation; `UPDATE organizations SET entra_tenant_id = @t, display_name = N'Riverside Health' WHERE uri = 'urn:provider:riverside'`; call `EnsureSchemaAsync()` → still 5 rows, both edits kept; `DeleteAllDataAsync()` → still 5 rows.
- `Tenant_belongs_to_one_provider_only`: setting the same tenant on two providers throws `SqlException` (`Db.IsUniqueViolation`); setting a tenant on `urn:app:kidney-check` throws `SqlException`.

`ClinicianAuthTests` becomes `IAsyncLifetime` over a test database: `SetEntraTenantAsync("urn:provider:riverside", Guid.Parse(RiversideTenant))`, `CallerManager(_clock, Options.Create(new AppOptions()), new OrganizationManager(db))`, every call awaited. Existing tests keep their assertions (`Untrusted_tenant_returns_UntrustedOrg` now means "no row has that tenant"). Add:
- `Organization_name_comes_from_the_organizations_row`: update Riverside's `display_name` to `Riverside Health` → `check.Party!.OrgName == "Riverside Health"`.
- `Non_guid_tenant_returns_UntrustedOrg`: `tid = "not-a-guid"` → `Copy.UntrustedOrg`.

`NetworkAccessTests.Organization_added_by_sysadmin_is_recognized`: `INSERT INTO organizations VALUES ('urn:provider:harbor', 'Provider', N'Harbor Clinic', 'harbor.providers.ezehr', NULL)`, issue `harbor.providers.ezehr`, `POST api/network/create-enrollment-invite` → 201.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EZEHR.Tests`
Expected: build FAIL (`OrganizationDirectory`, `OrganizationManager`, `SetEntraTenantAsync` missing).

- [ ] **Step 3: Implement the table and seeding**

Add to `Schema`:

```sql
IF OBJECT_ID(N'dbo.organizations') IS NULL
CREATE TABLE dbo.organizations(
    uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
    kind NVARCHAR(32) NOT NULL CONSTRAINT ck_organizations_kind CHECK (kind IN (N'Provider', N'App')),
    display_name NVARCHAR(200) NOT NULL,
    certificate_cn NVARCHAR(200) COLLATE Latin1_General_100_CI_AS NOT NULL CONSTRAINT uq_organizations_cn UNIQUE,
    entra_tenant_id UNIQUEIDENTIFIER NULL,
    CONSTRAINT ck_organizations_tenant_provider CHECK (entra_tenant_id IS NULL OR kind = N'Provider'),
    INDEX uq_organizations_tenant UNIQUE (entra_tenant_id) WHERE entra_tenant_id IS NOT NULL);
```

After `Schema`, `EnsureSchemaAsync` runs, for each of `OrgDirectory.DemoOrganizations`, one parameterized `IF NOT EXISTS (SELECT 1 FROM dbo.organizations WHERE uri = @Uri) INSERT ...` with `entra_tenant_id` NULL. `DeleteAllDataAsync` is unchanged (it must not list `organizations`).

- [ ] **Step 4: Implement the directory and switch every lookup to it**

- `OrgDirectory`: replace `KnownOrganizations` with `DemoOrganizations` (same five orgs, same names and CNs) and `Demo`; remove `FindByCertificate`/`FindBySubjectUri`.
- `OrganizationManager.LoadDirectoryAsync`: `SELECT uri AS Uri, kind AS Kind, display_name AS DisplayName, certificate_cn AS CertificateCommonName, entra_tenant_id AS EntraTenantId FROM organizations`.
- `CallerManager`: `FromCertificateAsync` uses `directory.FindByCertificate`. `EvaluateClinicianAsync`: `Guid.TryParse(tenantId)`, then `directory.FindProviderByTenant`; null → `Copy.UntrustedOrg`. The clinician's `OrgName` is that provider's `DisplayName`.
- `RecordVerifier.Verify` takes the directory and uses `FindByCertificate`. `RecordManager` loads the directory once per public method before verifying. `EnrollmentManager.ProviderName` becomes an instance method that loads the directory.
- Controllers and pages: `await callerManager.FromCertificateAsync(...)` / `EvaluateClinicianAsync` / `FromClinicianAsync` (handlers that were sync become `async Task<IActionResult>`).
- Remove `AppOptions.TrustedTenants` and its `PostConfigure` in `Program.cs`; register `OrganizationManager`.
- Tools: `CertsCommand` iterates `OrgDirectory.DemoOrganizations` CNs; `ProviderCommand.IsProvider` uses `OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri(slug))?.Kind == PartyKind.Provider`; `KidneyCheckCommand` passes `OrgDirectory.Demo` to `RecordVerifier.Verify`.
- Tests: `TestServices` builds `Organizations = new OrganizationManager(db)` and passes it to `RecordManager`. `SqlServer.TestDatabase.SetEntraTenantAsync` runs the `UPDATE`. `TestClinicianAuthHandler.InstallAsync` awaits `app.Database.SetEntraTenantAsync("urn:provider:riverside", Guid.Parse(RiversideTenant))` instead of setting `App:TrustedTenants`; its three callers await it. Static lookups in tests use `OrgDirectory.Demo.FindByUri` / `FindByCertificate`.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test EZEHR.Tests`
Expected: PASS, 0 failures.

- [ ] **Step 6: Update docs**

`docs/auth-flows.md`:
- §2 "What EZEHR trusts": item 1 becomes "Microsoft Entra ID, but only for tenants that a provider's row in `organizations` names". Item 2 adds "and only for CNs that have a row in `organizations`".
- §3: the provider and app rows say "CN found in `organizations`" instead of `OrgDirectory`.
- §4b: replace the `TrustedTenants` line with the sysadmin SQL:
  ```sql
  UPDATE organizations SET entra_tenant_id = '<tenant id>' WHERE uri = 'urn:provider:riverside';
  ```
  and a short "Adding an organization" snippet (`INSERT INTO organizations(uri, kind, display_name, certificate_cn) VALUES (...)`), noting that the CN must also be issued by EZCA.
- §6: `OrgDirectory` box becomes "`OrganizationDirectory`, loaded from `organizations`".

Add a **Deployment note** under §4b: before deploying, copy the tenant id out of the App Service setting `App__TrustedTenants__…` (the key, with underscores turned back into hyphens). After the deploy starts and seeds the table, run the `UPDATE` above against prod, then delete the setting. Dr. Rivera is refused between the deploy and the `UPDATE`. *(Human step: needs portal and database access.)*

- [ ] **Step 7: Commit**

```bash
dotnet csharpier format .
git add -A Models Services Managers Data Controllers Pages Program.cs EZEHR.Tools EZEHR.Tests docs/auth-flows.md
git commit -m "feat: organizations table replaces the hard-coded directory and TrustedTenants"
```

---

### Task 2: Organization identity on `Party`, schema migrations, grants keep it

**Files:**
- Modify: `Models/Party.cs`, `Models/Organization.cs` (`ToParty`), `Managers/CallerManager.cs` (clinician party)
- Modify: `Managers/GrantManager.cs` (`InsertAsync` ~line 470, `GrantRow` ~line 500)
- Modify: `Data/Db.cs` (new `Migrations`)
- Modify (add the fifth `Party` argument): `EZEHR.Tests/Access/AccessTests.cs`, `EZEHR.Tests/Auth/ClinicianAuthTests.cs`, `EZEHR.Tests/Flows/PatientPagesTests.cs`, `EZEHR.Tests/Pki/PkiTests.cs`
- Test: `EZEHR.Tests/Auth/ClinicianAuthTests.cs`, `EZEHR.Tests/Data/DbTests.cs`, `EZEHR.Tests/Access/AccessTests.cs`, `EZEHR.Tests/Models/OrganizationDirectoryTests.cs`

**Interfaces:**
- Consumes: `Organization`, `OrganizationDirectory.FindProviderByTenant` (Task 1).
- Produces:
  - `record Party(PartyKind Kind, string SubjectUri, string DisplayName, string OrgName, string OrgUri)`: `Organization.ToParty()` sets `OrgUri = Uri`; a clinician's `OrgUri` is their tenant's provider `Uri`.
  - `Db.Migrations` (private `string[]`), run in order by `EnsureSchemaAsync` after `Schema` and the seed, **one `SqlCommand` per entry** (SQL Server compiles a whole batch first, so a statement that uses a column added earlier in the same batch fails). Task 3 appends to it.
  - Column `grants.grantee_org_uri NVARCHAR(200) BIN2 NULL`.

- [ ] **Step 1: Write the failing tests**

- `ClinicianAuthTests.Valid_clinician` expects `new Party(PartyKind.Clinician, $"urn:entra:{RiversideTenant}:{RiveraObjectId}", "Dr. Rivera", "Riverside Primary Care", "urn:provider:riverside")`.
- `OrganizationDirectoryTests.Organizations_vouch_for_themselves`: for each of `OrgDirectory.DemoOrganizations`, `ToParty().OrgUri == Uri`.
- `DbTests.EnsureSchema_adds_org_columns_to_existing_tables`: run `ALTER TABLE dbo.grants DROP COLUMN grantee_org_uri`, call `EnsureSchemaAsync()` twice, assert `SELECT COL_LENGTH('dbo.grants', 'grantee_org_uri')` is not null.
- `AccessTests.Grant_keeps_grantee_org_uri`: create Rivera's grant as the existing tests in that file do, read it back with `Grants.ListForGranteeAsync(_sam, Rivera.SubjectUri)`, `Assert.Equal(Rivera, grants[0].Grantee)`. Give `Rivera`/`Other` `"urn:provider:riverside"` and `KidneyCheck` `"urn:app:kidney-check"` as `OrgUri`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EZEHR.Tests --filter "FullyQualifiedName~ClinicianAuthTests|FullyQualifiedName~OrganizationDirectoryTests|FullyQualifiedName~DbTests|FullyQualifiedName~AccessTests"`
Expected: build FAIL (`Party` has no 5-argument constructor).

- [ ] **Step 3: Implement**

- `Party`: add `string OrgUri` last; update the doc comment to say it is the organization that vouches for the party.
- `Organization.ToParty`, `CallerManager.EvaluateClinicianAsync` (`OrgUri = provider.SubjectUri`), and every test `new Party(...)` listed under Files.
- First `Migrations` entry:
  ```sql
  IF COL_LENGTH(N'dbo.grants', N'grantee_org_uri') IS NULL
  ALTER TABLE dbo.grants ADD grantee_org_uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL;
  ```
- `GrantManager.InsertAsync` writes `grant.Grantee.OrgUri`; the `SELECT`s that fill `GrantRow` add `grantee_org_uri AS GranteeOrgUri`; `GrantRow` gains `string? GranteeOrgUri` and builds the Party with `GranteeOrgUri ?? GranteeUri` (for rows written before the column existed).

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test EZEHR.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
dotnet csharpier format .
git add -A Models Managers Data EZEHR.Tests
git commit -m "feat: parties carry the organization that vouches for them"
```

---

### Task 3: Stamp the actor's organization on every audit event

**Files:**
- Modify: `Data/Db.cs` (`Migrations`), `Managers/AuditManager.cs` (`AppendAsync` insert, `ListForPatientAsync` select, `AuditRow`)
- Test: `EZEHR.Tests/Data/DbTests.cs`, `EZEHR.Tests/Access/AccessTests.cs`

**Interfaces:**
- Consumes: `Party.OrgUri`, `Db.Migrations` (Task 2).
- Produces: column `audit_events.actor_org_uri NVARCHAR(200) BIN2 NULL` (null exactly when the actor is unverified) and index `ix_audit_events_actor_org (actor_org_uri, id)`; Task 4 queries both.

- [ ] **Step 1: Write the failing tests**

Extend `DbTests.EnsureSchema_adds_org_columns_to_existing_tables`: before re-running `EnsureSchemaAsync`, also run `DROP INDEX ix_audit_events_actor_org ON dbo.audit_events` and `ALTER TABLE dbo.audit_events DROP COLUMN actor_org_uri`. Afterwards assert that `COL_LENGTH('dbo.audit_events', 'actor_org_uri')` is not null and that `SELECT COUNT(*) FROM sys.indexes WHERE name = 'ix_audit_events_actor_org' AND object_id = OBJECT_ID('dbo.audit_events')` is 1.

`AccessTests.Audit_event_keeps_actor_org_uri`: `Audit.AppendAsync(_sam, Rivera, "ReadFullRecord", null, true, null)` and `Audit.AppendAsync(_sam, null, "ReadFullRecord", "treatment", false, DenyReason.NoCertificate)`; `ListForPatientAsync(_sam)` returns `[0].Actor == Rivera` and `[1].Actor == null`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EZEHR.Tests --filter "FullyQualifiedName~DbTests|FullyQualifiedName~Audit_event_keeps_actor_org_uri"`
Expected: FAIL (the `DROP` statements hit a missing column and index; `Actor.OrgUri` reads back wrong).

- [ ] **Step 3: Implement**

Append two `Migrations` entries:

```sql
IF COL_LENGTH(N'dbo.audit_events', N'actor_org_uri') IS NULL
ALTER TABLE dbo.audit_events ADD actor_org_uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL;
```
```sql
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'ix_audit_events_actor_org' AND object_id = OBJECT_ID(N'dbo.audit_events'))
CREATE INDEX ix_audit_events_actor_org ON dbo.audit_events(actor_org_uri, id);
```

`AuditManager`: insert `actor_org_uri = actor?.OrgUri`; `ListForPatientAsync` selects `actor_org_uri AS ActorOrgUri`; `AuditRow` gains `string? ActorOrgUri` and builds the Party with `ActorOrgUri ?? ActorUri`.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test EZEHR.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
dotnet csharpier format .
git add Data/Db.cs Managers/AuditManager.cs EZEHR.Tests
git commit -m "feat: record the vouching organization on every audit event"
```

---

### Task 4: `read-organization-audit`

**Files:**
- Create: `Models/OrganizationAudit.cs`, `Managers/OrganizationAuditManager.cs`
- Modify: `Models/Results.cs`, `Models/NetworkModels.cs`
- Modify: `Controllers/NetworkController.cs` (inject `OrganizationAuditManager`, new action)
- Modify: `Program.cs` (`AddScoped<OrganizationAuditManager>()`), `EZEHR.Tests/TestServices.cs` (`OrganizationAudit` property)
- Test: `EZEHR.Tests/Audit/OrganizationAuditTests.cs`, `EZEHR.Tests/Api/OrganizationAuditApiTests.cs`

**Interfaces:**
- Consumes: `audit_events.actor_org_uri` (Task 3); `OrgDirectory.Demo` (Task 1).
- Produces:
  - `Models/OrganizationAudit.cs`:
    - `record OrganizationAuditFilter(string? ClinicianUri, Guid? PatientId, string? ProviderPatientId, DateTimeOffset? Since, DateTimeOffset? Until, long AfterId, int Limit)`: `Since` inclusive, `Until` exclusive, all filters ANDed.
    - `record OrganizationAuditEvent(long Id, DateTimeOffset At, Guid PatientId, string? ProviderPatientId, PartyKind ActorKind, string ActorUri, string ActorName, string Action, string? ClaimedPurpose, bool Granted, DenyReason? Reason)`
  - `Results.cs`: `record ReadOrganizationAuditResult(bool Allowed, DenyReason? Reason, IReadOnlyList<OrganizationAuditEvent> Events, long? NextAfterId)`
  - `NetworkModels.cs`: `record ReadOrganizationAuditResponse(IReadOnlyList<OrganizationAuditEvent> Events, long? NextAfterId)`
  - `OrganizationAuditManager(Db db)` with `Task<ReadOrganizationAuditResult> ReadOrganizationAuditAsync(Caller caller, OrganizationAuditFilter filter)` and a private `DenyReason? RefuseCaller(Caller caller)` that Task 5 reuses.
  - `GET api/network/read-organization-audit?clinicianUri=&patientId=&providerPatientId=&since=&until=&afterId=0&limit=100`

- [ ] **Step 1: Write the failing manager tests**

`OrganizationAuditTests` (same `IAsyncLifetime` shape as `AccessTests`, `TestServices` over a fresh database). Fixtures:
- `Riverside = OrgDirectory.Demo.FindByUri("urn:provider:riverside")!`, `Lakeside` likewise, `KidneyCheck = OrgDirectory.Demo.FindByUri("urn:app:kidney-check")!`
- `Rivera = new Party(PartyKind.Clinician, "urn:entra:tid-r:rivera", "Dr. Rivera", "Riverside Primary Care", "urn:provider:riverside")`, `Chen` likewise (`urn:entra:tid-r:chen`), `Okafor` at Lakeside (`"urn:provider:lakeside"`).
- Patients `_sam`, `_alex` (`Guid.NewGuid()`). Seed events with `Audit.AppendAsync`, and links with a raw `INSERT INTO provider_links` like `DbTests.InsertLink`.
- Helper `Read(Caller caller, OrganizationAuditFilter filter)`; `All = new OrganizationAuditFilter(null, null, null, null, null, 0, 100)`.

Tests and assertions:
- `Provider_sees_only_its_own_clinicians_and_systems`: events: Rivera reads Sam; Okafor reads Sam; Lakeside `PushSignedRecords` for Sam; the broker (null actor) is refused on Sam; Kidney Check reads Sam. Riverside with `All` → exactly Rivera's event. Lakeside with `All with { PatientId = _sam }` → Okafor's event and the push, in id order. Kidney Check with `All` → only its own read.
- `Denied_attempts_by_own_clinician_are_included`: Rivera refused with `GrantRevoked` → returned with `Granted == false`, `Reason == DenyReason.GrantRevoked`.
- `Filters_by_clinician_uri`: Rivera and Chen events; `ClinicianUri = Chen.SubjectUri` → only Chen's. As Riverside, `ClinicianUri = Okafor.SubjectUri` → empty.
- `Filters_by_patient_and_reports_providers_own_patient_id`: link Sam to Riverside as `RPC-1107` and to Lakeside as `LUC-20417`; Rivera reads Sam and Alex. `ProviderPatientId = "RPC-1107"` → only Sam's event, with `ProviderPatientId == "RPC-1107"`. `All` → Alex's event has `ProviderPatientId == null`. `ProviderPatientId = "RPC-0000"` → empty. `ProviderPatientId = "LUC-20417"` as Riverside → empty.
- `Patient_with_two_ids_at_one_provider_appears_once`: link Sam to Riverside as `RPC-1107` and `RPC-2208`; one Rivera event → `All` returns 1 event, `ProviderPatientId == "RPC-1107"` (lowest, ordinal).
- `Since_is_inclusive_and_until_exclusive`: events at t0, t0+1h, t0+2h (advance the clock); `Since = t0+1h, Until = t0+2h` → only the middle one.
- `Pages_by_after_id`: 3 Rivera events. `Limit = 2` → 2 events, `NextAfterId == events[1].Id`; then `AfterId = NextAfterId` → 1 event, `NextAfterId == null`. `Limit = 3` → 3 events, `NextAfterId == null`.
- `Unverified_caller_is_refused`: `new UnverifiedCaller(DenyReason.NoCertificate)` → `Allowed == false`, `Reason == NoCertificate`, `Events` empty.
- `Clinician_caller_is_refused`: `new VerifiedCaller(Rivera, now)` → `Reason == DenyReason.WrongPartyKind`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test EZEHR.Tests --filter "FullyQualifiedName~OrganizationAuditTests"`
Expected: build FAIL (`OrganizationAuditManager` missing).

- [ ] **Step 3: Implement the models, result and `ReadOrganizationAuditAsync`**

`RefuseCaller`: `UnverifiedCaller` → its `Reason`; a verified `Clinician` → `WrongPartyKind`; otherwise null. The org is `verified.Party.OrgUri`.

One query with `@take = filter.Limit + 1` to detect a further page. Return at most `Limit` events; `NextAfterId` is the id of the last returned event when the extra row existed, else null:

```sql
SELECT TOP (@take) a.id AS Id, a.at AS At, a.patient_id AS PatientId, link.provider_patient_id AS ProviderPatientId,
       a.actor_kind AS ActorKind, a.actor_uri AS ActorUri, a.actor_name AS ActorName, a.action AS Action,
       a.claimed_purpose AS ClaimedPurpose, a.granted AS Granted, a.reason AS Reason
FROM audit_events a
OUTER APPLY (SELECT TOP 1 l.provider_patient_id FROM provider_links l
             WHERE l.patient_id = a.patient_id AND l.provider_uri = @orgUri
             ORDER BY l.provider_patient_id) link
WHERE a.actor_org_uri = @orgUri AND a.id > @afterId
  AND (@clinicianUri IS NULL OR a.actor_uri = @clinicianUri)
  AND (@patientId IS NULL OR a.patient_id = @patientId)
  AND (@providerPatientId IS NULL OR EXISTS (SELECT 1 FROM provider_links p
       WHERE p.patient_id = a.patient_id AND p.provider_uri = @orgUri
         AND p.provider_patient_id = @providerPatientId))
  AND (@since IS NULL OR a.at >= @since) AND (@until IS NULL OR a.at < @until)
ORDER BY a.id
```

Map through a private row record (string kind and reason → enums), as `AuditManager.AuditRow` does. Register in `Program.cs`; add `OrganizationAudit = new OrganizationAuditManager(db)` to `TestServices`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test EZEHR.Tests --filter "FullyQualifiedName~OrganizationAuditTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing API tests**

`OrganizationAuditApiTests` (shape of `NetworkAccessTests`): `_riverside = _app.Pki.Issue(OrgDirectory.ProviderCommonName("riverside"))`; seed one Rivera event through `_app.WithAsync(... AuditManager.AppendAsync ...)`.
- `Provider_reads_its_audit`: `GET api/network/read-organization-audit` with `_riverside` → 200; body has `events.length == 1`, `events[0].actorUri == Rivera.SubjectUri`, `events[0].actorKind == "Clinician"`, `nextAfterId == null`.
- `No_certificate_is_401`: no certificate → 401 with body `{"error":"NoCertificate"}`.
- `Limit_out_of_range_is_400`: `?limit=0` and `?limit=501` → 400.

- [ ] **Step 6: Run to verify they fail**

Run: `dotnet test EZEHR.Tests --filter "FullyQualifiedName~OrganizationAuditApiTests"`
Expected: FAIL with 404.

- [ ] **Step 7: Implement `NetworkController.ReadOrganizationAuditAsync`**

```csharp
[HttpGet("read-organization-audit")]
public async Task<IActionResult> ReadOrganizationAuditAsync(
    [FromQuery] string? clinicianUri,
    [FromQuery] Guid? patientId,
    [FromQuery] string? providerPatientId,
    [FromQuery] DateTimeOffset? since,
    [FromQuery] DateTimeOffset? until,
    [FromQuery] long afterId = 0,
    [FromQuery, Range(1, 500)] int limit = 100
)
```

It builds an `OrganizationAuditFilter`, calls the manager, returns `DenialResult(result.Reason)` when not allowed, and otherwise `Ok(new ReadOrganizationAuditResponse(result.Events, result.NextAfterId))`.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test EZEHR.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
dotnet csharpier format .
git add Models Managers/OrganizationAuditManager.cs Controllers/NetworkController.cs Program.cs EZEHR.Tests
git commit -m "feat: read-organization-audit lets a provider audit its clinicians"
```

---

### Task 5: `list-organization-clinicians`

Clinicians aren't registered anywhere, so this is how a provider discovers the `clinicianUri` values to filter by.

**Files:**
- Modify: `Models/OrganizationAudit.cs`, `Models/Results.cs`, `Models/NetworkModels.cs`
- Modify: `Managers/OrganizationAuditManager.cs`, `Controllers/NetworkController.cs`
- Test: `EZEHR.Tests/Audit/OrganizationAuditTests.cs`, `EZEHR.Tests/Api/OrganizationAuditApiTests.cs`

**Interfaces:**
- Consumes: `OrganizationAuditManager.RefuseCaller` (Task 4).
- Produces:
  - `record OrganizationClinician(string ClinicianUri, string DisplayName, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, int EventCount, int DeniedCount)`; `DisplayName` is the name on that clinician's most recent event.
  - `record ListOrganizationCliniciansResult(bool Allowed, DenyReason? Reason, IReadOnlyList<OrganizationClinician> Clinicians)`
  - `record ListOrganizationCliniciansResponse(IReadOnlyList<OrganizationClinician> Clinicians)`
  - `OrganizationAuditManager.ListOrganizationCliniciansAsync(Caller caller) -> Task<ListOrganizationCliniciansResult>`, ordered by `LastSeenAt` descending.
  - `GET api/network/list-organization-clinicians`

- [ ] **Step 1: Write the failing tests**

`OrganizationAuditTests.Lists_each_clinician_once_with_latest_name_and_counts`: Rivera ("Dr. Rivera") granted at t0; Rivera renamed (`Rivera with { DisplayName = "Dr. Ana Rivera" }`) refused at t0+1h; Chen granted at t0+30m; Okafor (Lakeside) and a Riverside `PushSignedRecords` event also present. Riverside's list equals `[Rivera: "Dr. Ana Rivera", first t0, last t0+1h, 2 events, 1 refused; Chen: 1 event, 0 refused]` in that order, with no Okafor and no provider-system row.
`Unverified_caller_cannot_list_clinicians`: `Reason == NoCertificate`, empty list.

`OrganizationAuditApiTests.Provider_lists_its_clinicians`: 200, `clinicians[0].clinicianUri == Rivera.SubjectUri`. `Listing_without_certificate_is_401`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test EZEHR.Tests --filter "FullyQualifiedName~OrganizationAudit"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

One query over `audit_events WHERE actor_org_uri = @orgUri AND actor_kind = 'Clinician'`, grouped by `actor_uri` (`MIN(at)`, `MAX(at)`, `COUNT(*)`, `SUM(CASE WHEN granted = 0 THEN 1 ELSE 0 END)`), taking the latest name from `ROW_NUMBER() OVER (PARTITION BY actor_uri ORDER BY id DESC) = 1`. The controller action mirrors Task 4's.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test EZEHR.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
dotnet csharpier format .
git add Models Managers/OrganizationAuditManager.cs Controllers/NetworkController.cs EZEHR.Tests
git commit -m "feat: list-organization-clinicians"
```

---

### Task 6: Tool commands and docs

**Files:**
- Modify: `EZEHR.Tools/Commands/ProviderCommand.cs`
- Modify: `docs/auth-flows.md` (§5 new Flow 10, §6 component table, §7)

**Interfaces:**
- Consumes: both endpoints and their response records (Tasks 4–5).

- [ ] **Step 1: Add the commands**

The usage text gains:

```
  provider audit <lakeside|northwind|maple|riverside> [--clinician <uri>] [--patient <providerPatientId>]
  provider clinicians <lakeside|northwind|maple|riverside>
```

`RunAsync` accepts `args.Length >= 2` for these two verbs (keep exactly 3 for `invite` and `push`). Read options with `CommandLine.Option` and URL-encode query values with `Uri.EscapeDataString`. `audit` follows `nextAfterId` until it is null and prints one line per event:
`{At:yyyy-MM-dd HH:mm}  {ActorName,-20}  {Action,-18}  {"allowed" | "refused " + Reason}  patient {ProviderPatientId ?? PatientId}`.
`clinicians` prints `{DisplayName,-20}  {EventCount} events ({DeniedCount} refused)  last {LastSeenAt:yyyy-MM-dd HH:mm}  {ClinicianUri}`. A non-success response prints `NetworkClient.DescribeAsync` and returns 1.

- [ ] **Step 2: Verify against a local server**

Run the local stack and set Riverside's tenant with `scripts/local.sh sql "UPDATE organizations SET entra_tenant_id = '<tenant>' WHERE uri = 'urn:provider:riverside'"`. Demo through the clinician read: enroll Sam, link Riverside, share with Dr. Rivera, open the record. Then:

```bash
dotnet run --project EZEHR.Tools -- provider clinicians riverside
```
```bash
dotnet run --project EZEHR.Tools -- provider audit riverside --patient RPC-1107
```

Expected: Dr. Rivera listed once, and the audit shows `RedeemShareCode` and `ReadFullRecord` lines for `patient RPC-1107`. `provider audit lakeside` shows only Lakeside's `PushSignedRecords` and no Riverside lines.

- [ ] **Step 3: Update `docs/auth-flows.md`**

- New **Flow 10: A provider audits its clinicians**: a sequence diagram (Riverside system → Network API over mutual TLS → certificate check against `organizations` → `OrganizationAuditManager` → `audit_events WHERE actor_org_uri = urn:provider:riverside`), plus three bullets: what it sees (its clinicians' attempts, granted or refused, and its own system's calls), what it never sees (other parties' events), and that patients appear as EZEHR id plus the provider's own patient id.
- §6 component table: add `OrganizationManager` (loads `organizations` into an `OrganizationDirectory`) and `OrganizationAuditManager` (called by `NetworkController`; takes a `Caller` and a filter; returns the org's events or a `DenyReason`).
- §7 table, "Audit event" row: add "and which organization vouched for the actor".

- [ ] **Step 4: Commit**

```bash
dotnet csharpier format .
git add EZEHR.Tools/Commands/ProviderCommand.cs docs/auth-flows.md
git commit -m "feat: provider audit and clinicians tool commands; document organization audit"
```
