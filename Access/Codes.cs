using System.Security.Cryptography;

namespace EZEHR.Access;

/// <summary>Opaque random codes (invites, share offers): 8 characters with no look-alikes (no I, L, O, U, 0, 1).</summary>
public static class Codes
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    public static string New() => new(RandomNumberGenerator.GetItems<char>(Alphabet, 8));

    /// <summary>Accepts "abcd-efgh", "ABCD EFGH" and so on.</summary>
    public static string Normalize(string input) =>
        new(input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
