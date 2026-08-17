using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.CalDav;

/// <summary>
/// A client's WebDAV-Push registration for one collection (#564 slice 3, ADR 0622): an RFC 8030 endpoint the
/// server posts an encrypted notification to when the collection changes. This is how DAVx⁵ learns of a change
/// without polling — its endpoint is typically an ntfy/UnifiedPush distributor.
/// </summary>
public class DavPushSubscription : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The typed folder subscribed to.</summary>
    public Guid FolderId { get; set; }

    /// <summary>Who registered it — a subscription is personal, and dies with the account.</summary>
    public Guid UserId { get; set; }

    /// <summary>The RFC 8030 push endpoint. Opaque to us, and never logged (it identifies a device).</summary>
    public required string Endpoint { get; set; }

    /// <summary>The client's P-256 public key (RFC 8291 encryption).</summary>
    public required string P256dh { get; set; }

    /// <summary>The client's auth secret (RFC 8291 encryption).</summary>
    public required string Auth { get; set; }

    /// <summary>When the client says the registration lapses; null means no stated expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
