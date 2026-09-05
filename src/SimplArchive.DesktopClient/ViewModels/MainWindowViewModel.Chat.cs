using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The document's comment thread (ADR 0222) and the identity card opened from a message in it (ADR 0544):
// loading the thread, replying, posting, and showing who wrote a message.
//
// Named to MATCH the web client's Home.Chat.razor.cs, which holds the same subject. ADR 0511 asks that a
// web/desktop pair be reviewed as a single surface, and that is only possible when both halves can be found
// under one name.
//
// The card belongs WITH the thread rather than beside it: ShowAuthorCardAsync takes a ChatMessageViewModel --
// it answers "who wrote this message", which is a question only the thread raises. It had a heading of its own
// ("Author identity card") that then covered external links and the whole thread as well, so the heading
// described three of its members and none of the eighty after them (#941).
public sealed partial class MainWindowViewModel
{
    // The card currently shown in the author flyout. One at a time, so a single property serves every message.
    [ObservableProperty]
    private UserCardViewModel? _authorCard;

    [ObservableProperty]
    private bool _authorCardFailed;

    // Opens the card for a message's author by FOLLOWING the href the message advertised — the client never
    // builds that URL (ADR 0543). Only reachable from a message that has a card: an automation's name is not
    // a button at all.
    [RelayCommand]
    private async Task ShowAuthorCardAsync(ChatMessageViewModel? message)
    {
        AuthorCard = null;
        AuthorCardFailed = false;

        if (message?.AuthorCardHref is not { } href || _api is null)
        {
            return;
        }

        var loaded = await _api.Profile.GetUserCardAsync(href);
        if (loaded is not { } result)
        {
            AuthorCardFailed = true;
            return;
        }

        AuthorCard = new UserCardViewModel
        {
            DisplayName = result.Card.DisplayName,
            Email = result.Card.Email,
            IsActive = result.Card.IsActive,
            Photo = result.Photo,
        };
    }

    private async Task LoadCommentsAsync(string chatHref)
    {
        var thread = await _api!.Documents.GetChatAsync(chatHref);
        var comments = thread.Messages;
        _mentionableUsersHref = thread.MentionableUsersHref;
        MentionCandidates.Clear();
        HasMentionCandidates = false;
        var byId = comments.ToDictionary(
            c => c.Id,
            c => new ChatMessageViewModel { Id = c.Id, AuthorName = c.AuthorName, Body = c.Body, CreatedAt = c.CreatedAt, AuthorCardHref = c.AuthorCardHref, Kind = c.Kind, VersionNumber = c.VersionNumber, VersionComment = c.VersionComment, VersionCommentKind = c.VersionCommentKind, CanReply = c.ParentMessageId is null, Mentions = c.Mentions });

        Comments.Clear();
        foreach (var comment in comments.Where(c => c.ParentMessageId is null))
        {
            var vm = byId[comment.Id];
            foreach (var reply in comments.Where(c => c.ParentMessageId == comment.Id))
            {
                vm.Replies.Add(byId[reply.Id]);
            }

            Comments.Add(vm);
        }
    }

    // Opens the inline reply box under one message, closing whichever was open — one conversation at a time, the
    // same rule the web client follows. Re-clicking the same message closes it, and either way the half-typed
    // text is dropped: a reply is addressed to a specific message, so carrying it to another one would misfile it.
    [RelayCommand]
    private void ToggleReply(ChatMessageViewModel? message)
    {
        if (message is null)
        {
            return;
        }

        var opening = !message.IsReplying;

        foreach (var other in Comments)
        {
            other.IsReplying = false;
            other.ReplyText = string.Empty;
        }

        message.IsReplying = opening;
    }

    [RelayCommand]
    private async Task PostReplyAsync(ChatMessageViewModel? message)
    {
        if (_api is null || _selectedDocumentId is not { } documentId
            || message is null || string.IsNullOrWhiteSpace(message.ReplyText))
        {
            return;
        }

        try
        {
            await _api.Documents.PostCommentAsync(DetailHref("chat"), message.ReplyText, parentCommentId: message.Id);

            // Reloading rebuilds the collection, so the open reply box disappears with it — no need to reset the
            // flag on an instance that is about to be replaced.
            await LoadCommentsAsync(DetailHref("chat"));
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrPostComment"), e.Message));
        }
    }

    [RelayCommand]
    private async Task PostCommentAsync()
    {
        if (_api is null || _selectedDocumentId is not { } documentId || string.IsNullOrWhiteSpace(NewComment))
        {
            return;
        }

        try
        {
            await _api.Documents.PostCommentAsync(DetailHref("chat"), NewComment, parentCommentId: null);
            NewComment = string.Empty;
            await LoadCommentsAsync(DetailHref("chat"));
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrPostComment"), e.Message));
        }
    }
}
