using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Concurrency;

/// <summary>
/// The verb CONTRACT one concurrency-tracked entity's mutations must honour (ADR 0795): emit the ETag, honour
/// the caller's <c>If-Match</c>, run in ONE transaction with ONE commit, and fire side effects only AFTER that
/// commit. Not the handlers — the envelope around them.
/// </summary>
/// <typeparam name="TEntity">The tracked entity this contract belongs to.</typeparam>
/// <remarks>
/// <para>
/// ONE implementation, instantiated once per entity under its own name (<c>DocumentVerbs</c>,
/// <c>TenantVerbs</c>, …). The naming is the point: a mutation that does not go through its entity's contract is
/// visible in review and greppable by a guard, where a forgotten call to a shared helper is neither. That
/// distinction is not theoretical — <c>ConcurrencyHeaders</c> fixed the implementation and this fixes the shape,
/// because the thing it replaces had <c>SetETag</c> and <c>TryParseETag</c> written and called from NOWHERE
/// across six mutations of a tracked entity (#1167).
/// </para>
/// <para>
/// It holds no aspect knowledge — what a change IS stays in the controller, along with binding and
/// authorization. The controller holds no envelope knowledge in return. Per-entity differences ride as
/// constructor lambdas rather than as subclass bodies (the standing rule: a type-specific action forwards to
/// its generic with a lambda), so a named instantiation is one line.
/// </para>
/// </remarks>
/// <param name="dbContext">The scoped context the mutation writes through.</param>
/// <param name="staleToken">
/// The refusal this entity answers a stale precondition with — the per-entity difference that matters, because
/// "it changed" is only actionable if the reader knows WHAT changed.
/// </param>
public class EntityVerbContract<TEntity>(SimplArchiveDbContext dbContext, Func<Exception> staleToken)
    where TEntity : class, IConcurrencyTracked
{
    /// <summary>Puts the entity's current token on the response, so the caller can send it back.</summary>
    public void EmitETag(HttpResponse response, TEntity entity) => ConcurrencyHeaders.EmitETag(response, entity);

    /// <summary>
    /// Runs one mutation of <paramref name="entity"/> under the full contract: the caller's precondition is
    /// honoured, <paramref name="apply"/>'s changes commit in ONE transaction, and
    /// <paramref name="afterCommit"/> runs only once that commit has succeeded.
    /// </summary>
    /// <param name="request">The request, for its <c>If-Match</c>.</param>
    /// <param name="entity">The tracked entity being changed — already loaded and authorized by the caller.</param>
    /// <param name="apply">
    /// What the change IS. May touch other entities and child rows: everything it does joins the same
    /// transaction, which is what makes a partial failure impossible to observe.
    /// </param>
    /// <param name="afterCommit">
    /// Side effects — a search-index enqueue, an audit event, a webhook. Deliberately a separate argument rather
    /// than trailing code in <paramref name="apply"/>: fired before the commit, a side effect announces
    /// something that may then roll back, which is how an index comes to hold a version the database never had.
    /// </param>
    /// <param name="touchEntity">
    /// Marks the entity itself modified even when <paramref name="apply"/> only wrote CHILD rows. EF checks a
    /// concurrency token only on rows it is actually updating, so without this a precondition on a child-row
    /// write is silently ignored — the defect <c>PUT index-data</c> had (#1167). Default true, because the
    /// caller is by definition mutating this entity's resource.
    /// </param>
    public async Task MutateAsync(
        HttpRequest request,
        TEntity entity,
        Func<Task> apply,
        Func<Task>? afterCommit = null,
        bool touchEntity = true,
        CancellationToken cancellationToken = default)
    {
        await apply();

        ConcurrencyHeaders.ApplyIfMatch(dbContext, request, entity);
        if (touchEntity)
        {
            dbContext.Entry(entity).Property(e => e.ConcurrencyToken).IsModified = true;
        }

        // Owned only when nothing is already in flight: a module read-model context can enlist this one, and
        // beginning a second transaction inside that would throw where today it works (ADR 0781, the shape
        // BookingsController.Book established).
        var owned = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await using var transaction = owned;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw staleToken();
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        if (afterCommit is not null)
        {
            await afterCommit();
        }
    }

    /// <summary>The caller's token, or 428 — for a mutation where the header is already mandatory.</summary>
    public Guid RequireIfMatch(HttpRequest request) => ConcurrencyHeaders.RequireIfMatch(request);
}
