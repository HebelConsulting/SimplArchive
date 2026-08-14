using SimplArchive.Api.Errors.Exceptions.Inbox;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Inbox;
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
public sealed class InboxPageService(
    IObjectStorageClient storage,
    ISearchablePdfConverter converter,
    IPatchCodeDetector patchCodes)
{
    /// <summary>What the pages of one staged item look like: the format, and how many there are.</summary>
    /// <remarks>
    /// <see cref="PageComposer.PageFormat.None"/> or a count of 0 both mean "no page operations here" — an
    /// unreadable or non-paged file. The caller turns that into an absent rel rather than a button that fails
    /// on click (ADR 0554).
    /// </remarks>
    public sealed record PageInfo(PageComposer.PageFormat Format, int PageCount, bool Signed = false);

    public async Task<PageInfo> DescribeAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var format = PageComposer.FormatOf(name);
        if (format == PageComposer.PageFormat.None)
        {
            return new PageInfo(format, 0);
        }

        var bytes = await ReadAsync(prefix + name, cancellationToken);

        // A signed document reports its pages but offers no operation on them: the count is information, while
        // split and sort would void the signature. Zero pages is what the caller turns into "no rels".
        return DigitalSignature.IsSigned(bytes)
            ? new PageInfo(format, PageComposer.CountPages(bytes, format), Signed: true)
            : new PageInfo(format, PageComposer.CountPages(bytes, format));
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
            if (DigitalSignature.IsSigned(bytes))
            {
                throw new InboxItemIsSignedException(name);
            }

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
    /// Rewrites the item with its pages in the given order — 1-based page numbers, each at most once. Pages
    /// left out are DELETED, which is how the sort dialog's bin button removes a blank back or a separator
    /// sheet (#487).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one operation that replaces its source, because "sort these pages" is about THIS document; producing
    /// a sorted copy and leaving the unsorted original behind would add a second thing to clean up at exactly
    /// the moment the user is tidying.
    /// </para>
    /// <para>
    /// <b>That used to be safe because the order had to be a permutation</b> — a sort could not lose a page.
    /// Allowing deletion trades that guarantee for a smaller one: it loses only pages the caller explicitly
    /// listed out, never by accident. What survives the change is the validation that a page cannot be
    /// duplicated, cannot be out of range, and cannot all be dropped — and the clients confirm the count before
    /// sending, because in-place deletion has nothing to undo it.
    /// </para>
    /// </remarks>
    public async Task ReorderAsync(
        string prefix,
        string name,
        IReadOnlyList<int> pageOrder,
        CancellationToken cancellationToken)
    {
        var (format, bytes) = await LoadPagedAsync(prefix, name, cancellationToken);
        var pageCount = PageComposer.CountPages(bytes, format);

        // A subset is allowed (the omitted pages are deleted); a duplicate, an out-of-range page, or an empty
        // order is not — those are the shapes that mean the caller has made a mistake rather than a choice.
        if (pageOrder.Count == 0 || pageOrder.Count > pageCount
            || pageOrder.Distinct().Count() != pageOrder.Count
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

    /// <summary>
    /// Straightens the item on demand and returns its new name — a TIFF becomes a PDF, because straightening
    /// re-renders the pages and the converter only emits PDF. Null when nothing could be produced.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT consult the user's automatic-straightening preference or the "does this look like
    /// a scan" sniff, both of which exist to decide whether to act on somebody's behalf. Here they have asked.
    /// The signature refusal does still apply: that one is not a convenience.
    /// </remarks>
    public async Task<string?> DeskewAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(prefix + name, cancellationToken);
        if (DigitalSignature.IsSigned(bytes))
        {
            throw new InboxItemIsSignedException(name);
        }

        var kind = PageComposer.FormatOf(name) switch
        {
            PageComposer.PageFormat.Tiff => SearchablePdfSourceKind.Tiff,
            PageComposer.PageFormat.Pdf => SearchablePdfSourceKind.Pdf,
            _ => (SearchablePdfSourceKind?)null,
        };

        if (kind is not { } sourceKind)
        {
            throw new InboxPagesNotSupportedException(name);
        }

        var straightened = await converter.ConvertToSearchablePdfAsync(
            bytes, sourceKind, OcrLanguages, deskew: true, cancellationToken);

        if (straightened is null)
        {
            return null; // the sidecar is unavailable or refused; the item is left exactly as it was
        }

        var newName = $"{Stem(name)}.pdf";
        await WriteAsync(prefix + newName, straightened, PageComposer.PageFormat.Pdf, cancellationToken);

        if (!string.Equals(newName, name, StringComparison.Ordinal))
        {
            await CopyMaskDraftAsync(prefix, name, newName, cancellationToken);
            await storage.DeleteObjectAsync($"{prefix}{name}{MaskSidecarSuffix}", cancellationToken);
            await storage.DeleteObjectAsync(prefix + name, cancellationToken);
        }

        await SweepRenditionsAsync(prefix, newName, cancellationToken);
        return newName;
    }

    /// <summary>
    /// Cuts a batch scan into one item per document, at the Patch 3 separator sheets between them (#492), and
    /// returns what the batch became. Null when detection could not run at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The source is kept, renamed</b> with <see cref="InboxNaming.ToBeDeletedSuffix"/> rather than deleted.
    /// A batch cut in the wrong place — a sheet that jammed, a code the detector read on somebody's letterhead —
    /// is recoverable only from the batch, and at the moment this runs nobody has looked at the result yet. The
    /// suffix is what stops "kept for safety" turning into "an unexplained duplicate of everything".
    /// </para>
    /// <para>
    /// Unlike straightening, the format survives: a TIFF batch becomes TIFFs. No page is rasterised, so there
    /// is nothing to lose by staying where we started.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>?> CutAtPatchCodesAsync(
        string prefix,
        string name,
        CancellationToken cancellationToken)
    {
        var (format, bytes) = await LoadPagedAsync(prefix, name, cancellationToken);
        var kind = format == PageComposer.PageFormat.Pdf ? SearchablePdfSourceKind.Pdf : SearchablePdfSourceKind.Tiff;

        var separators = await patchCodes.DetectSeparatorPagesAsync(bytes, kind, cancellationToken);
        if (separators is null)
        {
            return null; // no sidecar, or it failed — the item is left exactly as it was
        }

        // Asked for explicitly, so "there is nothing here to cut" is an answer the user needs to be told,
        // not a silent no-op that looks like a broken button.
        if (separators.Count == 0)
        {
            throw new InboxNoPatchCodesFoundException(name);
        }

        var parts = PageComposer.CutAt(bytes, format, separators);
        if (parts.Count == 0)
        {
            throw new InboxNoPatchCodesFoundException(name); // nothing but separator sheets: no document in there
        }

        var stem = Stem(name);
        var extension = Path.GetExtension(name);
        var written = new List<string>(parts.Count);

        for (var i = 0; i < parts.Count; i++)
        {
            var partName = await FreeNameAsync(prefix, $"{stem} ({i + 1}){extension}", cancellationToken);
            await WriteAsync(prefix + partName, parts[i], format, cancellationToken);
            await CopyMaskDraftAsync(prefix, name, partName, cancellationToken);
            written.Add(partName);
        }

        var kept = await FreeNameAsync(prefix, $"{stem}{InboxNaming.ToBeDeletedSuffix}{extension}", cancellationToken);
        await storage.CopyObjectAsync(prefix + name, prefix + kept, cancellationToken);
        await InboxNaming.MoveMaskDraftAsync(storage, prefix, name, kept, cancellationToken);
        await storage.DeleteObjectAsync(prefix + name, cancellationToken);

        // The batch's cached preview and text layout describe a file that no longer exists under that name.
        await SweepRenditionsAsync(prefix, name, cancellationToken);
        return written;
    }

    // The sidecar's own default set: per-version OCR languages are a property of a FILED document (ADR 0272),
    // and nothing in the inbox has been filed yet, so there is nothing more specific to ask for.
    private const string OcrLanguages = "eng+deu+fra+ita";

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

        // Enforced here rather than only at the rel, because a rel is a client courtesy and this is a
        // correctness rule: a signature covers a byte range, so any rewrite voids it (#491).
        if (DigitalSignature.IsSigned(bytes))
        {
            throw new InboxItemIsSignedException(name);
        }

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

    private Task CopyMaskDraftAsync(
        string prefix,
        string sourceName,
        string targetName,
        CancellationToken cancellationToken) =>
        InboxNaming.CopyMaskDraftAsync(storage, prefix, sourceName, targetName, cancellationToken);

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

    // Shared with the ingest pipeline, which now does the same naming on the same prefix (#492) — one scheme
    // rather than two that can drift. The exception is this layer's, because "give up" is an HTTP answer.
    private async Task<string> FreeNameAsync(string prefix, string candidate, CancellationToken cancellationToken) =>
        await InboxNaming.FreeNameAsync(storage, prefix, candidate, cancellationToken)
        ?? throw new InboxItemNameConflictException(candidate);

    private static string EnsureExtension(string name, string extension) =>
        Path.GetExtension(name).Equals(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension;

    private static string Stem(string name) => InboxNaming.Stem(name);

    private static string ContentTypeOf(PageComposer.PageFormat format) => format switch
    {
        PageComposer.PageFormat.Pdf => "application/pdf",
        PageComposer.PageFormat.Tiff => "image/tiff",
        _ => "application/octet-stream",
    };

    /// <summary>The staged-mask sidecar's name suffix, as the Api's controllers already know it.</summary>
    /// <remarks>
    /// Forwards to <see cref="InboxNaming"/>, which is where the inbox's naming now lives: the ingest pipeline
    /// in Infrastructure needs the same constant, and Infrastructure cannot see the Api.
    /// </remarks>
    public const string MaskSidecarSuffix = InboxNaming.MaskSidecarSuffix;
}
