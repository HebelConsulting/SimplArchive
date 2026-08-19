using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// What the structured-item saves — the contact card, the appointment, and the raw source behind both — must do
/// to the DOCUMENT after writing a new version (#648, ADR 0643).
/// </summary>
public static class StructuredItemVersioning
{
    /// <summary>
    /// Moves the document's concurrency token, because its content changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These editors advertise the DOCUMENT's <c>ConcurrencyToken</c> as their ETag and require it back as
    /// <c>If-Match</c> — a version is append-only and carries none. But a content save writes a new
    /// <c>DocumentVersion</c> and, for an item already classified and already unpinned, modifies no column on
    /// the document at all. So <c>SaveChanges</c> had nothing to regenerate the token from, and the token
    /// **never moved**: two people editing the same card both held a valid <c>If-Match</c>, both saves
    /// succeeded, and the second silently overwrote the first. The guard was enforced and inert.
    /// </para>
    /// <para>
    /// That was survivable while every such save merged — the loser lost the fields they had changed. It is not
    /// survivable now: a raw source save REPLACES the stored item, so the same race loses somebody's whole card
    /// rather than one property. Marking the document modified is what makes the token move, and therefore what
    /// makes the second save fail with <c>412</c> as it always claimed it would.
    /// </para>
    /// <para>
    /// <c>State = Modified</c> rather than assigning a token: <c>SaveChanges</c> owns that value for every
    /// <c>IConcurrencyTracked</c> entity and setting it by hand is exactly what CLAUDE.md forbids.
    /// </para>
    /// <para>
    /// Deliberately NOT done inside <c>DocumentFinalizer</c>, which every upload, check-in, intray filing and
    /// WebDAV <c>PUT</c> also funnels through. Moving a document's token on every new version is arguably right
    /// and is a decision with a far wider blast radius than this one — it belongs to whoever takes it, not to a
    /// side effect of adding a raw editor.
    /// </para>
    /// </remarks>
    public static async Task MarkContentChangedAsync(
        SimplArchiveDbContext dbContext, Document document, CancellationToken cancellationToken)
    {
        dbContext.Entry(document).State = EntityState.Modified;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
