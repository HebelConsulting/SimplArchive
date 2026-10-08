using Avalonia.Media.Imaging;

namespace SimplArchive.DesktopClient.Views;

/// <summary>An image the server sent inline as a <c>data:</c> URL (a QR code it drew: two-factor enrollment, a revealed
/// scan address), turned into a bitmap. Null for no value; a malformed one throws, as any corrupt image would.</summary>
internal static class DataUrlBitmap
{
    public static Bitmap? TryDecode(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var comma = dataUrl.IndexOf(',');
        using var stream = new MemoryStream(Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl));
        return new Bitmap(stream);
    }
}
