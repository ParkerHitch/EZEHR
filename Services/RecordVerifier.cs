using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EZEHR.Models;

namespace EZEHR.Services;

public sealed class RecordSignatureException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Verifies a signed record: the signature against the x5c leaf, then the leaf's chain at the header's iat
/// (so records stay valid after their signer's certificate expires), then who the signer is.
/// Throws RecordSignatureException on any failure.
/// </summary>
public static class RecordVerifier
{
    public static VerifiedEntry Verify(Guid entryId, string jws, TrustAnchors trustAnchors)
    {
        try
        {
            return VerifyCore(entryId, jws, trustAnchors);
        }
        catch (RecordSignatureException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is FormatException
                        or JsonException
                        or CryptographicException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or ArgumentException
            )
        {
            throw new RecordSignatureException("Malformed signed record.", exception);
        }
    }

    private static VerifiedEntry VerifyCore(Guid entryId, string jws, TrustAnchors trustAnchors)
    {
        string[] parts = jws.Split('.');
        if (parts.Length != 3)
        {
            throw new RecordSignatureException("Not a compact JWS.");
        }

        using JsonDocument header = JsonDocument.Parse(Base64Url.Decode(parts[0]));
        JsonElement headerRoot = header.RootElement;
        string? algorithm = headerRoot.GetProperty("alg").GetString();
        DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeSeconds(
            headerRoot.GetProperty("iat").GetInt64()
        );
        List<X509Certificate2> x5c = headerRoot
            .GetProperty("x5c")
            .EnumerateArray()
            .Select(element =>
                X509CertificateLoader.LoadCertificate(
                    Convert.FromBase64String(element.GetString()!)
                )
            )
            .ToList();
        if (x5c.Count == 0)
        {
            throw new RecordSignatureException("No signer certificate.");
        }
        X509Certificate2 leaf = x5c[0];

        byte[] data = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        byte[] signature = Base64Url.Decode(parts[2]);
        if (!SignatureVerifies(algorithm, leaf, data, signature))
        {
            throw new RecordSignatureException("Signature does not verify.");
        }

        CertCheck check = CertificateValidator.Validate(leaf, x5c.Skip(1), issuedAt, trustAnchors);
        if (!check.Ok)
        {
            throw new RecordSignatureException($"Signer certificate rejected: {check.Reason}.");
        }

        Party signer = OrgDirectory.Resolve(leaf)
            ?? throw new RecordSignatureException("Signer is not a known organization.");

        using JsonDocument payload = JsonDocument.Parse(Base64Url.Decode(parts[1]));
        JsonElement resource = payload.RootElement.Clone();
        string resourceType = resource.GetProperty("resourceType").GetString()
            ?? throw new RecordSignatureException("No resourceType.");

        string? writtenBy = null;
        if (
            signer.Kind == PartyKind.Platform
            && resourceType == "Communication"
            && resource.TryGetProperty("sender", out JsonElement sender)
            && sender.TryGetProperty("display", out JsonElement display)
        )
        {
            writtenBy = display.GetString();
        }

        return new VerifiedEntry(entryId, resourceType, resource, signer, issuedAt, writtenBy);
    }

    private static bool SignatureVerifies(
        string? algorithm,
        X509Certificate2 leaf,
        byte[] data,
        byte[] signature
    )
    {
        if (algorithm == "ES256")
        {
            using ECDsa? ecdsa = leaf.GetECDsaPublicKey();
            return ecdsa is not null
                && ecdsa.VerifyData(
                    data,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                );
        }
        if (algorithm == "RS256")
        {
            using RSA? rsa = leaf.GetRSAPublicKey();
            return rsa is not null
                && rsa.VerifyData(
                    data,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1
                );
        }
        return false;
    }
}
