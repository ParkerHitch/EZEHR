using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace EZEHR.Security.Pki;

/// <summary>
/// Signs a FHIR resource as a compact JWS (RFC 7515). Protected header: {"alg": "ES256"|"RS256", "x5c": [leaf, chain...], "iat": unix}.
/// The payload is the resource's UTF-8 JSON.
/// </summary>
public static class RecordSigner
{
    public static string Sign(JsonElement resource, X509Certificate2 certWithKey, IReadOnlyList<X509Certificate2> chain, DateTimeOffset iat)
    {
        var ec = certWithKey.GetECDsaPrivateKey();
        var rsa = ec is null ? certWithKey.GetRSAPrivateKey() : null;
        if (ec is null && rsa is null) throw new ArgumentException("The certificate needs an EC or RSA private key.", nameof(certWithKey));

        var header = new MemoryStream();
        using (var w = new Utf8JsonWriter(header))
        {
            w.WriteStartObject();
            w.WriteString("alg", ec is not null ? "ES256" : "RS256");
            w.WriteStartArray("x5c");
            w.WriteStringValue(Convert.ToBase64String(certWithKey.RawData));
            foreach (var c in chain) w.WriteStringValue(Convert.ToBase64String(c.RawData));
            w.WriteEndArray();
            w.WriteNumber("iat", iat.ToUnixTimeSeconds());
            w.WriteEndObject();
        }

        var signingInput = $"{Jws.Encode(header.ToArray())}.{Jws.Encode(Encoding.UTF8.GetBytes(resource.GetRawText()))}";
        var data = Encoding.ASCII.GetBytes(signingInput);
        byte[] sig;
        using (ec)
        using (rsa)
            sig = ec is not null
                ? ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                : rsa!.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Jws.Encode(sig)}";
    }
}

internal static class Jws
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        if (s.Contains('=') || s.Contains('+') || s.Contains('/')) throw new FormatException("Not base64url.");
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
