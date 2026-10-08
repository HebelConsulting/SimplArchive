namespace SimplArchive.Domain.Documents;

/// <summary>Two documents under one parent would share a name (ADRs 0177/0200).</summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so the existing boundary catches keep working, like every
/// DbContext invariant. The dedicated type exists because those catches mapped EVERY invariant to "a document with
/// this name already exists", and that is now true only of this one (#1634).
/// </remarks>
public sealed class DocumentNameNotUniqueException(string message) : InvalidOperationException(message);
