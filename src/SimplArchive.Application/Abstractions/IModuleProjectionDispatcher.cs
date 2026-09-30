namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Tells loaded modules that a document they project has changed (ABI 1.1, ADR 0848) — the seam that keeps
/// <c>SimplArchiveDbContext</c> free of any knowledge of modules, exactly as
/// <see cref="IDavCollectionKindRegistry"/> does for DAV kinds.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ProjectedMasks"/> is the cheap gate, and it is what keeps this free where nobody uses it.</b>
/// The collector reads it first and returns before touching the <c>ChangeTracker</c> when it is empty — which
/// it is in every deployment with no such module, and in every deployment with modules that project only what
/// they themselves write.
/// </para>
/// <para>
/// A singleton, like the DAV registry and for the same reason: the declaration belongs to the loaded assembly,
/// not to a tenant.
/// </para>
/// </remarks>
public interface IModuleProjectionDispatcher
{
    /// <summary>
    /// Every mask any loaded module declares it projects. Empty means nothing to do — read this before doing
    /// any work at all.
    /// </summary>
    IReadOnlyCollection<Guid> ProjectedMasks { get; }

    /// <summary>
    /// Re-derives one document's projection in every module that declared its mask.
    /// </summary>
    /// <remarks>
    /// Called from inside the core's save, inside its transaction (ADR 0848). A module's exception is NOT
    /// swallowed — it propagates and fails the write, because a projection that silently did not happen is
    /// how a revoked certificate goes on being addressed.
    /// </remarks>
    /// <param name="ambient">
    /// The caller's own connection and open transaction, so the projection joins BOTH (see
    /// <see cref="AmbientDatabaseTransaction"/>). Null only where there is no transaction to join.
    /// </param>
    Task DispatchAsync(
        Guid documentId,
        Guid maskId,
        Guid tenantId,
        bool removed,
        AmbientDatabaseTransaction? ambient = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The core save's own database connection and the transaction it has open, handed to the projection so the
/// projection runs on both (ADR 0848).
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because a projection that does not share the connection cannot see what it is projecting.</b>
/// The dispatcher works in a DI scope of its own — it must, or it would hand a module the core context whose
/// ChangeTracker is mid-save — and a fresh scope means a fresh <c>DbContext</c>, which takes its own connection
/// from the pool. The save's rows are written but NOT yet committed, so on any other connection they do not
/// exist: the module's hook asked the facade for the document that triggered it and was told there is no such
/// document, took its own "nothing to derive" branch, and returned without writing or logging anything.
/// </para>
/// <para>
/// Measured on the demo stack: a certificate enrolled through the encryption module's own surface never reached
/// its read model, so the module answered "this reader holds no certificate" — and because a module that
/// answers is the ONLY source (ADR 0842), activating the module REMOVED the reader's access. The tell was that
/// <c>POST /api/modules/{id}/rebuild/{projection}</c> fixed it every single time: a rebuild is its own request,
/// after the commit, where the rows are simply there.
/// </para>
/// <para>
/// Sharing the transaction is the second half and is what ADR 0848 actually promised. A module read-model
/// context in the fresh scope wrote through a connection with no transaction on it, so its row committed on its
/// own — the projection could commit while the document write rolled back, which is the failure that ADR's
/// "one transaction" was written to make impossible. Both halves come from the same fact, which is why they are
/// one parameter rather than two.
/// </para>
/// <para>
/// <see cref="System.Data.Common"/> rather than anything of EF's on purpose: this layer knows nothing about
/// EF Core, and a connection plus a transaction is exactly what is being lent — no context, no options, no
/// provider.
/// </para>
/// </remarks>
public sealed record AmbientDatabaseTransaction(
    System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction);
