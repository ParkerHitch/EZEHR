using QRCoder;

namespace EZEHR.Services;

/// <summary>The share code as a QR image. It holds only the code: no data, no keys.</summary>
public static class ShareCodeQr
{
    public static string PngDataUri(string formattedCode)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(formattedCode, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(10);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }
}
