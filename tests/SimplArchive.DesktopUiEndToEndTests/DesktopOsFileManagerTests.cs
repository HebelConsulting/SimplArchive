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
}
