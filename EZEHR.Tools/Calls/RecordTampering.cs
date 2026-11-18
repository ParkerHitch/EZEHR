using System.Buffers.Text;

namespace EZEHR.Tools.Calls;

/// <summary>Deliberately broken records, to check that the network refuses them.</summary>
public static class RecordTampering
{
    /// <summary>The same JWS with one byte of its signature flipped: header and payload are untouched.</summary>
    public static string CorruptSignature(string jws)
    {
        string[] parts = jws.Split('.');
        byte[] signature = Base64Url.DecodeFromChars(parts[2]);
        signature[0] ^= 0xFF;
        return $"{parts[0]}.{parts[1]}.{Base64Url.EncodeToString(signature)}";
    }
}
