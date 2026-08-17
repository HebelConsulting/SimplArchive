namespace SimplArchive.Domain.Masks;

// Thrown when a well-known mask's specification gains a REQUIRED field that an already-seeded tenant is missing.
//
// The seeder reconciles missing fields onto an existing well-known mask at startup, which is safe precisely
// because an optional field is additive: no stored value changes meaning and no existing document becomes
// invalid. A required field is not additive — the required-field validation (ADR 0176) runs on mask
// (re)assignment, so adding one retroactively invalidates every document already carrying that mask.
//
// Failing loudly is the point. The two alternatives are worse: adding it anyway breaks existing documents at
// startup, and skipping it quietly leaves the mask permanently wrong in that tenant with nobody informed. If a
// well-known mask genuinely needs a new required field, that is a deliberate data migration which decides what
// the existing documents should carry — not something a startup probe can infer.
public sealed class RequiredFieldAddedToWellKnownMaskException : Exception
{
    public RequiredFieldAddedToWellKnownMaskException(Guid maskId, Guid tenantId, IReadOnlyList<string> fieldNames)
        : base($"Well-known mask {maskId} is missing required field(s) [{string.Join(", ", fieldNames)}] in tenant {tenantId}. "
            + "The seeder only adds OPTIONAL fields to an existing mask, because a required field would retroactively "
            + "invalidate the documents already carrying it. Add the field as optional, or write a data migration that "
            + "decides what existing documents should hold for it.")
    {
        MaskId = maskId;
        TenantId = tenantId;
        FieldNames = fieldNames;
    }

    public Guid MaskId { get; }

    public Guid TenantId { get; }

    public IReadOnlyList<string> FieldNames { get; }
}
