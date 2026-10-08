namespace SimplArchive.Domain.Masks;

/// <summary>
/// A field value does not satisfy its definition: its format or range (ADR 0162), or, for a document reference,
/// it names no document in the tenant.
/// </summary>
/// <remarks>Its own type for the same reason as <see cref="MissingRequiredFieldException"/> (#1634).</remarks>
public sealed class FieldValueRejectedException(string message) : InvalidOperationException(message);
