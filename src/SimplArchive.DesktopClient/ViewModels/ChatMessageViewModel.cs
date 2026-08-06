using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    // Drives the template: a person's name is clickable, an automation's is not.
    public bool HasAuthorCard => AuthorCardHref is not null;

    public bool HasNoAuthorCard => AuthorCardHref is null;

    // The timestamp half of the old single "Meta" string. The author is now its own element so it can carry the
    // card affordance, so what remains here is the separator + time.
    public string Timestamp => $"· {CreatedAt.ToLocalTime():g}";

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
