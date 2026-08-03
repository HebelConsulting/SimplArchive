using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Pure logic for the self-update check (issue #312, GitHub-Releases-sourced) + the "is this our server?" probe
// (issue #270) — no network: version ordering, /api server-version parsing, GitHub-release asset selection, and the
// API-root discovery-document shape check.
public class DesktopClientUpdateTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", ClientUpdateKind.UpToDate)]     // identical
    [InlineData("1.0.0", "1.2.0", ClientUpdateKind.UpdateAvailable)] // newer offered
    [InlineData("1.2.0", "1.0.0", ClientUpdateKind.UpToDate)]     // offered older → nothing to do
    [InlineData("v1.0.0", "1.2.0", ClientUpdateKind.UpdateAvailable)] // leading v tolerated
    [InlineData("1.0.0", "a1b2c3d", ClientUpdateKind.Inconclusive)] // offered is a git short-SHA
    [InlineData("a1b2c3d", "1.2.0", ClientUpdateKind.Inconclusive)] // running is a git short-SHA
    [InlineData("a1b2c3d", "a1b2c3d", ClientUpdateKind.UpToDate)]  // same dev build string
    public void Compare_orders_versions_and_flags_non_semver(string running, string offered, ClientUpdateKind expected)
    {
        Assert.Equal(expected, ClientUpdate.Compare(running, offered));
    }

    [Fact]
    public void ParseServerVersion_extracts_the_server_version_from_the_discovery_document()
    {
        // The /api discovery document carries the server's own build version (ADR 0512) alongside its links.
        var apiDoc = """
            {"serverVersion":"0.1.1","links":[{"rel":"self","href":"/api","method":"GET"}]}
            """;
        Assert.Equal("0.1.1", ClientUpdate.ParseServerVersion(apiDoc));
    }

    [Fact]
    public void ParseServerVersion_returns_null_when_absent_or_not_json()
    {
        Assert.Null(ClientUpdate.ParseServerVersion("""{"links":[]}"""));  // field absent
        Assert.Null(ClientUpdate.ParseServerVersion("""{"serverVersion":""}"""));  // blank
        Assert.Null(ClientUpdate.ParseServerVersion("<html>not our server</html>"));  // not JSON
    }

    [Fact]
    public void PickAsset_finds_the_download_url_of_the_os_arch_matching_asset()
    {
        // A GitHub Releases API payload for the tagged release, with one asset per platform.
        var releaseJson = """
            {"tag_name":"v0.1.1","assets":[
              {"name":"SimplArchive-0.1.1-linux-x64.tar.gz","browser_download_url":"https://example.test/linux"},
              {"name":"SimplArchive-0.1.1-win-x64.zip","browser_download_url":"https://example.test/win"},
              {"name":"SimplArchive-0.1.1-arm64.dmg","browser_download_url":"https://example.test/arm"},
              {"name":"SimplArchive-0.1.1-x64.dmg","browser_download_url":"https://example.test/intel"}
            ]}
            """;
        Assert.Equal("https://example.test/win", ClientUpdate.PickAsset(releaseJson, "win-x64.zip"));
        Assert.Equal("https://example.test/linux", ClientUpdate.PickAsset(releaseJson, "linux-x64.tar.gz"));
        Assert.Equal("https://example.test/arm", ClientUpdate.PickAsset(releaseJson, "arm64.dmg"));
        // The Intel .dmg suffix must NOT be caught by the arm64 asset (whose name ends "arm64.dmg", not "x64.dmg").
        Assert.Equal("https://example.test/intel", ClientUpdate.PickAsset(releaseJson, "x64.dmg"));
    }

    [Fact]
    public void PickAsset_returns_null_when_the_release_has_no_matching_asset()
    {
        // A release with only a Windows asset offers nothing to a Linux client (issue #312: don't nag).
        var releaseJson = """
            {"assets":[{"name":"SimplArchive-0.1.1-win-x64.zip","browser_download_url":"https://example.test/win"}]}
            """;
        Assert.Null(ClientUpdate.PickAsset(releaseJson, "linux-x64.tar.gz"));
        Assert.Null(ClientUpdate.PickAsset("""{"tag_name":"v0.1.1"}""", "win-x64.zip"));  // no assets array
    }

    [Fact]
    public void LooksLikeApiRoot_accepts_our_discovery_document_and_rejects_others()
    {
        var ours = """
            {"links":[{"rel":"self","href":"/api","method":"GET"},
                      {"rel":"repositories","href":"/api/repositories","method":"GET"},
                      {"rel":"openIdConfiguration","href":"/.well-known/openid-configuration","method":"GET"}]}
            """;
        Assert.True(ServerIdentity.LooksLikeApiRoot(ours));

        // A links array without the SimplArchive-specific rels is not our server.
        Assert.False(ServerIdentity.LooksLikeApiRoot("""{"links":[{"rel":"self","href":"/other"}]}"""));
        // self + openIdConfiguration but missing the repositories rel → not our server.
        Assert.False(ServerIdentity.LooksLikeApiRoot("""
            {"links":[{"rel":"self","href":"/api"},{"rel":"openIdConfiguration","href":"/.well-known/openid-configuration"}]}
            """));
        // Not even JSON.
        Assert.False(ServerIdentity.LooksLikeApiRoot("<html>hello</html>"));
        // JSON, but no links.
        Assert.False(ServerIdentity.LooksLikeApiRoot("""{"message":"hi"}"""));
    }
}
