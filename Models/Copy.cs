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

    /// <summary>What the Kidney Check grant covers, as the Access page says it.</summary>
    public const string AppGrantWhat = "lab results only";

    /// <summary>AppGrantLine without its trailing "Revoke", which the page shows as the button.</summary>
    public static string AppGrantSummary(string app, string what, string date)
    {
        string line = AppGrantLine(app, what, date);
        return line[..line.LastIndexOf(" · ", StringComparison.Ordinal)];
    }

    public static string ClinicianAccessUntil(string name, string org, string time)
    {
        return $"{name}, {org} · until {time}";
    }

    public static string ClinicianAccessPending(string name, string org)
    {
        return $"{name}, {org} · waiting for your approval";
    }

    /// <summary>Who for a clinician is "Dr. Rivera, Riverside Primary Care"; for an app, its name.</summary>
    public static string AccessEndedLine(string who, GrantStatus status)
    {
        string how = status switch
        {
            GrantStatus.Revoked => "you revoked access",
            GrantStatus.Denied => "you said no",
            _ => "access ended",
        };
        return $"{who} · {how}";
    }

    public static string ShareApproved(string name, string org, string time)
    {
        return $"{name} at {org} can see your record until {time}.";
    }

    public const string ShareDenied = "You said no. Nothing was shared.";

    public const string ShareWaiting =
        "Read this code to your clinician. It works once, for 10 minutes.";

    public const string ShareExpired = "This code has expired. Make a new one.";

    /// <summary>One line of the patient's activity, in plain language.</summary>
    public static string ActivityLine(AuditEvent auditEvent)
    {
        Party? actor = auditEvent.Actor;
        if (actor is null)
        {
            return BrokerBlocked(auditEvent.ClaimedPurpose ?? "no purpose");
        }
        string who =
            actor.Kind == PartyKind.Clinician
                ? $"{actor.DisplayName}, {actor.OrgName},"
                : actor.DisplayName;
        if (!auditEvent.Granted)
        {
            return $"Blocked: {who} {RefusedBecause(auditEvent.Reason)}";
        }
        return auditEvent.Action switch
        {
            "ReadFullRecord" => $"{who} viewed your record",
            "ReadEgfrLabs" => $"{who} read your lab results",
            "WriteSignedNote" => $"{who} added a note to your record",
            "PushSignedRecords" => $"{actor.OrgName} added records",
            "RedeemShareCode" => $"{who} asked to see your record",
            _ => $"{who}: {auditEvent.Action}",
        };
    }

    private static string RefusedBecause(DenyReason? reason)
    {
        return reason switch
        {
            DenyReason.GrantRevoked => "tried after you revoked access",
            DenyReason.GrantExpired => "tried after access ended",
            DenyReason.GrantPending => "tried before you approved",
            DenyReason.ShiftEnded => "tried after their shift ended",
            DenyReason.ScopeDenied => "asked for more than you allowed",
            _ => "had no access",
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
