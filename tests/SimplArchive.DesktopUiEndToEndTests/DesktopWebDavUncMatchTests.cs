using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// "Is this mapped drive OUR server?" answered from the remote UNC's host (#820) — the same host rule the
// macOS mount matching follows, replacing a volume-label test that a WebDAV mapping (commonly label-less)
// never satisfied. Pure, so every wire shape Windows produces is pinned here without a Windows machine:
// the @SSL marker, explicit ports, default ports by scheme, case, and the DavWWWRoot tail.
public class DesktopWebDavUncMatchTests
{
    [Theory]
    [InlineData(@"\\demo.simplarchive.dev@SSL@443\DavWWWRoot\SimplArchive", "https://demo.simplarchive.dev/SimplArchive", true)]
    [InlineData(@"\\demo.simplarchive.dev@SSL\DavWWWRoot\SimplArchive", "https://demo.simplarchive.dev/SimplArchive", true)]  // @SSL without an explicit port implies 443
    [InlineData(@"\\DEMO.SIMPLARCHIVE.DEV@SSL@443\DavWWWRoot\SimplArchive", "https://demo.simplarchive.dev/", true)]          // host is case-insensitive
    [InlineData(@"\\localhost@8080\DavWWWRoot\SimplArchive", "http://localhost:8080/SimplArchive", true)]
    [InlineData(@"\\localhost@80\DavWWWRoot\SimplArchive", "http://localhost/SimplArchive", true)]
    [InlineData(@"\\other.example.org@SSL@443\DavWWWRoot\SimplArchive", "https://demo.simplarchive.dev/", false)]             // another server's mount is NOT ours (the macOS lesson)
    [InlineData(@"\\demo.simplarchive.dev@SSL@8443\DavWWWRoot\SimplArchive", "https://demo.simplarchive.dev/", false)]        // same host, different port = a different deployment
    [InlineData(@"\\fileserver\share", "https://demo.simplarchive.dev/", false)]                                              // an ordinary SMB mapping
    public void A_remote_unc_matches_exactly_our_server(string remote, string server, bool expected)
        => Assert.Equal(expected, OsFileManager.UncMatchesServer(remote, new Uri(server)));
}
