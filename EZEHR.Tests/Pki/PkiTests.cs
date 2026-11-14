using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;

namespace EZEHR.Tests.Pki;

public class PkiTests
{
    private static readonly JsonElement Egfr = Resource(
        """
        {"resourceType":"Observation","id":"egfr-1","status":"final",
         "code":{"coding":[{"system":"http://loinc.org","code":"98979-8"}]},
         "valueQuantity":{"value":63,"unit":"mL/min/1.73m2"}}
        """
    );

    private readonly TestPki _pki = new();

    private DateTimeOffset Now
    {
        get { return _pki.Clock.GetUtcNow(); }
    }

    [Fact]
    public void LocalCa_uses_subjectName_not_csr_subject()
    {
        X509Certificate2 leaf = _pki.Issue("lakeside.providers.ezehr", csrSubject: "CN=Mallory");
        Assert.Equal("lakeside.providers.ezehr", leaf.GetNameInfo(X509NameType.SimpleName, false));
        X509EnhancedKeyUsageExtension eku = leaf
            .Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), oid =>
            oid.Value == CertificateValidator.ClientAuthenticationOid
        );
        Assert.DoesNotContain("Mallory", leaf.Subject);
    }

    [Fact]
    public async Task LocalCa_rejects_tampered_csr()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = new CertificateRequest(
            "CN=Mallory",
            key,
            HashAlgorithmName.SHA256
        ).CreateSigningRequest();
        int at = IndexOf(der, Encoding.ASCII.GetBytes("Mallory"));
        // "Mallorz": still valid ASN.1, but the signature no longer matches.
        der[at + 6] = (byte)'z';
        string pem = PemEncoding.WriteString("CERTIFICATE REQUEST", der);
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            _pki.Ca.IssueAsync(pem, "lakeside.providers.ezehr", 365, CancellationToken.None)
        );
    }

    [Fact]
    public void Validator_ok_for_local_chain()
    {
        X509Certificate2 leaf = _pki.Issue("lakeside.providers.ezehr");
        Assert.Equal(new CertificateCheck(true, null), Validate(leaf, Now));
    }

    [Fact]
    public void Validator_untrusted_root()
    {
        TestPki other = new();
        X509Certificate2 leaf = other.Issue("lakeside.providers.ezehr");
        Assert.Equal(
            new CertificateCheck(false, DenyReason.UntrustedCertificate),
            Validate(leaf, Now)
        );
        X509Certificate2 selfSigned = TestPki.SelfSigned("exchange.broker.example", Now);
        Assert.Equal(
            new CertificateCheck(false, DenyReason.UntrustedCertificate),
            Validate(selfSigned, Now)
        );
    }

    [Fact]
    public void Validator_expired()
    {
        X509Certificate2 leaf = _pki.Issue("lakeside.providers.ezehr", validityDays: 30);
        DateTimeOffset at = new DateTimeOffset(leaf.NotAfter.ToUniversalTime()).AddSeconds(1);
        Assert.Equal(
            new CertificateCheck(false, DenyReason.CertificateExpired),
            Validate(leaf, at)
        );
    }

    [Fact] // Review Focus 1
    public void Validator_tolerates_2min_skew()
    {
        X509Certificate2 leaf = _pki.Issue("lakeside.providers.ezehr");
        DateTimeOffset notBefore = new(leaf.NotBefore.ToUniversalTime());
        Assert.True(Validate(leaf, notBefore.AddMinutes(-2)).Valid);
        Assert.Equal(
            new CertificateCheck(false, DenyReason.CertificateExpired),
            Validate(leaf, notBefore.AddMinutes(-3))
        );
    }

    [Fact]
    public void Validator_requires_client_auth_eku()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(
            "CN=lakeside.providers.ezehr",
            key,
            HashAlgorithmName.SHA256
        );
        // serverAuth only.
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)
        );
        X509Certificate2 leaf = request.Create(
            _pki.Ca.Root,
            Now.AddMinutes(-1),
            Now.AddDays(10),
            [1, 2, 3, 4]
        );
        Assert.Equal(
            new CertificateCheck(false, DenyReason.UntrustedCertificate),
            Validate(leaf, Now)
        );
    }

    [Fact]
    public void Validator_trusts_nothing_without_anchors()
    {
        X509Certificate2 leaf = _pki.Issue("lakeside.providers.ezehr");
        CertificateCheck check = CertificateValidator.Validate(
            leaf,
            null,
            Now,
            new TrustAnchors([], [])
        );
        Assert.Equal(new CertificateCheck(false, DenyReason.UntrustedCertificate), check);
    }

    [Fact]
    public void OrgDirectory_resolves_known_and_rejects_unknown()
    {
        Party? lakeside = OrgDirectory.Demo.FindByCertificate(
            _pki.Issue("lakeside.providers.ezehr")
        );
        Assert.Equal(
            new Party(
                PartyKind.Provider,
                "urn:provider:lakeside",
                "Lakeside Urgent Care",
                "Lakeside Urgent Care",
                "urn:provider:lakeside"
            ),
            lakeside
        );
        Assert.Equal(
            "urn:app:kidney-check",
            OrgDirectory.Demo.FindByCertificate(_pki.Issue("kidney-check.apps.ezehr"))!.SubjectUri
        );
        Assert.Equal(
            "urn:provider:riverside",
            OrgDirectory.Demo.FindByCertificate(_pki.Issue("riverside.providers.ezehr"))!.SubjectUri
        );
        Assert.Null(OrgDirectory.Demo.FindByCertificate(_pki.Issue("platform.ezehr")));
        Assert.Null(OrgDirectory.Demo.FindByCertificate(_pki.Issue("mallory.providers.ezehr")));
    }

    [Theory]
    [InlineData(false, "ES256")]
    [InlineData(true, "RS256")]
    public void Signer_verifier_roundtrip(bool rsa, string algorithm)
    {
        X509Certificate2 certificate = _pki.Issue("maple.providers.ezehr", rsa);
        string jws = RecordSigner.Sign(Egfr, certificate, [], Now);
        Assert.Equal(algorithm, Header(jws).GetProperty("alg").GetString());

        Guid id = Guid.NewGuid();
        VerifiedEntry entry = Verify(id, jws);
        Assert.Equal(id, entry.Id);
        Assert.Equal("Observation", entry.ResourceType);
        Assert.Equal("urn:provider:maple", entry.Signer.SubjectUri);
        Assert.Equal(
            63,
            entry.Resource.GetProperty("valueQuantity").GetProperty("value").GetInt32()
        );
        Assert.Equal(Now.ToUnixTimeSeconds(), entry.SignedAt.ToUnixTimeSeconds());
        Assert.Null(entry.WrittenBy);
    }

    [Fact]
    public void Verifier_valid_after_cert_expiry_when_signed_inside_validity()
    {
        X509Certificate2 certificate = _pki.Issue("maple.providers.ezehr", validityDays: 30);
        string jws = RecordSigner.Sign(Egfr, certificate, [], Now);
        _pki.Clock.Advance(TimeSpan.FromDays(400));
        VerifiedEntry entry = Verify(Guid.NewGuid(), jws);
        Assert.Equal("urn:provider:maple", entry.Signer.SubjectUri);
    }

    [Fact]
    public void Verifier_rejects_signature_made_outside_validity()
    {
        X509Certificate2 certificate = _pki.Issue("maple.providers.ezehr", validityDays: 30);
        string jws = RecordSigner.Sign(Egfr, certificate, [], Now.AddDays(60));
        Assert.Throws<RecordSignatureException>(() => Verify(Guid.NewGuid(), jws));
    }

    [Fact]
    public void Verifier_rejects_tampered_payload()
    {
        X509Certificate2 certificate = _pki.Issue("maple.providers.ezehr");
        string[] parts = RecordSigner.Sign(Egfr, certificate, [], Now).Split('.');
        string payload = Encoding.UTF8.GetString(FromBase64Url(parts[1])).Replace("63", "93");
        string jws = $"{parts[0]}.{ToBase64Url(Encoding.UTF8.GetBytes(payload))}.{parts[2]}";
        Assert.Throws<RecordSignatureException>(() => Verify(Guid.NewGuid(), jws));
    }

    [Fact]
    public void Verifier_rejects_unknown_signer()
    {
        // A trusted root, but an unknown CN.
        X509Certificate2 certificate = _pki.Issue("mallory.providers.ezehr");
        string jws = RecordSigner.Sign(Egfr, certificate, [], Now);
        Assert.Throws<RecordSignatureException>(() => Verify(Guid.NewGuid(), jws));
    }

    [Fact]
    public void Verifier_rejects_untrusted_signer_and_garbage()
    {
        TestPki other = new();
        string jws = RecordSigner.Sign(Egfr, other.Issue("maple.providers.ezehr"), [], Now);
        Assert.Throws<RecordSignatureException>(() => Verify(Guid.NewGuid(), jws));
        Assert.Throws<RecordSignatureException>(() => Verify(Guid.NewGuid(), "not.a.jws"));
        Assert.Throws<RecordSignatureException>(() => Verify(Guid.NewGuid(), "onlyonepart"));
    }

    [Fact]
    public void Verifier_rejects_chain_to_attacker_root_supplied_in_x5c()
    {
        TestPki attacker = new();
        X509Certificate2 leaf = attacker.Issue("maple.providers.ezehr");
        X509Certificate2 attackerRoot = X509CertificateLoader.LoadCertificate(
            attacker.Ca.Root.RawData
        );
        string jws = RecordSigner.Sign(Egfr, leaf, [attackerRoot], Now);
        Assert.Throws<RecordSignatureException>(() => Verify(Guid.NewGuid(), jws));
        CertificateCheck check = CertificateValidator.Validate(
            leaf,
            [attackerRoot],
            Now,
            _pki.TrustAnchors
        );
        Assert.Equal(DenyReason.UntrustedCertificate, check.Reason);
    }

    [Fact]
    public void Verifier_rejects_alg_key_mismatch()
    {
        // An EC key, claimed as RS256.
        X509Certificate2 certificate = _pki.Issue("maple.providers.ezehr");
        string[] parts = RecordSigner.Sign(Egfr, certificate, [], Now).Split('.');
        string header = Encoding.UTF8.GetString(FromBase64Url(parts[0])).Replace("ES256", "RS256");
        string jws = $"{ToBase64Url(Encoding.UTF8.GetBytes(header))}.{parts[1]}.{parts[2]}";
        Assert.Throws<RecordSignatureException>(() => Verify(Guid.NewGuid(), jws));
    }

    [Fact]
    public void Provider_signed_communication_has_written_by()
    {
        X509Certificate2 riverside = _pki.Issue("riverside.providers.ezehr");
        JsonElement note = Resource(
            """
            {"resourceType":"Communication","id":"n1","status":"completed",
             "sender":{"display":"Dr. Rivera"},
             "payload":[{"contentString":"Reviewed labs."}]}
            """
        );
        VerifiedEntry entry = Verify(Guid.NewGuid(), RecordSigner.Sign(note, riverside, [], Now));
        Assert.Equal("Dr. Rivera", entry.WrittenBy);
        Assert.Equal("urn:provider:riverside", entry.Signer.SubjectUri);
    }

    [Fact]
    public void Provider_signed_referral_has_written_by()
    {
        X509Certificate2 riverside = _pki.Issue("riverside.providers.ezehr");
        JsonElement referral = Resource(
            """
            {"resourceType":"ServiceRequest","id":"r1","status":"active","intent":"order",
             "code":{"text":"Referral to nephrology"},
             "requester":{"display":"Dr. Rivera"}}
            """
        );
        VerifiedEntry entry = Verify(
            Guid.NewGuid(),
            RecordSigner.Sign(referral, riverside, [], Now)
        );
        Assert.Equal("Dr. Rivera", entry.WrittenBy);
    }

    private CertificateCheck Validate(X509Certificate2 leaf, DateTimeOffset at)
    {
        return CertificateValidator.Validate(leaf, null, at, _pki.TrustAnchors);
    }

    private VerifiedEntry Verify(Guid entryId, string jws)
    {
        return RecordVerifier.Verify(entryId, jws, _pki.TrustAnchors, OrgDirectory.Demo);
    }

    private static JsonElement Resource(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static JsonElement Header(string jws)
    {
        return JsonDocument.Parse(FromBase64Url(jws.Split('.')[0])).RootElement.Clone();
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] FromBase64Url(string text)
    {
        string base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(
            base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')
        );
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }
        throw new InvalidOperationException("not found");
    }
}
