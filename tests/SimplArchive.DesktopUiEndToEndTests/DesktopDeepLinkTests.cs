using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The deep-link address forms (#761), pinned pure: what "Copy link" produces and every shape the paste box
// and the simplarchive:// handler accept — including the shapes they must REFUSE, because a parser that
// takes any URL would navigate on garbage and read as broken in a different way.
public class DesktopDeepLinkTests
{
    private static readonly Guid Id = Guid.Parse("39029ca2-4943-5a55-8674-a93aa8cc9033");

    [Fact]
    public void The_copied_link_is_the_web_apps_go_route()
        => Assert.Equal($"https://demo.simplarchive.dev/go/{Id}", DeepLinks.BuildLink("https://demo.simplarchive.dev/", Id));

    [Theory]
    [InlineData("https://demo.simplarchive.dev/go/39029ca2-4943-5a55-8674-a93aa8cc9033", true)]
    [InlineData("http://localhost:8080/go/39029ca2-4943-5a55-8674-a93aa8cc9033", true)]
    [InlineData("simplarchive://go/39029ca2-4943-5a55-8674-a93aa8cc9033", true)]      // the scheme form
    [InlineData("  https://demo.simplarchive.dev/go/39029ca2-4943-5a55-8674-a93aa8cc9033  ", true)] // pasted with whitespace
    [InlineData("https://demo.simplarchive.dev/go/not-a-guid", false)]
    [InlineData("https://demo.simplarchive.dev/documents/39029ca2-4943-5a55-8674-a93aa8cc9033", false)] // not the /go route
    [InlineData("ftp://demo.simplarchive.dev/go/39029ca2-4943-5a55-8674-a93aa8cc9033", false)]
    [InlineData("just some text", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_two_link_forms_parse(string? text, bool parses)
        => Assert.Equal(parses ? Id : (Guid?)null, DeepLinks.ParseDocumentId(text) is { } id && parses ? id : DeepLinks.ParseDocumentId(text));

    [Fact]
    public void The_parsed_id_is_the_links_id()
        => Assert.Equal(Id, DeepLinks.ParseDocumentId($"simplarchive://go/{Id}"));
}
