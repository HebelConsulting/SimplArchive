using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.CalDav;

/// <summary>
/// The doorbell's Api half (#806): the DbContext's recorder announces committed non-DAV changes through the
/// <see cref="IDavChangeNotifier"/> abstraction, and this adapter rings the same WebDAV-Push bell the DAV
/// write path rings — one subscriber list, one message shape, whichever door the write came through.
/// </summary>
/// <remarks>
/// Scoped like <see cref="DavPushNotifier"/> itself. A DbContext cannot depend on the notifier concretely —
/// it lives two layers down — and it must also survive the CYCLE: the notifier uses a DbContext to read
/// subscriptions. That is why the abstraction is optional at the context and resolved lazily here via the
/// provider rather than constructor-injected into this adapter, which would recurse at scope build.
/// </remarks>
public sealed class DavChangeNotifierAdapter : IDavChangeNotifier
{
    private readonly IServiceProvider _services;

    public DavChangeNotifierAdapter(IServiceProvider services) => _services = services;

    public async Task NotifyAsync(Guid folderId, long sequence, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DavPushNotifier>().NotifyAsync(folderId, sequence, cancellationToken);
    }
}
