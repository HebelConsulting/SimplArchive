using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// The demo tenant's Flight School repository and the unattended principal that sets it up (FlightSchool#85).
/// </summary>
/// <remarks>
/// <para>
/// The Flight School demo used to be seeded by a program logged in as the TENANT ADMINISTRATOR, with the password
/// on its command line: it created the repository, filed the licence, activated the module and wrote its settings
/// by hand-composed requests. The Encryption Module's demo does all of that through <c>saconsole</c> as a narrow
/// seeded service account (ADR 0870), and two activation paths for one core feature drift. This is the Flight
/// School half of making them one.
/// </para>
/// <para>
/// <b>Broader than <c>crypto-setup</c>, deliberately, and still far short of an administrator</b> (owner,
/// 2026-10-05). The demo needs pilot USERS, so the account holds <c>CanManageUsers</c> beside
/// <c>CanManageModules</c>; and it isolates each pilot's dossier, so it holds every right on the Flight School
/// repository, including <c>CanManagePermissions</c>, and on nothing else. No tenant settings, no other
/// repository, no service-account management: it finds the module's own principal through a document's
/// <c>grantable-principals</c> rel, as <c>saconsole acl grant</c> does.
/// </para>
/// <para>
/// The licence goes into <b>Flight School/Administration</b>, which breaks inheritance with nothing copied down,
/// so pilots reading the school do not read the licence (owner, 2026-10-05). The account is granted there
/// explicitly, because a broken folder inherits nothing, not even from its own parent.
/// </para>
/// <para>A no-op unless <c>FlightSchoolDemo:ServiceAccount:ClientId</c>/<c>ClientSecret</c> are configured.</para>
/// </remarks>
public static class FlightSchoolDemoSetup
{
    /// <summary>The slugs the kiosk script derives the same UUIDv5 ids from (#781).</summary>
    public const string RepositorySlug = "repository/flight-school";
    public const string AdministrationSlug = "flight-school-administration";

    public static async Task AddIfConfiguredAsync(
        IServiceProvider services, SimplArchiveDbContext dbContext, IConfiguration configuration,
        Guid tenantId, Guid adminId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(configuration["FlightSchoolDemo:ServiceAccount:ClientId"])
            || string.IsNullOrWhiteSpace(configuration["FlightSchoolDemo:ServiceAccount:ClientSecret"]))
        {
            return;
        }

        var school = new Document
        {
            Id = DemoId.For(tenantId, RepositorySlug),
            TenantId = tenantId,
            ParentId = null,
            Name = "Flight School",
            // A repository wears the Repository mask, in lockstep with ParentId == null (ADR 0627).
            MaskVersionId = await Documents.FolderMask.CurrentVersionIdAsync(
                dbContext, tenantId, WellKnownMaskIds.Repository, CancellationToken.None) ?? Guid.Empty,
            CreatedByUserId = adminId,
            CreatedAt = now,
        };
        dbContext.Documents.Add(school);
        await dbContext.SaveChangesAsync();

        var folderMaskVersion = await dbContext.MaskVersions
            .SingleAsync(v => v.MaskId == WellKnownMaskIds.Folder && v.IsCurrent);
        var administration = await DemoDataSeeder.AddFolderAsync(dbContext, tenantId, school.Id, "Administration",
            adminId, now, folderMaskVersion.Id, AdministrationSlug);
        // Broken with no entry copied down: a break snapshots what is in force at that moment, and the pilots'
        // read grants, added later by the module's demo seeder, are exactly what must not reach the licence.
        administration.BreaksInheritance = true;

        await SeededServiceAccount.AddIfConfiguredAsync(services, dbContext, configuration, "FlightSchoolDemo",
            tenantId, grantOn: school.Id,
            rights: account =>
            {
                account.CanManageModules = true;
                account.CanManageUsers = true;
            },
            grant: Everything);

        // Still UNSAVED here — the helper adds and leaves the commit to its caller — so it is found in Local.
        var clientId = configuration["FlightSchoolDemo:ServiceAccount:ClientId"];
        var account = dbContext.ServiceAccounts.Local.Single(a => a.OpenIddictApplicationClientId == clientId);
        var onAdministration = new AclEntry
        {
            Id = DemoId.For(tenantId, "acl/flight-school-setup/administration"),
            TenantId = tenantId,
            DocumentId = administration.Id,
            ServiceAccountId = account.Id,
            CreatedAt = now,
        };
        Everything(onAdministration);
        dbContext.AclEntries.Add(onAdministration);
        await dbContext.SaveChangesAsync();
    }

    private static void Everything(AclEntry entry)
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
