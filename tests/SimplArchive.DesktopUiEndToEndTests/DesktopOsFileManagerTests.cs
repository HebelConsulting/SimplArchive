using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop "Open in file manager" button (ADR "Desktop inbox via WebDAV" + "Fix open-in-file-manager")
// mounts the WebDAV folder and opens it in the OS file manager, per-platform. The command construction is a pure
// function — assert it for each OS.
public class DesktopOsFileManagerTests
{
    private const string Url = "https://archive.example.com:8443/webdav/Inbox";

    [Fact]
    public void MacOs_mounts_the_volume_AND_opens_it_in_Finder()
    {
        var (file, args) = OsFileManager.BuildOpenCommand(Url, OsFileManager.Platform.MacOs);
        Assert.Equal("osascript", file);
        var script = string.Join("\n", args);
        Assert.Contains("mount volume", script);
        Assert.Contains(Url, script);
        // The fix: after mounting, open the disk in Finder + bring it to the front (mount volume alone opens no
        // window — the original bug).
        Assert.Contains("open d", script);
        Assert.Contains("Finder", script);
        Assert.Contains("activate", script);
    }

    [Fact]
    public void Linux_opens_the_davs_scheme_via_xdg_open()
    {
        var (file, args) = OsFileManager.BuildOpenCommand(Url, OsFileManager.Platform.Linux);
        Assert.Equal("xdg-open", file);
        Assert.Contains("davs://archive.example.com:8443/webdav/Inbox", args);
    }

    [Fact]
    public void Windows_opens_the_DavWWWRoot_unc_via_explorer()
    {
        var (file, args) = OsFileManager.BuildOpenCommand(Url, OsFileManager.Platform.Windows);
        Assert.Equal("explorer.exe", file);
        Assert.Contains(@"\\archive.example.com@SSL@8443\DavWWWRoot\webdav\Inbox", args);
    }

    [Fact]
    public void Http_url_uses_the_plain_dav_scheme_on_linux()
    {
        var (_, args) = OsFileManager.BuildOpenCommand("http://localhost:8080/webdav/Inbox", OsFileManager.Platform.Linux);
        Assert.Contains("dav://localhost:8080/webdav/Inbox", args);
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
}
