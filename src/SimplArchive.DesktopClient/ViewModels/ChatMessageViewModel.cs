using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.RegularExpressions;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using Avalonia.Media.Imaging;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

// A message in the chat pane. Top-level messages carry their replies (one level, like the web thread).
//
// Observable because the reply box opens INSIDE the item's template: whether this particular message is being
// replied to is per-item state, so it lives here rather than as one id on the window view-model.
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public required Guid Id { get; init; }

    public required string AuthorName { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    // The URL of the author's identity card, exactly as the SERVER advertised it via the "author-card" rel —
    // never composed here (ADR 0543). Null when a ServiceAccount posted the message: an automation has no card,
    // and that absence is what makes the name render as plain text rather than a link (ADR 0544).
    public string? AuthorCardHref { get; init; }

    // What produced this entry (ADR 0545): 0 UserPost · 1 VersionFiled · 2 VersionActivated ·
    // 3 AttachmentRefused (ADR 0718).
    public int Kind { get; init; }

    public int? VersionNumber { get; init; }

    public string? VersionComment { get; init; }

    public int? VersionCommentKind { get; init; }

    // The names behind the body's "@[id]" tokens (issue #383).
    public IReadOnlyList<Mention> Mentions { get; init; } = [];

    // The body with its mention tokens replaced by names. Unlike the web client — which wraps each mention in its
    // own coloured element — the desktop renders it as flat text, for the same toolkit reason SystemSentence
    // does: an Avalonia TextBlock has no render-fragment equivalent to splice elements into a bound string.
    public string DisplayBody => MentionToken.Replace(Body, match =>
    {
        var name = Guid.TryParse(match.Groups[1].Value, out var userId)
            ? Mentions.FirstOrDefault(m => m.UserId == userId)?.DisplayName
            : null;

        // A token whose user is gone still reads as a sentence: the record that somebody was addressed outlives
        // the account, so it becomes a tombstone rather than a raw id.
        return $"@{name ?? Strings.Get("ChatMentionUnknown")}";
    });

    // The wire format for a mention, normatively defined by Domain ChatMentions. Parsed locally rather than by
    // referencing that assembly: both clients deliberately depend on Localization alone, and pulling the server's
    // whole entity model into a desktop binary (and the web client's WASM payload) to reuse one regex is a bad
    // trade. This is the same hand-parsing the rest of this client already does for every wire shape.
    private static readonly Regex MentionToken =
        new(@"@\[([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\]", RegexOptions.Compiled);

    public bool IsUserPost => Kind == 0;

    public bool IsSystemEntry => Kind != 0;

    // The sentence for an automatic entry, from a localized template. Unlike the web client — which splices the
    // author in as a clickable element — the desktop renders it as text with the name inline, because Avalonia's
    // TextBlock has no equivalent of a render fragment here. The author's card stays reachable from the meta row.
    //
    // VersionFiled covers every version and carries BOTH filing sentences: version 1 is the document arriving,
    // later ones are new working versions of something already filed.
    public string SystemSentence => Kind switch
    {
        1 => string.Format(
            Strings.Get(VersionNumber is null or <= 1 ? "ChatFiledNewDocument" : "ChatSavedNewVersion"), AuthorName),
        2 => string.Format(Strings.Get("ChatActivatedVersion"), AuthorName, VersionNumber),
        // A refused attachment (ADR 0718): its {1} is the FILE NAME the body carries, not a version number —
        // the entry is about something that never became a version.
        3 => string.Format(Strings.Get("ChatAttachmentRefused"), AuthorName, Body),
        _ => Body,
    };

    public bool HasVersionEntry => VersionNumber is not null;

    public string VersionLabel => string.Format(Strings.Get("ChatVersionLabel"), VersionNumber);

    // A machine-generated comment carries no stored text; its wording is a localized string for the kind.
    public string? VersionCommentText => VersionCommentKind == 1
        ? Strings.Get("VersionCommentSearchablePdf")
        : string.IsNullOrWhiteSpace(VersionComment) ? null : VersionComment;

    public bool HasVersionComment => VersionCommentText is not null;

    // Drives the template: a person's name is clickable, an automation's is not.
    public bool HasAuthorCard => AuthorCardHref is not null;

    // The timestamp half of the old single "Meta" string. The author is its own element so it can carry the card
    // affordance, so what remains here is the time — with the separator only when an author precedes it. A system
    // entry names its author INSIDE the sentence ("Demo Admin filed a new document."), so repeating it in the meta
    // row read as a stutter; the row shows just the time there, matching the web client.
    public string Timestamp => IsUserPost ? $"· {CreatedAt.ToLocalTime():g}" : $"{CreatedAt.ToLocalTime():g}";

    // The author element belongs to a typed message only, for the same reason.
    public bool ShowAuthorLink => IsUserPost && HasAuthorCard;

    public bool ShowAuthorPlainName => IsUserPost && !HasAuthorCard;

    // Whether this message may be replied to. Set only for TOP-LEVEL messages in the live chat pane: the thread
    // is one level deep (a reply's parent must itself be top-level, enforced at POST), and the recycle bin's
    // read-only preview of a deleted document leaves this false — there is nothing to continue there.
    public bool CanReply { get; init; }

    // Matching the web client, an automatic entry gets no reply affordance: an event is not a conversation.
    public bool ShowReplyLink => CanReply && IsUserPost;

    [ObservableProperty] private bool _isReplying;

    [ObservableProperty] private string _replyText = "";

    public ObservableCollection<ChatMessageViewModel> Replies { get; } = [];
}

// The tenant-visible identity card behind an author name (ADR 0544).
public sealed class UserCardViewModel
{
    public required string DisplayName { get; init; }

    public required string Email { get; init; }

    public required bool IsActive { get; init; }

    // PNG bytes, already fetched with the bearer token — the endpoint is protected, so the image cannot be
    // bound by URL.
    public byte[]? Photo { get; init; }

    public bool HasPhoto => Photo is not null;

    // Decoded once on demand; Avalonia's Image binds a Bitmap, not raw bytes.
    public Bitmap? PhotoImage => Photo is { } bytes ? Decode(bytes) : null;

    // Initials stand in when a colleague has no photo, the same fallback the web card uses — now literally
    // the same, rather than a fifth copy that agreed by coincidence.
    public string Initials => ContactInitials.From(DisplayName);

    private static Bitmap Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }
}
