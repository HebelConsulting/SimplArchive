using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The server-profile rules and the language list the desktop and the mobile app share (ADR 0917): one answer for
// both, so the two server lists cannot accept different profiles or offer different languages.
public sealed class ServerProfileRulesTests
{
    [Theory]
    [InlineData("", "https://archive.example.test", "SmErrNameRequired")]
    [InlineData("   ", "https://archive.example.test", "SmErrNameRequired")]
    [InlineData("Office", "archive.example.test", "SmErrUrlInvalid")]
    [InlineData("Office", "ftp://archive.example.test", "SmErrUrlInvalid")]
    [InlineData("Office", "/api", "SmErrUrlInvalid")]   // a rooted path is an absolute file:// URI on Unix
    [InlineData("kiosk", "https://archive.example.test", "SmErrDuplicateName")]   // names compare ignoring case
    [InlineData("Office", " https://archive.example.test ", null)]
    [InlineData("Office", "http://192.168.1.20:8080", null)]
    public void A_profile_needs_a_name_an_absolute_http_address_and_a_name_of_its_own(string name, string url, string? problem) =>
        Assert.Equal(problem, ServerProfileRules.Problem(name, url, ["Kiosk", "Vendor"]));

    [Fact]
    public void Every_client_offers_the_same_four_languages_and_falls_back_to_English()
    {
        Assert.Equal(["en", "de", "it", "es"], Languages.Supported.Select(l => l.Code));
        Assert.Equal("Deutsch", Languages.ForCode("DE").Name);
        Assert.Equal("en", Languages.ForCode("fr").Code);
        Assert.Equal("en", Languages.ForCode(null).Code);
    }
}
