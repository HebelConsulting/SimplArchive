using System.Globalization;
using SimplArchive.Localization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

// A server-intray item (ADR "S3-backed inbox", phase 2) — a staged file with a presigned download URL. HasMask
// tells whether a `{name}.mask.json` staging sidecar exists (an un-masked item is shown in square brackets —
// ADR "Inbox item classification + preview"). Moving is by list selection + drag (ADR "Intray refinements").
public sealed partial class IntrayItemViewModel : ObservableObject
{
    public required string Name { get; init; }

    public required long Size { get; init; }

    public required string DownloadUrl { get; init; }

    // A non-own item's source queue (ADR 0532): a group intray or — for a CanManageIntrays admin viewing another
    // user — that user. Own items leave all four null. MoveUrl is the server-built move action (source query baked
    // in); SourceQuery is appended to the name-based preview/mask/file/delete endpoints.
    public Guid? GroupId { get; init; }
    public string? GroupName { get; init; }
    public Guid? UserId { get; init; }
    public string? UserName { get; init; }
    public string MoveUrl { get; init; } = "";

    // The row the server sent — preview / mask / file / delete follow the addresses IT advertised, each already
    // carrying the right source prefix (ADR 0543/0555). Null only for the designer-preview rows below.
    public SimplArchive.DesktopClient.Services.IntrayApi.IntrayItemInfo? Item { get; init; }

    public bool IsOwn => GroupId is null && UserId is null;
    public string SourceQuery => GroupId is { } g ? $"?group={g}" : UserId is { } u ? $"?user={u}" : "";
    public string? SourceLabel => GroupName ?? UserName;
    public bool HasSource => SourceLabel is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _hasMask;

    public string DisplayName => HasMask ? Name : $"[{Name}]";

    // The content carries a digital signature (#491). Badged, and no page operation is offered: a signature
    // covers a byte range, so splitting, sorting, joining or straightening it would void it — silently, since
    // the file still opens and still looks right.
    public bool IsSigned { get; init; }

    public string SignedTooltip => Strings.Get("SignedBadgeTip");

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

// A choice in the admin intray user-picker (ADR 0532) — a user's intray to open, or (null id) "My intray".
public sealed record IntrayUserPickerItem(Guid? UserId, string Name);

// A file sitting in the local intray folder, a candidate for upload to the server intray. HasMask mirrors the
// server item's meaning: a `{name}.mask.json` sidecar next to the file (carried by a move from the server).
public sealed partial class LocalFileViewModel : ObservableObject
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required long Size { get; init; }

    public required bool HasMask { get; init; }

    public string DisplayName => HasMask ? Name : $"[{Name}]";

    public string SizeText => IntrayItemViewModel.FormatSize(Size);
}

/// <summary>
/// One page of a staged item in the sort dialog (issue #487): its picture, and which page it STARTED as.
/// </summary>
/// <remarks>
/// The label is the original page number, not the current position, and that is the whole point: the position
/// is visible from where the tile sits, while "which page is this" is the thing the user is tracking as they
/// move tiles around. A tile relabelled on every move would make the list impossible to follow — and the
/// original number is also literally what the request sends.
/// </remarks>
public sealed partial class IntrayPageViewModel(int originalNumber, Bitmap? image) : ObservableObject
{
    public int OriginalNumber { get; } = originalNumber;

    public Bitmap? Image { get; } = image;

    public string Label => OriginalNumber.ToString(CultureInfo.CurrentCulture);

    /// <summary>
    /// Clockwise degrees the user has turned this page by (#522) — client-side preview state until Apply
    /// writes the whole arrangement in one request, exactly like the order itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetWidth))]
    [NotifyPropertyChangedFor(nameof(SheetHeight))]
    [NotifyPropertyChangedFor(nameof(PictureWidth))]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    private int _rotationDegrees;

    /// <summary>How wide to draw the sheet — the page's own proportions, turned with it.</summary>
    /// <remarks>
    /// The picture's pixel size IS the page's proportions: PDFium rasterises a page at a uniform scale, so a
    /// portrait page comes back a portrait bitmap. A page that failed to render has no size, and
    /// <see cref="PageTile"/> falls back to A4 rather than to a square.
    /// </remarks>
    public double SheetWidth => PageTile.Sheet(PixelWidth, PixelHeight, RotationDegrees).Width;

    /// <inheritdoc cref="SheetWidth"/>
    public double SheetHeight => PageTile.Sheet(PixelWidth, PixelHeight, RotationDegrees).Height;

    /// <summary>The picture's box before the turn — the sheet with its axes put back.</summary>
    public double PictureWidth => PageTile.Picture(PixelWidth, PixelHeight, RotationDegrees).Width;

    /// <inheritdoc cref="PictureWidth"/>
    public double PictureHeight => PageTile.Picture(PixelWidth, PixelHeight, RotationDegrees).Height;

    private double PixelWidth => Image?.PixelSize.Width ?? 0;

    private double PixelHeight => Image?.PixelSize.Height ?? 0;

    public void RotateLeft() => RotationDegrees = (RotationDegrees + 270) % 360;

    public void RotateRight() => RotationDegrees = (RotationDegrees + 90) % 360;
}
