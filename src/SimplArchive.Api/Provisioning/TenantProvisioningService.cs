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
        CancellationToken cancellationToken = default,
        Func<string, Guid>? idFor = null,
        DateTimeOffset? createdAt = null);
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
        CancellationToken cancellationToken = default,
        Func<string, Guid>? idFor = null,
        DateTimeOffset? createdAt = null)
    {
        // createdAt rides along with idFor (#832, same reason as #781's ids): the demo tenant is torn down
        // and reseeded nightly, and a real-clock Created stamp made every reseed — and every manual capture —
        // produce a tenant "created" at whatever minute the process booted. Null everywhere else: a real
        // tenant's Created is genuinely the moment of this call.
        var at = createdAt ?? DateTimeOffset.UtcNow;

        var tenant = new Tenant
        {
            // idFor (#781): a config-declared tenant (the demo) derives its ids from stable slugs, so a nightly
            // wipe-and-reseed produces the SAME archive rather than a new one wearing the same names. Every
            // client-visible identity (RFC 8474 MAILBOXID/EMAILID, DAV collections, UIDVALIDITY) rests on these
            // GUIDs. Null everywhere else — a real tenant keeps fresh ids, which is what lets a genuinely
            // purged-and-recreated folder read as recreated.
            Id = idFor?.Invoke("tenant") ?? Guid.NewGuid(),
            Name = name,
            Status = TenantStatus.Active,
            CreatedAt = at,
        };

        // Create the tenant's own object-storage bucket BEFORE the transaction, not inside it (#1226). Its id
        // is assigned above rather than by the database, so nothing here needs the row to exist first — and the
        // alternative is holding a Postgres transaction open across two network round-trips to object storage,
        // coupling every tenant creation to an external service's latency.
        //
        // That trade is the one CheckoutsController already made and wrote down: a rollback leaves an EMPTY
        // BUCKET for a tenant that does not exist, which is recoverable waste and is reused idempotently if the
        // same id is provisioned again — where a half-provisioned TENANT is not recoverable at all. It is also
        // still ordered before any blob could be written into it, which is what the original placement was for.
        await _objectStorage.EnsureTenantBucketAsync(tenant.Id, cancellationToken);
        await _objectStorage.SetBucketLifecycleAsync(tenant.Id, tenant.IncompleteUploadCleanupDays, cancellationToken);

        // ONE transaction for founding a tenant (#1226, ADR 0794). This committed THREE times — the tenant row,
        // the administrator, then the repository and its grant — so a failure between them left a tenant with
        // no administrator, or an administrator with no repository. Nobody can sign in to fix either, and the
        // tenant is the object every other invariant in the archive hangs off.
        //
        // Owned only when nothing is already in flight (ADR 0781), so a caller that provisions inside its own
        // transaction — the demo seeder does — has ours join theirs rather than throwing.
        var owned = _dbContext.Database.CurrentTransaction is null
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await using var transaction = owned;

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

        await _wellKnownMaskSeeder.EnsureWellKnownMasksAsync(tenant.Id, cancellationToken);
        // The default sensitivity labels (ADR "Configurable sensitivity labels + upload defaults").
        await _sensitivityLabelSeeder.EnsureDefaultLabelsAsync(tenant.Id, cancellationToken);

        var administrator = new User
        {
            Id = idFor?.Invoke("user/admin") ?? Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = administratorEmail,
            DisplayName = administratorDisplayName,
            IsActive = true,
            IsTenantAdmin = true,
            ImapShowAllDocuments = tenant.ImapShowAllDocumentsDefault,
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
            // Mail routing is part of founding a tenant too: the first administrator must be able to give a
            // department a mailbox without a second principal existing yet to grant it from (#703).
            CanManageMailRouting = true,
            CreatedAt = at,
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
        await _personalSpaces.EnsureAsync(administrator.Id, tenant.Id, cancellationToken,
            idFor is null ? null : slug => idFor($"personal/admin/{slug}"), createdAt);

        var repository = new Document
        {
            Id = idFor?.Invoke("repository") ?? Guid.NewGuid(),
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
            CreatedAt = at,
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
            CreatedAt = at,
        });

        // No app-level pre-check needed for the repository's own name: it's a brand-new tenant, so no
        // sibling can exist yet to conflict with.
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

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
