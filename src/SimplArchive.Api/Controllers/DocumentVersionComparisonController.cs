using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Comparing two of a document's versions — a sibling controller on <c>DocumentVersionsController</c>'s own
/// routes, holding the one door whose answer is the documents' PLAIN TEXT.
/// </summary>
/// <remarks>
/// <para>
/// Split out under ADR 0861, and the split is the point rather than housekeeping. Comparison is the only
/// version door that answers with content rather than about it, so it is the only one that has to refuse on a
/// tenant whose doors refuse plaintext — and while it sat among twenty siblings that legitimately serve, that
/// distinction was invisible. It shipped ungated for months, sixty lines from the gate its nearest relative
/// received.
/// </para>
/// <para>
/// The 1000-line rule is what forced the timing: the versions controller stood at 989 with ten lines of
/// headroom, so the gate did not fit, and CLAUDE.md is explicit that the exception is the owner's to grant
/// rather than the author's to assume. Owner-approved, 2026-09-30: split rather than raise a ceiling.
/// </para>
/// <para>
/// Same route prefix, so every address and rel is unchanged (ADR 0571's recipe — five sibling controllers
/// share <c>DocumentsController</c>'s routes). The <c>compare</c> rel is still advertised by the version
/// LISTING, which is the collection that owns it, and withheld there on the same predicate this refuses on.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/versions")]
[Authorize]
public class DocumentVersionComparisonController : ControllerBase
{
    private readonly IDocumentVersionComparer _comparer;
    private readonly DocumentVersionAccess _versions;
    private readonly StrictEnvelopeDelivery _strictEnvelopes;

    public DocumentVersionComparisonController(
        IDocumentVersionComparer comparer,
        DocumentVersionAccess versions,
        StrictEnvelopeDelivery strictEnvelopes)
    {
        _comparer = comparer;
        _versions = versions;
        _strictEnvelopes = strictEnvelopes;
    }

    // The two versions' extracted texts, for the client-side side-by-side diff (ADR 0712 — the server used
    // to diff here itself; now both clients compute identical rows from these texts via the shared
    // SimplArchive.Presentation.TextDiff, so word-level emphasis needs no wire shape of its own).
    // Requires CanReadContent on both (via CanAccessVersionContentAsync, which also enforces workflow gating).
    // Available is false when either version has no extractable text (a binary/image format, or Tika unavailable
    // for office/PDF) — the client then shows "comparison not available for this format".
    // The PAIR is expressed as a query, not as path segments (issue #416). A link names ONE resource, so
    // "/versions/{from}/compare/{to}" could never be advertised — the client had to build it, which is exactly
    // what ADR 0543 removes. As "/versions/compare?from=&to=" the collection advertises a single `compare`
    // address and the client supplies its two operands as parameters, the same shape as any other filter.
    [HttpGet("compare")]
    public async Task<IActionResult> Compare(Guid documentId, [FromQuery] Guid from, [FromQuery] Guid to, CancellationToken cancellationToken)
    {
        var fromVersionId = from;
        var toVersionId = to;
        var fromVersion = await _versions.LoadForReadAsync(documentId, fromVersionId, cancellationToken);
        var toVersion = await _versions.LoadForReadAsync(documentId, toVersionId, cancellationToken);
        if (fromVersion is null || toVersion is null)
        {
            return NotFound();
        }

        if (!await _versions.CanAccessVersionContentAsync(fromVersion.Id, documentId, cancellationToken)
            || !await _versions.CanAccessVersionContentAsync(toVersion.Id, documentId, cancellationToken))
        {
            return Forbid();
        }

        // THE COMPARISON IS THE DOCUMENT, twice: both versions' full plain text for a client-side diff
        // (ADR 0712) — a wider disclosure than the overlay ADR 0857 refused, on the same predicate, placed
        // after the access checks so an unauthorised caller learns nothing (#1485, ADR 0861).
        await _strictEnvelopes.RefuseIfStrictAsync("comparing two versions", cancellationToken);

        var comparison = await _comparer.CompareAsync(fromVersion.ObjectKey, toVersion.ObjectKey, cancellationToken: cancellationToken);

        return Ok(new VersionComparisonResource
        {
            FromVersionId = fromVersion.Id,
            FromVersionNumber = fromVersion.VersionNumber,
            ToVersionId = toVersion.Id,
            ToVersionNumber = toVersion.VersionNumber,
            Available = comparison.Available,
            FromText = comparison.FromText,
            ToText = comparison.ToText,
            Links = [new Link("self", $"/api/documents/{documentId}/versions/compare?from={fromVersionId}&to={toVersionId}", "GET")],
        });
    }

    [HttpHead("compare")]
    public async Task<IActionResult> CompareHead(Guid documentId, [FromQuery] Guid from, [FromQuery] Guid to, CancellationToken cancellationToken)
    {
        var fromVersion = await _versions.LoadForReadAsync(documentId, from, cancellationToken);
        var toVersion = await _versions.LoadForReadAsync(documentId, to, cancellationToken);
        if (fromVersion is null || toVersion is null)
        {
            return NotFound();
        }

        if (!await _versions.CanAccessVersionContentAsync(fromVersion.Id, documentId, cancellationToken)
            || !await _versions.CanAccessVersionContentAsync(toVersion.Id, documentId, cancellationToken))
        {
            return Forbid();
        }

        // Refuses where the GET refuses: a HEAD answering 204 to a door that answers 409 would tell a client
        // the comparison is there to be had (ADR 0861).
        await _strictEnvelopes.RefuseIfStrictAsync("comparing two versions", cancellationToken);

        return NoContent();
    }

    // Inline unified diff between two versions (ADR "Document version comparison"). Available == false → neither
    // side had extractable text (a binary/image format, or Tika unavailable).
    //
    // Nested HERE now rather than on the versions controller: it is this action's wire shape and nothing else
    // referenced it. The JSON is unchanged, which is what matters to a client (ADR 0543 — the rel and the
    // shape are the compatibility surface, not the C# type's home).
    public class VersionComparisonResource : HypermediaResource
    {
        public Guid FromVersionId { get; set; }
        public int? FromVersionNumber { get; set; }
        public Guid ToVersionId { get; set; }
        public int? ToVersionNumber { get; set; }
        public bool Available { get; set; }
        public string FromText { get; set; } = string.Empty;
        public string ToText { get; set; } = string.Empty;
    }
}
