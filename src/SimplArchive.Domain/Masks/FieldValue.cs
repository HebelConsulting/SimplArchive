using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Masks;

// The EAV row itself — see ADR "EAV field value storage strategy": Value is a single generic text column
// regardless of the field's DataType, and a multi-select field's several selections become several rows
// sharing the same (DocumentId, FieldDefinitionId).
//
// DocumentId is a real foreign key to Document — see ADR "Document/DocumentVersion data shape
// (entities-only slice)". RepositoryId used to be denormalized here to back the Unique constraint's
// partial index, but that constraint was removed entirely (ADR "Repository/Document unification"), so
// there's nothing left needing it.
public class FieldValue : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public required string Value { get; set; }
}
