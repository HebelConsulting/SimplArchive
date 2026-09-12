using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopUiEndToEndTests;

// The window title names the server the client is connected to (#1123).
//
// Reported from use: once the client is started, there is no way to see which server it is talking to. The
// address is chosen at the logon window and never shown again — so a client pointed at a test server and one
// pointed at the live demo are indistinguishable, including in the dock and the alt-tab list, which is where
// the mistake actually gets made.
//
// Tested here rather than by a screenshot because a native title bar is NOT rendered headlessly (the same
// reason the app icon has its own --icon-test): a capture would show the window and prove nothing about its
// title. So the string is built by a pure helper and asserted directly.
public class ServerLabelTests
{
    [Theory]
    // The port is KEPT when there is one: on a developer machine it is the only thing telling two servers
    // apart, so dropping it would collapse exactly the case this exists for.
    [InlineData("http://localhost:8080", "SimplArchive — localhost:8080")]
    [InlineData("http://localhost:5000/", "SimplArchive — localhost:5000")]
    // ...and dropped when it is the scheme's default, where it is noise on every entry alike.
    [InlineData("https://demo.simplarchive.dev", "SimplArchive — demo.simplarchive.dev")]
    [InlineData("https://demo.simplarchive.dev:443/", "SimplArchive — demo.simplarchive.dev")]
    [InlineData("http://kiosk.local:80", "SimplArchive — kiosk.local")]
    // A path is not part of what distinguishes one server from another, and spends title width saying so.
    [InlineData("https://host.example/api/", "SimplArchive — host.example")]
    public void The_title_names_the_host(string apiBaseUrl, string expected) =>
        Assert.Equal(expected, ServerLabel.TitleFor(apiBaseUrl));

    // A title is decoration: nothing should fail over one, so anything unreadable degrades to the bare product
    // name rather than throwing or showing a broken string.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/only")]
    public void An_unreadable_address_leaves_just_the_product_name(string? apiBaseUrl) =>
        Assert.Equal("SimplArchive", ServerLabel.TitleFor(apiBaseUrl));

    // Credentials in a URL must never reach a title bar, where they would be shouldered, screenshotted and
    // recorded by every window-list tool on the machine. Host-only is what keeps them out; Authority would not.
    [Fact]
    public void Userinfo_never_reaches_the_title() =>
        Assert.Equal("SimplArchive — host.example", ServerLabel.TitleFor("https://user:secret@host.example/"));
}
