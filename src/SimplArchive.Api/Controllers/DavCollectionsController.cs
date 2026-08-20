using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The caller's addressbooks and calendars, as a REST listing (#564, the Calendar/Contacts tabs).
/// </summary>
/// <remarks>
/// The CalDAV/CardDAV home set answers the same question, but it is a DAV surface for EXTERNAL clients — our
/// own clients navigate by rel and speak JSON, and asking them to parse a multistatus to draw a tab would be
/// absurd. So this is the same set of typed folders, shaped for the app: flat (the tabs present a pick-list,
/// ADR 0619), parent-qualified, with the caller's effective colour already resolved.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/dav-collections")]
[Authorize]
public class DavCollectionsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _rights;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public DavCollectionsController(
        SimplArchiveDbContext dbContext, IEffectiveRightsCalculator rights, ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _rights = rights;
        _currentUserAccessor = currentUserAccessor;
    }

    public class DavCollectionResource : HypermediaResource
    {
        public Guid Id { get; set; }

        /// <summary>Parent-qualified, so two same-named collections are tellable apart (ADR 0619).</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>The folder's own name, for when the pane shows the hierarchy itself.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary><c>addressbook</c> or <c>calendar</c>.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The caller's colour: their override if set, else the collection's own (ADR 0620).</summary>
        public string? Color { get; set; }

        /// <summary>True when the caller may add or change items — the tabs disable their editors otherwise.</summary>
        public bool Writable { get; set; }

        /// <summary>
        /// Whether this caller may add an entry here — the flag New is gated on.
        /// </summary>
        /// <remarks>
        /// A capability rather than a rel, because the typed rel (<c>contacts</c>/<c>appointments</c>) serves
        /// both the listing and the create, and one rel cannot say "read yes, write no". It reports
        /// <c>CanCreateSubItems</c>, the right the POST actually checks — not <c>Writable</c>, which is
        /// <c>CanEditContent</c> and answers a different question.
        /// </remarks>
        public bool CanCreateEntries { get; set; }

        /// <summary>The caller's own personal default, which the tabs list first.</summary>
        public bool IsPersonalDefault { get; set; }
    }

    public class DavCollectionListResource : HypermediaResource
    {
        public List<DavCollectionResource> Collections { get; set; } = [];
    }

    [HttpGet]
    [HttpHead]
    public async Task<IActionResult> List([FromQuery] string? kind, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var wanted = kind?.ToLowerInvariant() switch
        {
            "addressbook" => new[] { WellKnownMaskIds.Addressbook },
            "calendar" => [WellKnownMaskIds.Calendar],
            _ => [WellKnownMaskIds.Addressbook, WellKnownMaskIds.Calendar],
        };

        var maskVersions = await _dbContext.MaskVersions
            .Where(v => wanted.Contains(v.MaskId))
            .Select(v => new { v.Id, v.MaskId })
            .ToListAsync(cancellationToken);
        var maskVersionIds = maskVersions.Select(v => v.Id).ToList();

        var candidates = await _dbContext.Documents
            .Where(d => d.MaskVersionId != null && maskVersionIds.Contains(d.MaskVersionId.Value))
            .Select(d => new { d.Id, d.Name, d.ParentId, d.MaskVersionId })
            .ToListAsync(cancellationToken);

        var parentIds = candidates.Where(c => c.ParentId is not null).Select(c => c.ParentId!.Value).Distinct().ToList();
        var parents = await _dbContext.Documents
            .Where(d => parentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name, d.PersonalOfUserId })
            .ToDictionaryAsync(p => p.Id, p => p, cancellationToken);

        var colourFieldIds = await _dbContext.FieldDefinitions
            .Where(f => f.Name == "Colour" && maskVersionIds.Contains(f.MaskVersionId))
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var defaults = await _dbContext.FieldValues
            .Where(fv => candidateIds.Contains(fv.DocumentId) && colourFieldIds.Contains(fv.FieldDefinitionId))
            .Select(fv => new { fv.DocumentId, fv.Value })
            .ToDictionaryAsync(fv => fv.DocumentId, fv => fv.Value, cancellationToken);
        var overrides = await _dbContext.DavCollectionColors
            .Where(c => c.UserId == userId && candidateIds.Contains(c.DocumentId))
            .Select(c => new { c.DocumentId, c.Color })
            .ToDictionaryAsync(c => c.DocumentId, c => c.Color, cancellationToken);

        var kindByMaskVersion = maskVersions.ToDictionary(
            v => v.Id, v => v.MaskId == WellKnownMaskIds.Addressbook ? "addressbook" : "calendar");

        var resources = new List<(DavCollectionResource Resource, bool Personal)>();
        foreach (var candidate in candidates)
        {
            var effective = await _rights.GetEffectiveRightsAsync(userId, candidate.Id);
            if (!effective.CanSee)
            {
                continue;
            }

            var parent = candidate.ParentId is { } pid ? parents.GetValueOrDefault(pid) : null;
            var personal = parent?.PersonalOfUserId == userId;
            var folderMaskId = maskVersions.FirstOrDefault(v => v.Id == candidate.MaskVersionId)?.MaskId;

            // The collection's OWN create (#631) — the address New Contact / New Appointment acts from.
            //
            // This listing is where both tabs get their collections, so a rel absent here is absent from the one
            // screen the feature exists for, no matter how many other surfaces carry it. It shipped on the
            // document resource and on the children listing and not here, which is the third time that shape has
            // bitten (`folders`, #638) — hence the shared predicate rather than a fourth copy of the rule.
            //
            // ONE rel per typed collection, serving both methods: GET lists the entries, POST adds one. It is
            // therefore advertised to anyone who can SEE the collection — the listing is what fills the tab, and
            // withholding it from a reader would leave them a calendar with no appointments in it.
            //
            // The two methods need different rights, though, and a rel cannot say "read yes, write no". So the
            // right to create rides as an explicit capability instead: `CanCreateEntries`, from
            // CanCreateSubItems, which is what the POST itself requires — deliberately NOT `Writable` below,
            // which reports CanEditContent. Gating New on the wrong one either hides a create that would succeed
            // or offers one that is refused, and the second is what an unqualified rel would now do.
            var typedItems = new List<Link>();
            var contacts = ChildCreationPolicy.AdmitsTypedItem(folderMaskId, WellKnownMaskIds.Contact);
            var appointments = ChildCreationPolicy.AdmitsTypedItem(folderMaskId, WellKnownMaskIds.Appointment);
            if (contacts)
            {
                typedItems.Add(new Link("contacts", $"/api/documents/{candidate.Id}/contacts", "GET"));
            }

            if (appointments)
            {
                typedItems.Add(new Link("appointments", $"/api/documents/{candidate.Id}/appointments", "GET"));
            }

            resources.Add((new DavCollectionResource
            {
                Id = candidate.Id,
                Name = candidate.Name,
                DisplayName = parent is null ? candidate.Name : $"{parent.Name} / {candidate.Name}",
                Kind = kindByMaskVersion.GetValueOrDefault(candidate.MaskVersionId!.Value, "calendar"),
                Color = overrides.GetValueOrDefault(candidate.Id) ?? defaults.GetValueOrDefault(candidate.Id),
                Writable = effective.CanEditContent,
                CanCreateEntries = effective.CanCreateSubItems && (contacts || appointments),
                IsPersonalDefault = personal,
                Links =
                [
                    new Link("self", $"/api/documents/{candidate.Id}", "GET"),
                    new Link("children", $"/api/documents/{candidate.Id}/children", "GET"),
                    // Everything a tab needs to act on the collection, so it never composes a URL (ADR 0543).
                    new Link("collection-color", $"/api/documents/{candidate.Id}/collection-color", "PUT"),
                    new Link("acl-entries", $"/api/documents/{candidate.Id}/acl-entries", "GET"),
                    .. typedItems,
                ],
            }, personal));
        }

        return Ok(new DavCollectionListResource
        {
            Collections = resources
                .OrderByDescending(r => r.Personal)
                .ThenBy(r => r.Resource.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(r => r.Resource)
                .ToList(),
            Links = [new Link("self", "/api/dav-collections", "GET")],
        });
    }
}
