using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
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
        // The repository and the account, as every module demo has them (ModuleDemoSetup, ADR 0901).
        var seeded = await ModuleDemoSetup.AddIfConfiguredAsync(services, dbContext, configuration, "FlightSchoolDemo",
            tenantId, adminId, now, [new ModuleDemoSetup.Repository(RepositorySlug, "Flight School")],
            account =>
            {
                account.CanManageModules = true;
                account.CanManageUsers = true;
            });
        if (seeded is null)
        {
            return;
        }

        // Flight School's own addition: the Administration folder holding the licence.
        var folderMaskVersion = await dbContext.MaskVersions
            .SingleAsync(v => v.MaskId == WellKnownMaskIds.Folder && v.IsCurrent);
        var administration = await DemoDataSeeder.AddFolderAsync(dbContext, tenantId, seeded.Repositories[RepositorySlug], "Administration",
            adminId, now, folderMaskVersion.Id, AdministrationSlug);
        // Broken with no entry copied down: a break snapshots what is in force at that moment, and the pilots'
        // read grants, added later by the module's demo seeder, are exactly what must not reach the licence.
        administration.BreaksInheritance = true;

        var onAdministration = new AclEntry
        {
            Id = DemoId.For(tenantId, "acl/flight-school-setup/administration"),
            TenantId = tenantId,
            DocumentId = administration.Id,
            ServiceAccountId = seeded.Account.Id,
            CreatedAt = now,
        };
        ModuleDemoSetup.Everything(onAdministration);
        dbContext.AclEntries.Add(onAdministration);
        await dbContext.SaveChangesAsync();
    }
}
