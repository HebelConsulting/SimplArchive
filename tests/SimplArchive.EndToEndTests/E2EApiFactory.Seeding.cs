using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SimplArchive.EndToEndTests;

// The DATA half of the end-to-end harness: tenants, principals, masks, grants and tenant settings.
//
// Split from E2EApiFactory.cs when the combined file crossed the 1000-line ceiling. It is a real seam rather
// than a line-count dodge: the other half boots containers, wires configuration and hands out clients — the
// harness — while everything here puts rows in a database so a test has something to act on. They change for
// different reasons, and a test author reaching for "how do I get a tenant with X" should not have to read
// Testcontainers setup to find it.
public sealed partial class E2EApiFactory
{
    // Seeds a Tenant + a ServiceAccount + its client-credentials OpenIddict app, returning the credentials —
    // the same shape ServiceAccountsController creates. Returns a fresh tenant per call (isolated); TenantId is
    // returned so a reviewer User can be seeded into the same tenant (workflow tests).
    /// <summary>Creates a tenant under a NAME the configuration has already given an encryption mode.</summary>
    /// <remarks>
    /// Tenants are otherwise seeded with a random name, which cannot be named in configuration read at
    /// startup — so a test that needs a tenant in a particular tier has to create it under the declared name.
    /// Re-used across tests rather than re-created: only the name matters to the gate, and each test still
    /// creates its own documents inside it.
    /// </remarks>
    public async Task<Guid> SeedTenantNamedAsync(string name)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        if (await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Name == name) is { } existing)
        {
            return existing.Id;
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // The same three provisioning steps SeedServiceAccountAsync performs, and for the same reasons — a
        // tenant created straight in the database has none of them. Skipping them fails LATER and elsewhere:
        // auto-classification at finalize resolves the well-known "Basic Entry" mask and answers
        // "Sequence contains no elements" from inside DocumentFinalizer, which reads as a bug in whatever
        // change is in flight rather than as a half-built tenant.
        await scope.ServiceProvider.GetRequiredService<IWellKnownMaskSeeder>().EnsureWellKnownMasksAsync(tenant.Id);
        await scope.ServiceProvider.GetRequiredService<ISensitivityLabelSeeder>().EnsureDefaultLabelsAsync(tenant.Id);
        await scope.ServiceProvider.GetRequiredService<IObjectStorageClient>().EnsureTenantBucketAsync(tenant.Id);

        return tenant.Id;
    }

    public async Task<(string ClientId, string Secret, Guid TenantId)> SeedServiceAccountAsync(bool canManageRepositories)
    {
        var tenantId = Guid.NewGuid();
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = $"T-{tenantId:N}", Status = TenantStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            // Auto-classification at finalize assigns the "Basic Entry" mask, so the tenant needs the well-known
            // masks seeded (a real tenant gets these at onboarding, ADR 0209).
            await scope.ServiceProvider.GetRequiredService<IWellKnownMaskSeeder>().EnsureWellKnownMasksAsync(tenantId);
            await scope.ServiceProvider.GetRequiredService<ISensitivityLabelSeeder>().EnsureDefaultLabelsAsync(tenantId);

            // This seed bypasses TenantProvisioningService, so create the tenant's object-storage bucket directly
            // (ADR "Per-tenant object-storage bucket") — uploads to tenants/{tenantId}/... need it to exist.
            await scope.ServiceProvider.GetRequiredService<IObjectStorageClient>().EnsureTenantBucketAsync(tenantId);
        }

        var (clientId, secret) = await SeedServiceAccountInTenantAsync(tenantId, canManageRepositories);
        return (clientId, secret, tenantId);
    }

    // Seeds a PlatformAdministrator (+ OpenIddict app) — the tenant-less principal platform maintenance
    // (tenant onboarding, the search reindex) authorizes against. Same client-credentials flow as a
    // ServiceAccount; TokenController checks this table when a client_id matches no ServiceAccount.
    public async Task<(string ClientId, string Secret)> SeedPlatformAdministratorAsync()
    {
        var clientId = Guid.NewGuid().ToString();
        var secret = Guid.NewGuid().ToString("N");

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        db.PlatformAdministrators.Add(new SimplArchive.Domain.PlatformAdministrators.PlatformAdministrator
        {
            Id = Guid.NewGuid(),
            Name = $"platform-{clientId[..8]}",
            OpenIddictApplicationClientId = clientId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
            },
        });

        return (clientId, secret);
    }

    // Seeds an additional ServiceAccount (+ OpenIddict app) into an existing tenant — for tests needing a second
    // principal in the same tenant (e.g. a caller with no ACL grant, to prove indexed-ACL search filtering).
    public async Task<(string ClientId, string Secret)> SeedServiceAccountInTenantAsync(Guid tenantId, bool canManageRepositories)
    {
        var clientId = Guid.NewGuid().ToString();
        var secret = Guid.NewGuid().ToString("N");

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        db.ServiceAccounts.Add(new ServiceAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = $"svc-{clientId[..8]}",
            OpenIddictApplicationClientId = clientId,
            IsActive = true,
            CanManageRepositories = canManageRepositories,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await applications.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
            },
        });

        return (clientId, secret);
    }

    // Seeds an active User (with a password) into a tenant, for the interactive-login flow. Returns the user id.
    public async Task<Guid> SeedUserAsync(Guid tenantId, string email, string password, string displayName, bool canViewAuditLog = false, bool canManageUsers = false, bool canResetMfa = false, bool canExport = false, bool canImport = false, bool canManageServiceAccounts = false, bool canManageRepositories = false, bool canManageIntrays = false, bool canCreateExternalLink = false, bool canManageMailRouting = false, bool canBlockResources = false, bool canReleaseResources = false, bool isTenantAdmin = false)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = email,
            DisplayName = displayName,
            IsActive = true,
            CanViewAuditLog = canViewAuditLog,
            CanManageUsers = canManageUsers,
            CanResetMfa = canResetMfa,
            CanExport = canExport,
            CanImport = canImport,
            CanManageServiceAccounts = canManageServiceAccounts,
            CanManageRepositories = canManageRepositories,
            CanManageIntrays = canManageIntrays,
            CanCreateExternalLink = canCreateExternalLink,
            CanManageMailRouting = canManageMailRouting,
            CanBlockResources = canBlockResources,
            CanReleaseResources = canReleaseResources,
            IsTenantAdmin = isTenantAdmin,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    // Seeds a group with one member, returning its Group id — for testing group-targeted sharing / flow-down
    // without needing the admin group-management API.
    public async Task<Guid> SeedGroupWithMemberAsync(Guid tenantId, string name, Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var group = new SimplArchive.Domain.Groups.Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = name, CreatedAt = DateTimeOffset.UtcNow };
        db.Groups.Add(group);
        db.GroupMemberships.Add(new SimplArchive.Domain.Groups.GroupMembership { TenantId = tenantId, GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();
        return group.Id;
    }

    // Adds another member to an existing group (for a multi-member group-intray test).
    public async Task AddGroupMemberAsync(Guid tenantId, Guid groupId, Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        db.GroupMemberships.Add(new SimplArchive.Domain.Groups.GroupMembership { TenantId = tenantId, GroupId = groupId, UserId = userId });
        await db.SaveChangesAsync();
    }

    // Grants CanLegalHold to a seeded user by email (there's no API to grant an arbitrary system right without
    // already holding it), so a legal-hold test can act as a compliance user.
    public async Task GrantCanLegalHoldAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var normalized = email.ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == normalized);
        user.CanLegalHold = true;
        await db.SaveChangesAsync();
    }

    // Grants CanOverrideCheckout to a seeded user by email — so a checkout test can force-release another
    // user's lock (ADR "Document check-out / check-in").
    public async Task GrantCanOverrideCheckoutAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var normalized = email.ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == normalized);
        user.CanOverrideCheckout = true;
        await db.SaveChangesAsync();
    }

    // Seeds a custom mask (document type) with a review SLA, returning its Mask id (assignable via PUT
    // /documents/{id}/mask). No fields, so no required-field validation blocks the assignment.
    public async Task<Guid> SeedMaskWithSlaAsync(Guid tenantId, int reviewSlaDays)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var mask = new SimplArchive.Domain.Masks.Mask { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
        db.Masks.Add(mask);
        db.MaskVersions.Add(new SimplArchive.Domain.Masks.MaskVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaskId = mask.Id,
            Name = $"SLA {Guid.NewGuid():N}",
            ReviewSlaDays = reviewSlaDays,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return mask.Id;
    }

    // Seeds a custom mask with one SingleSelect field, returning (MaskId, FieldDefinitionId) — for the search
    // index-field facet (ADR "Search facet refinements").
    public async Task<(Guid MaskId, Guid FieldDefinitionId)> SeedMaskWithSelectFieldAsync(Guid tenantId, string fieldName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var mask = new SimplArchive.Domain.Masks.Mask { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
        db.Masks.Add(mask);
        var version = new SimplArchive.Domain.Masks.MaskVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaskId = mask.Id,
            Name = $"Select {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.MaskVersions.Add(version);
        var field = new SimplArchive.Domain.Masks.FieldDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaskVersionId = version.Id,
            Name = fieldName,
            DataType = SimplArchive.Domain.Masks.FieldDataType.SingleSelect,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.FieldDefinitions.Add(field);
        await db.SaveChangesAsync();
        return (mask.Id, field.Id);
    }

    // Sets a tenant's storage quota directly (for the WebDAV 507 test) — null = unlimited.
    public async Task SetTenantStorageQuotaAsync(Guid tenantId, long? quotaBytes)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        tenant.StorageQuotaBytes = quotaBytes;
        await db.SaveChangesAsync();
    }

    // Toggles a tenant's tag-catalog enforcement directly (for the tag-catalog test).
    public async Task SetTenantRestrictTagsToCatalogAsync(Guid tenantId, bool restrict)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        tenant.RestrictTagsToCatalog = restrict;
        await db.SaveChangesAsync();
    }

    // Data-classification clearance enforcement (ADR "Sensitivity clearance enforcement") — set the tenant switch
    // and a service-account's clearance directly (the SA has no interactive admin path to tenant-settings).
    public async Task SetTenantEnforceClearanceAsync(Guid tenantId, bool enforce)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        tenant.EnforceClearance = enforce;
        await db.SaveChangesAsync();
    }

    public async Task SetServiceAccountClearanceAsync(Guid tenantId, string clientId, int clearanceRank)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var sa = await db.ServiceAccounts.IgnoreQueryFilters().SingleAsync(s => s.TenantId == tenantId && s.OpenIddictApplicationClientId == clientId);
        sa.ClearanceRank = clearanceRank;
        await db.SaveChangesAsync();
    }

    // Runs the workflow escalation sweep synchronously (the hosted worker's on-demand equivalent) for tests.
    // Returns what the sweep ACTED on, which a concurrency test needs: two overlapping sweeps must report one
    // winner between them (#1425), and a test that could only see the notifications would pass on the right
    // count reached the wrong way.
    public async Task<int> RunEscalationSweepAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SimplArchive.Application.Abstractions.IWorkflowEscalationService>().SweepAsync();
    }

    // Seeds a custom mask with a retention period, returning its Mask id (assignable via PUT /documents/{id}/mask).
    public async Task<Guid> SeedMaskWithRetentionAsync(Guid tenantId, int retentionYears)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var mask = new SimplArchive.Domain.Masks.Mask { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
        db.Masks.Add(mask);
        db.MaskVersions.Add(new SimplArchive.Domain.Masks.MaskVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaskId = mask.Id,
            Name = $"Retained {Guid.NewGuid():N}",
            RetentionYears = retentionYears,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return mask.Id;
    }

    // Grants IsTenantAdmin to a seeded user by email, so a purge test can act as a tenant admin. Also grants
    // CanExport/CanImport (ADR "Dedicated CanExport/CanImport rights") — a real provisioned tenant admin holds
    // every right, and export/import now gate on those specific rights rather than IsTenantAdmin.
    // Takes the x-ray away from an administrator (ADR 0670) without touching IsTenantAdmin — the state that
    // proves the right is revocable rather than a relabelled admin check.
    public async Task RevokeAccessWithoutGrantAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var normalized = email.ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == normalized);
        user.CanAccessWithoutGrant = false;
        await db.SaveChangesAsync();
    }

    public async Task GrantTenantAdminAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var normalized = email.ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == normalized);
        user.IsTenantAdmin = true;
        user.CanExport = true;
        user.CanImport = true;
        user.CanManageClassification = true;
        user.CanManageMasks = true;

        // Promotion grants the x-ray into personal spaces (ADR 0670) — the bypass no longer reaches there, so
        // an admin seeded WITHOUT this would be refused the Administration → Users view, and every test that
        // uses this helper as "make them an admin" would be quietly testing a half-promoted user.
        user.CanAccessWithoutGrant = true;

        // The founding-admin bundle carries the routing right (#703) — this helper stands in for "a fully
        // provisioned tenant admin", so it mirrors TenantProvisioningService rather than bare promotion.
        user.CanManageMailRouting = true;
        await db.SaveChangesAsync();
    }

    // Sets the upload-time default sensitivity label on a well-known mask's current version (ADR "Configurable
    // sensitivity labels + upload defaults") — reaches into the DB since masks are immutable versions with no
    // update endpoint. Used to test that an auto-classified upload inherits the default.
    public async Task SetMaskDefaultSensitivityAsync(Guid tenantId, string maskName, Guid labelId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var version = await db.MaskVersions.IgnoreQueryFilters()
            .SingleAsync(v => v.TenantId == tenantId && v.Name == maskName && v.IsCurrent);
        version.DefaultSensitivityLabelId = labelId;
        await db.SaveChangesAsync();
    }

    // Back-dates a reminder's due time so the next sweep fires it (the API rejects a past RemindAt, so this
    // reaches into the DB directly) — used by the reminders E2E to exercise the sweep without waiting.
    public async Task BackdateReminderAsync(Guid reminderId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var reminder = await db.DocumentReminders.IgnoreQueryFilters().SingleAsync(r => r.Id == reminderId);
        reminder.RemindAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    // Runs the document-reminder sweep once (ADR "Document reminders") and returns how many fired.
    public async Task<int> RunReminderSweepAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentReminderService>().SweepAsync();
    }

    // Grants CanImpersonate to a seeded user by email, so an impersonation test has a valid actor.
    public async Task GrantCanImpersonateAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var normalized = email.ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == normalized);
        user.CanImpersonate = true;
        await db.SaveChangesAsync();
    }

    // Grants CanManageClassification to a seeded user by email, so a retention test can view the schedule.
    public async Task GrantCanManageClassificationAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var normalized = email.ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == normalized);
        user.CanManageClassification = true;
        await db.SaveChangesAsync();
    }

    // Runs the retention sweep synchronously (the hosted worker's on-demand equivalent) for tests.
    public async Task RunRetentionSweepAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SimplArchive.Application.Abstractions.IRetentionService>().SweepAsync();
    }

    public async Task<string> GetTokenAsync(string clientId, string secret)
    {
        using var client = CreateClient();
        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        }));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }
}
