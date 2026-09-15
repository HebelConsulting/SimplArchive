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
    /// <param name="gateBeforeApply">
    /// States the precondition BEFORE <paramref name="apply"/> runs, for the case where <paramref name="apply"/>
    /// delegates to a collaborator that SAVES. See the remarks on the ordering below: the default is correct
    /// while apply only mutates the change tracker, and is actively wrong once it does not.
    /// </param>
    public async Task MutateAsync(
        HttpRequest request,
        TEntity entity,
        Func<Task> apply,
        Func<Task>? afterCommit = null,
        bool touchEntity = true,
        bool gateBeforeApply = false,
        CancellationToken cancellationToken = default)
    {
        // Owned only when nothing is already in flight: a module read-model context can enlist this one, and
        // beginning a second transaction inside that would throw where today it works (ADR 0781, the shape
        // BookingsController.Book established).
        //
        // It opens BEFORE apply rather than after. For an apply that only mutates the change tracker — which is
        // all 37 call sites at the time of writing, every one a single-expression lambda with no external I/O —
        // that is indistinguishable, since nothing is written until the save below. It stops being
        // indistinguishable the moment an apply SAVES: opened afterwards, those saves are each separately
        // durable and a failure part-way leaves a state the user never asked for. Opening first means an apply
        // that saves is atomic by construction rather than by the caller remembering (ADR 0794, #1171).
        var owned = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await using var transaction = owned;

        try
        {
            // WHEN THE PRECONDITION IS STATED, and why it is not always the same moment.
            //
            // Stating it after apply is right while apply only mutates the change tracker: nothing has been
            // written, so the token still holds the value the caller read, and one save carries both the
            // change and the precondition.
            //
            // It is WRONG when apply delegates to something that saves. DocumentFinalizer saves seven times,
            // and several of those write the document row itself — Name, MaskVersionId, SensitivityLabelId,
            // CurrentVersionId. Each save regenerates the token, so a precondition applied afterwards compares
            // the caller's tag against a value the finalizer itself has just written, and refuses EVERY
            // caller with 412 — including the one holding a perfectly current tag. That is not a theoretical
            // ordering concern: it is what five tests reported when this was first attempted as a plain wrap.
            //
            // Gating first hands the caller's tag to that collaborator's own first save, which is also the
            // honest reading of ADR 0794 — the tag must be the one the USER saw, not one minted moments ago by
            // the very operation being gated.
            if (gateBeforeApply)
            {
                Gate();
            }

            await apply();

            if (!gateBeforeApply)
            {
                Gate();
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Covers apply() too, deliberately. Once the gate is stated first, the refusal is raised by the
            // COLLABORATOR's save rather than by ours, and left untranslated it would surface as a bare 500
            // instead of the 412 the whole mechanism exists to produce.
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

        void Gate()
        {
            ConcurrencyHeaders.ApplyIfMatch(dbContext, request, entity);
            if (touchEntity)
            {
                dbContext.Entry(entity).Property(e => e.ConcurrencyToken).IsModified = true;
            }
        }
    }

    /// <summary>The caller's token, or 428 — for a mutation where the header is already mandatory.</summary>
    public Guid RequireIfMatch(HttpRequest request) => ConcurrencyHeaders.RequireIfMatch(request);
}
