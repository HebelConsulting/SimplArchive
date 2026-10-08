using QRCoder;

namespace SimplArchive.Api.Security;

/// <summary>
/// The one place the server draws a QR code: two-factor enrollment's <c>otpauth://</c> address and a module's revealed
/// scan address (ADR 0913). A PNG data URL, so a client shows it as an image without a QR library of its own.
/// </summary>
/// <remarks>What it encodes is usually a secret; the caller's response carries it as such (no-store, never logged).</remarks>
public static class QrCodes
{
    public static byte[] Png(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(6);
    }

    public static string PngDataUrl(string text) => $"data:image/png;base64,{Convert.ToBase64String(Png(text))}";
}
