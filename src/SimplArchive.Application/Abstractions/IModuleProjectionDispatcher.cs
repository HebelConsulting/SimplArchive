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
    Task DispatchAsync(
        Guid documentId, Guid maskId, Guid tenantId, bool removed, CancellationToken cancellationToken = default);
}
