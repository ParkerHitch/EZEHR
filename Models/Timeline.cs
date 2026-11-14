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

/// <summary>Notes written by apps (Kidney Check) sit above the dated history, newest day first.</summary>
public sealed record Timeline(
    IReadOnlyList<TimelineItem> AppNotes,
    IReadOnlyList<TimelineDay> Days
);

/// <summary>What the patient's home page shows.</summary>
public sealed record PatientHome(string PatientName, Timeline Timeline);
