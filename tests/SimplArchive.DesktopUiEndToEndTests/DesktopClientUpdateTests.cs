using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Pure logic for the self-update check (issue #271) + the "is this our server?" probe (issue #270) — no network:
// version ordering, artifact-name parsing, and the API-root discovery-document shape check.
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
    public void ParseOfferedBuild_extracts_the_version_from_a_directory_listing()
    {
        // A UseDirectoryBrowser-style listing (only the anchor text matters here).
        var html = """
            <html><body><ul>
              <li><a href="SimplArchive-1.4.2-win-x64.zip">SimplArchive-1.4.2-win-x64.zip</a></li>
            </ul></body></html>
            """;

        var offered = ClientUpdate.ParseOfferedBuild(html);
        Assert.NotNull(offered);
        Assert.Equal("1.4.2", offered!.Value.Version);
        Assert.Equal("SimplArchive-1.4.2-win-x64.zip", offered.Value.FileName);
    }

    [Fact]
    public void ParseOfferedBuild_picks_the_highest_semver_when_several_are_offered()
    {
        // macOS ships arm64 + x64 of the same version; a stale one could linger — pick the newest.
        var html = "SimplArchive-1.0.0-arm64.dmg SimplArchive-1.2.0-arm64.dmg SimplArchive-1.2.0-x64.dmg";
        var offered = ClientUpdate.ParseOfferedBuild(html);
        Assert.Equal("1.2.0", offered!.Value.Version);
    }

    [Fact]
    public void ParseOfferedBuild_returns_null_when_nothing_matches()
    {
        Assert.Null(ClientUpdate.ParseOfferedBuild("<html><body>no client here</body></html>"));
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
