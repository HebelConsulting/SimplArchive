using SimplArchive.Api.Errors.Exceptions.Inbox;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Api.Inbox;

/// <summary>
/// The inbox's page operations — split one staged scan into its pages, join several into one, and sort the
/// pages of one without splitting it (issue #487).
/// </summary>
/// <remarks>
/// <para>
/// A scanner does not know where one document ends and the next begins: it produces a stack. The inbox is
/// where that stack is sorted out before anything is filed, so these three verbs belong here and nowhere
/// else — once a document is in a repository it is versioned, ACL'd and possibly under legal hold, and
/// rearranging its pages is a different question entirely.
/// </para>
/// <para>
/// The arithmetic itself is <see cref="PageComposer"/>, which is pure bytes-in/bytes-out. What this adds is
/// everything about the inbox: which object key a name maps to, what the results are called, that a staged
/// mask travels with the pages it belongs to, and that stale preview renditions are swept so the next preview
/// shows what the file now is. Keeping the two apart is what lets the algebra be tested on real PDFs and TIFFs
/// without a storage fleet.
/// </para>
/// <para>
/// <b>Nothing here destroys a source except the sort.</b> Split and join leave every input where it was and add
/// their result alongside — a scan is the only copy of a piece of paper that may already be in a shredder bin,
/// so the operation that turns out to be wrong must be survivable by deleting its output. Sorting is the
/// exception, and is safe for the opposite reason: it is a permutation, so no page can be lost, and sorting
/// back restores the original exactly.
/// </para>
/// </remarks>
public sealed class InboxPageService(IObjectStorageClient storage)
{
    /// <summary>What the pages of one staged item look like: the format, and how many there are.</summary>
    /// <remarks>
    /// <see cref="PageComposer.PageFormat.None"/> or a count of 0 both mean "no page operations here" — an
    /// unreadable or non-paged file. The caller turns that into an absent rel rather than a button that fails
    /// on click (ADR 0554).
    /// </remarks>
    public sealed record PageInfo(PageComposer.PageFormat Format, int PageCount);

    public async Task<PageInfo> DescribeAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var format = PageComposer.FormatOf(name);
        if (format == PageComposer.PageFormat.None)
        {
            return new PageInfo(format, 0);
        }

        var bytes = await ReadAsync(prefix + name, cancellationToken);
        return new PageInfo(format, PageComposer.CountPages(bytes, format));
    }

    /// <summary>
    /// One new inbox item per page, named "&lt;stem&gt; (n)". The source is left alone.
    /// </summary>
    /// <remarks>
    /// Every page inherits the source's staged mask sidecar. A scan batch is usually one mask for the whole
    /// stack — that is why it was scanned together — so carrying the draft over means the index data is typed
    /// once rather than once per resulting document.
    /// </remarks>
    public async Task<IReadOnlyList<string>> SplitAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var (format, bytes) = await LoadPagedAsync(prefix, name, cancellationToken);
        var pages = PageComposer.Split(bytes, format);
        if (pages.Count < 2)
        {
            throw new InboxItemHasNoPagesToSplitException(name);
        }

        var stem = Stem(name);
        var extension = Path.GetExtension(name);
        var written = new List<string>(pages.Count);

        for (var i = 0; i < pages.Count; i++)
        {
            var pageName = await FreeNameAsync(prefix, $"{stem} ({i + 1}){extension}", cancellationToken);
            await WriteAsync(prefix + pageName, pages[i], format, cancellationToken);
            await CopyMaskDraftAsync(prefix, name, pageName, cancellationToken);
            written.Add(pageName);
        }

        return written;
    }

    /// <summary>
    /// One new inbox item holding every page of the named items, in the order given. The sources are left alone.
    /// </summary>
    /// <remarks>
    /// The order is the caller's: a join without a stated order is a coin flip, and "which half of the stack was
    /// scanned first" is knowledge only the person at the scanner has.
    /// </remarks>
    public async Task<string> JoinAsync(
        string prefix,
        IReadOnlyList<string> names,
        string? targetName,
        CancellationToken cancellationToken)
    {
        if (names.Count < 2)
        {
            throw new InboxJoinNeedsSeveralItemsException();
        }

        // Homogeneous only. A PDF page and a TIFF page are not the same kind of thing, and the honest ways to
        // mix them (rasterise the PDF, or wrap the TIFF) each silently change what the pages ARE — resolution,
        // searchable text, colour. Converting a scan behind the user's back is worse than declining.
        var format = PageComposer.FormatOf(names[0]);
        if (format == PageComposer.PageFormat.None || names.Any(n => PageComposer.FormatOf(n) != format))
        {
            throw new InboxJoinNotHomogeneousException();
        }

        var sources = new List<byte[]>(names.Count);
        foreach (var name in names)
        {
            var bytes = await ReadAsync(prefix + name, cancellationToken);
            if (PageComposer.CountPages(bytes, format) == 0)
            {
                throw new InboxItemHasNoPagesException(name);
            }

            sources.Add(bytes);
        }

        var joined = PageComposer.Join(sources, format);
        var candidate = string.IsNullOrWhiteSpace(targetName)
            ? $"{Stem(names[0])} + {names.Count - 1} more{Path.GetExtension(names[0])}"
            : EnsureExtension(targetName, Path.GetExtension(names[0]));

        var joinedName = await FreeNameAsync(prefix, Path.GetFileName(candidate), cancellationToken);
        await WriteAsync(prefix + joinedName, joined, format, cancellationToken);
        await CopyMaskDraftAsync(prefix, names[0], joinedName, cancellationToken);
        return joinedName;
    }

    /// <summary>
    /// Rewrites the item with its pages in the given order — 1-based page numbers, every page exactly once.
    /// </summary>
    /// <remarks>
    /// The one operation that replaces its source, because "sort these pages" is about THIS document; producing
    /// a sorted copy and leaving the unsorted original behind would just add a second thing to clean up. The
    /// permutation check below is what makes that safe: a request that would drop or duplicate a page is
    /// refused before anything is written, so the file cannot end up shorter than it started.
    /// </remarks>
    public async Task ReorderAsync(
        string prefix,
        string name,
        IReadOnlyList<int> pageOrder,
        CancellationToken cancellationToken)
    {
        var (format, bytes) = await LoadPagedAsync(prefix, name, cancellationToken);
        var pageCount = PageComposer.CountPages(bytes, format);

        if (pageOrder.Count != pageCount || pageOrder.Distinct().Count() != pageCount
            || pageOrder.Any(p => p < 1 || p > pageCount))
        {
            throw new InboxPageOrderInvalidException(name, pageCount);
        }

        var reordered = PageComposer.Reorder(bytes, format, pageOrder);
        await WriteAsync(prefix + name, reordered, format, cancellationToken);

        // The cached preview renditions and text layout now describe the previous page order. Sweeping them (and
        // NOT the mask sidecar, which is still true) makes the next preview regenerate from what the file now is.
        await SweepRenditionsAsync(prefix, name, cancellationToken);
    }

    private async Task<(PageComposer.PageFormat Format, byte[] Bytes)> LoadPagedAsync(
        string prefix,
        string name,
        CancellationToken cancellationToken)
    {
        var format = PageComposer.FormatOf(name);
        if (format == PageComposer.PageFormat.None)
        {
            throw new InboxPagesNotSupportedException(name);
        }

        var bytes = await ReadAsync(prefix + name, cancellationToken);
        if (PageComposer.CountPages(bytes, format) == 0)
        {
            throw new InboxItemHasNoPagesException(name);
        }

        return (format, bytes);
    }

    private async Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
    {
        await using var stream = await storage.GetObjectAsync(key, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private async Task WriteAsync(
        string key,
        byte[] bytes,
        PageComposer.PageFormat format,
        CancellationToken cancellationToken)
    {
        await using var payload = new MemoryStream(bytes);
        await storage.PutObjectAsync(key, payload, ContentTypeOf(format), cancellationToken);
    }

    // The staged mask draft, if the source has one. Best-effort by design: a missing draft is the normal case
    // (nothing has been typed yet), not an error that should fail the split.
    private async Task CopyMaskDraftAsync(
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

    private async Task SweepRenditionsAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var stem = Stem(name);
        foreach (var storageObject in await storage.ListObjectsAsync(prefix, cancellationToken))
        {
            var candidate = storageObject.Key[prefix.Length..];
            var isStale = candidate.StartsWith($"{stem}.preview.", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals($"{stem}.textlayout.json", StringComparison.OrdinalIgnoreCase);
            if (isStale)
            {
                await storage.DeleteObjectAsync(storageObject.Key, cancellationToken);
            }
        }
    }

    // Splitting the same file twice is a normal thing to do, so a taken name gets a numeric suffix the way a
    // file manager would rather than failing the whole operation half-written. The cap is a guard against an
    // unbounded loop, not a real limit anyone reaches.
    private async Task<string> FreeNameAsync(string prefix, string candidate, CancellationToken cancellationToken)
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

        throw new InboxItemNameConflictException(candidate);
    }

    private static string EnsureExtension(string name, string extension) =>
        Path.GetExtension(name).Equals(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension;

    private static string Stem(string name)
    {
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name[..lastDot] : name;
    }

    private static string ContentTypeOf(PageComposer.PageFormat format) => format switch
    {
        PageComposer.PageFormat.Pdf => "application/pdf",
        PageComposer.PageFormat.Tiff => "image/tiff",
        _ => "application/octet-stream",
    };

    /// <summary>The staged-mask sidecar's name suffix. Defined here and used by the controller too, so the
    /// sidecar naming is one scheme rather than two copies that can drift apart.</summary>
    public const string MaskSidecarSuffix = ".mask.json";
}
