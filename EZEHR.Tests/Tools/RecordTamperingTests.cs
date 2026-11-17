using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EZEHR.Services;
using EZEHR.Tools.Calls;

namespace EZEHR.Tests.Tools;

public sealed class RecordTamperingTests
{
    [Fact]
    public void ChangesOnlyTheSignature()
    {
        TestPki pki = new();
        X509Certificate2 lakeside = pki.Issue("lakeside.providers.ezehr");
        using JsonDocument resource = JsonDocument.Parse(
            """{"resourceType":"Observation","id":"obs-1"}"""
        );
        string jws = RecordSigner.Sign(resource.RootElement, lakeside, [], TestPki.Start);

        string corrupted = RecordTampering.CorruptSignature(jws);

        string[] original = jws.Split('.');
        string[] changed = corrupted.Split('.');
        Assert.Equal(3, changed.Length);
        Assert.Equal(original[0], changed[0]);
        Assert.Equal(original[1], changed[1]);
        Assert.NotEqual(original[2], changed[2]);
        RecordVerifier.Verify(Guid.NewGuid(), jws, pki.TrustAnchors, OrgDirectory.Demo);
        Assert.ThrowsAny<Exception>(() =>
            RecordVerifier.Verify(Guid.NewGuid(), corrupted, pki.TrustAnchors, OrgDirectory.Demo)
        );
    }
}
