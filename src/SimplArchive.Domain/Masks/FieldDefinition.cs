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

    public string? FormatPattern { get; set; }

    public int? MaxTextLength { get; set; }

    public string? MinValue { get; set; }

    public string? MaxValue { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
