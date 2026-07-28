using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Masks;

// A stable identity that outlives edits — see ADR "Mask versioning data shape". Name and field
// definitions belong to a specific MaskVersion, not to this identity, since masks are immutable
// versions (ADR "Mask/schema versioning and migration strategy"): editing a mask creates a new
// MaskVersion rather than mutating an existing one.
public class Mask : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
