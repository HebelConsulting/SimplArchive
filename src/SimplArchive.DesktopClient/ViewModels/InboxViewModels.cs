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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _hasMask;

    public string DisplayName => HasMask ? Name : $"[{Name}]";

    public string IconValue => "mdi-file-document-outline";

    public string SizeText => FormatSize(Size);

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}

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
