using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// A module demo's repositories and the unattended principal that sets them up (ADR 0894's shape, generalised for the
/// Licensing Module's demo, ADR 0901): one narrow service account with full rights on exactly those repositories and
/// the system rights the caller names, everything else done through <c>saconsole</c>. A no-op unless
/// <c>{section}:ServiceAccount:ClientId</c>/<c>ClientSecret</c> are configured.
/// </summary>
/// <remarks>Flight School and Licensing differ only in data (the repositories, the system rights) and in Flight
/// School's extra Administration folder, which it adds on top; so this is one implementation (ADR 0877).</remarks>
public static class ModuleDemoSetup
{
    /// <summary>A repository to seed: the slug its stable id derives from (#781) and its name.</summary>
    public sealed record Repository(string Slug, string Name);

    /// <summary>What was seeded: the repositories by slug, and the account (unsaved changes are committed).</summary>
    public sealed record Seeded(IReadOnlyDictionary<string, Guid> Repositories, ServiceAccount Account);

    public static async Task<Seeded?> AddIfConfiguredAsync(
        IServiceProvider services, SimplArchiveDbContext dbContext, IConfiguration configuration, string section,
        Guid tenantId, Guid adminId, DateTimeOffset now, IReadOnlyList<Repository> repositories, Action<ServiceAccount> rights)
    {
        if (repositories.Count == 0
            || string.IsNullOrWhiteSpace(configuration[$"{section}:ServiceAccount:ClientId"])
            || string.IsNullOrWhiteSpace(configuration[$"{section}:ServiceAccount:ClientSecret"]))
        {
            return null;
        }

        // A repository wears the Repository mask, in lockstep with ParentId == null (ADR 0627).
        var repositoryMask = await Documents.FolderMask.CurrentVersionIdAsync(
            dbContext, tenantId, WellKnownMaskIds.Repository, CancellationToken.None) ?? Guid.Empty;
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var repository in repositories)
        {
            var id = DemoId.For(tenantId, repository.Slug);
            ids[repository.Slug] = id;
            dbContext.Documents.Add(new Document
            {
                Id = id,
                TenantId = tenantId,
                ParentId = null,
                Name = repository.Name,
                MaskVersionId = repositoryMask,
                CreatedByUserId = adminId,
                CreatedAt = now,
            });
        }

        await dbContext.SaveChangesAsync();

        await SeededServiceAccount.AddIfConfiguredAsync(services, dbContext, configuration, section,
            tenantId, grantOn: ids[repositories[0].Slug], rights, grant: Everything);

        // Still UNSAVED here — the helper adds and leaves the commit to its caller — so it is found in Local.
        var clientId = configuration[$"{section}:ServiceAccount:ClientId"];
        var account = dbContext.ServiceAccounts.Local.Single(a => a.OpenIddictApplicationClientId == clientId);
        foreach (var repository in repositories.Skip(1))
        {
            var entry = new AclEntry
            {
                Id = DemoId.For(tenantId, $"acl/{section}/{repository.Slug}"),
                TenantId = tenantId,
                DocumentId = ids[repository.Slug],
                ServiceAccountId = account.Id,
                CreatedAt = now,
            };
            Everything(entry);
            dbContext.AclEntries.Add(entry);
        }

        await dbContext.SaveChangesAsync();
        return new Seeded(ids, account);
    }

    /// <summary>Every right on a document: what a setup account holds on its own repositories.</summary>
    public static void Everything(AclEntry entry)
    {
        entry.CanSee = true;
        entry.CanReadContent = true;
        entry.CanEditContent = true;
        entry.CanEditIndexData = true;
        entry.CanDelete = true;
        entry.CanCreateSubItems = true;
        entry.CanManagePermissions = true;
    }
}
