# Manual testing with ezehr-tools

`ezehr-tools` (the `EZEHR.Tools` project) is the laptop side of EZEHR. It plays every party that isn't a person
in a browser: a provider's EHR, an app, the platform operator, an unverified caller. It also has backstage
commands that go straight to the database. Every command explains itself:

```bash
dotnet run --project EZEHR.Tools -- --help
```

```bash
dotnet run --project EZEHR.Tools -- provider push --help
```

The examples below write `ezehr-tools` for `dotnet run --project EZEHR.Tools --`.

## Before you start

Run `env` first. It shows which site the tools will call and whether it answers, which database the backstage
commands use, where the certificates are, and the platform pin the server needs.

```bash
ezehr-tools env
```

| Variable | What it does | Default |
|---|---|---|
| `EZEHR_BASE_URL` | The site to call. Every network command also takes `--base-url`. | `https://localhost:7189` |
| `AZURE_SQL_CONNECTIONSTRING` | The database for `patient`, `db`, `demo`, and for naming a patient as `sam` or `provider:chart`. | none |
| `EZEHR_PKI_DIRECTORY` | Where certificates are read and written. Use a separate directory for each stack, so the local and deployed certificates don't overwrite each other. | `<repo>/.pki` |
| `EZEHR_CLINICIAN_OBJECT_ID` | Dr. Rivera's object id in Riverside's tenant, for `demo stage`'s launch step. | none |

`source scripts/local.env` points the tools at a local stack (`scripts/local.sh up`). `source scripts/azure.env`
points them at the deployed site, and `source scripts/nuc.env` at the NUC.

Exit codes: `0` means it worked, `1` means it failed or the server refused, and `2` means the command line was
wrong. A typo in a flag is an error, not something the tool ignores.

## The commands

```
ezehr-tools
├─ env                                   where the tools point, and what they hold
├─ org list                              the organizations the tools know, with certificate and seed status
├─ certs                                 issue certificates into .pki/ (the CA only; EZEHR isn't called)
│  ├─ issue                              every demo organization, plus anchors.pem and intermediates.pem
│  ├─ platform                           the platform operator's certificate, and the pin to set on the server
│  ├─ provider <slug>                    a new provider's certificate; EZEHR doesn't know it yet
│  ├─ app <slug>                         a new app's certificate; EZEHR doesn't know it yet
│  └─ broker                             the broker's self-signed certificate
├─ platform                              the operator, with .pki/platform.pfx
│  ├─ register-provider <slug> --name
│  ├─ register-app <slug> --name --scope --description
│  ├─ onboard-provider <slug> --name [--invite <chart>]     certs provider + register-provider (+ invite)
│  └─ onboard-app <slug> --name --scope --description        certs app + register-app
├─ provider                              a provider's EHR system, with .pki/<provider>.pfx
│  ├─ invite <provider> <chart>
│  ├─ push <provider> <chart> [--file <path>] [--sign-as <identity>] [--corrupt-signature]
│  ├─ launch <provider> <chart> --clinician <object id> [--encounter <id>]
│  ├─ audit <provider> [--clinician <uri>] [--chart <chart>] [--patient <patient>] [--since] [--until] [--limit]
│  └─ clinicians <provider>
├─ app                                   the example apps, each with its own certificate
│  ├─ kidney-check <patient>
│  └─ medication-check <patient>
├─ network
│  └─ read <patient> <record> --as <identity> [--purpose <p>] [--expect <outcome>] [--raw]
├─ patient                               backstage: the database, never the network
│  ├─ find <patient>                     prints the EZEHR patient id only, for $(...)
│  ├─ list
│  └─ status <patient>
├─ db
│  └─ query "<sql>"                      backstage
└─ demo                                  the recorded demo
   ├─ reset [--yes]
   ├─ tamper
   └─ stage [--from <n>] [--list] [ca options]
```

The certificate commands take `--ca local|ezca` (`local` is a test CA on this laptop; `ezca` is portal.ezca.io,
signed in as `az login`), `--key ec|rsa`, `--ca-id` and `--template-id`.

## Naming patients and callers

- **`<patient>`** is an EZEHR patient id; `sam`; or `<provider>:<chart>`, such as `lakeside:LUC-20417`. Only the id
  works without the database.
- **`<identity>`** is whose certificate a call presents:
  - `none`;
  - `broker`, which is self-signed;
  - `platform`;
  - any organization slug the tools know (`org list`);
  - or a path to a `.pfx` file.
- **`<chart>`** is the provider's own patient id, such as `LUC-20417`. The API calls it `providerPatientId`.
- **`<outcome>`** for `--expect` is `ok`, an HTTP status (`403`), or a deny reason (`ScopeDenied`). With
  `--expect`, the exit code says whether the server answered that way. That turns any check below into a
  one-line command you can rerun after a change.

## Checks, by what a realistic system should do

These follow the tests in `docs/direction.md`. Most need an enrolled patient. Enrolling and turning access on are
browser steps (see "What stays in the browser" below), so the quickest setup is `demo reset`, then
`demo stage` up to the point you need.

### An organization is responsible for its people

1. Riverside's system opens EZEHR for one of its clinicians, from a chart:

   ```bash
   ezehr-tools provider launch riverside RPC-1107 --clinician "$EZEHR_CLINICIAN_OBJECT_ID"
   ```

   The link only works for that clinician, and only within a few minutes.

2. Riverside reads back what its people did, with filters:

   ```bash
   ezehr-tools provider audit riverside --chart RPC-1107
   ```

   ```bash
   ezehr-tools provider audit riverside --since 2026-11-17 --limit 20
   ```

   ```bash
   ezehr-tools provider clinicians riverside
   ```

### People read; organizations write, and sign what they write

Each of these pushes something the network should refuse. The server checks that the patient is linked to the
chart before it checks signatures, so use a chart the patient enrolled with.

- **Signed by the wrong organization:**

  ```bash
  ezehr-tools provider push lakeside LUC-20417 --sign-as maple
  ```

- **A broken signature:**

  ```bash
  ezehr-tools provider push lakeside LUC-20417 --corrupt-signature
  ```

- **Your own record**, from a file holding one resource, an array of them, or the seed format:

  ```bash
  cat > lab.json <<'JSON'
  {"resourceType": "Observation", "id": "manual-egfr-1", "status": "final",
   "code": {"coding": [{"system": "http://loinc.org", "code": "98979-8"}]},
   "effectiveDateTime": "2026-11-17", "valueQuantity": {"value": 48, "unit": "mL/min/1.73m2"}}
  JSON
  ezehr-tools provider push lakeside LUC-20417 --file lab.json
  ```

Then read it back the way any reader would: every entry's signature is checked on this laptop, against
`.pki/anchors.pem`.

```bash
ezehr-tools network read lakeside:LUC-20417 egfr-labs --as kidney-check
```

### The patient decides who sees the record

- **An app asks for more than its grant covers:**

  ```bash
  ezehr-tools network read sam medications --as kidney-check --expect ScopeDenied
  ```

- **An app the patient never turned on.** This needs Medication Check registered (`demo stage` step 12 does it;
  `demo reset` removes it, and until then the answer is `UntrustedCertificate`):

  ```bash
  ezehr-tools network read sam egfr-labs --as medication-check --expect NoGrant
  ```

- **No certificate:**

  ```bash
  ezehr-tools network read sam full-record --as none --purpose treatment --expect NoCertificate
  ```

- **A self-made certificate:**

  ```bash
  ezehr-tools network read sam full-record --as broker --purpose treatment --expect UntrustedCertificate
  ```

- **A real certificate from an organization EZEHR never admitted.** The network refuses it too
  (`401 UntrustedCertificate`, checked against a local server) until the platform registers it:

  ```bash
  ezehr-tools certs provider harbor
  ```

  ```bash
  ezehr-tools network read sam full-record --as harbor --expect UntrustedCertificate
  ```

  ```bash
  ezehr-tools platform register-provider harbor --name "Harbor Clinic"
  ```

### The patient sees every attempt

Every call above, allowed or refused, lands in the patient's Activity page. Check that the audit log still
verifies:

```bash
ezehr-tools patient status sam
```

Then edit one event in place, as an insider with database access would. The log shows as broken, on the patient's
Activity page and in `patient status`. Run it again to undo the edit.

```bash
ezehr-tools demo tamper
```

## Scripting

- `patient find` prints only the id: `id=$(ezehr-tools patient find lakeside:LUC-20417)`.
- Piped output never wraps, so `grep` sees whole lines. Set `COLUMNS` to change that.
- `demo stage` reads one key per line when its input is piped: an empty line is Enter, and the end of the input
  quits. For example, to skip the first two steps and run the third:

  ```bash
  printf 's\ns\n\nq\n' | ezehr-tools demo stage
  ```

- `db query` runs any SQL as-is, against whichever database is configured.

## What stays in the browser

Enrolling, share codes, approving or revoking access, and emergency access are the patient's and the clinician's
own steps in the browser. The patient signs in with a passkey. Faking those steps by writing grants into the
database would skip the very thing being tested, so the tools leave them to the browser.
