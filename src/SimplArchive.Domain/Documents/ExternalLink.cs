using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A share of one document with someone who has NO account (ADR 0546, issue #385): a URL carrying an opaque token
// that serves the document's currently-active version, bounded by an expiry and an access count.
//
// The token IS the credential. Anyone holding the URL has the access — there is no principal behind it — which is
// why this entity carries its own bounds (expiry, access count, revocation) rather than leaning on the ACL that
// protects every other path to a document.
//
// IConcurrencyTracked because extending and revoking are real mutations that two administrators could race on;
// they carry If-Match like every other mutation in the API.
public class ExternalLink : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The shared document. Never a folder — a folder has no version to serve (enforced at creation).
    public Guid DocumentId { get; set; }

    // The public credential that appears in the URL, and the ONLY thing a recipient holds.
    //
    // 256 bits of cryptographic randomness rendered base64url — deliberately NOT a Guid. A v4 Guid's ~122 bits
    // would do, but the TYPE invites a later switch to Guid.CreateVersion7(), which is time-ordered and therefore
    // partly predictable from when the link was made. An opaque string removes that failure mode by construction
    // rather than by warning comment (ADR 0546).
    //
    // Never logged. The audit trail records this link's Id instead, which identifies the row without putting a
    // live credential into a log that gets exported and streamed to a SIEM.
    public required string Token { get; set; }

    // Hard stop, set at creation and capped by Tenant.ExternalLinkMaxDays. Extendable while the link is alive.
    public DateTimeOffset ExpiresAt { get; set; }

    // How many successful accesses the link allows; null = unlimited. Seeded from
    // Tenant.ExternalLinkDefaultAccesses when the creator doesn't choose.
    public int? MaxAccesses { get; set; }

    public int AccessCount { get; set; }

    // A PERSON shares a document; a service account never does (ADR 0546). This deliberately breaks the
    // creator-pair pattern every other entity follows: handing a document to someone outside the tenant is an
    // act of judgement about a recipient, and an automation has no one to answer for that judgement. Required,
    // so "a person did this" is a property of the schema rather than a convention the code upholds.
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Revocation is a STAMP, not a delete: the row is the evidence of what was shared and when, which is exactly
    // what an investigation needs after a link leaks. Revoked and expired links are hidden from the management
    // lists — a display concern, not a storage one (ADR 0546).
    public DateTimeOffset? RevokedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    // Whether this link may still serve, ignoring the tenant-level switch (which is checked separately at access
    // time so that turning it off is a genuine kill switch for links already in the wild).
    public bool IsLive(DateTimeOffset now) =>
        RevokedAt is null
        && ExpiresAt > now
        && (MaxAccesses is not { } max || AccessCount < max);
}
