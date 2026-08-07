using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.UiEndToEndTests;

// One entry per version now carries BOTH filing sentences (ADR 0545): a first version announces the document's
// arrival, every later one a new working version. The split used to live in the data — a first version emitted a
// second, separate "filed a new document" entry beside its own — which meant filing said the same thing twice,
// the second time falsely ("saved a new working version" of a document that had no earlier version).
//
// A view-model test rather than a rendered one: the choice is made in SystemSentence, and pinning it here is what
// stops the false sentence coming back without a display or a running Api.
public class DesktopChatSystemSentenceTests
{
    private const int UserPost = 0, VersionFiled = 1, VersionActivated = 2;

    [Theory]
    [InlineData(1, "ChatFiledNewDocument")]
    // A null number can only mean an unnumbered first version, so it reads as filing rather than as a successor
    // to something that isn't there.
    [InlineData(null, "ChatFiledNewDocument")]
    [InlineData(2, "ChatSavedNewVersion")]
    [InlineData(7, "ChatSavedNewVersion")]
    public void A_version_entry_picks_its_sentence_from_the_version_number(int? versionNumber, string expectedKey)
    {
        var message = Entry(VersionFiled, versionNumber);

        Assert.Equal(string.Format(Strings.Get(expectedKey), "Demo Admin"), message.SystemSentence);
    }

    [Fact]
    public void Making_an_older_version_current_keeps_its_own_sentence()
    {
        var message = Entry(VersionActivated, versionNumber: 3);

        Assert.Equal(string.Format(Strings.Get("ChatActivatedVersion"), "Demo Admin", 3), message.SystemSentence);
    }

    // A typed message is not a system entry at all: its text is its own, and no template applies.
    [Fact]
    public void A_typed_message_renders_its_own_body()
    {
        var message = Entry(UserPost, versionNumber: null, body: "the customer came back on the price");

        Assert.Equal("the customer came back on the price", message.SystemSentence);
        Assert.True(message.IsUserPost);
    }

    private static ChatMessageViewModel Entry(int kind, int? versionNumber, string body = "") =>
        new()
        {
            Id = Guid.NewGuid(),
            AuthorName = "Demo Admin",
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow,
            Kind = kind,
            VersionNumber = versionNumber,
        };
}
