// Everything the shared DAV endpoint logic needs from a request, gathered once so DavEndpoints stays free of
// controller plumbing and each route in the two protocol controllers is a single forwarding line.
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.CalDav.Http;
using SimplArchive.Api.CalDav.Xml;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

internal sealed class DavControllerContext
{
    private readonly HttpRequest _request;
    private readonly IObjectStorageClient _storage;

    internal DavControllerContext(
        DavProtocol protocol, HttpRequest request, SimplArchiveDbContext db, IEffectiveRightsCalculator rights,
        IObjectStorageClient storage, Guid userId, Guid tenantId, string displayName, int depth, string? vapidPublicKey, CancellationToken cancellation)
    {
        Protocol = protocol;
        Db = db;
        Rights = rights;
        UserId = userId;
        TenantId = tenantId;
        DisplayName = displayName;
        Depth = depth;
        VapidPublicKey = vapidPublicKey;
        Cancellation = cancellation;
        _request = request;
        _storage = storage;
    }

    internal DavProtocol Protocol { get; }

    internal SimplArchiveDbContext Db { get; }

    internal IEffectiveRightsCalculator Rights { get; }

    internal Guid UserId { get; }

    internal Guid TenantId { get; }

    internal string DisplayName { get; }

    internal int Depth { get; }

    /// <summary>Advertised when push is enabled; null keeps the capability off the wire.</summary>
    internal string? VapidPublicKey { get; }

    internal CancellationToken Cancellation { get; }

    internal Task<System.Xml.Linq.XElement?> ReadBodyAsync() => DavXml.ReadBodyAsync(_request, Cancellation);

    internal IActionResult MultiStatus(PropRequest request, IEnumerable<DavResource> resources) =>
        DavXml.MultiStatus(Xml.MultiStatus.Build(request, resources));

    /// <summary>The response headers a conditional client needs on a served item.</summary>
    internal void SetItemHeaders(DavItem item)
    {
        _request.HttpContext.Response.Headers.ETag = $"\"{item.ETag}\"";
        _request.HttpContext.Response.Headers.LastModified = item.LastModified.UtcDateTime.ToString("R");
    }

    /// <summary>The raw request, for the write path's body and conditional headers.</summary>
    internal HttpRequest Request => _request;

    /// <summary>The response, for the write path's ETag and status.</summary>
    internal HttpResponse Response => _request.HttpContext.Response;

    /// <summary>The stored blob, or null when it cannot be read — a gap in one item, not a failed sync.</summary>
    internal async Task<string?> ReadItemAsync(DavItem item)
    {
        try
        {
            await using var stream = await _storage.GetObjectAsync(item.ObjectKey, Cancellation);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(Cancellation);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
