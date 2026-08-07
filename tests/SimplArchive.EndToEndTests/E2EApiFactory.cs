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
using Microsoft.AspNetCore.Hosting;
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

// Hosts the real API in-process (WebApplicationFactory<Program>) against real Postgres + SeaweedFS object-store
// containers (Testcontainers), with migrations applied at startup — see ADR "Container-backed end-to-end
// integration tests" + ADR 0360 (SeaweedFS replaced the EOL MinIO). Auth uses real OpenIddict client-credentials
// tokens for a seeded ServiceAccount.
public sealed class E2EApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Bucket = "simplarchive";
    private const string StorageUser = "storageadmin";
    private const string StoragePassword = "storageadmin";

    // The SeaweedFS S3 identity config (mirrors scripts/seaweedfs-s3.json) — mapped into the container so its S3
    // API authenticates the same credentials the Api uses.
    private const string SeaweedS3Config =
        """{"identities":[{"name":"storageadmin","credentials":[{"accessKey":"storageadmin","secretKey":"storageadmin"}],"actions":["Admin","Read","Write","List","Tagging"]}]}""";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    // SeaweedFS via the generic container builder (no dedicated Testcontainers module) — it supports S3 Object
    // Lock, which our WORM tests need. Pinned by the same digest as the compose stack (ADR 0360).
    private readonly IContainer _storage = new ContainerBuilder()
        .WithImage("chrislusf/seaweedfs@sha256:c7d6c721b30ae711db766bbbfd40192776e263d4e51e22f57baef7bef93c12c6")
        .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(SeaweedS3Config), "/s3.json")
        // -volume.max: SeaweedFS defaults to only 8 volume slots, but per-tenant buckets (ADR "Per-tenant
        // object-storage bucket") make every test tenant's bucket its own collection consuming SeaweedFS volumes,
        // and the full suite creates hundreds of tenants. When the cap is hit, SeaweedFS can't allocate a volume
        // for a new bucket and returns 500 ("no writable volume") on the upload PUT — a deterministic burst of
        // object-storage 500s across every later test. Each bucket takes several volumes, so the suite sat right
        // at the old cap of 500 (a single new test file tipped it over); this is raised well above the suite's
        // peak so it has real headroom to grow. Volumes are created on demand (memory/disk scale with USED volumes,
        // not the cap), so a high slot cap costs nothing until volumes are actually written.
        .WithCommand("server", "-dir=/data", "-s3", "-s3.port=8333", "-s3.config=/s3.json", "-volume.max=5000")
        .WithPortBinding(8333, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S3 API Server"))
        .Build();

    // OpenSearch (single-node, security disabled — dev only, same image/env as the Compose stack) + Tika, so
    // the search slice exercises the real OpenSearch full-text path incl. document-content extraction, not the
    // Postgres fallback. Started only for the search tests, but shared across the whole E2E collection.
    private readonly IContainer _openSearch = new ContainerBuilder()
        .WithImage("opensearchproject/opensearch:2")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithPortBinding(9200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/_cluster/health").ForStatusCode(HttpStatusCode.OK)))
        .Build();

    private readonly IContainer _tika = new ContainerBuilder()
        .WithImage("apache/tika:latest-full")
        // Cap the Tika JVM heap so the fleet fits a memory-constrained (≈16 GB) runner. Left uncapped, the JVM
        // sizes its max heap to a fraction of *visible* host RAM (GBs), and combined with OpenSearch + Gotenberg
        // the fleet overcommits, which surfaced on the runner as SeaweedFS S3 500s ("internal error") partway
        // through a full run. JAVA_TOOL_OPTIONS (not JAVA_OPTS) because the image's entrypoint runs `exec java …`
        // directly and never references JAVA_OPTS, whereas the JVM auto-reads JAVA_TOOL_OPTIONS at startup.
        .WithEnvironment("JAVA_TOOL_OPTIONS", "-Xmx512m")
        .WithPortBinding(9998, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9998).ForPath("/version").ForStatusCode(HttpStatusCode.OK)))
        .Build();

    // Gotenberg (LibreOffice + Chromium routes) for the preview-rendition tests — office/email → PDF and
    // markdown/html → PDF. Same image as the Compose stack.
    private readonly IContainer _gotenberg = new ContainerBuilder()
        .WithImage("gotenberg/gotenberg:8")
        .WithPortBinding(3000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/health").ForStatusCode(HttpStatusCode.OK)))
        .Build();

    // Valkey (Redis-compatible) — the SignalR backplane (ADR "SignalR Valkey backplane"). Enabling it for the whole
    // E2E collection proves the backplane doesn't break single-instance realtime, and backs the cross-replica test.
    private readonly IContainer _valkey = new ContainerBuilder()
        .WithImage("valkey/valkey:8.1.1-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    private string _storageUrl = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _storage.StartAsync(), _openSearch.StartAsync(), _tika.StartAsync(), _gotenberg.StartAsync(), _valkey.StartAsync());
        _storageUrl = $"http://{_storage.Hostname}:{_storage.GetMappedPublicPort(8333)}";

        // Point the app at the containers via environment variables — these win over appsettings*.json (whose
        // Development profile hardcodes a localhost connection string), unlike an in-memory config source.
        // Set before the host builds (first CreateClient), which happens after this fixture InitializeAsync.
        // Single object-store endpoint: both the in-process Api and the test process reach it at the mapped host port.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("App__ApplyMigrationsAtStartup", "true");
        // The startup blazor-client seed builds its redirect URIs from App:BaseUrl; use the in-process host so
        // the interactive-login redirect_uri (below) matches a registered URI.
        Environment.SetEnvironmentVariable("App__BaseUrl", "http://localhost");
        // Hermetic in-memory OpenIddict keys — the dev-cert store fails in a headless CI runner environment (ADR
        // "Continuous integration"); ephemeral keys need no store.
        Environment.SetEnvironmentVariable("OpenIddict__UseEphemeralKeys", "true");
        Environment.SetEnvironmentVariable("ObjectStorage__ServiceUrl", _storageUrl);
        Environment.SetEnvironmentVariable("ObjectStorage__PublicServiceUrl", _storageUrl);
        Environment.SetEnvironmentVariable("ObjectStorage__Region", "us-east-1");
        Environment.SetEnvironmentVariable("ObjectStorage__BucketName", Bucket);
        Environment.SetEnvironmentVariable("ObjectStorage__AccessKey", StorageUser);
        Environment.SetEnvironmentVariable("ObjectStorage__SecretKey", StoragePassword);
        // OpenSearch + Tika → the real full-text path (name + index-field values + document-content). Configured
        // for the whole collection; the round-trip/workflow tests don't search, so this only adds startup cost.
        Environment.SetEnvironmentVariable("OpenSearch__Url", $"http://{_openSearch.Hostname}:{_openSearch.GetMappedPublicPort(9200)}");
        Environment.SetEnvironmentVariable("Tika__Url", $"http://{_tika.Hostname}:{_tika.GetMappedPublicPort(9998)}");
        // Gotenberg → the preview-rendition path (office/markdown/html → PDF).
        Environment.SetEnvironmentVariable("Gotenberg__Url", $"http://{_gotenberg.Hostname}:{_gotenberg.GetMappedPublicPort(3000)}");
        // SignalR Valkey backplane — process-global, so a second in-process host (the cross-replica test) shares it.
        Environment.SetEnvironmentVariable("ConnectionStrings__Valkey", $"{_valkey.Hostname}:{_valkey.GetMappedPublicPort(6379)}");

        // Create the bucket the Api expects (the Compose stack does this via storage-init).
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(StorageUser, StoragePassword),
            new AmazonS3Config { ServiceURL = _storageUrl, ForcePathStyle = true, UseHttp = true, AuthenticationRegion = "us-east-1" });
        // Object-lock-enabled (versioning + WORM) so the WORM/Object-Lock tests can apply retention/legal holds
        // (ADR "WORM / immutable document versions"). A short retry absorbs any S3-listener startup race after the
        // container logs it started.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket, ObjectLockEnabledForBucket = true });
                break;
            }
            catch (Exception) when (attempt < 10)
            {
                await Task.Delay(500);
            }
        }
    }

    // Seals a tenant's pending audit events into WORM segments (ADR "Audit-log WORM") — lets a test verify the
    // sealed segments without waiting for the ~hourly background worker.
    public async Task RunWormArchiveAsync(Guid tenantId)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        await scope.ServiceProvider.GetRequiredService<IAuditWormArchiver>().ArchiveAsync(tenantId);
    }

    // Simulates a tenant whose blobs predate storage accounting (ADR "Per-tenant storage quota"): zeroes the used
    // counter and clears every version's SizeBytes, so a recompute has to rebuild both from the actual blobs.
    public async Task SimulatePreQuotaStateAsync(Guid tenantId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        await db.DocumentVersions.IgnoreQueryFilters().Where(v => v.TenantId == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.SizeBytes, (long?)null));
        await db.Tenants.Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.StorageUsedBytes, 0L));
    }

    // A raw S3 client + the per-tenant bucket name (ADR "Per-tenant object-storage bucket") — lets a test assert
    // an object landed in its own tenant's bucket and nowhere else.
    public IAmazonS3 CreateStorageClient() => new AmazonS3Client(
        new BasicAWSCredentials(StorageUser, StoragePassword),
        new AmazonS3Config { ServiceURL = _storageUrl, ForcePathStyle = true, UseHttp = true, AuthenticationRegion = "us-east-1" });

    public static string BucketForTenant(Guid tenantId) => $"{Bucket}-{tenantId:D}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development so OpenIddict allows token requests over the in-process HTTP test server (its transport
        // security requirement is disabled only in Development). Ocr stays unset → searchable-PDF no-op (not
        // exercised here); OpenSearch/Tika (search) and Gotenberg (preview) are configured via env vars above.
        builder.UseEnvironment("Development");
    }

    // Seeds a Tenant + a ServiceAccount + its client-credentials OpenIddict app, returning the credentials —
    // the same shape ServiceAccountsController creates. Returns a fresh tenant per call (isolated); TenantId is
    // returned so a reviewer User can be seeded into the same tenant (workflow tests).
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
    public async Task<Guid> SeedUserAsync(Guid tenantId, string email, string password, string displayName, bool canViewAuditLog = false, bool canManageUsers = false, bool canResetMfa = false, bool canExport = false, bool canImport = false, bool canManageServiceAccounts = false, bool canManageRepositories = false, bool canManageInboxes = false, bool canCreateExternalLink = false, bool isTenantAdmin = false)
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
            CanManageInboxes = canManageInboxes,
            CanCreateExternalLink = canCreateExternalLink,
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

    // Adds another member to an existing group (for a multi-member group-inbox test).
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
    public async Task RunEscalationSweepAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SimplArchive.Application.Abstractions.IWorkflowEscalationService>().SweepAsync();
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

    // Drives the real interactive OAuth2 Authorization Code + PKCE login for a seeded User (the only way to get
    // a User-scoped token — there's no password grant), mirroring the Blazor client's flow: authorize → login
    // form → code → token. Returns the access token.
    // mfaCode, when supplied, computes the current TOTP (or a recovery code) for the MFA second step — used by
    // the MFA end-to-end test (ADR "MFA (interactive login, TOTP)"). Null = password-only (MFA disabled).
    public async Task<string> GetUserTokenAsync(string email, string password, Func<string>? mfaCode = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        const string redirectUri = "http://localhost/authentication/login-callback";
        var authorize = "/connect/authorize?" + string.Join('&', new[]
        {
            "client_id=blazor-client", "response_type=code", $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            "scope=openid", $"code_challenge={challenge}", "code_challenge_method=S256", "state=x",
        });

        // authorize → 302 to the login page.
        var loginPath = (await client.GetAsync(authorize)).Headers.Location!.ToString();
        var loginHtml = await client.GetStringAsync(loginPath);
        var antiforgery = Regex.Match(loginHtml, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://localhost" + loginPath).Query)["ReturnUrl"].ToString();

        // post credentials → 302 back to the authorize request (or a 200 MFA step if MFA is enabled).
        var login = await client.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = antiforgery,
        }));

        // MFA second step: the password POST returned the page (no redirect) with a signed MfaTicket; post the
        // code to the Verify handler, which then 302s back to the authorize request.
        if (login.Headers.Location is null)
        {
            Assert.NotNull(mfaCode);
            var mfaHtml = await login.Content.ReadAsStringAsync();
            var ticket = Regex.Match(mfaHtml, @"name=""MfaTicket""[^>]*value=""([^""]*)""").Groups[1].Value;
            var mfaAntiforgery = Regex.Match(mfaHtml, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
            login = await client.PostAsync(loginPath + "&handler=Verify", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Code"] = mfaCode!(),
                ["MfaTicket"] = ticket,
                ["ReturnUrl"] = returnUrl,
                ["__RequestVerificationToken"] = mfaAntiforgery,
            }));
        }

        // follow redirects until the authorization code comes back on the callback redirect.
        var next = login.Headers.Location!.ToString();
        string? code = null;
        for (var i = 0; i < 8 && code is null; i++)
        {
            var response = await client.GetAsync(next);
            if (response.Headers.Location is not { } location)
            {
                break;
            }

            var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri("http://localhost"), location);
            code = QueryHelpers.ParseQuery(absolute.Query).TryGetValue("code", out var c) ? c.ToString() : null;
            next = absolute.ToString();
        }

        Assert.NotNull(code);

        using var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = "blazor-client",
            ["code_verifier"] = verifier,
        }));
        tokenResponse.EnsureSuccessStatusCode();
        var json = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Lists the object keys under a storage prefix — lets a test inspect what's actually in the bucket (e.g.
    // asserting cached preview artifacts exist / were purged), going through the app's own storage client.
    public async Task<IReadOnlyList<string>> ListObjectKeysAsync(string prefix)
    {
        using var scope = Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var objects = await storage.ListObjectsAsync(prefix);
        return objects.Select(o => o.Key).ToList();
    }

    // An in-process Api client with a bearer token. (Presigned MinIO URLs are fetched with a plain HttpClient,
    // since they go over the network to the MinIO container, not through the Api.)
    public HttpClient CreateAuthedClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _gotenberg.DisposeAsync();
        await _tika.DisposeAsync();
        await _openSearch.DisposeAsync();
        await _storage.DisposeAsync();
        await _valkey.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
