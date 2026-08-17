// Whether WebDAV-Push is available, and the VAPID public key to advertise (#564 slice 3, ADR 0622).
// Keys come from configuration — which in a real deployment means OpenBao (ADR 0339's sourcing) — and are
// GENERATED IN MEMORY when absent in Development, so local dev exercises the push path with nothing to set up.
// A generated pair lives only as long as the process: clients re-register after a restart, which is exactly
// what a rotated key means and is why this is confined to Development.
using Microsoft.Extensions.Options;
using WebPush;

namespace SimplArchive.Api.CalDav;

public sealed class DavPushConfiguration
{
    private readonly ILogger<DavPushConfiguration> _logger;

    public DavPushConfiguration(IOptions<DavPushOptions> options, IHostEnvironment environment, ILogger<DavPushConfiguration> logger)
    {
        _logger = logger;
        var configured = options.Value;
        Subject = configured.Subject;
        SubscriptionTtlDays = configured.SubscriptionTtlDays;

        if (configured.VapidPublicKey is { Length: > 0 } publicKey && configured.VapidPrivateKey is { Length: > 0 } privateKey)
        {
            (VapidPublicKey, VapidPrivateKey) = (publicKey, privateKey);
            return;
        }

        if (!environment.IsDevelopment())
        {
            // Silence is the right behaviour, not an error: a deployment that has not configured push simply
            // does not advertise it, and clients poll instead.
            _logger.LogInformation("WebDAV-Push is disabled — no VAPID key pair configured");
            return;
        }

        var generated = VapidHelper.GenerateVapidKeys();
        (VapidPublicKey, VapidPrivateKey) = (generated.PublicKey, generated.PrivateKey);
        _logger.LogWarning(
            "WebDAV-Push is using an EPHEMERAL VAPID key pair (Development only) — clients must re-register after a restart");
    }

    /// <summary>True when a key pair exists; gates both advertisement and registration.</summary>
    public bool IsEnabled => VapidPublicKey is { Length: > 0 } && VapidPrivateKey is { Length: > 0 };

    public string? VapidPublicKey { get; }

    public string? VapidPrivateKey { get; }

    public string Subject { get; }

    public int SubscriptionTtlDays { get; }
}
