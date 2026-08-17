// The CalDAV routes. Deliberately thin: every action states its route and forwards to the generic
// DavEndpoints with this protocol's descriptor, so the two protocols cannot drift (the standing rule — a
// type-specific action forwards to its generic). Ported in shape from SimplCalCon (ADR 0621).
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.CalDav.Http;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

public sealed class CalDavController : DavControllerBase
{
    private static DavProtocol Protocol => DavProtocol.CalDav;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _rights;
    private readonly IObjectStorageClient _storage;
    private readonly IServiceProvider _services;
    private readonly DavPushConfiguration _push;

    public CalDavController(
        SimplArchiveDbContext dbContext, IEffectiveRightsCalculator rights, IObjectStorageClient storage, IServiceProvider services, DavPushConfiguration push)
    {
        _dbContext = dbContext;
        _rights = rights;
        _storage = storage;
        _services = services;
        _push = push;
    }

    private DavControllerContext Context()
    {
        ApplyPrincipal(_services);
        return new DavControllerContext(
            Protocol, Request, _dbContext, _rights, _storage, CurrentUserId, CurrentTenantId,
            User.Identity?.Name ?? "SimplArchive", Depth(), _push.VapidPublicKey, HttpContext.RequestAborted);
    }

    // Discovery is answered WITHOUT credentials — a client probes it before it has any (RFC 6764).
    [AllowAnonymous]
    [HttpGet("~/.well-known/caldav")]
    [HttpPropfind("~/.well-known/caldav")]
    public IActionResult WellKnown()
    {
        Response.Headers.Location = Protocol.BasePath + "/";
        return StatusCode(StatusCodes.Status301MovedPermanently);
    }

    [HttpOptions("~/caldav")]
    [HttpOptions("~/caldav/{*rest}")]
    [AllowAnonymous]
    public IActionResult Options()
    {
        Response.Headers["DAV"] = $"1, 3, {Protocol.DavCompliance}";
        Response.Headers["Allow"] = "OPTIONS, PROPFIND, PROPPATCH, REPORT, GET, HEAD, PUT, DELETE";
        return Ok();
    }

    [HttpPropfind("~/caldav")]
    public Task<IActionResult> Root() => DavEndpoints.RootAsync(Context());

    [HttpPropfind("~/caldav/principals/{userId:guid}")]
    public Task<IActionResult> Principal(Guid userId) => DavEndpoints.PrincipalAsync(Context());

    [HttpPropfind("~/caldav/calendars")]
    public Task<IActionResult> Home() => DavEndpoints.HomeAsync(Context());

    [HttpPropfind("~/caldav/calendars/{folderId:guid}")]
    public Task<IActionResult> Collection(Guid folderId) => DavEndpoints.CollectionAsync(Context(), folderId);

    [HttpPropfind("~/caldav/calendars/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> Item(Guid folderId, string resourceName) =>
        DavEndpoints.ItemAsync(Context(), folderId, resourceName);

    [HttpProppatch("~/caldav/calendars/{folderId:guid}")]
    [HttpProppatch("~/caldav/calendars/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> PropPatch() => DavEndpoints.PropPatchAsync(Context(), Request.Path.Value ?? "/");

    [HttpReport("~/caldav/calendars/{folderId:guid}")]
    public Task<IActionResult> Report(Guid folderId) => DavEndpoints.ReportAsync(Context(), folderId);

    [HttpGet("~/caldav/calendars/{folderId:guid}/{resourceName}")]
    [HttpHead("~/caldav/calendars/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> Get(Guid folderId, string resourceName) =>
        DavEndpoints.GetAsync(Context(), folderId, resourceName, body: HttpMethods.IsGet(Request.Method));

    [HttpPut("~/caldav/calendars/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> Put(Guid folderId, string resourceName) =>
        DavEndpoints.PutAsync(Context(), _services, folderId, resourceName);

    // WebDAV-Push: a client POSTs push-register to the COLLECTION, and deletes the returned URL to stop.
    [HttpPost("~/caldav/calendars/{folderId:guid}")]
    public Task<IActionResult> RegisterPush(Guid folderId) =>
        DavPushRegistration.RegisterAsync(Context(), _push, folderId);

    [HttpDelete("~/caldav/calendars/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> Delete(Guid folderId, string resourceName) =>
        DavEndpoints.DeleteAsync(Context(), _services, folderId, resourceName);
}
