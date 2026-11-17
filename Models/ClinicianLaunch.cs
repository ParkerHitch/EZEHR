namespace EZEHR.Models;

/// <summary>
/// An organization's system opening EZEHR for one of its clinicians, from one patient's chart, for one visit. It is
/// how the organization takes part in each access: without an open launch a clinician can't ask for anyone's record.
/// ProviderPatientId and EncounterId are the organization's own ids for the chart and the visit. EndsAt is when the
/// launch stops being usable for new requests; access the patient already gave runs on its own clock.
/// </summary>
public sealed record ClinicianLaunch(
    Guid Id,
    string ProviderUri,
    string ClinicianUri,
    string ProviderPatientId,
    string EncounterId,
    DateTimeOffset EndsAt
);
