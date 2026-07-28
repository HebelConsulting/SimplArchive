using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Masks;

// An immutable version of a Mask — see ADR "Mask versioning data shape". Created new on every edit
// (renaming, or adding/removing/changing fields), never mutated afterward. IsCurrent identifies the
// newest version for its Mask, maintained by SimplArchiveDbContext.SaveChanges (see ADR "Mask name
// uniqueness across versions"), which also drives the partial unique index on (TenantId, Name).
public class MaskVersion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid MaskId { get; set; }

    public int VersionNumber { get; set; }

    public required string Name { get; set; }

    public bool IsCurrent { get; set; }

    // The approval-review SLA (in days) for documents of this mask type (ADR "Workflow escalation / SLA
    // reminders"). Null = no SLA → a review of such a document gets no deadline tracking. Versioned with the
    // mask like the fields: editing the SLA means a new MaskVersion.
    public int? ReviewSlaDays { get; set; }

    // The records-retention period (in years) for documents of this mask type (ADR "Retention policies
    // (auto-disposition)"). Null = no retention → documents of this type are never auto-disposed. Versioned
    // with the mask like ReviewSlaDays. A document's period comes from its assigned MaskVersion, so an older
    // version keeps its own value.
    public int? RetentionYears { get; set; }

    // The upload-time default sensitivity label for documents of this type (ADR "Configurable sensitivity labels
    // + upload defaults") — applied by DocumentFinalizer when a document is auto-classified/assigned this mask and
    // has no label yet. Null = no default. A per-tenant SensitivityLabelDefinition id.
    public Guid? DefaultSensitivityLabelId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
