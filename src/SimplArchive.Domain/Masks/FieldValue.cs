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

    /// <summary>This value's position within its field's list, counting from zero (#703).</summary>
    /// <remarks>
    /// <para>
    /// Rows carry no inherent order, so without this a list came back in whatever order the database chose —
    /// and that order CHANGED between reads. A user who typed three addresses and reopened the pane found
    /// them rearranged, intermittently, which reads as the application losing track of what they entered.
    /// </para>
    /// <para>
    /// Zero for a single-valued field, and zero for every row that predates this — correct rather than a
    /// guess, since every one of them is either a lone value or a set nobody ordered. That is also why reads
    /// tie-break on <c>Id</c>: a MultiSelect field seeded before this has several rows all sharing ordinal 0,
    /// and an arbitrary-but-STABLE order is the improvement available to them without inventing one.
    /// </para>
    /// </remarks>
    public int Ordinal { get; set; }
}
