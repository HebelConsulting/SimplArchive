using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Audit;

// Who performed an audited action — see ADR "Audit trail (first slice)". A PlatformAdministrator can act on
// a tenant (e.g. creating it), so it's a valid actor even though it isn't itself ITenantScoped.
public enum AuditActorType
{
    User = 0,
    ServiceAccount = 1,
    PlatformAdministrator = 2,

    // A background process with no interactive principal — e.g. the retention sweep auto-disposing an expired
    // document (ADR "Retention policies (auto-disposition)"). ActorId is Guid.Empty.
    System = 3,

    // An anonymous access through an external link (ADR 0546). ActorId is the LINK's id — the link is the
    // credential that acted, and recording it lets an investigator pivot from a leaked token to every access it
    // made. Deliberately not folded into System, which would lump third-party reads in with the retention sweep,
    // and deliberately not the link's creator, who did not perform the access.
    ExternalLink = 4,
}

// An append-only audit record of a security-sensitive action (ADR "Audit trail (first slice)"). Never
// edited or deleted (no IConcurrencyTracked/ISoftDeletable); retention/purge is deferred. ITenantScoped, so
// the tenant query filter scopes reads to the caller's tenant. Actor/target names are snapshots taken at
// record time, since the underlying rows can be renamed or removed later. Tamper-evident via a per-tenant
// hash chain (ADR "Audit trail hash chain") — see Sequence/Hash below.
public class AuditEvent : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Per-tenant monotonic chain position (from 0). A unique (TenantId, Sequence) index enforces one fork-free
    // chain per tenant and backstops concurrent appends. See ADR "Audit trail hash chain".
    public long Sequence { get; set; }

    // SHA-256 (hex) of the previous event's Hash (a fixed seed for the genesis event) plus this event's
    // canonical fields, making any edit/deletion/reorder detectable. Never set manually — AuditRecorder
    // computes it; the verifier recomputes + compares.
    public required string Hash { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public AuditActorType ActorType { get; set; }

    public Guid ActorId { get; set; }

    public required string ActorName { get; set; }

    // A stable action code, e.g. "Document.Deleted" / "Acl.Granted" / "User.RightsChanged" — see AuditActions.
    public required string Action { get; set; }

    // The affected entity (null for target-less events like a login).
    public string? TargetType { get; set; }

    public Guid? TargetId { get; set; }

    public string? TargetName { get; set; }

    // Small free-form detail (a rejection reason, the rights changed, the granted rights, …). No JSON —
    // provider-agnostic text.
    public string? Details { get; set; }
}
