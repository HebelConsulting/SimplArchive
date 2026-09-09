using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Whether a booking is suspended — DERIVED from the maintenance blocks of its resource (ADR 0778, #1091),
/// never stored.
/// </summary>
/// <remarks>
/// <para>
/// A booking is suspended exactly while an Active <see cref="ResourceBlock"/> of the resource it claims
/// overlaps its slot. Deriving it rather than writing a status is what makes revival correct by
/// construction: clearing a block un-suspends precisely what it caught, because there was never a second
/// copy of the fact to update. The alternative's failure mode — a cleared block with rows left suspended, a
/// flight nobody may take and nothing explains — cannot occur here.
/// </para>
/// <para>
/// Stated once, in one class, because it is asked from three places that must agree: the booking resource
/// the app renders, the CalDAV bytes a phone is served, and the audit detail written when a block is placed.
/// Two copies of an overlap test is how one surface ends up calling a flight suspended while another shows
/// it as normal.
/// </para>
/// <para>
/// Both ranges are half-open <c>[start, end)</c>, the same semantics bookings already use against each
/// other — so a block ending exactly when a booking starts does not catch it.
/// </para>
/// </remarks>
internal static class BookingSuspension
{
    /// <summary>The subset of the given BOOKING DOCUMENTS whose claims are caught by an active block.</summary>
    /// <remarks>
    /// Set-based on purpose: a listing asks this once for a page rather than once per row, which is the
    /// per-row cost ADR 0557 forbids. The overlap comparison runs in memory because the SQLite provider
    /// cannot translate <see cref="DateTimeOffset"/> range predicates — the same reason the booking overlap
    /// invariant does — and the candidate set is one page of bookings against one resource's blocks.
    /// </remarks>
    internal static async Task<HashSet<Guid>> SuspendedAmongAsync(
        SimplArchiveDbContext db, IReadOnlyCollection<Guid> bookingDocumentIds, CancellationToken cancellationToken)
    {
        if (bookingDocumentIds.Count == 0)
        {
            return [];
        }

        var claims = await db.ResourceBookings
            .Where(b => bookingDocumentIds.Contains(b.BookingDocumentId) && b.Status == BookingStatus.Active)
            .Select(b => new { b.BookingDocumentId, b.ResourceDocumentId, b.StartsAtUtc, b.EndsAtUtc })
            .ToListAsync(cancellationToken);
        if (claims.Count == 0)
        {
            return [];
        }

        var resourceIds = claims.Select(c => c.ResourceDocumentId).Distinct().ToList();
        var blocks = await db.ResourceBlocks
            .Where(b => resourceIds.Contains(b.ResourceDocumentId) && b.Status == BlockStatus.Active)
            .Select(b => new { b.ResourceDocumentId, b.StartsAtUtc, b.EndsAtUtc })
            .ToListAsync(cancellationToken);
        if (blocks.Count == 0)
        {
            return [];
        }

        // ANY claim being blocked suspends the whole booking (owner decision: a partial overlap suspends the
        // whole thing). A training flight whose aircraft is grounded does not become a flight with two
        // people and no aeroplane — it does not happen.
        return [.. claims
            .Where(c => blocks.Any(b => b.ResourceDocumentId == c.ResourceDocumentId
                && b.StartsAtUtc < c.EndsAtUtc && c.StartsAtUtc < b.EndsAtUtc))
            .Select(c => c.BookingDocumentId)
            .Distinct()];
    }

    /// <summary>Whether this one booking document is suspended.</summary>
    internal static async Task<bool> IsSuspendedAsync(
        SimplArchiveDbContext db, Guid bookingDocumentId, CancellationToken cancellationToken) =>
        (await SuspendedAmongAsync(db, [bookingDocumentId], cancellationToken)).Count > 0;

    /// <summary>
    /// Rewrites a served <c>.ics</c> so every event reads <c>STATUS:TENTATIVE</c> (ADR 0778, owner decision).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bytes served here differ from the bytes stored.</b> That is deliberate and is the whole point:
    /// a pilot's phone is where they will actually look, and a grounded aircraft's flight showing as a normal
    /// confirmed booking is the failure this prevents. A raw WebDAV download of the same document returns the
    /// stored bytes, unchanged — so the two surfaces disagree for exactly as long as the block is active.
    /// </para>
    /// <para>
    /// A targeted text edit rather than an Ical.Net round trip: deserialising and re-serialising rewrites
    /// line folding, property order and generated fields throughout, so the served bytes would differ from
    /// the stored ones in a dozen ways that have nothing to do with the status. Minimal edits keep the
    /// difference exactly as large as the fact being projected.
    /// </para>
    /// </remarks>
    internal static string ProjectSuspended(string ics)
    {
        // Split keeping the line endings the file actually uses: RFC 5545 says CRLF, and a client parsing a
        // file we re-joined with bare LF is a compatibility risk taken for nothing.
        var newline = ics.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = ics.Split(newline);
        var result = new List<string>(lines.Length + 4);
        var insideEvent = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                insideEvent = true;
                result.Add(line);
                result.Add("STATUS:TENTATIVE");
                continue;
            }

            if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                insideEvent = false;
                result.Add(line);
                continue;
            }

            // Drop any status the stored file carried, so the projection cannot end up with two.
            if (insideEvent && line.StartsWith("STATUS:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(line);
        }

        return string.Join(newline, result);
    }
}
