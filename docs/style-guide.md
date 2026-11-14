# EZEHR Style Guide

How we write code in this repo: one ASP.NET Core Razor Pages site, built in 24 hours, that handles
identity, certificates and health data. The rules lean toward explicit, boring code that is easy
to review, because most of what we're building is security code.

This code has to work and stay readable until we submit at 1 PM, and no longer. Don't add
anything whose only payoff is easier maintenance later (see section 5).

---

## 1. Formatting

- **CSharpier** formats all C#, with `printWidth: 100`. Don't hand-format, and don't argue with
  its output. Run `dotnet csharpier format .` before you commit.
- 4-space indent in `.cs`; 2-space in `.json`, `.csproj`, `.cshtml`, `.js`, `.xml`. LF line endings.
- **File-scoped namespaces** (`namespace EZEHR.Managers;`).
- `using` directives at the top, never fully qualified names inline. For a real name clash, add a
  `using` alias at the top of the file.

## 2. C# rules

### Explicit types: never `var`

```csharp
// NO
var grant = await grantManager.FindAsync(grantId);

// YES
Grant? grant = await grantManager.FindAsync(grantId);
```

### Block bodies: never expression-bodied members

```csharp
// NO
public static string SignedBy(string org) => $"Signed by {org} ✓";

// YES
public static string SignedBy(string org)
{
    return $"Signed by {org} ✓";
}
```

### Dependency injection: primary constructors

Inject through primary constructors. Keep `private readonly` fields only for values derived from
configuration:

```csharp
public sealed class GrantManager(
    Db db,
    AccessManager accessManager,
    TimeProvider time,
    IOptions<AppOptions> options
)
{
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(
        options.Value.TimeZone
    );
    ...
}
```

Never `new` up a dependency that DI could give you.

### Naming

| Thing | Style | Example |
|---|---|---|
| Types, methods, properties | PascalCase | `AccessManager.AuthorizeAsync` |
| Private fields | `_camelCase` | `_timeZone` |
| Locals, parameters | camelCase, meaningful | `wrappedKey`, not `data`, `tmp` or `res` |
| Async methods | `Async` suffix | `UnwrapAsync`, `OnPostApproveAsync` |
| Controllers | `<Area>Controller` | `NetworkController` |
| Managers | `<Area>Manager` | `GrantManager` |
| Services | `I<Thing>Service`, implementation named for its backend | `IKeyWrapService`, `KeyVaultKeyWrapService` |
| Identifiers | `Id`, as in the plan | `PatientId`, `GrantId` |

Write `Id`, never `ID`: the plan's contracts already use `Id`, and one spelling across the repo
beats either one.

## 3. Controllers, managers, services

Every request goes through three layers:
- a **controller** handles HTTP,
- a **manager** makes every decision,
- a **service** talks to one external system.

The managers hold all the logic, so they can be tested without HTTP.

```
Controllers/   API controllers ([ApiController])
Pages/         Razor pages; each PageModel follows the controller rules
Managers/      all business logic
Services/      external systems behind interfaces, plus static classes of pure logic
Models/        records, enums, request/response models, Copy
Data/          Db (connection factory and schema)
```

### Controllers (and PageModels)

A controller action, or a `PageModel` handler (`OnGet`, `OnPost…`), does only this:
1. **Gets the caller's raw identity:** the client certificate (from `IClientCertificateService`),
   or `User` for the cookie schemes.
2. **Turns it into a `Caller`** with `CallerManager`.
3. **Calls one manager method,** passing the `Caller` and the request's values.
4. **Maps the result** to a status code, a page or a redirect.

Rules:
- **No logic beyond that.** Not a query, not a loop, not a second manager call, not a check that
  decides anything. If a handler needs gate → store → verify, that sequence is one manager method.
- **No `try`/`catch`.** Unexpected exceptions go to the app's exception handler. Expected failures
  (a denied access, a used share code) come back from the manager as results.
- **Rely on `[ApiController]` for request validation.** Put `[Required]` and similar attributes on
  the request model and let the automatic 400 do the work. Don't write validation classes.
- Controllers inject managers and services only. They never touch `Db`.

```csharp
[ApiController]
[Route("api/network")]
public sealed class NetworkController(
    IClientCertificateService clientCertificateService,
    CallerManager callerManager,
    RecordManager recordManager
) : ControllerBase
{
    [HttpGet("patients/{patientId:guid}/labs")]
    public async Task<IActionResult> ReadLabsAsync(Guid patientId)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = callerManager.FromCertificate(certificate);
        LabsResult result = await recordManager.ReadLabsAsync(patientId, caller);
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return Ok(result.Labs);
    }
}
```

```csharp
public sealed class RecordModel(CallerManager callerManager, RecordManager recordManager)
    : PageModel
{
    public IReadOnlyList<VerifiedEntry> Entries { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid patientId)
    {
        Caller caller = callerManager.FromClinician(User);
        RecordResult result = await recordManager.ReadRecordAsync(patientId, caller);
        if (!result.Allowed)
        {
            return RedirectToPage("/Clinician/AccessEnded");
        }
        Entries = result.Entries;
        return Page();
    }
}
```

### Managers

- **All the business logic lives here:** access decisions, grant rules, ingest, reads, export,
  enrollment.
- A manager takes a `Caller` and plain values, never `HttpContext`, `ClaimsPrincipal` or a
  request object. The exception is `CallerManager`, whose whole job is turning those into a
  `Caller`.
- A manager returns models or results, never `IActionResult`.
- Managers query `Db` directly with Dapper. There are no repositories.
- Managers may inject other managers (for example, `RecordManager` uses `AccessManager` for every
  read).

### Services

There are two kinds, and both live in `Services/`:
- **An external system behind an interface,** so tests can swap in a fake: Key Vault, EZCA, the
  forwarded client-certificate header, Fido2. A service only does I/O with its system; decisions
  stay in the manager that calls it.
- **A `static` class of pure logic** with no external dependency: certificate chain checks, record
  signing and verification, AES-GCM, the CN → `Party` table. These have no interface and no DI
  registration; call them directly. They take what they need (trust anchors, the time) as
  arguments.

### Registration

`Program.cs` only wires things up: register managers and services, set up authentication, and map
controllers and pages. If it starts making decisions, move them into a manager.

```csharp
builder.Services.AddScoped<AccessManager>();
builder.Services.AddScoped<GrantManager>();
builder.Services.AddSingleton<IKeyWrapService, KeyVaultKeyWrapService>();
```

Register managers as scoped. Register services as singletons unless they hold per-request state.


### Keep these in one place

Duplication is fine in general (section 5). These four are the exception, because two copies that
drift break the demo tonight:
- every user-visible sentence: `Copy`
- subject URIs (`urn:provider:{slug}`, `urn:entra:{tid}:{oid}`) and certificate CNs: built in the
  `OrgDirectory` static class
- `DenyReason` to HTTP status (401 for certificate reasons, 403 for grant reasons): one private
  method in `NetworkController`
- share-code generation and formatting (`XXXX-XXXX`): `GrantManager`

## 4. Data access

- SQL is parameterized every time. Never concatenate or interpolate values into SQL text.
- Write multi-line SQL as raw string literals (`"""`), as `DbTest` already does.
- Use the async APIs and `await using` for connections, commands and readers.
- Storage is **Dapper over Azure SQL**. The plan's "Storage rules" are binding:
  - one connection per operation, taken from `Db.OpenAsync`
  - a binary collation on identifier columns
  - `SELECT` aliases that match record parameters
  - a conditional `UPDATE` for anything single-use
