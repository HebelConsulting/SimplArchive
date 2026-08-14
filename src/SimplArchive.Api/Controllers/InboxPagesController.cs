using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Inbox;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Inbox;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Page operations on staged inbox items (issue #487, ADR 0575): split one scan into its pages, join several
/// into one, and sort the pages of one without splitting it.
/// </summary>
/// <remarks>
/// <para>
/// Its own controller rather than more of <c>InboxController</c>, which was already at the 1000-line ceiling:
/// these four actions share a subject with the inbox but not a concern — they are about what is INSIDE a staged
/// file, where the rest of the inbox is about the file as a whole. Both are routed under <c>api/inbox</c>, so
/// the split is invisible to a client; it is the code that stops being one thing.
/// </para>
/// <para>
/// The controller stays an HTTP edge: authorize via <see cref="InboxScopeResolver"/>, delegate to
/// <see cref="InboxPageService"/>, shape the response. Nothing here knows what a PDF page is.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/inbox")]
[Authorize]
public class InboxPagesController(
    InboxScopeResolver scopes,
    IObjectStorageClient objectStorageClient,
    InboxPageService pageService,
    InboxIngestPipeline pipeline) : ControllerBase
{
    /// <summary>
    /// What the item's pages are, and which page operations it can actually take.
    /// </summary>
    /// <remarks>
    /// The rels are the answer to "what can I do here" (ADR 0554): a one-page PDF advertises no <c>split</c> and
    /// no <c>sort</c>, because both would be no-ops, and a file whose bytes cannot be read as its format
    /// advertises neither either. The client disables the affordance instead of offering a button that 400s.
    /// </remarks>
    [HttpGet("{name}/pages")]
    public async Task<IActionResult> Pages(
        string name,
        [FromQuery] Guid? group,
        [FromQuery] Guid? user,
        CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        if (!await objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken))
        {
            return NotFound();
        }

        var info = await pageService.DescribeAsync(scope.Prefix, name, cancellationToken);
        var links = new List<Link> { new("self", Href(name, "pages", group, user), "GET") };
        if (info.PageCount > 1 && !info.Signed)
        {
            links.Add(new Link("split", Href(name, "pages/split", group, user), "POST"));
            links.Add(new Link("sort", Href(name, "pages/order", group, user), "POST"));
        }

        // Straightening needs only a page, not several — and it is offered for a signed document never, for the
        // same reason as the rest: any rewrite voids the signature (#491).
        if (info.PageCount > 0 && !info.Signed)
        {
            links.Add(new Link("deskew", Href(name, "deskew", group, user), "POST"));
        }

        return Ok(new InboxPagesResource
        {
            Format = info.Format.ToString().ToLowerInvariant(),
            PageCount = info.PageCount,
            Signed = info.Signed,
            Links = links,
        });
    }

    [HttpHead("{name}/pages")]
    public async Task<IActionResult> PagesHead(
        string name,
        [FromQuery] Guid? group,
        [FromQuery] Guid? user,
        CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        return await objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken) ? NoContent() : NotFound();
    }

    /// <summary>One new inbox item per page. The source is kept.</summary>
    [HttpPost("{name}/pages/split")]
    public async Task<IActionResult> Split(
        string name,
        [FromQuery] Guid? group,
        [FromQuery] Guid? user,
        CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        if (!await objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken))
        {
            return NotFound();
        }

        var written = await pageService.SplitAsync(scope.Prefix, name, cancellationToken);

        return Ok(new InboxPageItemsResource
        {
            Names = written.ToList(),
            Links = [new Link("self", "/api/inbox", "GET")],
        });
    }

    /// <summary>
    /// The item's pages, rewritten in the given order (1-based, each page exactly once).
    /// </summary>
    /// <remarks>
    /// POST, not PUT, and the difference is not pedantry: a permutation applied twice does not land where it
    /// landed once, so this cannot be the idempotent replace that PUT promises. It is a transition, which is
    /// exactly the case CLAUDE.md keeps POST on an action sub-resource for.
    /// </remarks>
    [HttpPost("{name}/pages/order")]
    public async Task<IActionResult> Sort(
        string name,
        [FromQuery] Guid? group,
        [FromQuery] Guid? user,
        [FromBody] InboxPageOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        if (!await objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken))
        {
            return NotFound();
        }

        await pageService.ReorderAsync(scope.Prefix, name, request.PageOrder ?? [], cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// One new inbox item holding every page of the named items, in the order given. The sources are kept.
    /// </summary>
    /// <remarks>
    /// Named <c>from-items</c> to sit beside the inbox's existing <c>from-document</c>: both create an inbox
    /// item out of something that is already here, which is a create — not a verb-phrase route.
    /// </remarks>
    [HttpPost("from-items")]
    public async Task<IActionResult> Join(
        [FromQuery] Guid? group,
        [FromQuery] Guid? user,
        [FromBody] InboxJoinRequest request,
        CancellationToken cancellationToken)
    {
        var names = request.Names ?? [];

        // Every source is authorized in its own right, and they all have to resolve to the same inbox — joining
        // across inboxes would let a caller pull a file out of one place and into another as a side effect.
        InboxScopeResolver.InboxScope? scope = null;
        foreach (var name in names)
        {
            if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } itemScope
                || (scope is not null && itemScope.Prefix != scope.Prefix))
            {
                return Forbid();
            }

            if (!await objectStorageClient.ExistsAsync(itemScope.Prefix + name, cancellationToken))
            {
                return NotFound();
            }

            scope = itemScope;
        }

        if (scope is null)
        {
            return Forbid();
        }

        var joined = await pageService.JoinAsync(scope.Prefix, names, request.Name, cancellationToken);

        return Ok(new InboxPageItemsResource
        {
            Names = [joined],
            Links = [new Link("self", "/api/inbox", "GET")],
        });
    }

    /// <summary>
    /// Straightens the item on demand (#491) — the deliberate counterpart to the automatic path.
    /// </summary>
    /// <remarks>
    /// Unlike the automatic path this does NOT consult the user's preference or the "does it look like a scan"
    /// sniff: the user has said what they want about this document, and a guess has no business overriding
    /// them. The signature refusal still applies, because that one is a correctness rule rather than a
    /// convenience.
    /// </remarks>
    [HttpPost("{name}/deskew")]
    public async Task<IActionResult> Deskew(
        string name,
        [FromQuery] Guid? group,
        [FromQuery] Guid? user,
        CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        if (!await objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken))
        {
            return NotFound();
        }

        var straightened = await pageService.DeskewAsync(scope.Prefix, name, cancellationToken);

        return Ok(new InboxPageItemsResource
        {
            Names = [straightened ?? name],
            Links = [new Link("self", "/api/inbox", "GET")],
        });
    }

    /// <summary>
    /// Runs the ingest pipeline over a freshly uploaded item (#494) — straightening today, patch-code splitting
    /// later — and answers with the item's name afterwards, which differs when a processor changed the format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client calls this straight after its PUT to object storage, because the Api never sees the bytes and
    /// therefore gets no completion signal of its own. That makes this the FAST path, not the only one: the
    /// Worker's sweep is the backstop for items that arrived over WebDAV or from a browser tab that closed
    /// between the upload and this call.
    /// </para>
    /// <para>
    /// Idempotent by way of the marker: calling it twice processes once. So a client that retries after a
    /// timeout cannot convert an already-converted file, which matters when the first call succeeded and only
    /// its response was lost.
    /// </para>
    /// </remarks>
    [HttpPost("{name}/processed")]
    public async Task<IActionResult> Processed(
        string name,
        [FromQuery] Guid? group,
        [FromQuery] Guid? user,
        CancellationToken cancellationToken)
    {
        if (await scopes.ResolveAsync(group, user, name, cancellationToken) is not { } scope)
        {
            return Forbid();
        }

        if (!await objectStorageClient.ExistsAsync(scope.Prefix + name, cancellationToken))
        {
            return NotFound();
        }

        var processed = await pipeline.RunAsync(scope.TenantId, scope.UserId, scope.Prefix, name, cancellationToken);

        return Ok(new InboxPageItemsResource
        {
            Names = [processed ?? name],
            Links = [new Link("self", "/api/inbox", "GET")],
        });
    }

    // The `?group=`/`?user=` source query, so a link keeps acting on the prefix it was read from; own-inbox
    // items carry no query. Mirrors InboxController's own href shape.
    private static string Href(string name, string suffix, Guid? group, Guid? user)
    {
        var query = group is { } g ? $"?group={g}" : user is { } u ? $"?user={u}" : string.Empty;
        return $"/api/inbox/{Uri.EscapeDataString(name)}/{suffix}{query}";
    }

    public class InboxPagesResource : HypermediaResource
    {
        /// <summary>"pdf", "tiff", or "none" — the lowercase <see cref="PageComposer.PageFormat"/>.</summary>
        public string Format { get; set; } = string.Empty;

        public int PageCount { get; set; }

        /// <summary>True when the content carries a digital signature — which is why no operation is offered.</summary>
        public bool Signed { get; set; }
    }

    // Split and join both answer with "these inbox items now exist", so they share one shape rather than each
    // having a near-identical one.
    public class InboxPageItemsResource : HypermediaResource
    {
        public List<string> Names { get; set; } = [];
    }

    public class InboxPageOrderRequest
    {
        /// <summary>1-based page numbers, each page exactly once — the order the pages should end up in.</summary>
        public List<int>? PageOrder { get; set; }
    }

    public class InboxJoinRequest
    {
        /// <summary>The items to join, in the order their pages should appear.</summary>
        public List<string>? Names { get; set; }

        /// <summary>What to call the result. Optional — a name is derived from the first item when absent.</summary>
        public string? Name { get; set; }
    }
}
