using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Intray;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Intray;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Page operations on staged intray items (issue #487, ADR 0575): split one scan into its pages, join several
/// into one, and sort the pages of one without splitting it.
/// </summary>
/// <remarks>
/// <para>
/// Its own controller rather than more of <c>IntrayController</c>, which was already at the 1000-line ceiling:
/// these four actions share a subject with the intray but not a concern — they are about what is INSIDE a staged
/// file, where the rest of the intray is about the file as a whole. Both are routed under <c>api/intray</c>, so
/// the split is invisible to a client; it is the code that stops being one thing.
/// </para>
/// <para>
/// The controller stays an HTTP edge: authorize via <see cref="IntrayScopeResolver"/>, delegate to
/// <see cref="IntrayPageService"/>, shape the response. Nothing here knows what a PDF page is.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/intray")]
[Authorize]
public class IntrayPagesController(
    IntrayScopeResolver scopes,
    IObjectStorageClient objectStorageClient,
    IntrayPageService pageService,
    IntrayIngestPipeline pipeline) : ControllerBase
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
        }

        // Sort needs only a page, not several, since the same request also ROTATES (#522/#549) — a one-page
        // scan fed upside-down is exactly what the affordance exists for.
        if (info.PageCount > 0 && !info.Signed)
        {
            links.Add(new Link("sort", Href(name, "pages/order", group, user), "POST"));
        }

        // Straightening needs only a page, not several — and it is offered for a signed document never, for the
        // same reason as the rest: any rewrite voids the signature (#491).
        if (info.PageCount > 0 && !info.Signed)
        {
            links.Add(new Link("deskew", Href(name, "deskew", group, user), "POST"));
        }

        // Cutting at separator sheets needs at least two pages, since a one-page file is either the separator
        // or the document (#492). Whether it CONTAINS any is not knowable without rasterising every page, and
        // paying for that to decide whether to draw a button would cost a sidecar round trip per row.
        if (info.PageCount > 1 && !info.Signed)
        {
            links.Add(new Link("patchCodes", Href(name, "patch-codes", group, user), "POST"));
        }

        return Ok(new IntrayPagesResource
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

    /// <summary>One new intray item per page. The source is kept.</summary>
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

        return Ok(new IntrayPageItemsResource
        {
            Names = written.ToList(),
            Links = [new Link("self", "/api/intray", "GET")],
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
        [FromBody] IntrayPageOrderRequest request,
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

        var rotations = request.Rotations?.ToDictionary(r => r.Page, r => r.Degrees);
        await pageService.ReorderAsync(scope.Prefix, name, request.PageOrder ?? [], rotations, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// One new intray item holding every page of the named items, in the order given. The sources are kept.
    /// </summary>
    /// <remarks>
    /// Named <c>from-items</c> to sit beside the intray's existing <c>from-document</c>: both create an intray
    /// item out of something that is already here, which is a create — not a verb-phrase route.
    /// </remarks>
    [HttpPost("from-items")]
    public async Task<IActionResult> Join(
        [FromQuery] Guid? group,
        [FromQuery] Guid? user,
        [FromBody] IntrayJoinRequest request,
        CancellationToken cancellationToken)
    {
        var names = request.Names ?? [];

        // Every source is authorized in its own right, and they all have to resolve to the same intray — joining
        // across intrays would let a caller pull a file out of one place and into another as a side effect.
        IntrayScopeResolver.IntrayScope? scope = null;
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

        return Ok(new IntrayPageItemsResource
        {
            Names = [joined],
            Links = [new Link("self", "/api/intray", "GET")],
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

        return Ok(new IntrayPageItemsResource
        {
            Names = [straightened ?? name],
            Links = [new Link("self", "/api/intray", "GET")],
        });
    }

    /// <summary>The printable Patch 3 separator sheet — the piece of paper the whole feature is about (#492).</summary>
    /// <remarks>
    /// <para>
    /// Generated on request rather than served as a checked-in binary. The geometry lives in exactly one place
    /// (<see cref="PatchCodePage"/>), so the sheet a user prints cannot drift away from the one the detector is
    /// taught to find — which is the failure that would be hardest to diagnose, because both halves look right.
    /// </para>
    /// <para>
    /// <b>Anonymous, deliberately.</b> It is the same page for every tenant and carries nothing about anybody —
    /// the same status as the user manual, which is served as a static file. It also has to be: the web client
    /// reaches it with a plain link, so the browser can show the PDF and print it, and a plain link carries no
    /// bearer token. Fetching it into a blob would take the browser's own PDF viewer away and give nothing back.
    /// </para>
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("patch-code-sheet")]
    public IActionResult PatchCodeSheet() =>
        File(PatchCodePage.CreatePdf(), "application/pdf", "SimplArchive-Patch3-Separator.pdf");

    [HttpHead("patch-code-sheet")]
    public IActionResult PatchCodeSheetHead() => NoContent();

    /// <summary>
    /// A sample batch scan: three short documents with a separator sheet between each (#492).
    /// </summary>
    /// <remarks>
    /// So the feature can be tried without owning a scanner. One page is upside-down and one is crooked, which
    /// is what makes it a fixture rather than a demo — a batch of correctly-oriented pages exercises neither
    /// the orientation detection nor the straightening that runs before the cut (ADR 0576).
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("patch-code-sample")]
    public IActionResult PatchCodeSample() =>
        File(Intray.PatchCodeSampleBatch.CreatePdf(), "application/pdf", "SimplArchive-Patch3-Sample-Batch.pdf");

    [HttpHead("patch-code-sample")]
    public IActionResult PatchCodeSampleHead() => NoContent();

    /// <summary>
    /// The same batch as a SCAN — a bilevel multi-page TIFF, which is what a document scanner actually
    /// produces (#492).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because the PDF sample cannot demonstrate everything: <b>deskew declines a PDF</b>, since it
    /// cannot correct a sub-degree tilt without re-rendering the page and trading real text for an OCR
    /// approximation. So the PDF sample carries no crooked page — a sample must not show a feature that cannot
    /// act on it — and this one does.
    /// </para>
    /// <para>
    /// Checked in rather than composed per request: building it means rasterising PDFs, and the Api image has
    /// no rasteriser. Regenerate with <c>scripts/generate-scan-sample.sh</c>, which reproduces it byte for byte.
    /// </para>
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("patch-code-sample-scan")]
    public IActionResult PatchCodeSampleScan() =>
        File(Intray.PatchCodeSampleBatch.CreateTiff(), "image/tiff", "SimplArchive-Patch3-Sample-Scan.tif");

    [HttpHead("patch-code-sample-scan")]
    public IActionResult PatchCodeSampleScanHead() => NoContent();

    /// <summary>
    /// Cuts a batch scan into one item per document, at the Patch 3 separator sheets between them (#492).
    /// </summary>
    /// <remarks>
    /// The deliberate counterpart to the automatic path, and like straightening it ignores the user's standing
    /// preference — they have asked about this document. The source is kept under a
    /// <c>_to_be_deleted</c> name rather than removed, and the answer lists what came out of it, so a client
    /// can select the results without re-reading the whole intray.
    /// </remarks>
    [HttpPost("{name}/patch-codes")]
    public async Task<IActionResult> CutAtPatchCodes(
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

        var parts = await pageService.CutAtPatchCodesAsync(scope.Prefix, name, cancellationToken);

        return Ok(new IntrayPageItemsResource
        {
            // Null is "detection could not run" — no sidecar, or it failed. The item is untouched, and saying so
            // by naming it back is what stops a client redrawing a list that did not change.
            Names = parts is null ? [name] : [.. parts],
            Links = [new Link("self", "/api/intray", "GET")],
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

        return Ok(new IntrayPageItemsResource
        {
            // Usually the one item, under its own name or a new one when a processor changed the format; several
            // when a processor cut it up (#492); the name it came in under when nothing ran.
            Names = processed.Count > 0 ? [.. processed] : [name],
            Links = [new Link("self", "/api/intray", "GET")],
        });
    }

    // The `?group=`/`?user=` source query, so a link keeps acting on the prefix it was read from; own-intray
    // items carry no query. Mirrors IntrayController's own href shape.
    private static string Href(string name, string suffix, Guid? group, Guid? user)
    {
        var query = group is { } g ? $"?group={g}" : user is { } u ? $"?user={u}" : string.Empty;
        return $"/api/intray/{Uri.EscapeDataString(name)}/{suffix}{query}";
    }

    public class IntrayPagesResource : HypermediaResource
    {
        /// <summary>"pdf", "tiff", or "none" — the lowercase <see cref="PageComposer.PageFormat"/>.</summary>
        public string Format { get; set; } = string.Empty;

        public int PageCount { get; set; }

        /// <summary>True when the content carries a digital signature — which is why no operation is offered.</summary>
        public bool Signed { get; set; }
    }

    // Split and join both answer with "these intray items now exist", so they share one shape rather than each
    // having a near-identical one.
    public class IntrayPageItemsResource : HypermediaResource
    {
        public List<string> Names { get; set; } = [];
    }

    public class IntrayPageOrderRequest
    {
        /// <summary>1-based page numbers, each page exactly once — the order the pages should end up in.</summary>
        public List<int>? PageOrder { get; set; }

        /// <summary>Per-page rotations to apply while reordering (#522) — pages not listed keep their turn.</summary>
        public List<IntrayPageRotation>? Rotations { get; set; }
    }

    // A list of pairs rather than a dictionary: these DTOs round-trip through the XmlSerializer for the
    // vendor XML media type, and a Dictionary does not.
    public class IntrayPageRotation
    {
        /// <summary>The 1-based ORIGINAL page number, matching the entries of PageOrder.</summary>
        public int Page { get; set; }

        /// <summary>Clockwise degrees: 90, 180 or 270.</summary>
        public int Degrees { get; set; }
    }

    public class IntrayJoinRequest
    {
        /// <summary>The items to join, in the order their pages should appear.</summary>
        public List<string>? Names { get; set; }

        /// <summary>What to call the result. Optional — a name is derived from the first item when absent.</summary>
        public string? Name { get; set; }
    }
}
