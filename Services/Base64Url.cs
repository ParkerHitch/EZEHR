namespace EZEHR.Services;

/// <summary>Unpadded base64url (RFC 4648 §5), as JWS uses.</summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Decode(string text)
    {
        if (text.Contains('=') || text.Contains('+') || text.Contains('/'))
        {
            throw new FormatException("Not base64url.");
        }
        string base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(
            base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')
        );
    }
}
