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
}
