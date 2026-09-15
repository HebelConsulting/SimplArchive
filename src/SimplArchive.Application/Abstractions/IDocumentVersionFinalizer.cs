using SimplArchive.Domain.Documents;

namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Finalizes a freshly written document version the way an upload's does — confirm it, and let whatever reads
/// the bytes read them (ADR 0781).
/// </summary>
/// <remarks>
/// <para>
/// The seam exists for a layering reason with a real consequence. The finalizer lives in the Api project
/// because classification is an Api concern; the module facade lives in Infrastructure, which may not
/// reference Api. Without this abstraction a module's content write can only take the facade's own shortcut —
/// a confirmed version with no classification — and for a document whose bytes MEAN something (an
/// <c>.ics</c> that is a booking, ADR 0744) that shortcut writes bytes and claim rows that disagree.
/// </para>
/// <para>
/// The implementation is resolved LAZILY by its consumers, and must be: the finalizer reaches the classifier,
/// which reaches the booking admission seam, which may call back into the facade — a cycle that is fine at
/// runtime and fatal at construction time.
/// </para>
/// </remarks>
public interface IDocumentVersionFinalizer
{
    /// <summary>Finalizes <paramref name="version"/> — the same work the upload path does after the bytes
    /// land, including classification of the formats the core interprets.</summary>
    Task FinalizeAsync(DocumentVersion version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Files a version end to end — adding it when new, saving, confirming and classifying — as ONE unit of
    /// work, opening a transaction only when one is not already in flight (#1171, ADR 0781).
    /// </summary>
    /// <remarks>
    /// This is what a caller OUTSIDE a transition wants, and since #1171 it is what such a caller must use:
    /// <see cref="FinalizeAsync"/> now REFUSES to run without a transaction, because it commits several times
    /// and the caller has usually committed once already. A module reaching the archive from its own HTTP
    /// controller has no engine-owned transaction around it, so the plain finalize would throw where it used
    /// to warn (#1223).
    /// </remarks>
    Task FileAsync(DocumentVersion version, CancellationToken cancellationToken = default);
}
