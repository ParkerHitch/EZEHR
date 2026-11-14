using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace EZEHR.Services;

/// <summary>
/// Signs a FHIR resource as a compact JWS (RFC 7515). Protected header: {"alg": "ES256"|"RS256", "x5c": [leaf, chain...], "iat": unix}.
/// The payload is the resource's UTF-8 JSON.
/// </summary>
public static class RecordSigner
{
    public static string Sign(
        JsonElement resource,
        X509Certificate2 certificateWithKey,
        IReadOnlyList<X509Certificate2> chain,
        DateTimeOffset issuedAt
    )
    {
        using ECDsa? ecdsa = certificateWithKey.GetECDsaPrivateKey();
        using RSA? rsa = ecdsa is null ? certificateWithKey.GetRSAPrivateKey() : null;
        if (ecdsa is null && rsa is null)
        {
            throw new ArgumentException(
                "The certificate needs an EC or RSA private key.",
                nameof(certificateWithKey)
            );
        }

        MemoryStream header = new();
        using (Utf8JsonWriter writer = new(header))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", ecdsa is not null ? "ES256" : "RS256");
            writer.WriteStartArray("x5c");
            writer.WriteStringValue(Convert.ToBase64String(certificateWithKey.RawData));
            foreach (X509Certificate2 certificate in chain)
            {
                writer.WriteStringValue(Convert.ToBase64String(certificate.RawData));
            }
            writer.WriteEndArray();
            writer.WriteNumber("iat", issuedAt.ToUnixTimeSeconds());
            writer.WriteEndObject();
        }

        string payload = Base64Url.Encode(Encoding.UTF8.GetBytes(resource.GetRawText()));
        string signingInput = $"{Base64Url.Encode(header.ToArray())}.{payload}";
        byte[] data = Encoding.ASCII.GetBytes(signingInput);
        byte[] signature = ecdsa is not null
            ? ecdsa.SignData(
                data,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            )
            : rsa!.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url.Encode(signature)}";
    }
}
