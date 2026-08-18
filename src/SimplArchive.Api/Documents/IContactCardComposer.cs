namespace SimplArchive.Api.Documents;

/// <summary>
/// A structured, editable view of a contact card for the rich edit form (SimplCalCon ADR 0082, ported per ADR 0621). Read from and
/// merged back into the vCard blob so that properties the form doesn't model (PHOTO, X-*, IMPP,
/// CATEGORIES, extra fields…) survive an edit.
/// </summary>
public sealed record ContactCard(
    string? FormattedName,
    string? GivenName,
    string? FamilyName,
    string? Organization,
    string? Title,
    IReadOnlyList<ContactField> Emails,
    IReadOnlyList<ContactField> Phones,
    IReadOnlyList<ContactAddress> Addresses,
    string? Birthday,
    string? Url,
    string? Note)
{
    public static ContactCard Empty { get; } =
        new(null, null, null, null, null, [], [], [], null, null, null);
}

/// <summary>A typed multi-value field (email/phone). <paramref name="Type"/> is a vCard TYPE like home/work/cell (nullable).</summary>
public sealed record ContactField(string Value, string? Type);

/// <summary>A postal address (vCard ADR). All parts optional.</summary>
public sealed record ContactAddress(
    string? Type, string? Street, string? City, string? Region, string? PostalCode, string? Country);

/// <summary>
/// Lossless structured read/merge of a contact vCard (SimplCalCon ADR 0082, ported per ADR 0621). <see cref="Merge"/> updates only the
/// modelled properties on the existing card and leaves everything else intact.
/// </summary>
public interface IContactCardComposer
{
    /// <summary>Parses a vCard blob into the structured editable view.</summary>
    ContactCard Read(string blob);

    /// <summary>
    /// Applies <paramref name="card"/> onto <paramref name="existingBlob"/> (or a fresh card when null),
    /// preserving unmodelled properties, and returns the serialized vCard. <paramref name="uid"/> is kept.
    /// </summary>
    string Merge(string? existingBlob, ContactCard card, string uid);
}
