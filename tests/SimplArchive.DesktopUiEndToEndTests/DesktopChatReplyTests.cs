using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of chat replies (issue #383, follow-up to #382). The desktop already RENDERED threads but had
// no way to create one — PostCommentAsync was hardwired to a null parent, so every desktop message landed at the
// top level however clearly it answered another. ADR 0511 makes that a parity gap to close, since the web client
// has had the affordance all along.
[Collection(UiCollection.Name)]
public class DesktopChatReplyTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopChatReplyTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_reply_is_filed_under_the_message_it_answers()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // A folder is a Document (ADR 0200), so it carries a chat thread like any other — which keeps this test
        // free of an upload it does not need.
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"chat-{Guid.NewGuid():N}";
        await api.Documents.CreateFolderAsync(repo.Id, name);
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == name);

        await api.Documents.PostCommentAsync(folder.Id, "Is this the final layout?", parentCommentId: null);
        var top = (await api.Documents.GetCommentsAsync(folder.Id)).Single(c => c.Body == "Is this the final layout?");

        await api.Documents.PostCommentAsync(folder.Id, "Yes, signed off yesterday.", parentCommentId: top.Id);

        var thread = await api.Documents.GetCommentsAsync(folder.Id);
        var reply = thread.Single(c => c.Body == "Yes, signed off yesterday.");

        Assert.Equal(top.Id, reply.ParentMessageId);
        // The parent stays top-level: one level of threading, so a reply never re-parents what it answers.
        Assert.Null(thread.Single(c => c.Id == top.Id).ParentMessageId);
    }

    // Who gets the affordance at all. A reply cannot itself be replied to (the thread is one level deep, enforced
    // at POST), an automatic entry is not a conversation, and the recycle bin's read-only preview of a DELETED
    // document offers nothing — it constructs these with CanReply left false.
    [Theory]
    [InlineData(true, 0, true)]    // a top-level typed message — the only case that shows it
    [InlineData(false, 0, false)]  // a reply, or the recycle bin's read-only preview
    [InlineData(true, 1, false)]   // VersionFiled
    [InlineData(true, 2, false)]   // VersionActivated
    public void Only_a_top_level_typed_message_can_be_replied_to(bool canReply, int kind, bool expected)
    {
        var message = new ChatMessageViewModel
        {
            Id = Guid.NewGuid(),
            AuthorName = "Demo Admin",
            Body = "text",
            CreatedAt = DateTimeOffset.UtcNow,
            Kind = kind,
            CanReply = canReply,
        };

        Assert.Equal(expected, message.ShowReplyLink);
    }

    [Fact]
    public void Opening_one_reply_box_closes_the_other_and_drops_its_text()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();

        var first = Message();
        var second = Message();
        vm.Comments.Add(first);
        vm.Comments.Add(second);

        vm.ToggleReplyCommand.Execute(first);
        first.ReplyText = "half-typed";
        Assert.True(first.IsReplying);

        // Switching threads must not carry the text across: a reply is addressed to one specific message, so
        // leaving it in the box would misfile it under whichever message is open next.
        vm.ToggleReplyCommand.Execute(second);
        Assert.False(first.IsReplying);
        Assert.Equal("", first.ReplyText);
        Assert.True(second.IsReplying);

        // Clicking the same message again closes it — the link toggles, it does not only open.
        vm.ToggleReplyCommand.Execute(second);
        Assert.False(second.IsReplying);
    }

    private static ChatMessageViewModel Message() => new()
    {
        Id = Guid.NewGuid(),
        AuthorName = "Demo Admin",
        Body = "text",
        CreatedAt = DateTimeOffset.UtcNow,
        Kind = 0,
        CanReply = true,
    };
}
