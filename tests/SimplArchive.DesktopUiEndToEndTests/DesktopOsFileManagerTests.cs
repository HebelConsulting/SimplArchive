using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop "Open in file manager" button (ADR "Desktop inbox via WebDAV" + "Fix open-in-file-manager")
// mounts the WebDAV folder and opens it in the OS file manager, per-platform. The command construction is a pure
// function — assert it for each OS.
public class DesktopOsFileManagerTests
{
    private const string Url = "https://archive.example.com:8443/SimplArchive/Intray";

    [Fact]
    public void MacOs_only_mounts_and_names_no_volume_path()
    {
        var (file, args) = OsFileManager.BuildOpenCommand(Url, OsFileManager.Platform.MacOs);
        Assert.Equal("osascript", file);
        var script = string.Join("\n", args);
        Assert.Contains("mount volume", script);
        Assert.Contains(Url, script);

        // No /Volumes path may appear here. macOS SUFFIXES a colliding volume name — a second SimplArchive
        // mounts at /Volumes/SimplArchive-1 — so any path derived from the URL is a guess, and the guess opened
        // the WRONG SERVER'S files when two were mounted. The caller asks the OS where the mount landed instead,
        // which it can only do after the mount has finished.
        Assert.DoesNotContain("/Volumes", script);
    }

    // The parsing behind "where did the OS actually put this mount?" — pinned without running `mount`.
    [Fact]
    public void The_mount_point_is_taken_from_the_os_not_derived_from_the_url()
    {
        const string output = """
            /dev/disk3s1s1 on / (apfs, sealed, local, read-only, journaled)
            http://localhost:8080/SimplArchive/ on /Volumes/SimplArchive (webdav, nodev, noexec, nosuid, mounted by flhe)
            https://demo.simplarchive.dev/SimplArchive/ on /Volumes/SimplArchive-1 (webdav, nodev, noexec, nosuid, mounted by flhe)
            /dev/disk5s1 on /Volumes/My Backup Disk (hfs, local, nodev)
            """;

        var entries = OsFileManager.ParseMountOutput(output).ToList();

        // The real case that broke it: two servers whose URLs BOTH end in /SimplArchive, distinguished only by
        // host. Deriving the volume name from the last path segment gives "SimplArchive" for both.
        Assert.Equal("/Volumes/SimplArchive",
            entries.Single(e => e.Source == "http://localhost:8080/SimplArchive/").Point);
        Assert.Equal("/Volumes/SimplArchive-1",
            entries.Single(e => e.Source == "https://demo.simplarchive.dev/SimplArchive/").Point);

        // A mount point may contain spaces, so the point cannot be parsed as "the token after ' on '".
        Assert.Equal("/Volumes/My Backup Disk", entries.Single(e => e.Source == "/dev/disk5s1").Point);
    }

    // `mount volume` RETURNS NOTHING. Capturing its result fails with "The variable d is not defined" (-2753) —
    // and most reliably when the volume is ALREADY mounted, i.e. for anyone who uses the button twice. This was
    // a real failure in the field, and the previous version of this test asserted the broken form, so it would
    // have held the bug in place rather than catching it.
    [Fact]
    public void The_mount_result_is_never_captured_in_a_variable()
    {
        var scripts = new[]
        {
            string.Join("\n", OsFileManager.BuildOpenCommand(Url, OsFileManager.Platform.MacOs).Arguments),
            string.Join("\n", OsFileManager.BuildOpenWebDavFileCommand(Url, "Personal/Intray", OsFileManager.Platform.MacOs).Arguments),
        };

        foreach (var script in scripts)
        {
            Assert.DoesNotContain("set d to", script);
            Assert.DoesNotContain("open d", script);

            // The general form of the same mistake: assigning the command's result at all.
            Assert.DoesNotContain("to (mount volume", script);
        }
    }

    [Fact]
    public void Linux_opens_the_davs_scheme_via_xdg_open()
    {
        var (file, args) = OsFileManager.BuildOpenCommand(Url, OsFileManager.Platform.Linux);
        Assert.Equal("xdg-open", file);
        Assert.Contains("davs://archive.example.com:8443/SimplArchive/Intray", args);
    }

    [Fact]
    public void Windows_opens_the_DavWWWRoot_unc_via_explorer()
    {
        var (file, args) = OsFileManager.BuildOpenCommand(Url, OsFileManager.Platform.Windows);
        Assert.Equal("explorer.exe", file);
        // The canonical mount path — the /webdav alias is retired (#794), and this pinned it for months after.
        Assert.Contains(@"\\archive.example.com@SSL@8443\DavWWWRoot\SimplArchive\Intray", args);
    }

    [Fact]
    public void Http_url_uses_the_plain_dav_scheme_on_linux()
    {
        var (_, args) = OsFileManager.BuildOpenCommand("http://localhost:8080/SimplArchive/Intray", OsFileManager.Platform.Linux);
        Assert.Contains("dav://localhost:8080/SimplArchive/Intray", args);
        Assert.DoesNotContain(args, a => a.Contains("davs://"));
    }

    // The Check-out Edit button (ADR 0513) opens a SINGLE file inside the mount in its native app.
    private const string Base = "https://archive.example.com:8443/SimplArchive";
    private const string Rel = "Personal/Check-out/Report Q1.txt"; // a space, to exercise escaping

    [Fact]
    public void MacOs_edit_mounts_then_opens_the_posix_path_under_Volumes()
    {
        var (file, args) = OsFileManager.BuildOpenWebDavFileCommand(Base, Rel, OsFileManager.Platform.MacOs);
        Assert.Equal("osascript", file);
        var script = string.Join("\n", args);
        Assert.Contains("mount volume \"https://archive.example.com:8443/SimplArchive\"", script);
        // Volume name = the URL's last path segment; the file opens by its POSIX path under /Volumes.
        Assert.Contains("/Volumes/SimplArchive/Personal/Check-out/Report Q1.txt", script);
        Assert.Contains("open ", script);
    }

    [Fact]
    public void Linux_edit_opens_the_url_encoded_davs_file_url()
    {
        var (file, args) = OsFileManager.BuildOpenWebDavFileCommand(Base, Rel, OsFileManager.Platform.Linux);
        Assert.Equal("xdg-open", file);
        Assert.Contains("davs://archive.example.com:8443/SimplArchive/Personal/Check-out/Report%20Q1.txt", args);
    }

    [Fact]
    public void Windows_edit_starts_the_DavWWWRoot_unc_file_path()
    {
        var (file, args) = OsFileManager.BuildOpenWebDavFileCommand(Base, Rel, OsFileManager.Platform.Windows);
        Assert.Equal("cmd.exe", file);
        Assert.Contains(@"\\archive.example.com@SSL@8443\DavWWWRoot\SimplArchive\Personal\Check-out\Report Q1.txt", args);
    }

    // The desktop Intray / Check-out "Open in file manager" buttons deep-open a FOLDER within the single mount
    // (OpenWebDavFolderAsync → BuildOpenWebDavFileCommand with a folder relative path).
    [Fact]
    public void Folder_deep_open_targets_the_subfolder_within_the_single_mount()
    {
        var (mac, macArgs) = OsFileManager.BuildOpenWebDavFileCommand(Base, "Personal/Check-out", OsFileManager.Platform.MacOs);
        Assert.Equal("osascript", mac);
        Assert.Contains("/Volumes/SimplArchive/Personal/Check-out", string.Join("\n", macArgs));

        var (_, linuxArgs) = OsFileManager.BuildOpenWebDavFileCommand(Base, "Personal/Intray", OsFileManager.Platform.Linux);
        Assert.Contains("davs://archive.example.com:8443/SimplArchive/Personal/Intray", linuxArgs);

        var (_, winArgs) = OsFileManager.BuildOpenWebDavFileCommand(Base, "Personal/Check-out", OsFileManager.Platform.Windows);
        Assert.Contains(@"\\archive.example.com@SSL@8443\DavWWWRoot\SimplArchive\Personal\Check-out", winArgs);
    }

    // ---- Already mounted: open the folder on disk, do NOT re-mount ----------------------------------------
    //
    // The Intray / Check-out button's whole point is that a user who already has the volume lands in that tab's
    // folder immediately (ADR "One WebDAV button per tab, deep-linked"). Re-issuing a mount would ask the OS to
    // redo work it has done, which on macOS is the difference between Finder coming forward and a spinner.

    [Theory]
    [InlineData(OsFileManager.Platform.MacOs, "open")]
    [InlineData(OsFileManager.Platform.Linux, "xdg-open")]
    public void An_already_mounted_folder_is_opened_directly_without_mounting(OsFileManager.Platform platform, string expected)
    {
        var (file, args) = OsFileManager.BuildOpenLocalFolderCommand("/Volumes/SimplArchive/Personal/Intray", platform);

        Assert.Equal(expected, file);
        Assert.Contains("/Volumes/SimplArchive/Personal/Intray", args);

        // The assertion that matters: nothing here mounts anything.
        Assert.DoesNotContain("mount volume", string.Join("\n", args));
        Assert.DoesNotContain(args, a => a.Contains("davs://", StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_opens_the_mapped_drive_path_with_backslashes()
    {
        var (file, args) = OsFileManager.BuildOpenLocalFolderCommand("Z:/Personal/Check-out", OsFileManager.Platform.Windows);

        Assert.Equal("explorer.exe", file);
        Assert.Contains(@"Z:\Personal\Check-out", args);
    }

    // The already-mounted check must not confuse two servers. This is the bug that had no error message: a
    // client connected to demo found localhost's mount, said "already mounted", and opened the WRONG archive.
    [Theory]
    [InlineData("http://localhost:8080", "/Volumes/SimplArchive")]
    [InlineData("https://demo.simplarchive.dev", "/Volumes/SimplArchive-1")]
    public void The_already_mounted_check_matches_the_server_not_the_volume_name(string serverUrl, string expected)
    {
        const string output = """
            http://localhost:8080/SimplArchive/ on /Volumes/SimplArchive (webdav, nodev, noexec, nosuid, mounted by flhe)
            https://demo.simplarchive.dev/SimplArchive/ on /Volumes/SimplArchive-1 (webdav, nodev, noexec, nosuid, mounted by flhe)
            """;

        var server = new Uri(serverUrl);
        var match = OsFileManager.ParseMountOutput(output)
            .Where(e => e.Point.StartsWith("/Volumes/", StringComparison.Ordinal))
            .Where(e => Uri.TryCreate(e.Source, UriKind.Absolute, out var src)
                        && string.Equals(src.Host, server.Host, StringComparison.OrdinalIgnoreCase)
                        && src.Port == server.Port)
            .Select(e => e.Point)
            .FirstOrDefault();

        Assert.Equal(expected, match);
    }
}
