// The one route that belongs to NEITHER protocol: a push subscription is identified by its own id, so
// unregistering it has nothing to say about calendars or address books. Declaring it on both protocol
// controllers would be an ambiguous route match at runtime (#564 slice 3, ADR 0622).
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

public sealed class DavPushController : DavControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _rights;
    private readonly IObjectStorageClient _storage;
    private readonly IServiceProvider _services;

    public DavPushController(
        SimplArchiveDbContext dbContext, IEffectiveRightsCalculator rights, IObjectStorageClient storage, IServiceProvider services)
    {
        _dbContext = dbContext;
        _rights = rights;
        _storage = storage;
        _services = services;
    }

    [HttpDelete("~/dav/push-subscriptions/{id:guid}")]
    public Task<IActionResult> Unregister(Guid id)
    {
        ApplyPrincipal(_services);
        var context = new DavControllerContext(
            DavProtocol.CalDav, Request, _dbContext, _rights, _storage, CurrentUserId, CurrentTenantId,
            User.Identity?.Name ?? "SimplArchive", 0, null, HttpContext.RequestAborted);
        return DavPushRegistration.UnregisterAsync(context, id);
    }
}
