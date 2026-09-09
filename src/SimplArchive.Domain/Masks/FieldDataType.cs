namespace SimplArchive.Domain.Masks;

public enum FieldDataType
{
    Text,
    Number,
    Date,

    /// <summary>
    /// A point in time, stored ISO-8601 with an OFFSET (<c>2026-08-29T19:00:00-04:00</c>) — #660.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Date"/> because a date has no time and therefore no zone, while a moment has
    /// both and is ambiguous without them. Where the source carries a zone its own is used; where it floats,
    /// the SERVER's zone is stamped at index time, so every stored value is a real instant that sorts against
    /// every other. That makes the value environment-dependent — the same floating item indexed on a UTC
    /// container and on a machine in Zurich differs by the offset — which is the deliberate cost of having one
    /// comparable instant instead of a wall clock that sorts by coincidence.
    /// </remarks>
    DateTime,
    Boolean,
    SingleSelect,
    MultiSelect,

    /// <summary>
    /// An e-mail address, validated for shape and compared case-insensitively (#703).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Appended last, and every future value must be too.</b> The value is persisted as its integer
    /// ordinal in <c>FieldDefinitions.DataType</c>, so inserting a value anywhere but the end silently
    /// re-types every stored field definition in every tenant.
    /// </para>
    /// <para>
    /// Distinct from a <see cref="Text"/> field carrying a <c>FormatPattern</c>, though that would validate
    /// the same shape today. What the type adds is that the meaning travels with the field rather than with
    /// a pattern a tenant may edit away: an address is compared case-insensitively (the
    /// <c>NormalizedEmail</c> precedent, ADR 0150), and a later slice claims addresses for mail delivery —
    /// both of which have to know an address IS one, not that it happened to match a regex.
    /// </para>
    /// </remarks>
    EmailAddress,

    /// <summary>
    /// An absolute http/https URL, validated for shape (ABI 0.6) — rendered as a clickable link in both
    /// clients' detail panes (the DABS folder's link to its source portal).
    /// </summary>
    /// <remarks>
    /// <b>Appended last, and every future value must be too</b> — the ordinal is persisted in
    /// <c>FieldDefinitions.DataType</c>, so inserting anywhere but the end silently re-types every stored
    /// field. Distinct from a <see cref="Text"/> field with a URL <c>FormatPattern</c>, the same way
    /// <see cref="EmailAddress"/> is: the meaning (this IS a link, render it clickable) travels with the type
    /// rather than with a pattern a tenant could edit away.
    /// </remarks>
    Url,

    /// <summary>
    /// Another document in this tenant, stored as its id — the typed relationship a mask names
    /// ("Flight", "Syllabus", "Aircraft") rather than a generic association between two documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Appended last, and every future value must be too</b> — the ordinal is persisted in
    /// <c>FieldDefinitions.DataType</c>, so inserting anywhere but the end silently re-types every stored
    /// field. The same reasoning as <see cref="EmailAddress"/> and <see cref="Url"/> puts this in the type
    /// rather than in a <see cref="Text"/> field's <c>FormatPattern</c>: what the type adds is that the
    /// value IS a document — so it is validated against one existing, resolved to a name and an address the
    /// client can follow, and rendered as something to open rather than a raw GUID.
    /// </para>
    /// <para>
    /// It stays index data on purpose, which is what a generic document-to-document link could not be: the
    /// relationship is named by its field, appears in the index pane, is exportable and searchable, can be
    /// <c>Required</c>, and — the deciding one for industry modules — is readable by the state-machine
    /// condition grammar, which sees fields and nothing else.
    /// </para>
    /// <para>
    /// WHICH documents may be chosen is deliberately NOT declared here. A module expresses that through its
    /// machine's <c>Proposal</c> (ABI 0.11, ADR 0769), whose query is strictly more expressive than a
    /// mask-or-folder rule could be — "dossiers whose newest examiner certificate is valid" is not a folder.
    /// </para>
    /// </remarks>
    DocumentReference,
}
