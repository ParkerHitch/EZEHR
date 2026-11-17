namespace EZEHR.Models;

/// <summary>Every user-facing string the spec fixes. Change copy here only.</summary>
public static class Copy
{
    public const string KidneyOptIn =
        "Allow the Kidney Check app to read your lab results. It looks for slow changes no single provider would see. You can turn it off any time.";

    public const string SomethingWentWrong =
        "We couldn't finish that. Please go back and try again.";

    public static string TurnOnApp(string app)
    {
        return $"Turn on {app}";
    }

    public static string TurnOffApp(string app)
    {
        return $"Turn off {app}";
    }

    public const string NoApps = "No apps are available yet.";

    /// <summary>What turning an app on would allow, under its description on the Access page.</summary>
    public static string AppOffer(string what)
    {
        return $"If you turn it on, it can read: {what}. It can add notes to your record. You can turn it off any time.";
    }

    public const string AppsHint =
        "Apps see only what you allow, and every time one reads your record it shows in Activity.";

    public const string AccessEnded =
        "Your access to this record has ended. Ask the patient to share again.";

    public const string NotAClinician =
        "Your organization hasn't approved this account as a clinician.";

    public const string UntrustedOrg = "This organization isn't connected to EZEHR yet.";

    public const string ClinicianSignInUnavailable =
        "Clinician sign-in isn't set up on this site yet.";

    public const string ShareCodeUsed =
        "That code has already been used. Ask the patient to share again.";

    public const string ShareCodeExpired = "That code has expired. Ask the patient to share again.";

    public const string ShareCodeUnknown = "We don't recognize that code. Check it and try again.";

    public const string WaitingForApproval = "Waiting for the patient to approve on their phone…";

    public const string EmergencyAccessOffHint =
        "If you turn this on, you get an emergency code to keep where emergency staff will find it. A verified clinician who enters it can open your record for 1 hour without waiting for your approval. They must give a reason, and you see who, when and why.";

    public const string EmergencyAccessOnHint =
        "Keep this code where emergency staff will find it, like your phone's medical ID or a card in your wallet. A verified clinician who enters it can open your record for 1 hour without waiting for your approval. They must give a reason, and you see who, when and why.";

    public const string TurnOnEmergencyAccess = "Turn on emergency access";

    public const string TurnOffEmergencyAccess = "Turn off emergency access";

    public const string NewEmergencyCode = "Make a new code";

    public const string EmergencyAccessWarning =
        "Emergency access opens the record for 1 hour without the patient's approval. The patient and your organization will see your name, the time and the reason you give.";

    public const string EmergencyCodeUnknown =
        "We don't recognize that emergency code. Check it and try again.";

    public const string EmergencyReasonMissing = "Give a reason for emergency access.";

    /// <summary>What the clinician sees when emergency access can't be opened.</summary>
    public static string EmergencyAccessFailed(GrantError error)
    {
        return error switch
        {
            GrantError.EmergencyReasonMissing => EmergencyReasonMissing,
            _ => EmergencyCodeUnknown,
        };
    }

    /// <summary>An open emergency grant on the patient's Access page.</summary>
    public static string EmergencyAccessUntil(string name, string org, string time, string reason)
    {
        return $"Emergency access: {name}, {org} · until {time} · reason given: {reason}";
    }

    /// <summary>Marks an ended emergency grant on the Access page: "Dr. Rivera, Riverside Primary Care (emergency access)".</summary>
    public static string EmergencyAccessWho(string who)
    {
        return $"{who} (emergency access)";
    }

    public static string ClinicianEmergencyRecordHeader(string patient, string time)
    {
        return $"{patient} · emergency access until {time}";
    }

    public static string ClinicianSignedIn(string name, string org, string time)
    {
        return $"{name} · {org} · signed in until {time}";
    }

    public static string ClinicianRecordHeader(string patient, string time)
    {
        return $"{patient} · access until {time}";
    }

    /// <summary>What the clinician sees when a share code can't be redeemed.</summary>
    public static string ShareCodeFailed(GrantError error)
    {
        return error switch
        {
            GrantError.OfferUsed => ShareCodeUsed,
            GrantError.OfferExpired => ShareCodeExpired,
            _ => ShareCodeUnknown,
        };
    }

    public const string Pitch =
        "Epic says it blocks data to protect patients. We make blocking unnecessary.";

    public const string InviteUnusable =
        "This invite can't be used. It may have expired or already been used. Ask your provider for a new one.";

    public const string InviteLinkedElsewhere =
        "This provider's record is already linked to a different EZEHR account.";

    public const string PasskeyFailed = "That passkey didn't work. Please try again.";

    public const string PasskeyUnknown =
        "We don't recognize that passkey. Use the link from your provider to create one.";

    public static string InvitedBy(string provider)
    {
        return $"{provider} invited you to EZEHR, one place for your health record that only you control.";
    }

    public const string NotYouSignOut = "Not you? Sign out and create your own account";

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

    /// <summary>What an app's scope covers, as the Access page says it.</summary>
    public static string AppScopeWhat(GrantScope scope)
    {
        return scope switch
        {
            GrantScope.MedicationsLabsAndNotes => "medications and lab results only",
            _ => "lab results only",
        };
    }

    /// <summary>AppGrantLine without its trailing "Revoke", which the page shows as the button.</summary>
    public static string AppGrantSummary(string app, string what, string date)
    {
        string line = AppGrantLine(app, what, date);
        return line[..line.LastIndexOf(" · ", StringComparison.Ordinal)];
    }

    public const string ConnectedProvidersHint =
        "These providers can add records to your EZEHR record. They can't see it. To reconnect one, ask it for a new invite.";

    public const string NoConnectedProviders = "No providers are connected.";

    /// <summary>"Lakeside Urgent Care · can add records · linked Nov 14". Links made before dates were kept have none.</summary>
    public static string ConnectedProviderLine(string provider, string? date)
    {
        return date is null
            ? $"{provider} · can add records"
            : $"{provider} · can add records · linked {date}";
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
            "OpenEmergencyAccess" =>
                $"Emergency access: {who} opened your record without your approval. Reason given: {auditEvent.ClaimedPurpose}",
            "ReadFullRecord" when auditEvent.ClaimedPurpose == "emergency" =>
                $"{who} viewed your record (emergency access)",
            "ReadFullRecord" => $"{who} viewed your record",
            "ReadEgfrLabs" => $"{who} read your lab results",
            "ReadMedications" => $"{who} read your medications",
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

    /// <summary>Shown above the record when stored entries fail their signature or certificate check.</summary>
    public static string UnverifiedEntriesHidden(int count)
    {
        return count == 1
            ? "1 entry couldn't be verified, so it isn't shown."
            : $"{count} entries couldn't be verified, so they aren't shown.";
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

    public static string MedicationNote(string medication, int percent)
    {
        return $"You take {medication}, and your kidney function (eGFR) has dropped {percent}%. Medicines like {medication} can be hard on the kidneys. Consider asking a doctor to review it.";
    }

    public static string KidneyNote(int percent, int providers)
    {
        return $"Kidney function (eGFR) dropped {percent}% across {providers} providers. No single result was flagged. Consider asking a doctor to review it.";
    }
}
