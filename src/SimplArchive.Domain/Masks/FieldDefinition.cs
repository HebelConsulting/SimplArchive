using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Masks;

// Validation rule shape decided in ADR "Metadata field validation rules": Required and Format/range are
// configurable per field (Unique was also originally part of this, but was removed entirely — see ADR
// "Repository/Document unification" — field metadata doesn't need cross-document uniqueness).
// FormatPattern/MaxTextLength only apply to Text fields; MinValue/MaxValue apply to Number/Date fields and
// are stored generically (as text, parsed based on DataType) — consistent with the same reasoning already
// applied to FieldValue.Value in ADR "EAV field value storage strategy": these constraints are checked at
// the application layer, not via SQL range queries.
public class FieldDefinition : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid MaskVersionId { get; set; }

    public required string Name { get; set; }

    public FieldDataType DataType { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>
    /// Whether the field holds MANY values of its <see cref="DataType"/> rather than one (#703).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Multiplicity is <b>orthogonal</b> to type: any basic type may be a list, and each element is validated
    /// against the type exactly as a single value would be — which the storage already permits, since
    /// <c>FieldValue</c> rows were never unique per <c>(DocumentId, FieldDefinitionId)</c>.
    /// </para>
    /// <para>
    /// <see cref="FieldDataType.MultiSelect"/> is <b>grandfathered</b> and keeps its own multiplicity: it is
    /// already a list by virtue of its type, so it accepts many values whether or not this flag is set, and
    /// setting the flag on one changes nothing. Migrating it to <c>SingleSelect</c> + <c>IsList</c> would be
    /// the cleaner end state, but it is a data migration across every type-switching surface for no
    /// user-visible gain. Dedicated <c>*List</c> types were rejected outright — that is one copy of every
    /// type, which is the shape the standing generic-implementation principle exists to forbid.
    /// </para>
    /// </remarks>
    public bool IsList { get; set; }

    public string? FormatPattern { get; set; }

    public int? MaxTextLength { get; set; }

    public string? MinValue { get; set; }

    public string? MaxValue { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
