using System.Collections.ObjectModel;

namespace SimplArchive.DesktopClient.ViewModels;

// A comment in the chat pane. Top-level comments carry their replies (one level, like the web thread).
public sealed class ChatMessageViewModel
{
    public required Guid Id { get; init; }

    public required string AuthorName { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string Meta => $"{AuthorName} · {CreatedAt.ToLocalTime():g}";

    public ObservableCollection<ChatMessageViewModel> Replies { get; } = [];
}
