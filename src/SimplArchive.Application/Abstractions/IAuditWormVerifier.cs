namespace SimplArchive.Application.Abstractions;

// Verifies the current tenant's sealed WORM audit segments against the DB (ADR "Audit WORM segment verify"). The
// segments are object-lock-immutable, so comparing each sealed event's hash to the DB event's hash detects a DB
// tamper that recomputed the whole chain (which the DB chain check, ADR "Audit trail hash chain", cannot catch).
// Also confirms the segments are contiguous from Sequence 0 and reach the archived checkpoint. Tenant-scoped.
public interface IAuditWormVerifier
{
    Task<AuditWormVerification> VerifyAsync(CancellationToken cancellationToken = default);
}

// Valid = the sealed segments are contiguous and every sealed event's hash matches the DB (where the DB still has
// that Sequence). SegmentCount = sealed segment objects read; CheckedCount = sealed events walked. On a failure,
// BrokenAtSequence + Reason locate it ("segment-gap" — a missing/reordered sealed event; "db-mismatch" — the
// immutable segment disagrees with the DB, i.e. the DB was tampered; "missing-segment" — segments don't reach the
// archived checkpoint).
public sealed record AuditWormVerification(bool Valid, int SegmentCount, int CheckedCount, long? BrokenAtSequence, string? Reason);
