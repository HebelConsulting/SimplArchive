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
}
