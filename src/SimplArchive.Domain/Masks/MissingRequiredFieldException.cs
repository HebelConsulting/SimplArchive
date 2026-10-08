namespace SimplArchive.Domain.Masks;

/// <summary>A document's mask requires a field it has no value for (ADR 0176).</summary>
/// <remarks>
/// Its own type so the Api can say so without GUESSING. SaveChanges raised the same bare exception for a missing
/// required field and for an invalid value, and the endpoints guessed between them by what the request changed,
/// which gave the wrong advice whenever the guess was wrong (#1634).
/// </remarks>
public sealed class MissingRequiredFieldException(string message) : InvalidOperationException(message);
