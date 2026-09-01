namespace SimplArchive.Client.Components.Tabs;

/// <summary>
/// One staged file in a user's (or group's) intray, as the listing advertised it.
/// </summary>
/// <remarks>
/// Public and in its own file since the page-operations toolbar became its own component: a row that two
/// components both render is a shared type, and the alternative — passing five loose primitives per item —
/// loses the addresses the listing handed over (ADR 0555) the moment a third caller wants a sixth one.
/// </remarks>
public record IntrayItem(string Name, long Size, bool HasMask, string? DownloadHref, string? PreviewHref, string? FileHref, string? DeleteHref, string? MoveHref, string? MaskHref, Guid? GroupId, string? GroupName, Guid? UserId, string? UserName, string? PagesHref, bool Signed)
{
    // Un-classified items (no staged mask sidecar) show in square brackets (ADRs 0279/0281).
    public string DisplayName => HasMask ? Name : $"[{Name}]";

    // True for the caller's own items (no group/user source) — they get the "Send to…" action; non-own items
    // get "Move to my intray".
    public bool IsOwn => GroupId is null && UserId is null;

    // The `[GroupName]` / `[UserName]` source label shown before the name; null for own items.
    public string? SourceLabel => GroupName ?? UserName;
}
