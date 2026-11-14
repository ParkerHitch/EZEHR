using System.Security.Cryptography.X509Certificates;

namespace EZEHR.Models;

public sealed record CertificateCheck(bool Valid, DenyReason? Reason);

/// <summary>The issued leaf (public only) and its chain up to and including the root.</summary>
public sealed record IssuedCertificate(
    X509Certificate2 Leaf,
    IReadOnlyList<X509Certificate2> Chain
);

/// <summary>The roots we trust (the EZCA root) and the issuing certificates that chain to them.</summary>
public sealed record TrustAnchors(
    IReadOnlyList<X509Certificate2> Roots,
    IReadOnlyList<X509Certificate2> Intermediates
);
