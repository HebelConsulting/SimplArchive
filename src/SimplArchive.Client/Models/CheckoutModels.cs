using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>
/// A document the caller has checked out, and the addresses its row advertised (ADR 0543).
/// </summary>
/// <remarks>
/// Shared: the shell holds the list because the bottom tab bar draws a badge with its count, and the
/// Check-out tab renders the rows. One shape read two ways rather than two shapes (ADR 0558).
/// </remarks>
public record CheckoutListResponse { public List<CheckoutDto> Items { get; set; } = []; }
public record CheckoutDto
{
    // The current version's content carries a digital signature (#491), examined once at finalize. TRI-STATE:
    // null means the version was NEVER EXAMINED — every version filed before this shipped — which is not the
    // same as "not signed", so the badge shows only for a definite true.
    public bool? IsSigned { get; set; }

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string FileExtension { get; set; } = "";
    public bool HasStash { get; set; }
    public bool IsModified { get; set; }

    /// <summary>
    /// The client that took this lock without the user asking — a save-by-rename edit over WebDAV (ADR 0562);
    /// null for an explicit check-out. Client-supplied text: render it, never act on it.
    /// </summary>
    public string? ImplicitAgent { get; set; }
    public string? StashDownloadUrl { get; set; }
    public string? DownloadUrl { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public List<LinkResponse> Links { get; set; } = [];
}
public record WorkingCopyUploadResponse { public string UploadUrl { get; set; } = ""; }
