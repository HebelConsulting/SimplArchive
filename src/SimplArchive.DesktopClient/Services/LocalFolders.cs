namespace SimplArchive.DesktopClient.Services;

// The desktop client's per-user local temp folder (ADR "S3-backed inbox", phase 2; ADR 0513):
// `~/SimplArchive/{TenantName}/{UserName}/temp` (macOS/Linux) or `%AppData%\SimplArchive\{TenantName}\{UserName}\temp`
// (Windows). `temp` backs native-open — download a file there, then open it in its OS app (NativeFileOpener).
// The former local `intray`/`checkout` working-copy folders were retired when those flows moved to WebDAV (ADR 0513).
public sealed class LocalFolders
{
    public LocalFolders(string tenantName, string userName)
    {
        var basePath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimplArchive")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SimplArchive");

        Root = Path.Combine(basePath, Sanitize(tenantName), Sanitize(userName));
        TempDirectory = Path.Combine(Root, "temp");

        Directory.CreateDirectory(TempDirectory);
    }

    public string Root { get; }

    public string TempDirectory { get; }

    private static string Sanitize(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}
