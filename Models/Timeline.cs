namespace EZEHR.Models;

/// <summary>One record entry as the patient and clinician pages show it.</summary>
public sealed record TimelineItem(
    Guid EntryId,
    DateOnly ClinicalDate,
    string ResourceType,
    string Title,
    string? Detail,
    string Badge
);

public sealed record TimelineDay(DateOnly Date, IReadOnlyList<TimelineItem> Items);

/// <summary>
/// Notes written by apps (Kidney Check) sit above the dated history, newest day first. UnverifiedCount is how many
/// stored entries failed verification and aren't shown; the page says so instead of hiding them silently.
/// </summary>
public sealed record Timeline(
    IReadOnlyList<TimelineItem> AppNotes,
    IReadOnlyList<TimelineDay> Days,
    int UnverifiedCount = 0
);

/// <summary>
/// What the patient's home page shows. EmergencyAlerts has one line for each emergency access open right now:
/// who, until when, and the reason they gave.
/// </summary>
public sealed record PatientHome(
    string PatientName,
    Timeline Timeline,
    string RecordVersion,
    IReadOnlyList<string> EmergencyAlerts
);

/// <summary>A share offer as the Share page shows it, and what became of it.</summary>
public sealed record ShareStatus(
    Guid OfferId,
    string FormattedCode,
    string QrPngDataUri,
    bool OfferExpired,
    Grant? Grant,
    string? UntilIfApprovedNow,
    string? Until
);

/// <summary>One line of the patient's Access page. Emergency lines (opened without an approval) stand out.</summary>
public sealed record AccessLine(Guid GrantId, string Text, bool CanRevoke, bool Emergency = false);

/// <summary>A provider that can add records for the patient (a provider link). It can't read the record.</summary>
public sealed record ConnectedProviderLine(string ProviderUri, string Text);

/// <summary>
/// The Access page: who can see the record (grants), which providers can add to it (links), and Kidney Check's
/// current grant, or null when it's off (then the page offers to turn it on). EmergencyCode is the patient's
/// emergency code as XXXX-XXXX, or null when emergency access is off.
/// </summary>
public sealed record AccessOverview(
    IReadOnlyList<AccessLine> Lines,
    IReadOnlyList<ConnectedProviderLine> ConnectedProviders,
    AccessLine? KidneyCheck,
    string? EmergencyCode = null
);

/// <summary>
/// One line of the patient's Activity page. Blocked lines show in red; a clinician opening the record with
/// emergency access shows as a warning.
/// </summary>
public sealed record ActivityLine(
    long Id,
    string Text,
    string Time,
    bool Blocked,
    bool Emergency = false
);
