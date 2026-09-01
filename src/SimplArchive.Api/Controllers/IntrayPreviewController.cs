using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Intray;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The intray item's derived-artifact views: inline preview, per-page images, and the text layout for the
/// hit-overlay — the read-only "look inside" endpoints on the same <c>api/intray</c> routes.
/// </summary>
/// <remarks>
/// A sibling of <see cref="IntrayController"/> on the same route prefix, split out as a #466 burn-down tranche —
/// the same recipe that took <c>DocumentsController</c> from 2,613 lines to five sibling controllers (ADR 0571).
/// Serving renditions is a responsibility, not a region: it has its own dependencies (the preview and
/// text-layout services), its own failure mode (204, "no preview available"), and no write path. The route
/// space stays the API's own; only the class housing these actions changed, so every advertised href is intact.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/intray")]
public class IntrayPreviewController(
    IObjectStorageClient objectStorageClient,
    IDocumentPreviewService documentPreviewService,
    IDocumentTextLayoutService textLayoutService,
    IntrayScopeResolver scopes) : ControllerBase
{
    // Inline preview for the item, via the rendition service on the intray object key (renditions for TIFF/
    // office/email, else the object shown as-is). 204 when no preview is available.
    [HttpGet("{name}/preview")]
    public async Task<IActionResult> Preview(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var key = scope.Prefix + name;
        if (!await objectStorageClient.ExistsAsync(key, cancellationToken))
        {
            return NotFound();
        }

        var preview = await documentPreviewService.GetPreviewUrlAsync(key, IntrayController.PresignedUrlExpiry, name, cancellationToken);
        if (preview is null)
        {
            return NoContent();
        }

        return Ok(new IntrayPreviewResource
        {
            PreviewUrl = preview.Url.ToString(),
            PreviewConverted = preview.IsConverted,
            Links =
            [
                new Link("self", IntrayController.ItemHref(name, "preview", group, user), "GET"),
                new Link("preview-pages", IntrayController.ItemHref(name, "preview-pages", group, user), "GET"),
                new Link("text-layout", IntrayController.ItemHref(name, "text-layout", group, user), "GET"),
            ],
        });
    }

    [HttpHead("{name}/preview")]
    public async Task<IActionResult> PreviewHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        return await objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken) ? NoContent() : NotFound();
    }

    // Ordered per-page image URLs for a multi-page TIFF; 204 for every other format (the client uses `preview`).
    [HttpGet("{name}/preview-pages")]
    public async Task<IActionResult> PreviewPages(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var key = scope.Prefix + name;
        if (!await objectStorageClient.ExistsAsync(key, cancellationToken))
        {
            return NotFound();
        }

        var pages = await documentPreviewService.GetPreviewPagesAsync(key, IntrayController.PresignedUrlExpiry, cancellationToken: cancellationToken);
        if (pages is null)
        {
            return NoContent();
        }

        return Ok(new IntrayPreviewPagesResource
        {
            Converted = pages.IsConverted,
            Pages = pages.Urls.Select(u => new IntrayPreviewPageResource { Url = u.ToString() }).ToList(),
            Links = [new Link("self", IntrayController.ItemHref(name, "preview-pages", group, user), "GET")],
        });
    }

    [HttpHead("{name}/preview-pages")]
    public async Task<IActionResult> PreviewPagesHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken) =>
        await PreviewHead(name, group, user, cancellationToken);

    // Per-page word boxes for hit-overlay / find-in-document, via the text-layout service on the object key.
    [HttpGet("{name}/text-layout")]
    public async Task<IActionResult> TextLayout(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        var key = scope.Prefix + name;
        if (!await objectStorageClient.ExistsAsync(key, cancellationToken))
        {
            return NotFound();
        }

        var layout = await textLayoutService.GetTextLayoutAsync(key, cancellationToken);
        if (layout is null)
        {
            return NoContent();
        }

        return Ok(new IntrayTextLayoutResource
        {
            Pages = layout.Pages
                .Select(p => new IntrayTextLayoutPageResource
                {
                    Words = p.Words
                        .Select(w => new IntrayTextLayoutWordResource { Text = w.Text, X = w.X, Y = w.Y, Width = w.Width, Height = w.Height })
                        .ToList(),
                })
                .ToList(),
            Links = [new Link("self", IntrayController.ItemHref(name, "text-layout", group, user), "GET")],
        });
    }

    [HttpHead("{name}/text-layout")]
    public async Task<IActionResult> TextLayoutHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken) =>
        await PreviewHead(name, group, user, cancellationToken);

    public class IntrayPreviewResource : HypermediaResource
    {
        public string? PreviewUrl { get; set; }

        public bool PreviewConverted { get; set; }
    }

    public class IntrayPreviewPagesResource : HypermediaResource
    {
        public bool Converted { get; set; }

        public List<IntrayPreviewPageResource> Pages { get; set; } = [];
    }

    public class IntrayPreviewPageResource
    {
        public string Url { get; set; } = string.Empty;
    }

    public class IntrayTextLayoutResource : HypermediaResource
    {
        public List<IntrayTextLayoutPageResource> Pages { get; set; } = [];
    }

    public class IntrayTextLayoutPageResource
    {
        public List<IntrayTextLayoutWordResource> Words { get; set; } = [];
    }

    public class IntrayTextLayoutWordResource
    {
        public string Text { get; set; } = string.Empty;

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }
}
