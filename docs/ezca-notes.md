# EZCA notes

What the EZCA setup on Nov 14 showed. The tenant is `mwpg089outlook.onmicrosoft.com` (`fd4e23f3-7ba3-49f1-bdcd-3f3c55d42f0d`).

## Accounts

- **Use a member account of the tenant, not a personal Microsoft account.** Signing in to the Keytos consent link as an invited personal account (`@gmail.com`) fails with "Selected user account does not exist in tenant 'Keytos Prod'", because the link isn't tenant-specific. We created `ezca-admin@mwpg089outlook.onmicrosoft.com` (Global Administrator) and used it for the consent, the `keytos` CLI and the cert tool.
- **The cert tool signs in through `az login`** (DefaultAzureCredential). That account has no Azure subscription, so it needs `az login --tenant fd4e23f3-7ba3-49f1-bdcd-3f3c55d42f0d --allow-no-subscriptions`.

## CAs (created with the `keytos` CLI, EZCA subscription `EZEHR`, location `westus`)

- `EZEHR Root CA`: RSA 4096, template `ssl`.
- `EZEHR Issuing CA`: RSA 2048, template `ssl`.
  - EKUs: client auth and server auth. **Client auth is required**, because `CertificateValidator` checks the clientAuth policy across the whole chain, and it can't be changed after the CA is created.
  - `pathlen:0`, and issued certificates last up to 400 days.
- Domains registered on the issuing CA, each with `ezca-admin` as owner and certificate administrator: `lakeside`, `northwind`, `maple` and `riverside` (each `.providers.ezehr`), and `kidney-check.apps.ezehr`.

## Issuance (`ezehr-tools certs issue --ca ezca --key ec`)

- **EC P-256 CSRs are accepted**, so no RSA fallback is needed.
- Each leaf certificate has the requested CN, a DNS SAN with the same name, only the clientAuth EKU, and 365 days of validity.
- The response carries the issuing and root certificates. The tool writes them to `.pki/intermediates.pem` and `.pki/anchors.pem`, and they're committed as `Security/Trust/*.pem`.
- Check: `openssl verify -CAfile Security/Trust/anchors.pem -untrusted Security/Trust/intermediates.pem -purpose sslclient <leaf>` prints `OK` for all five.
- The server never calls EZCA. It only trusts `Security/Trust/`, which is copied into the build and publish output, and relative paths are resolved from the app's folder.
