using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Api.Concurrency;

/// <summary>
/// The ETag/<c>If-Match</c> edge, written ONCE (#1083). Emitting the token on a read and honouring the
/// caller's precondition on a write is the same three lines at every mutation, and it had already been copied
/// into three controllers under three names — <c>TryParseETag</c> twice and <c>RequireIfMatch</c> once — which
/// is how the fourth copy comes to disagree with the first about a quoted or weak tag.
/// </summary>
/// <remarks>
/// <para>
/// ADR 0003's contract: a stale token is <b>412</b> (the DbUpdateConcurrencyException the EF token raises), a
/// missing one where it is demanded is <b>428</b>. The mechanism itself lives in the DbContext — the token is a
/// real column, regenerated for every Added/Modified <see cref="IConcurrencyTracked"/> entity — so a handler's
/// whole job is to tell EF which value the caller believed it was editing.
/// </para>
/// <para>
/// <see cref="ApplyIfMatch"/> is DELIBERATELY tolerant of an absent header, and that is a staging decision, not
/// a weakening: requiring <c>If-Match</c> is a breaking change for every client that does not yet send one, so
/// the rollout emits tags and honours preconditions first, the clients learn to send them, and only then does
/// the header become mandatory. A caller that sends a token gets full protection immediately; one that sends
/// none keeps today's last-write-wins until the third step. Use <see cref="RequireIfMatch"/> where the header
/// is already mandatory.
/// </para>
/// </remarks>
internal static class ConcurrencyHeaders
{
    /// <summary>Puts the entity's current token on the response, so the caller can send it back.</summary>
    internal static void EmitETag(HttpResponse response, IConcurrencyTracked entity) =>
        response.Headers.ETag = $"\"{entity.ConcurrencyToken}\"";

    /// <summary>The caller's <c>If-Match</c> token, or null when it sent none (or sent an unparseable one).</summary>
    /// <remarks>
    /// An unparseable tag reads as ABSENT rather than as a mismatch, matching the copies this replaces. It is
    /// the honest reading while the header is optional: a client that garbles the tag is one that does not
    /// really send it, and answering 412 would claim we compared something.
    /// </remarks>
    internal static Guid? IfMatch(HttpRequest request) =>
        request.Headers.TryGetValue("If-Match", out var values) && Guid.TryParse(values.ToString().Trim('"'), out var token)
            ? token
            : null;

    /// <summary>
    /// Honours the caller's precondition when it sent one: EF then compares this value to the stored column and
    /// raises <see cref="DbUpdateConcurrencyException"/> — which the Api boundary renders as 412
    /// <c>ETAG_MISMATCH</c> — if somebody else has written since. A no-op when no token was sent.
    /// </summary>
    internal static void ApplyIfMatch<TEntity>(DbContext dbContext, HttpRequest request, TEntity entity)
        where TEntity : class, IConcurrencyTracked
    {
        if (IfMatch(request) is { } token)
        {
            dbContext.Entry(entity).Property(e => e.ConcurrencyToken).OriginalValue = token;
        }
    }

    /// <summary>The caller's token, or 428 when it sent none — for a mutation where the header is mandatory.</summary>
    internal static Guid RequireIfMatch(HttpRequest request) =>
        IfMatch(request) ?? throw new Errors.Exceptions.Concurrency.IfMatchRequiredException();
}
