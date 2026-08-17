namespace SimplArchive.Api.CalDav;

// Binds from the "DavPush" section (#564 slice 3, ADR 0622). Push is available only when a VAPID key pair
// exists: the keys identify THIS server to the push service (RFC 8292), so without them a notification cannot
// be sent and the capability must not be advertised.
public class DavPushOptions
{
    public const string SectionName = "DavPush";

    /// <summary>VAPID public key (base64url, uncompressed P-256) — advertised to clients.</summary>
    public string? VapidPublicKey { get; set; }

    /// <summary>VAPID private key. Sourced from OpenBao where configured, like the OpenIddict certs (ADR 0339).</summary>
    public string? VapidPrivateKey { get; set; }

    /// <summary>The `mailto:`/`https:` subject VAPID requires, so a push service can contact the operator.</summary>
    public string Subject { get; set; } = "mailto:admin@simplarchive.local";

    /// <summary>Days a subscription lives before a client must re-register; a client may ask for less.</summary>
    public int SubscriptionTtlDays { get; set; } = 30;
}
