using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using SimplArchive.Localization;
using Avalonia.Media.Imaging;

namespace SimplArchive.DesktopClient.ViewModels;

// A message in the chat pane. Top-level messages carry their replies (one level, like the web thread).
public sealed class ChatMessageViewModel
{
    public required Guid Id { get; init; }

    public required string AuthorName { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    // The URL of the author's identity card, exactly as the SERVER advertised it via the "author-card" rel —
    // never composed here (ADR 0543). Null when a ServiceAccount posted the message: an automation has no card,
    // and that absence is what makes the name render as plain text rather than a link (ADR 0544).
    public string? AuthorCardHref { get; init; }

    // What produced this entry (ADR 0545): 0 UserPost · 1 DocumentFiled · 2 VersionFiled · 3 VersionActivated.
    public int Kind { get; init; }

    public int? VersionNumber { get; init; }

    public string? VersionComment { get; init; }

    public int? VersionCommentKind { get; init; }

    public bool IsUserPost => Kind == 0;

    public bool IsSystemEntry => Kind != 0;

    // The sentence for an automatic entry, from a localized template. Unlike the web client — which splices the
    // author in as a clickable element — the desktop renders it as text with the name inline, because Avalonia's
    // TextBlock has no equivalent of a render fragment here. The author's card stays reachable from the meta row.
    public string SystemSentence => Kind switch
    {
        1 => string.Format(Strings.Get("ChatFiledNewDocument"), AuthorName),
        2 => string.Format(Strings.Get("ChatSavedNewVersion"), AuthorName),
        3 => string.Format(Strings.Get("ChatActivatedVersion"), AuthorName, VersionNumber),
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

    // Initials stand in when a colleague has no photo, the same fallback the web card uses.
    public string Initials =>
        string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2).Select(part => part[0])).ToUpperInvariant();

    private static Bitmap Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }
}
