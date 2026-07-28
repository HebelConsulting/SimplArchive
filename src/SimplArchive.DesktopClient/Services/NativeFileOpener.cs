using System.Diagnostics;

namespace SimplArchive.DesktopClient.Services;

// Downloads a document to a local temp file and opens it in the OS's associated desktop application — the
// fat-client capability a browser can't offer (no download dialog, opens in Word/Acrobat/Preview/etc.). See
// ADR "Cross-platform desktop fat client (Avalonia)".
public static class NativeFileOpener
{
    private static readonly HttpClient Http = new();

    // The per-user temp directory (ADR "S3-backed inbox", phase 2) — set to LocalFolders.TempDirectory after
    // login; falls back to the OS temp dir before then.
    public static string? TempDirectoryOverride { get; set; }

    // downloadUrl is a presigned URL (no auth header needed). fileName carries the correct extension so the
    // OS picks the right application.
    public static async Task OpenAsync(string downloadUrl, string fileName, CancellationToken cancellationToken = default)
    {
        var path = await DownloadToTempAsync(downloadUrl, fileName, cancellationToken);
        OpenPath(path);
    }

    // Writes already-fetched bytes to the temp folder and opens them — used for archive entries and inbox
    // items whose bytes come from an authenticated Api endpoint, not a presigned URL.
    public static async Task OpenBytesAsync(byte[] bytes, string fileName, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(TempDirectory(), SafeFileName(fileName));
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        OpenPath(path);
    }

    public static Task<string> DownloadToTempAsync(string downloadUrl, string fileName, CancellationToken cancellationToken = default) =>
        DownloadToAsync(downloadUrl, Path.Combine(TempDirectory(), SafeFileName(fileName)), cancellationToken);

    private static async Task<string> DownloadToAsync(string downloadUrl, string path, CancellationToken cancellationToken)
    {
        var bytes = await Http.GetByteArrayAsync(downloadUrl, cancellationToken);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    private static string TempDirectory()
    {
        var directory = TempDirectoryOverride ?? Path.Combine(Path.GetTempPath(), "SimplArchive");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string SafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    // Opens a directory in the OS file manager (used for the local inbox folder).
    public static void RevealDirectory(string path) => OpenPath(path);

    private static void OpenPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", [path]);
        }
        else
        {
            Process.Start("xdg-open", [path]);
        }
    }
}
