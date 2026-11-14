using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EZEHR.Domain;
using EZEHR.Security.Pki;

namespace EZEHR.Tests.Pki;

public class PkiTests
{
    private readonly TestPki pki = new();

    private static JsonElement Resource(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement Egfr = Resource("""
        {"resourceType":"Observation","id":"egfr-1","status":"final",
         "code":{"coding":[{"system":"http://loinc.org","code":"98979-8"}]},
         "valueQuantity":{"value":63,"unit":"mL/min/1.73m2"}}
        """);

    [Fact]
    public void LocalCa_uses_subjectName_not_csr_subject()
    {
        var leaf = pki.Issue("lakeside.providers.ezehr", csrSubject: "CN=Mallory");
        Assert.Equal("lakeside.providers.ezehr", leaf.GetNameInfo(X509NameType.SimpleName, false));
        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == CertificateValidator.ClientAuthOid);
        Assert.DoesNotContain("Mallory", leaf.Subject);
    }

    [Fact]
    public async Task LocalCa_rejects_tampered_csr()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var der = new CertificateRequest("CN=Mallory", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        var at = IndexOf(der, Encoding.ASCII.GetBytes("Mallory"));
        der[at + 6] = (byte)'z'; // "Mallorz": still valid ASN.1, signature no longer matches
        var pem = PemEncoding.WriteString("CERTIFICATE REQUEST", der);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => pki.Ca.IssueAsync(pem, "lakeside.providers.ezehr", 365, CancellationToken.None));
    }

    [Fact]
    public void Validator_ok_for_local_chain()
    {
        var leaf = pki.Issue("lakeside.providers.ezehr");
        Assert.Equal(new CertCheck(true, null), pki.Validator.Validate(leaf, null, pki.Clock.GetUtcNow()));
    }

    [Fact]
    public void Validator_untrusted_root()
    {
        var other = new TestPki();
        var leaf = other.Issue("lakeside.providers.ezehr");
        Assert.Equal(new CertCheck(false, DenyReason.UntrustedCertificate), pki.Validator.Validate(leaf, null, pki.Clock.GetUtcNow()));
        var selfSigned = TestPki.SelfSigned("exchange.broker.example", pki.Clock.GetUtcNow());
        Assert.Equal(new CertCheck(false, DenyReason.UntrustedCertificate), pki.Validator.Validate(selfSigned, null, pki.Clock.GetUtcNow()));
    }

    [Fact]
    public void Validator_expired()
    {
        var leaf = pki.Issue("lakeside.providers.ezehr", validityDays: 30);
        var at = new DateTimeOffset(leaf.NotAfter.ToUniversalTime()).AddSeconds(1);
        Assert.Equal(new CertCheck(false, DenyReason.CertificateExpired), pki.Validator.Validate(leaf, null, at));
    }

    [Fact] // Review Focus 1
    public void Validator_tolerates_2min_skew()
    {
        var leaf = pki.Issue("lakeside.providers.ezehr");
        var notBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime());
        Assert.True(pki.Validator.Validate(leaf, null, notBefore.AddMinutes(-2)).Ok);
        Assert.Equal(new CertCheck(false, DenyReason.CertificateExpired), pki.Validator.Validate(leaf, null, notBefore.AddMinutes(-3)));
    }

    [Fact]
    public void Validator_requires_client_auth_eku()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=lakeside.providers.ezehr", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth only
        var now = pki.Clock.GetUtcNow();
        var leaf = req.Create(pki.Ca.Root, now.AddMinutes(-1), now.AddDays(10), [1, 2, 3, 4]);
        Assert.Equal(new CertCheck(false, DenyReason.UntrustedCertificate), pki.Validator.Validate(leaf, null, now));
    }

    [Fact]
    public void OrgDirectory_resolves_known_and_rejects_unknown()
    {
        var lakeside = pki.Directory.Resolve(pki.Issue("lakeside.providers.ezehr"));
        Assert.Equal(new Party(PartyKind.Provider, "urn:provider:lakeside", "Lakeside Urgent Care", "Lakeside Urgent Care"), lakeside);
        Assert.Equal("urn:app:kidney-check", pki.Directory.Resolve(pki.Issue("kidney-check.apps.ezehr"))!.SubjectUri);
        Assert.Equal(PartyKind.Platform, pki.Directory.Resolve(pki.Issue("platform.ezehr"))!.Kind);
        Assert.Null(pki.Directory.Resolve(pki.Issue("mallory.providers.ezehr")));
    }

    [Theory]
    [InlineData(false, "ES256")]
    [InlineData(true, "RS256")]
    public void Signer_verifier_roundtrip(bool rsa, string alg)
    {
        var cert = pki.Issue("maple.providers.ezehr", rsa);
        var jws = RecordSigner.Sign(Egfr, cert, [], pki.Clock.GetUtcNow());
        Assert.Equal(alg, Header(jws).GetProperty("alg").GetString());

        var id = Guid.NewGuid();
        var entry = new RecordVerifier(pki.Validator, pki.Directory).Verify(id, jws);
        Assert.Equal(id, entry.Id);
        Assert.Equal("Observation", entry.ResourceType);
        Assert.Equal("urn:provider:maple", entry.Signer.SubjectUri);
        Assert.Equal(63, entry.Resource.GetProperty("valueQuantity").GetProperty("value").GetInt32());
        Assert.Equal(pki.Clock.GetUtcNow().ToUnixTimeSeconds(), entry.SignedAt.ToUnixTimeSeconds());
        Assert.Null(entry.WrittenBy);
    }

    [Fact]
    public void Verifier_valid_after_cert_expiry_when_signed_inside_validity()
    {
        var cert = pki.Issue("maple.providers.ezehr", validityDays: 30);
        var jws = RecordSigner.Sign(Egfr, cert, [], pki.Clock.GetUtcNow());
        pki.Clock.Advance(TimeSpan.FromDays(400));
        var entry = new RecordVerifier(pki.Validator, pki.Directory).Verify(Guid.NewGuid(), jws);
        Assert.Equal("urn:provider:maple", entry.Signer.SubjectUri);
    }

    [Fact]
    public void Verifier_rejects_signature_made_outside_validity()
    {
        var cert = pki.Issue("maple.providers.ezehr", validityDays: 30);
        var jws = RecordSigner.Sign(Egfr, cert, [], pki.Clock.GetUtcNow().AddDays(60));
        Assert.Throws<RecordSignatureException>(() => new RecordVerifier(pki.Validator, pki.Directory).Verify(Guid.NewGuid(), jws));
    }

    [Fact]
    public void Verifier_rejects_tampered_payload()
    {
        var cert = pki.Issue("maple.providers.ezehr");
        var parts = RecordSigner.Sign(Egfr, cert, [], pki.Clock.GetUtcNow()).Split('.');
        var forged = Base64Url(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(FromBase64Url(parts[1])).Replace("63", "93")));
        var jws = $"{parts[0]}.{forged}.{parts[2]}";
        Assert.Throws<RecordSignatureException>(() => new RecordVerifier(pki.Validator, pki.Directory).Verify(Guid.NewGuid(), jws));
    }

    [Fact]
    public void Verifier_rejects_unknown_signer()
    {
        var cert = pki.Issue("mallory.providers.ezehr"); // trusted root, unknown CN
        var jws = RecordSigner.Sign(Egfr, cert, [], pki.Clock.GetUtcNow());
        Assert.Throws<RecordSignatureException>(() => new RecordVerifier(pki.Validator, pki.Directory).Verify(Guid.NewGuid(), jws));
    }

    [Fact]
    public void Verifier_rejects_untrusted_signer_and_garbage()
    {
        var other = new TestPki();
        var jws = RecordSigner.Sign(Egfr, other.Issue("maple.providers.ezehr"), [], pki.Clock.GetUtcNow());
        var verifier = new RecordVerifier(pki.Validator, pki.Directory);
        Assert.Throws<RecordSignatureException>(() => verifier.Verify(Guid.NewGuid(), jws));
        Assert.Throws<RecordSignatureException>(() => verifier.Verify(Guid.NewGuid(), "not.a.jws"));
        Assert.Throws<RecordSignatureException>(() => verifier.Verify(Guid.NewGuid(), "onlyonepart"));
    }

    [Fact]
    public void Verifier_rejects_alg_key_mismatch()
    {
        var cert = pki.Issue("maple.providers.ezehr"); // EC key
        var parts = RecordSigner.Sign(Egfr, cert, [], pki.Clock.GetUtcNow()).Split('.');
        var header = Encoding.UTF8.GetString(FromBase64Url(parts[0])).Replace("ES256", "RS256");
        var jws = $"{Base64Url(Encoding.UTF8.GetBytes(header))}.{parts[1]}.{parts[2]}";
        Assert.Throws<RecordSignatureException>(() => new RecordVerifier(pki.Validator, pki.Directory).Verify(Guid.NewGuid(), jws));
    }

    [Fact]
    public void Platform_signed_communication_has_written_by()
    {
        var platform = pki.Issue("platform.ezehr");
        var note = Resource("""
            {"resourceType":"Communication","id":"n1","status":"completed",
             "sender":{"display":"Dr. Rivera, Riverside Primary Care"},
             "payload":[{"contentString":"Reviewed labs."}]}
            """);
        var entry = new RecordVerifier(pki.Validator, pki.Directory).Verify(Guid.NewGuid(), RecordSigner.Sign(note, platform, [], pki.Clock.GetUtcNow()));
        Assert.Equal("Dr. Rivera, Riverside Primary Care", entry.WrittenBy);
    }

    private static JsonElement Header(string jws) => JsonDocument.Parse(FromBase64Url(jws.Split('.')[0])).RootElement.Clone();

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        throw new InvalidOperationException("not found");
    }
}
