using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Lets a caller outside the Api project run the upload path's finalize (ADR 0781) — today, a module
/// replacing a document's content through the facade.
/// </summary>
/// <remarks>
/// <para>
/// <b>The finalizer is resolved lazily, and must be.</b> <see cref="DocumentFinalizer"/> reaches
/// <see cref="CalendarContactClassifier"/>, which reaches the booking-admission seam, which hands a module
/// the archive facade — whose own dependency is this adapter. As a constructor edge that is a cycle the
/// container follows at startup; resolved on call it is ordinary re-entrancy, which the booking path already
/// expects and documents.
/// </para>
/// </remarks>
public sealed class DocumentVersionFinalizerAdapter : IDocumentVersionFinalizer
{
    private readonly IServiceProvider _services;

    public DocumentVersionFinalizerAdapter(IServiceProvider services) => _services = services;

    public Task FinalizeAsync(DocumentVersion version, CancellationToken cancellationToken = default) =>
        ((DocumentFinalizer)_services.GetService(typeof(DocumentFinalizer))!).FinalizeAsync(version, cancellationToken);
}
