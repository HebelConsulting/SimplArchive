using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Checkouts;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Page operations on a check-out's WORKING COPY (ADR 0593) — the Check-out tab's Rotate/Sort. Sibling of
/// <see cref="CheckoutsController"/> the way <c>IntrayPagesController</c> sits beside the intray (ADR 0575):
/// same holder-only rule (via <see cref="HeldCheckout"/>), its own resource. The subject is the working copy —
/// the stash, falling back to the archived current version when none exists yet — and the result always lands
/// in the stash; the archive changes only through a normal check-in.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/checkouts")]
[Authorize]
public class CheckoutPagesController(
    SimplArchiveDbContext dbContext,
    ICurrentUserAccessor currentUserAccessor,
    ICurrentTenantAccessor currentTenantAccessor,
    IObjectStorageClient objectStorage,
    CheckoutPageService pageService) : ControllerBase
{
    public class CheckoutPagesResource : HypermediaResource
    {
        public string Format { get; set; } = "none";
        public int PageCount { get; set; }
        public bool Signed { get; set; }
    }

    public class CheckoutPageOrderRequest
    {
        /// <summary>1-based ORIGINAL page numbers; the order given is the result order, omission deletes.</summary>
        public List<int>? PageOrder { get; set; }

        public List<CheckoutPageRotation>? Rotations { get; set; }
    }

    // A list of pairs, not a dictionary — these DTOs round-trip through XmlSerializer for the vendor XML
    // media type, and a dictionary does not (the IntrayPageRotation precedent).
    public class CheckoutPageRotation
    {
        public int Page { get; set; }
        public int Degrees { get; set; }
    }

    [HttpGet("{documentId:guid}/working-copy/pages")]
    public async Task<IActionResult> Pages(Guid documentId, CancellationToken cancellationToken)
    {
        var held = await ResolveAsync(documentId, cancellationToken);
        if (held.Refusal is { } refusal)
        {
            return refusal;
        }

        var (sourceKey, _, extension, _) = held;
        var info = await pageService.DescribeAsync(sourceKey, extension, cancellationToken);

        var links = new List<Link> { new("self", Href(documentId, "pages"), "GET") };

        // Like the intray (#549): sort needs only a page, since the same request also rotates — and never a
        // signed working copy, because any rewrite voids the signature.
        if (info.PageCount > 0 && !info.Signed)
        {
            links.Add(new Link("sort", Href(documentId, "pages/order"), "POST"));
        }

        return Ok(new CheckoutPagesResource
        {
            Format = info.Format.ToString().ToLowerInvariant(),
            PageCount = info.PageCount,
            Signed = info.Signed,
            Links = links,
        });
    }

    [HttpHead("{documentId:guid}/working-copy/pages")]
    public async Task<IActionResult> PagesHead(Guid documentId, CancellationToken cancellationToken)
    {
        var held = await ResolveAsync(documentId, cancellationToken);
        return held.Refusal ?? NoContent();
    }

    /// <summary>One whole-file rewrite of the working copy: reorder + delete (omission) + rotate.</summary>
    [HttpPost("{documentId:guid}/working-copy/pages/order")]
    public async Task<IActionResult> Sort(
        Guid documentId,
        [FromBody] CheckoutPageOrderRequest request,
        CancellationToken cancellationToken)
    {
        var held = await ResolveAsync(documentId, cancellationToken);
        if (held.Refusal is { } refusal)
        {
            return refusal;
        }

        var (sourceKey, stashKey, extension, _) = held;
        var rotations = request.Rotations?.ToDictionary(r => r.Page, r => r.Degrees);
        await pageService.ReorderAsync(sourceKey, stashKey, extension, request.PageOrder ?? [], rotations, cancellationToken);
        return NoContent();
    }

    // The working copy this caller holds: source = the stash when one exists, else the archived current
    // version (the login-reconcile fallback, ADR 0332); the stash key is where any rewrite lands. A check-out
    // whose document has no version yet has no working copy, so it reads as not found.
    private async Task<(string SourceKey, string StashKey, string Extension, IActionResult? Refusal)> ResolveAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        var held = await HeldCheckout.ResolveAsync(
            dbContext, currentUserAccessor.UserId, currentTenantAccessor.TenantId, documentId, cancellationToken);
        switch (held.Refusal)
        {
            case HeldCheckout.Refusal.Forbidden:
                return (string.Empty, string.Empty, string.Empty, Forbid());
            case HeldCheckout.Refusal.NotFound:
                return (string.Empty, string.Empty, string.Empty, NotFound());
        }

        if (held.Version is null)
        {
            return (string.Empty, string.Empty, string.Empty, NotFound());
        }

        var source = await objectStorage.ExistsAsync(held.StashKey, cancellationToken) ? held.StashKey : held.Version.ObjectKey;
        return (source, held.StashKey, Path.GetExtension(held.Version.ObjectKey), null);
    }

    private static string Href(Guid documentId, string suffix) => $"/api/checkouts/{documentId}/working-copy/{suffix}";
}
