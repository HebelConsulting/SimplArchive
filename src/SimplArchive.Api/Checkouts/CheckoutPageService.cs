using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Api.Checkouts;

/// <summary>
/// Page operations on a check-out's WORKING COPY (ADR 0593): sort / rotate / delete pages against the stash,
/// never the archived version — the result reaches the archive only through a normal check-in. The source is
/// the stash when one exists, else the current version (the same fallback the login reconcile uses, ADR 0332);
/// the result always lands in the stash, creating it lazily like any other first save.
/// </summary>
/// <remarks>
/// The algebra itself is <see cref="PageComposer"/>, untouched. Unlike the intray sibling there is no rendition
/// sweep here: the checkout preview is generated with <c>sourceMayHaveChanged</c> (the stash is rewritten under
/// one key on every WebDAV save), so the next preview re-renders from what the file now is.
/// </remarks>
public sealed class CheckoutPageService(IObjectStorageClient storage)
{
    public sealed record PageInfo(PageComposer.PageFormat Format, int PageCount, bool Signed);

    /// <summary>Describes the working copy. The stash key is extensionless (ADR 0517), so the format comes from
    /// the version's extension, threaded in by the caller.</summary>
    public async Task<PageInfo> DescribeAsync(string sourceKey, string fileExtension, CancellationToken cancellationToken)
    {
        var format = PageComposer.FormatOf($"working-copy{fileExtension}");
        if (format == PageComposer.PageFormat.None)
        {
            return new(format, 0, false);
        }

        var bytes = await ReadAsync(sourceKey, cancellationToken);
        return new(format, PageComposer.CountPages(bytes, format), DigitalSignature.IsSigned(bytes));
    }

    /// <summary>Applies one whole-file rewrite: reorder + delete (omission) + rotate, validated as a set first.</summary>
    public async Task ReorderAsync(
        string sourceKey,
        string stashKey,
        string fileExtension,
        IReadOnlyList<int> pageOrder,
        IReadOnlyDictionary<int, int>? rotations,
        CancellationToken cancellationToken)
    {
        var format = PageComposer.FormatOf($"working-copy{fileExtension}");
        if (format == PageComposer.PageFormat.None)
        {
            throw new CheckoutPagesNotSupportedException();
        }

        var bytes = await ReadAsync(sourceKey, cancellationToken);

        // Enforced here rather than only at the rel, because a rel is a client courtesy and this is a
        // correctness rule: a signature covers a byte range, so any rewrite voids it (#491).
        if (DigitalSignature.IsSigned(bytes))
        {
            throw new CheckoutWorkingCopySignedException();
        }

        var pageCount = PageComposer.CountPages(bytes, format);
        if (!PageComposer.IsValidOrder(pageCount, pageOrder, rotations))
        {
            throw new CheckoutPageOrderInvalidException(pageCount);
        }

        var reordered = PageComposer.Reorder(bytes, format, pageOrder, rotations);
        await using var payload = new MemoryStream(reordered);
        await storage.PutObjectAsync(stashKey, payload, PageComposer.ContentTypeOf(format), cancellationToken);
    }

    private async Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
    {
        await using var stream = await storage.GetObjectAsync(key, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
