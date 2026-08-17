using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Intray;

/// <summary>
/// What things in an intray are called, and how a new name is found when the obvious one is taken.
/// </summary>
/// <remarks>
/// One place rather than two, because the intray now has <b>two</b> callers doing the same naming: the ingest
/// pipeline, which cuts a batch as it arrives, and the Api's on-demand page operations, which cut the same
/// batch when somebody presses the button. Copies of a naming scheme are how a suffix ends up meaning one
/// thing on one path and something slightly different on the other.
/// </remarks>
public static class IntrayNaming
{
    /// <summary>An item's staged mask/index-data draft.</summary>
    public const string MaskSidecarSuffix = ".mask.json";

    /// <summary>
    /// What a source is renamed to once it has been cut into several items — appended to the stem, so
    /// <c>batch.pdf</c> becomes <c>batch_to_be_deleted.pdf</c>.
    /// </summary>
    /// <remarks>
    /// <b>Kept, not deleted.</b> A scan is often the only copy of a piece of paper that is already in a
    /// shredder bin, so an automatic operation must not be the thing that destroys it — but leaving the batch
    /// sitting there under its own name is worse than useless, because the user cannot tell it apart from the
    /// documents that came out of it. The suffix says both: still here, and safe to throw away.
    /// </remarks>
    public const string ToBeDeletedSuffix = "_to_be_deleted";

    /// <summary>Everything before the last dot — the file name without its extension.</summary>
    public static string Stem(string name)
    {
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name[..lastDot] : name;
    }

    /// <summary>
    /// <paramref name="candidate"/> if it is free, else the same name with " (2)", " (3)"… appended.
    /// </summary>
    /// <remarks>
    /// Cutting or splitting the same file twice is a normal thing to do, so a taken name gets a numeric suffix
    /// the way a file manager would rather than failing an operation half-written. The cap is a guard against
    /// an unbounded loop, not a limit anybody reaches; the caller decides what to throw when it is hit.
    /// </remarks>
    public static async Task<string?> FreeNameAsync(
        IObjectStorageClient storage,
        string prefix,
        string candidate,
        CancellationToken cancellationToken)
    {
        if (!await storage.ExistsAsync(prefix + candidate, cancellationToken))
        {
            return candidate;
        }

        var stem = Stem(candidate);
        var extension = Path.GetExtension(candidate);
        for (var attempt = 2; attempt <= 999; attempt++)
        {
            var next = $"{stem} ({attempt}){extension}";
            if (!await storage.ExistsAsync(prefix + next, cancellationToken))
            {
                return next;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies the source's staged mask draft onto a new item, if it has one.
    /// </summary>
    /// <remarks>
    /// Best-effort by design: a missing draft is the normal case — nothing has been typed yet — not an error
    /// that should fail the operation. A batch is usually one mask for the whole stack (that is why it was
    /// scanned together), so carrying the draft over means the index data is typed once rather than per result.
    /// </remarks>
    public static async Task CopyMaskDraftAsync(
        IObjectStorageClient storage,
        string prefix,
        string sourceName,
        string targetName,
        CancellationToken cancellationToken)
    {
        var source = $"{prefix}{sourceName}{MaskSidecarSuffix}";
        if (await storage.ExistsAsync(source, cancellationToken))
        {
            await storage.CopyObjectAsync(source, $"{prefix}{targetName}{MaskSidecarSuffix}", cancellationToken);
        }
    }

    /// <summary>Moves the staged mask draft to follow an item that has been renamed.</summary>
    public static async Task MoveMaskDraftAsync(
        IObjectStorageClient storage,
        string prefix,
        string sourceName,
        string targetName,
        CancellationToken cancellationToken)
    {
        var source = $"{prefix}{sourceName}{MaskSidecarSuffix}";
        if (await storage.ExistsAsync(source, cancellationToken))
        {
            await storage.CopyObjectAsync(source, $"{prefix}{targetName}{MaskSidecarSuffix}", cancellationToken);
            await storage.DeleteObjectAsync(source, cancellationToken);
        }
    }
}
