using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EZEHR.Domain;

namespace EZEHR.Security.Pki;

public sealed class RecordSignatureException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Verifies a signed record: the signature against the x5c leaf, then the leaf's chain at the header's iat
/// (so records stay valid after their signer's certificate expires), then who the signer is.
/// </summary>
public sealed class RecordVerifier(CertificateValidator validator, OrgDirectory directory)
{
    public VerifiedEntry Verify(Guid entryId, string jws)
    {
        try
        {
            return VerifyCore(entryId, jws);
        }
        catch (RecordSignatureException) { throw; }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new RecordSignatureException("Malformed signed record.", e);
        }
    }

    private VerifiedEntry VerifyCore(Guid entryId, string jws)
    {
        var parts = jws.Split('.');
        if (parts.Length != 3) throw new RecordSignatureException("Not a compact JWS.");

        using var header = JsonDocument.Parse(Jws.Decode(parts[0]));
        var h = header.RootElement;
        var alg = h.GetProperty("alg").GetString();
        var iat = DateTimeOffset.FromUnixTimeSeconds(h.GetProperty("iat").GetInt64());
        var x5c = h.GetProperty("x5c").EnumerateArray().Select(e => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(e.GetString()!))).ToList();
        if (x5c.Count == 0) throw new RecordSignatureException("No signer certificate.");
        var leaf = x5c[0];

        var data = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var sig = Jws.Decode(parts[2]);
        var valid = alg switch
        {
            "ES256" => leaf.GetECDsaPublicKey() is { } ec && Using(ec, k => k.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            "RS256" => leaf.GetRSAPublicKey() is { } rsa && Using(rsa, k => k.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            _ => false,
        };
        if (!valid) throw new RecordSignatureException("Signature does not verify.");

        var check = validator.Validate(leaf, x5c.Skip(1), iat);
        if (!check.Ok) throw new RecordSignatureException($"Signer certificate rejected: {check.Reason}.");

        var signer = directory.Resolve(leaf) ?? throw new RecordSignatureException("Signer is not a known organization.");

        using var payload = JsonDocument.Parse(Jws.Decode(parts[1]));
        var resource = payload.RootElement.Clone();
        var resourceType = resource.GetProperty("resourceType").GetString() ?? throw new RecordSignatureException("No resourceType.");

        string? writtenBy = null;
        if (signer.Kind == PartyKind.Platform && resourceType == "Communication"
            && resource.TryGetProperty("sender", out var sender) && sender.TryGetProperty("display", out var display))
            writtenBy = display.GetString();

        return new VerifiedEntry(entryId, resourceType, resource, signer, iat, writtenBy);
    }

    private static bool Using<T>(T key, Func<T, bool> f) where T : IDisposable
    {
        using (key) return f(key);
    }
}
