namespace SimplArchive.Domain.Documents;

/// <summary>A parent assignment would make a document its own ancestor (ADR 0177).</summary>
/// <remarks>Its own type so the Api reports the move it refuses, not a name clash (#1634).</remarks>
public sealed class DocumentParentCycleException(string message) : InvalidOperationException(message);
