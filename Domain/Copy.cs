namespace EZEHR.Domain;

/// <summary>Every user-facing string the spec fixes. Change copy here only.</summary>
public static class Copy
{
    public static string SignedBy(string org) => $"Signed by {org} ✓";

    public static string WrittenBy(string name, string org) => $"Written by {name}, {org} · signed by EZEHR ✓";

    public static string ApprovePrompt(string name, string org, string time) => $"Give {name} at {org} access until {time}?";

    public static string BrokerBlocked(string purpose) => $"Blocked: unverified requester claimed {purpose}";

    public static string AppGrantLine(string app, string what, string date) => $"{app} · {what} · you allowed this {date} · Revoke";

    public const string KidneyOptIn =
        "Allow the Kidney Check app to read your lab results. It looks for slow changes no single clinic would see. You can turn it off any time.";

    public static string KidneyNote(int percent, int providers) =>
        $"Kidney function (eGFR) dropped {percent}% across {providers} providers. No single result was flagged. Consider asking a doctor to review it.";

    public const string AccessEnded = "Your access to this record has ended. Ask the patient to share again.";

    public const string NotAClinician = "Your organization hasn't approved this account as a clinician.";

    public const string UntrustedOrg = "This organization isn't connected to EZEHR yet.";

    public const string Pitch = "Epic says it blocks data to protect patients. We make blocking unnecessary.";
}
