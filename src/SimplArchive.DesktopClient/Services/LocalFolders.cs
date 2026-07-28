namespace SimplArchive.DesktopClient.Services;

// The desktop client's per-user local working folders (ADR "S3-backed inbox", phase 2):
// `~/SimplArchive/{TenantName}/{UserName}/{inbox,temp}` (macOS/Linux) or
// `%AppData%\SimplArchive\{TenantName}\{UserName}\…` (Windows). `inbox` is where the user drops files to
// upload to the server inbox; `temp` backs native-open (download a file there, then open it in its OS app).
public sealed class LocalFolders
{
    public LocalFolders(string tenantName, string userName)
    {
        var basePath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimplArchive")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SimplArchive");

        Root = Path.Combine(basePath, Sanitize(tenantName), Sanitize(userName));
        InboxDirectory = Path.Combine(Root, "inbox");
        TempDirectory = Path.Combine(Root, "temp");
        CheckoutDirectory = Path.Combine(Root, "checkout");

        Directory.CreateDirectory(InboxDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(CheckoutDirectory);
    }

    public string Root { get; }

    public string InboxDirectory { get; }

    public string TempDirectory { get; }

    // The working-copy folder for checked-out documents (ADR "Document check-out / check-in"): a document is
    // downloaded here on check-out and edited in place; the SHA-256 of the local file vs the repo version tells
    // whether it was modified. Filenames are `{stem}{ext}` derived per checkout.
    public string CheckoutDirectory { get; }

    public string CheckoutFilePath(string fileName) => Path.Combine(CheckoutDirectory, fileName);

    // A staged mask/index-data draft travels next to its file as `{name}.mask.json` (ADR "Inbox item
    // classification + preview"); these sidecars are hidden from the file list, same as on the server.
    public const string MaskSidecarSuffix = ".mask.json";

    public static bool IsMaskSidecar(string fileName) =>
        fileName.EndsWith(MaskSidecarSuffix, StringComparison.OrdinalIgnoreCase);

    public string InboxFilePath(string fileName) => Path.Combine(InboxDirectory, fileName);

    public string SidecarPath(string fileName) => Path.Combine(InboxDirectory, fileName + MaskSidecarSuffix);

    public bool HasSidecar(string fileName) => File.Exists(SidecarPath(fileName));

    // The checkout working-copy folder carries a hidden bookkeeping manifest (the synced-hash / orphan tracking,
    // ADR "Web check-out + orphaned local copy"). It's a dotfile so it never surfaces in a file view; this
    // constant + the general dotfile filter below keep it (and any hidden file) out of the local listing.
    public const string CheckoutManifestFileName = ".checkout-manifest.json";

    public IReadOnlyList<FileInfo> ListInboxFiles() =>
        Directory.Exists(InboxDirectory)
            ? new DirectoryInfo(InboxDirectory).GetFiles()
                .Where(f => !IsMaskSidecar(f.Name) && !f.Name.StartsWith('.')) // hide sidecars + hidden bookkeeping files
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList()
            : [];

    private static string Sanitize(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}
