# Onboarding a new provider through EZCA

## Why

The demo shows EZEHR leaning on EZCA for organization identity. On camera, one tool command takes a provider that
has never existed — say Harbor Clinic — registers its name on our EZCA issuing CA, has EZCA issue its client
certificate, and adds it to EZEHR's organizations table. Harbor then sends a patient an enrollment invite. No Entra
tenant is involved: Harbor has no clinicians on EZEHR, only a system that can invite and push.

## Goals

- A network endpoint that adds a provider to the `organizations` table, callable only by the platform.
- One tool command that does the whole onboarding: EZCA domain, certificate, registration.
- The tool works for any registered provider, not just the four demo slugs.

## Non-goals

- No new `PartyKind`. The platform isn't a party on records; it's a separate `Caller`.
- No Entra tenant for new providers (the column stays NULL; a sysadmin can still set it by hand).
- No apps through the endpoint, and no update or delete: deleting the row by hand stays the kill switch.
- No seed records for new providers; they invite, they don't push in the demo.

## Approaches considered

1. **Pinned platform certificate (chosen).** The endpoint accepts only an EZCA certificate whose SHA-256 thumbprint
   matches the App Service setting `App__PlatformCertificateThumbprint`. Unset means the endpoint refuses everyone.
   Small, and the platform's credential is itself issued by EZCA.
2. A `Platform` `PartyKind` with a row in `organizations`. Rejected by the user: it leaks into grants, audit and
   every switch over `PartyKind`.
3. A shared secret header. Weaker than mutual TLS, and doesn't show off EZCA.

## Design

**Server**

- `AppOptions.PlatformCertificateThumbprint` (hex SHA-256, case ignored).
- `CallerManager.FromPlatformCertificate(certificate)` returns `PlatformCaller` (a new `Caller` subtype, not a
  `Party`) when the certificate chains to the EZCA root, is valid, and matches the pinned thumbprint; otherwise
  `UnverifiedCaller` (`NoCertificate`, `CertificateExpired` or `UntrustedCertificate`, so 401). `FromCertificateAsync`
  never returns it, so no other endpoint can see a platform caller.
- `POST /api/network/register-provider` with `{ slug, displayName }`. `OrganizationManager.RegisterProviderAsync`
  builds the URI and CN through `OrgDirectory` (`urn:provider:{slug}`, `{slug}.providers.ezehr`) and inserts the row.
  201 with `{ uri, certificateCommonName }`; 409 `OrganizationExists` when the URI or CN is taken; 400 for a slug
  outside `[a-z0-9-]`.
- Registration isn't written to `audit_events` (its rows belong to a patient); it's logged.

**Tool**

- `certs issue --ca ezca|local --platform` issues `.pki/platform.pfx` (CN `platform.ezehr`) and prints the thumbprint
  to put in App Service.
- `certs issue --ca ezca|local --provider <slug> --name "<display name>"`:
  1. EZCA only: registers `<slug>.providers.ezehr` on the issuing CA if it isn't already
     (`RegisterDomainAsync`, owner and certificate administrator = the signed-in account). New domains work at once.
  2. Issues `.pki/<slug>.pfx`.
  3. Calls `register-provider` with `.pki/platform.pfx`; 409 counts as already registered.
  4. Records the provider in `.pki/organizations.json`.
- The tools' directory is the demo list plus `.pki/organizations.json`. `provider invite|push|audit|clinicians` take
  any slug in it, and Kidney Check verifies signatures against it.
- `certs issue` with neither flag still issues the five demo certificates.

EZCA sign-in stays `DefaultAzureCredential` (`az login --allow-no-subscriptions` as the EZCA admin).

## Left to the builder

Names of result types, test layout, and how the tool reads the signed-in account's object id.
