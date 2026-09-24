using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Encryption;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Search;

/// <summary>
/// Whether a tenant's documents may be indexed, and whether its searches may reach the index at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The strict tier's one surviving exclusion is content search</b> (ADR 0825), and the reason is custody
/// rather than authorisation: an OpenSearch index is a SECOND plaintext copy at rest, in a store that
/// <c>IObjectStorageClient</c> never touches, so its disks, snapshots and operators all sit outside the
/// boundary the tier is sold on. Within the application, search is already filtered twice — an indexed ACL
/// pre-filter inside the query and a per-hit <c>CanSee</c> post-filter (ADR 0137) — so cross-user leakage
/// would be a defect, not the design.
/// </para>
/// <para>
/// <b>Stopping the QUERY is the lesser half.</b> Routing a strict tenant's search to the metadata service
/// while its documents kept being indexed would leave the plaintext copy exactly where it is and merely stop
/// reading it — which is the appearance of the property without the property. So indexing stops first, and
/// the routing follows from it.
/// </para>
/// <para>
/// <b>Two paths write to the index, and only one of them is the interface.</b> <c>IDocumentIndexer</c> handles
/// a document at a time, and <c>OpenSearchIndexRebuilder</c> writes directly over HTTP in its own loop — so a
/// decorator on the interface alone would have left the full rebuild happily indexing every strict tenant.
/// Both ask this type.
/// </para>
/// <para>
/// <b>Switching a tenant to strict does not retroactively empty the index</b>, and that has to be said out
/// loud because it fails silently: entries written before the switch stay until something removes them.
/// <see cref="ExcludesAsync"/> makes <c>SyncAsync</c> REMOVE rather than merely skip, so any document touched
/// after the switch evicts itself — and a full reindex excludes strict tenants by construction, which is the
/// operator step that purges the rest. The runbook says so rather than leaving it to be discovered.
/// </para>
/// </remarks>
public class StrictTenantSearchPolicy(SimplArchiveDbContext dbContext, EncryptionModes modes)
{
    /// <summary>True when this tenant's content must not be in the search index.</summary>
    /// <remarks>
    /// Virtual, and the class unsealed, for exactly one reason: it is the only way the ROUTER's decision can
    /// be tested against the shipped router rather than against a re-implementation of it. A test that
    /// copies the routing logic asserts its own copy and passes whatever the real class does — which is the
    /// failure mode this seam exists to avoid, on the one path where a wrong answer is a leak.
    /// </remarks>
    public virtual async Task<bool> ExcludesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: the indexer and the rebuilder both run outside an ambient tenant — the rebuilder
        // sets the accessor per document as it walks every tenant, and a background sync has none at all.
        var name = await dbContext.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Name)
            .SingleOrDefaultAsync(cancellationToken);

        return name is not null && modes.IsStrict(name);
    }
}
