// The CardDAV routes. Deliberately thin: every action states its route and forwards to the generic
// DavEndpoints with this protocol's descriptor, so the two protocols cannot drift (the standing rule — a
// type-specific action forwards to its generic). Ported in shape from SimplCalCon (ADR 0621).
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.CalDav.Http;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

public sealed class CardDavController : DavControllerBase
{
    private static DavProtocol Protocol => DavProtocol.CardDav;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _rights;
    private readonly IObjectStorageClient _storage;
    private readonly IServiceProvider _services;

    public CardDavController(
        SimplArchiveDbContext dbContext, IEffectiveRightsCalculator rights, IObjectStorageClient storage, IServiceProvider services)
    {
        _dbContext = dbContext;
        _rights = rights;
        _storage = storage;
        _services = services;
    }

    private DavControllerContext Context()
    {
        ApplyPrincipal(_services);
        return new DavControllerContext(
            Protocol, Request, _dbContext, _rights, _storage, CurrentUserId, CurrentTenantId,
            User.Identity?.Name ?? "SimplArchive", Depth(), HttpContext.RequestAborted);
    }

    // Discovery is answered WITHOUT credentials — a client probes it before it has any (RFC 6764).
    [AllowAnonymous]
    [HttpGet("~/.well-known/carddav")]
    [HttpPropfind("~/.well-known/carddav")]
    public IActionResult WellKnown()
    {
        Response.Headers.Location = Protocol.BasePath + "/";
        return StatusCode(StatusCodes.Status301MovedPermanently);
    }

    [HttpOptions("~/carddav")]
    [HttpOptions("~/carddav/{*rest}")]
    [AllowAnonymous]
    public IActionResult Options()
    {
        Response.Headers["DAV"] = $"1, 3, {Protocol.DavCompliance}";
        Response.Headers["Allow"] = "OPTIONS, PROPFIND, PROPPATCH, REPORT, GET, HEAD, PUT, DELETE";
        return Ok();
    }

    [HttpPropfind("~/carddav")]
    public Task<IActionResult> Root() => DavEndpoints.RootAsync(Context());

    [HttpPropfind("~/carddav/principals/{userId:guid}")]
    public Task<IActionResult> Principal(Guid userId) => DavEndpoints.PrincipalAsync(Context());

    [HttpPropfind("~/carddav/addressbooks")]
    public Task<IActionResult> Home() => DavEndpoints.HomeAsync(Context());

    [HttpPropfind("~/carddav/addressbooks/{folderId:guid}")]
    public Task<IActionResult> Collection(Guid folderId) => DavEndpoints.CollectionAsync(Context(), folderId);

    [HttpPropfind("~/carddav/addressbooks/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> Item(Guid folderId, string resourceName) =>
        DavEndpoints.ItemAsync(Context(), folderId, resourceName);

    [HttpProppatch("~/carddav/addressbooks/{folderId:guid}")]
    [HttpProppatch("~/carddav/addressbooks/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> PropPatch() => DavEndpoints.PropPatchAsync(Context(), Request.Path.Value ?? "/");

    [HttpReport("~/carddav/addressbooks/{folderId:guid}")]
    public Task<IActionResult> Report(Guid folderId) => DavEndpoints.ReportAsync(Context(), folderId);

    [HttpGet("~/carddav/addressbooks/{folderId:guid}/{resourceName}")]
    [HttpHead("~/carddav/addressbooks/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> Get(Guid folderId, string resourceName) =>
        DavEndpoints.GetAsync(Context(), folderId, resourceName, body: HttpMethods.IsGet(Request.Method));

    [HttpPut("~/carddav/addressbooks/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> Put(Guid folderId, string resourceName) =>
        DavEndpoints.PutAsync(Context(), _services, folderId, resourceName);

    [HttpDelete("~/carddav/addressbooks/{folderId:guid}/{resourceName}")]
    public Task<IActionResult> Delete(Guid folderId, string resourceName) =>
        DavEndpoints.DeleteAsync(Context(), _services, folderId, resourceName);
}
