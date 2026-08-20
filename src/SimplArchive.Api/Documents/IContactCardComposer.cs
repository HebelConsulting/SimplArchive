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

/// <summary>A contact's picture, decoded from the card's own bytes.</summary>
/// <param name="ContentType">
/// One of a small allowlist of raster image types. Never taken on trust from the card: a vCard is user-supplied
/// data, and serving back whatever content type it names is how <c>image/svg+xml</c> — a scriptable document —
/// gets echoed to a browser from our own origin.
/// </param>
public sealed record ContactPhoto(byte[] Bytes, string ContentType);

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

    /// <summary>
    /// The card's INLINE photo, or null when it has none — including when it names one somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inline only. An external <c>PHOTO</c> URL is deliberately not fetched.</b> Cards exported from some
    /// address books carry <c>PHOTO;VALUE=URI:https://…</c>, and following it would mean this server issuing
    /// requests to arbitrary attacker-controllable hosts on behalf of whoever imported the card — an SSRF hole
    /// that a connect-time IP guard can close but that nothing here needs to open in the first place. The
    /// consequence is stated rather than hidden: such a card shows initials, not a face.
    /// </para>
    /// <para>
    /// PHOTO survives an edit either way — it is one of the unmodelled properties <see cref="Merge"/> preserves
    /// verbatim — so this reads what is already there and never writes.
    /// </para>
    /// </remarks>
    ContactPhoto? ReadPhoto(string blob);
}
