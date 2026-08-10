using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// A server-inbox item (ADR "S3-backed inbox", phase 2) — a staged file with a presigned download URL. HasMask
// tells whether a `{name}.mask.json` staging sidecar exists (an un-masked item is shown in square brackets —
// ADR "Inbox item classification + preview"). Moving is by list selection + drag (ADR "Intray refinements").
public sealed partial class InboxItemViewModel : ObservableObject
{
    public required string Name { get; init; }

    public required long Size { get; init; }

    public required string DownloadUrl { get; init; }

    // A non-own item's source queue (ADR 0532): a group inbox or — for a CanManageInboxes admin viewing another
    // user — that user. Own items leave all four null. MoveUrl is the server-built move action (source query baked
    // in); SourceQuery is appended to the name-based preview/mask/file/delete endpoints.
    public Guid? GroupId { get; init; }
    public string? GroupName { get; init; }
    public Guid? UserId { get; init; }
    public string? UserName { get; init; }
    public string MoveUrl { get; init; } = "";

    // The row the server sent — preview / mask / file / delete follow the addresses IT advertised, each already
    // carrying the right source prefix (ADR 0543/0555). Null only for the designer-preview rows below.
    public SimplArchive.DesktopClient.Services.SimplArchiveApiClient.InboxItemInfo? Item { get; init; }

    public bool IsOwn => GroupId is null && UserId is null;
    public string SourceQuery => GroupId is { } g ? $"?group={g}" : UserId is { } u ? $"?user={u}" : "";
    public string? SourceLabel => GroupName ?? UserName;
    public bool HasSource => SourceLabel is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _hasMask;

    public string DisplayName => HasMask ? Name : $"[{Name}]";

    // A person icon for another user's item, a group icon for a group item, else a file.
    public string IconValue => GroupId is not null ? "mdi-account-group-outline" : UserId is not null ? "mdi-account-outline" : "mdi-file-document-outline";

    public string SizeText => FormatSize(Size);

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}

// A choice in the admin inbox user-picker (ADR 0532) — a user's inbox to open, or (null id) "My inbox".
public sealed record InboxUserPickerItem(Guid? UserId, string Name);

// A file sitting in the local inbox folder, a candidate for upload to the server inbox. HasMask mirrors the
// server item's meaning: a `{name}.mask.json` sidecar next to the file (carried by a move from the server).
public sealed partial class LocalFileViewModel : ObservableObject
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required long Size { get; init; }

    public required bool HasMask { get; init; }

    public string DisplayName => HasMask ? Name : $"[{Name}]";

    public string SizeText => InboxItemViewModel.FormatSize(Size);
}
