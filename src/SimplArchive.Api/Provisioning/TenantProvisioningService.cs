using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Tenant;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// The outcome of provisioning a tenant — includes the administrator's initial password, which is only
/// ever available here (it's stored hashed and never retrievable again).
/// </summary>
public sealed record ProvisionedTenant(
    Guid TenantId,
    string TenantName,
    Guid AdministratorId,
    string AdministratorEmail,
    string AdministratorPassword,
    Guid RepositoryId,
    string RepositoryName);

/// <summary>
/// Provisions a new tenant: the Tenant, its 3 well-known masks, a full-rights TenantAdministrator User, and
/// the tenant's first repository (a root Document with a full-rights AclEntry for that administrator). See
/// ADR "Tenant onboarding and platform-admin mechanism" and ADR "Document-scope authorization retrofit for
/// User, and tenant-administrator-driven onboarding" for why this is the one place full rights are granted
/// with no escalation cap. Extracted from TenantsController so both the HTTP endpoint and the Compose
/// demo-data seeder share the exact same (security-sensitive) logic rather than duplicating it — see ADR
/// "Compose demo-data seeding".
/// </summary>
public interface ITenantProvisioningService
{
    Task<ProvisionedTenant> ProvisionAsync(
        string name,
        string administratorEmail,
        string administratorDisplayName,
        string? repositoryName,
        string? administratorPassword,
        CancellationToken cancellationToken = default);
}

public sealed class TenantProvisioningService : ITenantProvisioningService
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IWellKnownMaskSeeder _wellKnownMaskSeeder;
    private readonly ISensitivityLabelSeeder _sensitivityLabelSeeder;
    private readonly IObjectStorageClient _objectStorage;
    private readonly PasswordHasher<User> _passwordHasher = new();

    private readonly Documents.PersonalRepositoryProvisioner _personalSpaces;

    public TenantProvisioningService(SimplArchiveDbContext dbContext, IWellKnownMaskSeeder wellKnownMaskSeeder, ISensitivityLabelSeeder sensitivityLabelSeeder, IObjectStorageClient objectStorage, Documents.PersonalRepositoryProvisioner personalSpaces)
    {
        _personalSpaces = personalSpaces;
        _dbContext = dbContext;
        _wellKnownMaskSeeder = wellKnownMaskSeeder;
        _sensitivityLabelSeeder = sensitivityLabelSeeder;
        _objectStorage = objectStorage;
    }

    /// <param name="administratorPassword">
    /// The administrator's initial password. When null a cryptographically random one is generated (the
    /// HTTP onboarding path — returned once in the response); the demo seeder passes an explicit one so the
    /// operator can log straight in.
    /// </param>
    public async Task<ProvisionedTenant> ProvisionAsync(
        string name,
        string administratorEmail,
        string administratorDisplayName,
        string? repositoryName,
        string? administratorPassword,
        CancellationToken cancellationToken = default)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Tenants.Add(tenant);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Tenant.Name's existing partial unique index (WHERE Status = Active, ADR "Tenant name
            // uniqueness") — a real DB constraint, no app-level pre-check.
            throw TenantNameConflictException.OnCreate();
        }

        // Create the tenant's own object-storage bucket (ADR "Per-tenant object-storage bucket") before any blob
        // could be written to it — object-lock-enabled (WORM) + browser CORS + ops tags, idempotent — then apply
        // its lifecycle policy (ADR "Per-tenant bucket policy knobs").
        await _objectStorage.EnsureTenantBucketAsync(tenant.Id, cancellationToken);
        await _objectStorage.SetBucketLifecycleAsync(tenant.Id, tenant.IncompleteUploadCleanupDays, cancellationToken);

        await _wellKnownMaskSeeder.EnsureWellKnownMasksAsync(tenant.Id, cancellationToken);
        // The default sensitivity labels (ADR "Configurable sensitivity labels + upload defaults").
        await _sensitivityLabelSeeder.EnsureDefaultLabelsAsync(tenant.Id, cancellationToken);

        var administrator = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = administratorEmail,
            DisplayName = administratorDisplayName,
            IsActive = true,
            IsTenantAdmin = true,
            // The tenant administrator gets EVERY system-level right at provisioning. No right is implied by
            // IsTenantAdmin, and a caller can only grant a right it already holds (SystemRightsPolicy) — so if
            // the founding admin lacked a right, there'd be nobody in the tenant able to delegate it (it would
            // be permanently un-grantable). Granting all of them makes every right delegable from day one.
            CanImpersonate = true,
            CanOverrideCheckout = true,
            CanLegalHold = true,
            CanManageClassification = true,
            CanResetMfa = true,
            CanManageRepositories = true,
            CanManageMasks = true,
            CanManageServiceAccounts = true,
            CanManageUsers = true,
            CanViewAuditLog = true,
            CanExport = true,
            CanImport = true,
            CanManageIntrays = true,
            CanCreateExternalLink = true,
            // The tenant's first administrator gets the x-ray into personal spaces (ADR 0670) — implied at
            // GRANT time, so it is an ordinary revocable column from here on. Without it the founding admin
            // could not see the Administration → Users branch at all, the bypass no longer reaching there.
            CanAccessWithoutGrant = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var password = administratorPassword ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        administrator.PasswordHash = _passwordHasher.HashPassword(administrator, password);

        _dbContext.Users.Add(administrator);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // (TenantId, NormalizedEmail)'s existing unique index — no app-level pre-check, same shape
            // ServiceAccount.Name/User.Email hit elsewhere.
            throw new AdministratorEmailConflictException();
        }

        // The tenant administrator is a user like any other, so their personal space is provisioned at creation
        // rather than on first sign-in (#634) — the first level is closed now, and My Documents is the only home
        // for their own content.
        await _personalSpaces.EnsureAsync(administrator.Id, tenant.Id, cancellationToken);

        var repository = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            ParentId = null,
            Name = repositoryName ?? tenant.Name,
            // A repository wears the Repository mask, in lockstep with ParentId == null (ADR 0627) — the same
            // rule RepositoriesController.Create already followed. This path did NOT, and the drift was
            // invisible on any long-lived installation: WellKnownMaskSeeder's backfill promotes Folder-masked
            // roots on startup, but it runs BEFORE this service creates them, so a repository born here wore
            // the wrong mask until the NEXT restart healed it. The one place that never gets a second start is
            // a freshly reset demo — which is why the kiosk showed a plain folder icon on its repository every
            // morning after the nightly `down -v`, and nowhere else did.
            MaskVersionId = await Documents.FolderMask.CurrentVersionIdAsync(
                _dbContext, tenant.Id, Domain.Masks.WellKnownMaskIds.Repository, cancellationToken),
            CreatedByUserId = administrator.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(repository);

        _dbContext.AclEntries.Add(new AclEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            DocumentId = repository.Id,
            UserId = administrator.Id,
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanEditIndexData = true,
            CanDelete = true,
            CanCreateSubItems = true,
            CanManagePermissions = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // No app-level pre-check needed for the repository's own name: it's a brand-new tenant, so no
        // sibling can exist yet to conflict with.
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new ProvisionedTenant(
            tenant.Id,
            tenant.Name,
            administrator.Id,
            administrator.Email,
            password,
            repository.Id,
            repository.Name);
    }
}
