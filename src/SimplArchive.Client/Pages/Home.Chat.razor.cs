using System.Net.Http.Json;
using MudBlazor;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;
using SimplArchive.Localization;

namespace SimplArchive.Client.Pages;

// The document's comment thread (ADR 0222): loading it, replying in it, composing with an @-mention, and
// posting. One subject that was living in THREE places -- its state stranded up in the shell's field block, its
// loading under a "Chat thread" heading, and its composing under an "@-mentions" heading immediately after.
//
// The two headings were adjacent and both accurate, which is why this reads as a merge rather than a rescue:
// the split was along the seam of two ISSUES (the thread, then #383's mention picker) rather than along a seam
// in the subject. Posting a comment is the commit of a composed message, and a composed message is where a
// mention comes from, so they answer to each other.
//
// The mention picker's href comes from the thread's own "mentionable-users" rel: the server decides who may be
// addressed here, because offering somebody who cannot see the document would leak it.
//
// A partial rather than a component, by ADR 0733's test: the markup is the chat pane, which stays in the shell
// and binds these fields by name.
public partial class Home
{
    private List<ChatMessageResponse> _comments = [];
    private string _newComment = string.Empty;
    private Guid? _replyingTo;
    private string _replyText = string.Empty;

    // The @-mention picker (issue #383). The href comes from the thread's "mentionable-users" rel — the server
    // decides who may be addressed here, because offering somebody who cannot see the document would leak it.
    private string? _mentionableUsersHref;
    private List<MentionableUserResponse> _mentionCandidates = [];


    // The row's advertised "chat" rel; a row whose listing does not emit it (a reference row) resolves the
    // target once and follows its own rel instead of composing the path it used to (ADR 0543, #416).
    private async Task<string> ChatUrlForAsync(BrowseNode node) =>
        node.ChatHref ?? await Browse.FetchRelAsync(node.Id, "chat");

    private async Task LoadCommentsAsync(BrowseNode node)
    {
        _comments = [];
        _replyingTo = null;
        _mentionCandidates = [];
        _mentionableUsersHref = null;
        try
        {
            var url = await ChatUrlForAsync(node);
            while (url is not null)
            {
                var page = await Http.GetFromJsonAsync<ChatMessageListResponse>(url);
                _comments.AddRange(page?.Messages ?? []);
                // Captured from the FIRST page: the rel is a property of the thread, not of a page of it.
                _mentionableUsersHref ??= Links.Href(page?.Links, "mentionable-users");
                url = Links.Href(page?.Links, "next");
            }
        }
        catch (HttpRequestException)
        {
            // No CanSee (shouldn't happen for a visible document) — leave the thread empty.
        }
    }

    private void ToggleReply(Guid commentId)
    {
        _replyingTo = _replyingTo == commentId ? null : commentId;
        _replyText = string.Empty;
        _mentionCandidates = [];
    }

    // ---- @-mentions (issue #383) --------------------------------------------------------------------

    // A mention is stored as a token holding the USER ID, never the typed name: display names contain spaces (so
    // "@Demo Admin" has no delimiter), are not unique, and can be renamed. The name shown here is resolved from
    // the id at render time, so it can never go stale.
    private async Task OnComposeChangedAsync(string value)
    {
        _newComment = value;
        await RefreshMentionCandidatesAsync(value);
    }

    private async Task OnReplyChangedAsync(string value)
    {
        _replyText = value;
        await RefreshMentionCandidatesAsync(value);
    }

    // The picker is driven off the text AFTER the last '@'. It deliberately does NOT stop at a space: display
    // names contain them, so "@Demo Ad" has to keep matching. The cost is that the query only makes sense while
    // the caret is at the end — which is where typing happens — so a stale run is capped at MentionQueryLimit
    // characters rather than searching the rest of the message.
    private const int MentionQueryLimit = 30;

    private async Task RefreshMentionCandidatesAsync(string text)
    {
        if (_mentionableUsersHref is not { } href || MentionQuery(text) is not { } query)
        {
            _mentionCandidates = [];
            return;
        }

        try
        {
            var page = await Http.GetFromJsonAsync<MentionableUserListResponse>($"{href}?q={Uri.EscapeDataString(query)}");
            _mentionCandidates = page?.Users ?? [];
        }
        catch (HttpRequestException)
        {
            _mentionCandidates = [];
        }
    }

    private static string? MentionQuery(string text)
    {
        var at = text.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        // Not a mention if the '@' is part of a word (an email address, say) — it has to start the token.
        if (at > 0 && !char.IsWhiteSpace(text[at - 1]))
        {
            return null;
        }

        var query = text[(at + 1)..];
        return query.Length > MentionQueryLimit || query.Contains('\n') ? null : query;
    }

    // Replaces the half-typed "@Dem" with the token, so what is stored is the id and what is shown is the name.
    private void PickMention(MentionableUserResponse user)
    {
        var target = _replyingTo is null ? _newComment : _replyText;
        var at = target.LastIndexOf('@');
        if (at < 0)
        {
            return;
        }

        var replaced = $"{target[..at]}@[{user.Id}] ";

        if (_replyingTo is null)
        {
            _newComment = replaced;
        }
        else
        {
            _replyText = replaced;
        }

        _mentionCandidates = [];
    }



    private async Task PostCommentAsync()
    {
        if (_selectedNode is not { } item || string.IsNullOrWhiteSpace(_newComment))
        {
            return;
        }

        var response = await Http.PostAsJsonAsync(await ChatUrlForAsync(item), new { body = _newComment });
        if (response.IsSuccessStatusCode)
        {
            _newComment = string.Empty;
            await LoadCommentsAsync(item);
        }
        else
        {
            Snackbar.Add(Strings.Get("StErrCommentPost"), Severity.Error);
        }
    }

    private async Task PostReplyAsync(Guid parentMessageId)
    {
        if (_selectedNode is not { } item || string.IsNullOrWhiteSpace(_replyText))
        {
            return;
        }

        var response = await Http.PostAsJsonAsync(await ChatUrlForAsync(item), new { body = _replyText, parentMessageId });
        if (response.IsSuccessStatusCode)
        {
            _replyText = string.Empty;
            _replyingTo = null;
            await LoadCommentsAsync(item);
        }
        else
        {
            Snackbar.Add(Strings.Get("StErrPostReply"), Severity.Error);
        }
    }
}
