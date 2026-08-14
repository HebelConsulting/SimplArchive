using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Inbox;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The inbox item's derived-artifact views: inline preview, per-page images, and the text layout for the
/// hit-overlay — the read-only "look inside" endpoints on the same <c>api/inbox</c> routes.
/// </summary>
/// <remarks>
/// A sibling of <see cref="InboxController"/> on the same route prefix, split out as a #466 burn-down tranche —
/// the same recipe that took <c>DocumentsController</c> from 2,613 lines to five sibling controllers (ADR 0571).
/// Serving renditions is a responsibility, not a region: it has its own dependencies (the preview and
/// text-layout services), its own failure mode (204, "no preview available"), and no write path. The route
/// space stays the API's own; only the class housing these actions changed, so every advertised href is intact.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/inbox")]
public class InboxPreviewController(
    IObjectStorageClient objectStorageClient,
    IDocumentPreviewService documentPreviewService,
    IDocumentTextLayoutService textLayoutService,
    InboxScopeResolver scopes) : ControllerBase
{
    // Inline preview for the item, via the rendition service on the inbox object key (renditions for TIFF/
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

        var preview = await documentPreviewService.GetPreviewUrlAsync(key, InboxController.PresignedUrlExpiry, name, cancellationToken);
        if (preview is null)
        {
            return NoContent();
        }

        return Ok(new InboxPreviewResource
        {
            PreviewUrl = preview.Url.ToString(),
            PreviewConverted = preview.IsConverted,
            Links =
            [
                new Link("self", InboxController.ItemHref(name, "preview", group, user), "GET"),
                new Link("preview-pages", InboxController.ItemHref(name, "preview-pages", group, user), "GET"),
                new Link("text-layout", InboxController.ItemHref(name, "text-layout", group, user), "GET"),
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

        var pages = await documentPreviewService.GetPreviewPagesAsync(key, InboxController.PresignedUrlExpiry, cancellationToken: cancellationToken);
        if (pages is null)
        {
            return NoContent();
        }

        return Ok(new InboxPreviewPagesResource
        {
            Converted = pages.IsConverted,
            Pages = pages.Urls.Select(u => new InboxPreviewPageResource { Url = u.ToString() }).ToList(),
            Links = [new Link("self", InboxController.ItemHref(name, "preview-pages", group, user), "GET")],
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

        return Ok(new InboxTextLayoutResource
        {
            Pages = layout.Pages
                .Select(p => new InboxTextLayoutPageResource
                {
                    Words = p.Words
                        .Select(w => new InboxTextLayoutWordResource { Text = w.Text, X = w.X, Y = w.Y, Width = w.Width, Height = w.Height })
                        .ToList(),
                })
                .ToList(),
            Links = [new Link("self", InboxController.ItemHref(name, "text-layout", group, user), "GET")],
        });
    }

    [HttpHead("{name}/text-layout")]
    public async Task<IActionResult> TextLayoutHead(string name, [FromQuery] Guid? group, [FromQuery] Guid? user, CancellationToken cancellationToken) =>
        await PreviewHead(name, group, user, cancellationToken);

    public class InboxPreviewResource : HypermediaResource
    {
        public string? PreviewUrl { get; set; }

        public bool PreviewConverted { get; set; }
    }

    public class InboxPreviewPagesResource : HypermediaResource
    {
        public bool Converted { get; set; }

        public List<InboxPreviewPageResource> Pages { get; set; } = [];
    }

    public class InboxPreviewPageResource
    {
        public string Url { get; set; } = "";
    }

    public class InboxTextLayoutResource : HypermediaResource
    {
        public List<InboxTextLayoutPageResource> Pages { get; set; } = [];
    }

    public class InboxTextLayoutPageResource
    {
        public List<InboxTextLayoutWordResource> Words { get; set; } = [];
    }

    public class InboxTextLayoutWordResource
    {
        public string Text { get; set; } = "";

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }
}
