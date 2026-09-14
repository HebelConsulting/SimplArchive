using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Tags;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Replaces a document's whole tag set: normalize, enforce the tenant's tag catalog, then delete and re-insert
/// the <see cref="DocumentTag"/> rows.
/// </summary>
/// <remarks>
/// Extracted from <c>DocumentTagsController.Set</c> unchanged, so ADR 0794's combined <c>PUT .../detail</c>
/// delegates here instead of carrying a second copy — <c>DocumentBulkController</c> already adds tags its own
/// way, and a third spelling of "what a tag is" is how they come to disagree.
///
/// APPLIES ONLY — it neither gates nor saves. Note that tags are gated more WEAKLY than the document's other
/// metadata: <c>CanEditIndexData</c> alone, with no legal-hold or checked-out block, because tags are
/// lightweight labels like comments. That is deliberate and documented on the controller, and it is why the
/// combined endpoint gates per CHANGED aspect rather than applying one blanket check: a blanket check would
/// quietly remove the ability to tag a document under legal hold, which is a capability somebody chose.
/// </remarks>
public class TagSetWriter(SimplArchiveDbContext dbContext, TimeProvider clock)
{
    /// <summary>
    /// Stages the replacement and returns the normalized tags actually written, which the caller needs for its
    /// response and its audit line. Throws <see cref="UnknownTagException"/> when the tenant restricts tagging
    /// to its catalog and a tag is not in it.
    /// </summary>
    public async Task<List<string>> ApplyAsync(Document document, IReadOnlyList<string>? tags, CancellationToken cancellationToken)
    {
        var documentId = document.Id;

        var normalized = (tags ?? [])
            .Select(t => (t ?? string.Empty).Trim().ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= 100)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        // Tag-catalog enforcement (ADR "Tag controlled vocabulary"): when the tenant restricts tagging, every
        // tag must already be in the active catalog; otherwise a newly-typed tag is added to the catalog so it
        // curates itself going forward.
        var restrict = await dbContext.Tenants.Where(t => t.Id == document.TenantId).Select(t => t.RestrictTagsToCatalog).SingleAsync(cancellationToken);
        var activeCatalog = (await dbContext.TagDefinitions.Where(t => t.RetiredAt == null).Select(t => t.Name).ToListAsync(cancellationToken)).ToHashSet();
        if (restrict)
        {
            if (normalized.FirstOrDefault(t => !activeCatalog.Contains(t)) is { } unknown)
            {
                throw new UnknownTagException(unknown);
            }
        }
        else
        {
            foreach (var tag in normalized.Where(t => !activeCatalog.Contains(t)))
            {
                dbContext.TagDefinitions.Add(new TagDefinition { Id = Guid.NewGuid(), TenantId = document.TenantId, Name = tag, CreatedAt = clock.GetUtcNow() });
            }
        }

        var existing = await dbContext.DocumentTags.Where(t => t.DocumentId == documentId).ToListAsync(cancellationToken);
        dbContext.DocumentTags.RemoveRange(existing);
        var now = clock.GetUtcNow();
        foreach (var tag in normalized)
        {
            dbContext.DocumentTags.Add(new DocumentTag { Id = Guid.NewGuid(), TenantId = document.TenantId, DocumentId = documentId, Tag = tag, CreatedAt = now });
        }

        return normalized;
    }
}
