namespace EZEHR.Models;

/// <summary>Every user-facing string the spec fixes. Change copy here only.</summary>
public static class Copy
{
    public const string KidneyOptIn =
        "Allow the Kidney Check app to read your lab results. It looks for slow changes no single clinic would see. You can turn it off any time.";

    public const string AccessEnded =
        "Your access to this record has ended. Ask the patient to share again.";

    public const string NotAClinician =
        "Your organization hasn't approved this account as a clinician.";

    public const string UntrustedOrg = "This organization isn't connected to EZEHR yet.";

    public const string Pitch =
        "Epic says it blocks data to protect patients. We make blocking unnecessary.";

    public const string InviteUnusable =
        "This invite can't be used. It may have expired or already been used. Ask your clinic for a new one.";

    public const string InviteLinkedElsewhere =
        "This clinic's record is already linked to a different EZEHR account.";

    public const string PasskeyFailed = "That passkey didn't work. Please try again.";

    public const string PasskeyUnknown =
        "We don't recognize that passkey. Use the link from your clinic to create one.";

    public static string InvitedBy(string provider)
    {
        return $"{provider} invited you to EZEHR, one place for your health record that only you control.";
    }

    public static string LinkProviderPrompt(string provider)
    {
        return $"Add your records from {provider} to EZEHR?";
    }

    /// <summary>What the patient sees when enrollment, linking an invite or passkey sign-in fails.</summary>
    public static string EnrollmentFailed(EnrollmentError error)
    {
        return error switch
        {
            EnrollmentError.InviteLinkedElsewhere => InviteLinkedElsewhere,
            EnrollmentError.PasskeyRejected => PasskeyFailed,
            EnrollmentError.PasskeyUnknown => PasskeyUnknown,
            _ => InviteUnusable,
        };
    }

    public static string SignedBy(string org)
    {
        return $"Signed by {org} ✓";
    }

    public static string WrittenBy(string name, string org)
    {
        return $"Written by {name} · signed by {org} ✓";
    }

    public static string ApprovePrompt(string name, string org, string time)
    {
        return $"Give {name} at {org} access until {time}?";
    }

    public static string BrokerBlocked(string purpose)
    {
        return $"Blocked: unverified requester claimed {purpose}";
    }

    public static string AppGrantLine(string app, string what, string date)
    {
        return $"{app} · {what} · you allowed this {date} · Revoke";
    }

    public static string KidneyNote(int percent, int providers)
    {
        return $"Kidney function (eGFR) dropped {percent}% across {providers} providers. No single result was flagged. Consider asking a doctor to review it.";
    }
}
