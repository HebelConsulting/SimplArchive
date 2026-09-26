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

// Hosts the real API in-process (WebApplicationFactory<Program>) against real Postgres + SeaweedFS object-store
// containers (Testcontainers), with migrations applied at startup — see ADR "Container-backed end-to-end
// integration tests" + ADR 0360 (SeaweedFS replaced the EOL MinIO). Auth uses real OpenIddict client-credentials
// tokens for a seeded ServiceAccount.
public sealed partial class E2EApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Bucket = "simplarchive";
    private const string StorageUser = "storageadmin";
    private const string StoragePassword = "storageadmin";

    // The SeaweedFS S3 identity config (mirrors scripts/seaweedfs-s3.json) — mapped into the container so its S3
    // API authenticates the same credentials the Api uses.
    private const string SeaweedS3Config =
        """{"identities":[{"name":"storageadmin","credentials":[{"accessKey":"storageadmin","secretKey":"storageadmin"}],"actions":["Admin","Read","Write","List","Tagging"]}]}""";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage(SimplArchive.SelfHosting.ImagePins.Image("postgres", "POSTGRES_TAG"))
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
        // Pinned to the version Compose, the kiosk and the Helm chart all run, so the suite tests what ships and
        // an image change arrives as a deliberate bump rather than overnight (#663).
        .WithImage(SimplArchive.SelfHosting.ImagePins.Image("opensearchproject/opensearch", "OPENSEARCH_TAG"))
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
        // Disk watermarks off — THIS is what made CI refuse every index with a 403. A node above the HIGH
        // watermark makes OpenSearch set `cluster.blocks.create_index` on the cluster, and a hosted runner sits
        // at 93% full before the fleet even starts (4.6 GB free of 71.6 GB). The cluster it is protecting holds
        // 111 KB of indices in a container discarded at the end of the run, so the watermark is guarding nothing
        // and costing the whole suite.
        .WithEnvironment("cluster.routing.allocation.disk.threshold_enabled", "false")
        // Index State Management off. Nothing here uses it, and its start-up template migration sets
        // `cluster.blocks.create_index` too — at t≈50-60s, LONG after /_cluster/health answers at t≈1s. That
        // was a real second route to the same 403, just not the one that was firing.
        // Kept in step with SelfHostedApp by OpenSearchContainerParityTests.
        .WithEnvironment("plugins.index_state_management.enabled", "false")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithPortBinding(9200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/_cluster/health").ForStatusCode(HttpStatusCode.OK)))
        .Build();

    private readonly IContainer _tika = new ContainerBuilder()
        .WithImage(SimplArchive.SelfHosting.ImagePins.Image("apache/tika", "TIKA_TAG"))
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
        .WithImage(SimplArchive.SelfHosting.ImagePins.Image("gotenberg/gotenberg", "GOTENBERG_TAG"))
        .WithPortBinding(3000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/health").ForStatusCode(HttpStatusCode.OK)))
        .Build();

    // Valkey (Redis-compatible) — the SignalR backplane (ADR "SignalR Valkey backplane"). Enabling it for the whole
    // E2E collection proves the backplane doesn't break single-instance realtime, and backs the cross-replica test.
    private readonly IContainer _valkey = new ContainerBuilder()
        .WithImage(SimplArchive.SelfHosting.ImagePins.Image("valkey/valkey", "VALKEY_TAG"))
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    private string _storageUrl = "";

    // A STUB encryption service for the IMAP envelope hook (SimplArchiveEncryption ADR 0007). Hosted for the
    // whole collection with 404 as its default answer, which means every existing IMAP test continuously
    // exercises the no-certificate → plaintext contract as a side effect of merely running. A test opts a
    // user in via RegisterEncryptionRecipient; the stub then returns a marker message rather than real CMS —
    // the core treats the bytes as opaque, so this tests the WIRING end to end while the crypto is proven in
    // the service's own repository (its cross-implementation tests). Faking the crypto here would prove
    // nothing those tests do not, and would couple this suite to another repo's packages.
    private WebApplication? _encryptionStub;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _encryptionRecipients = new(StringComparer.OrdinalIgnoreCase);

    public const string EnvelopedMarkerHeader = "X-SimplArchive-Test-Enveloped";

    // The seeded ENCRYPTED demo tenant (ADR 0813/0825) — the one name given a mode, so tests reach
    // the per-tenant gate's positive path through it and every per-test tenant exercises the negative one.
    public const string CryptoTenantName = "Crypto";

    // A tenant declared STRICT (#1376, ADR 0825). Declared in configuration here because a mode is read from
    // configuration, which is built once at startup — a test cannot switch a tenant into the tier afterwards.
    // The tenant itself is created by whichever test wants it, under exactly this name.
    public const string StrictTenantName = "StrictTier";
    public const string CryptoAdminEmail = "crypt@crypto.e2e.local";
    public const string CryptoPassword = "CryptoDemo-1234!";

    public void RegisterEncryptionRecipient(string email) => _encryptionRecipients[email] = true;

    // A certificate held by the SERVICE's registry rather than on the user row (#1433). That is how a strict
    // tenant's identities actually arrive — self-service is closed for exactly those tenants (ADR 0813), and the
    // service has the provisioning door (PUT /api/users/{email}/certificate) — so a test that planted the column
    // instead would be testing a state no installation can reach.
    //
    // Returns the PKCS#12 so the test can OPEN what the server envelopes: proving the certificate was found is
    // not the same as proving the envelope is addressed to its key.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _registryCertificates =
        new(StringComparer.OrdinalIgnoreCase);

    public byte[] RegisterEncryptionCertificate(string email, string password = "reader")
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={email}", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        _registryCertificates[email] = certificate.ExportCertificatePem();
        return certificate.Export(
            System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, password);
    }

    /// <summary>Raw storage access for tests that must see what the BUCKET holds — the at-rest tests'
    /// whole point is that stored bytes differ from served bytes (ADR 0818), which no API-level read can
    /// show (the decorator decrypts every server-side path).</summary>
    public AmazonS3Client CreateRawStorageClient() => new(
        new BasicAWSCredentials(StorageUser, StoragePassword),
        new AmazonS3Config { ServiceURL = _storageUrl, ForcePathStyle = true, UseHttp = true, AuthenticationRegion = "us-east-1" });

    private async Task<string> StartEncryptionStubAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/api/users/{email}/enveloped", async (string email, HttpRequest request) =>
        {
            if (!_encryptionRecipients.ContainsKey(email))
            {
                return Results.NotFound();
            }

            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            var enveloped = "Subject: enveloped\r\n"
                + $"{EnvelopedMarkerHeader}: {body.Length}\r\n"
                + "Content-Type: application/pkcs7-mime; smime-type=enveloped-data; name=\"smime.p7m\"\r\n"
                + "\r\nMIAGCSqGSIb3DQEHA6CAMIACAQA=\r\n";
            return Results.Bytes(System.Text.Encoding.ASCII.GetBytes(enveloped), "message/rfc822");
        });

        // The registry lookup the core asks when a user's own column is empty — the source a strict tenant's
        // identities actually live in (#1433). A real PEM, because the core VALIDATES it and then envelopes to
        // it; a marker string would pass the fetch and fail the parse, which is the wrong half to stub.
        app.MapGet("/api/users/{email}/certificate", (string email) =>
            _registryCertificates.TryGetValue(email, out var pem)
                ? Results.Text(pem, "application/x-pem-file")
                : Results.NotFound());

        // The at-rest half (ADR 0818): unlike the envelope leg, these two answer with REAL crypto — an
        // in-memory RSA keypair standing in for the HSM. The decorator's whole write path runs through
        // kek/current at the CryptoDemo SEED, so a 404 here would fail every E2E boot; and the unwrap must
        // actually invert the wrap or every gated read dies. The crypto is trivial on purpose (RSA-OAEP +
        // the shared blob format); what this proves is the CORE's seam, not the service's HSM.
        // Generations are REAL keypairs here, one per generation, because the rotation tests assert
        // cryptographic facts (a re-wrap must yield the same DEK under a different wrapping) — a stub that
        // faked the wrapping would green exactly the mistakes those tests exist to catch.
        var keks = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Security.Cryptography.RSA>();
        keks["kek-v1"] = System.Security.Cryptography.RSA.Create(2048);
        var currentGeneration = "kek-v1";

        app.MapGet("/api/kek/current", () => Results.Ok(new
        {
            generation = currentGeneration,
            publicKeyPem = keks[currentGeneration].ExportSubjectPublicKeyInfoPem(),
            oaepHash = "SHA256",
        }));
        app.MapGet("/api/kek/generations", () => Results.Ok(new
        {
            current = currentGeneration,
            generations = keks.Keys.OrderBy(k => int.Parse(k["kek-v".Length..])).ToArray(),
        }));
        app.MapPost("/api/kek/rotate", () =>
        {
            var next = $"kek-v{keks.Keys.Max(k => int.Parse(k["kek-v".Length..])) + 1}";
            keks[next] = System.Security.Cryptography.RSA.Create(2048);
            currentGeneration = next;
            return Results.Ok(new
            {
                generation = next,
                publicKeyPem = keks[next].ExportSubjectPublicKeyInfoPem(),
                oaepHash = "SHA256",
            });
        });
        app.MapPost("/api/unwrapped-dek", async (HttpRequest request) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(request.Body);
            var generation = body.GetProperty("kekGeneration").GetString()!;
            if (!keks.TryGetValue(generation, out var kek))
            {
                return Results.Problem(statusCode: 400, title: "Unknown generation.");
            }

            var wrapped = Convert.FromBase64String(body.GetProperty("wrappedDek").GetString()!);
            return Results.Bytes(
                kek.Decrypt(wrapped, System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256),
                "application/octet-stream");
        });
        app.MapPost("/api/rewrapped-dek", async (HttpRequest request) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(request.Body);
            var from = body.GetProperty("fromGeneration").GetString()!;
            if (!keks.TryGetValue(from, out var old))
            {
                return Results.Problem(statusCode: 400, title: "Unknown generation.");
            }

            var dek = old.Decrypt(Convert.FromBase64String(body.GetProperty("wrappedDek").GetString()!),
                System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256);
            return Results.Ok(new
            {
                wrappedDek = Convert.ToBase64String(keks[currentGeneration].Encrypt(dek,
                    System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256)),
                kekGeneration = currentGeneration,
            });
        });
        app.MapDelete("/api/kek/{generation}", (string generation) =>
        {
            if (generation == currentGeneration)
            {
                return Results.Problem(statusCode: 409, title: "Cannot retire the current generation.");
            }

            keks.TryRemove(generation, out _);
            return Results.NoContent();
        });

        await app.StartAsync();
        _encryptionStub = app;
        return app.Urls.First();
    }

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

        // The retention sweep's first run must land beyond this leg's lifetime — the same override
        // SelfHostedApp already applies for the UI fixtures, which this factory never got (#744).
        //
        // Its default is app-start+3min and this leg runs ~12 min on CI, so the sweep fires INSIDE it and
        // disposes any overdue-at-birth document a test created. That is not hypothetical: it took
        // RetentionDispositionReviewTests down with `Conflict` → `NotFound`, the document having been disposed
        // between the test creating it and the legal hold being asked to block it. It reads as a flake — it is
        // a fixed-clock race that only a slow enough leg enters, which is why it never reproduces locally.
        //
        // A test that WANTS the sweep calls RunRetentionSweepAsync; no test wants it firing on a wall clock it
        // cannot see.
        Environment.SetEnvironmentVariable("Retention__InitialDelay", "02:00:00");
        // The startup blazor-client seed builds its redirect URIs from App:BaseUrl; use the in-process host so
        // the interactive-login redirect_uri (below) matches a registered URI.
        Environment.SetEnvironmentVariable("App__BaseUrl", "http://localhost");
        // Hermetic in-memory OpenIddict keys — the dev-cert store fails in a headless CI runner environment (ADR
        // "Continuous integration"); ephemeral keys need no store.
        Environment.SetEnvironmentVariable("OpenIddict__UseEphemeralKeys", "true");

        // A redeemed rolling refresh token stays usable for a reuse leeway — 30 seconds in production, so a
        // client retrying after a lost response is not signed out. Shortened here so the security property
        // (reuse PAST the window is refused) can be asserted in a second instead of costing 30 per assertion,
        // which is how a property ends up untested.
        Environment.SetEnvironmentVariable("OpenIddict__RefreshTokenReuseLeewaySeconds", "1");
        Environment.SetEnvironmentVariable("ObjectStorage__ServiceUrl", _storageUrl);
        Environment.SetEnvironmentVariable("ObjectStorage__PublicServiceUrl", _storageUrl);
        Environment.SetEnvironmentVariable("Encryption__ServiceUrl", await StartEncryptionStubAsync());

        // The per-tenant half of the switch (ADR 0813), in the kiosk's exact shape: the CryptoDemo tenant
        // seeded at startup is the ONLY listed tenant, so every per-test tenant is UNLISTED — which makes
        // the existing envelope tests the gate's negative case (a registered recipient in an unlisted
        // tenant still gets plaintext) while the seeded tenant carries the positive one.
        Environment.SetEnvironmentVariable($"Encryption__Modes__{CryptoTenantName}", "Storage");
        Environment.SetEnvironmentVariable($"Encryption__Modes__{StrictTenantName}", "Strict");
        Environment.SetEnvironmentVariable("CryptoDemo__Tenant__Name", CryptoTenantName);
        Environment.SetEnvironmentVariable("CryptoDemo__Administrator__Email", CryptoAdminEmail);
        Environment.SetEnvironmentVariable("CryptoDemo__Password", CryptoPassword);

        // Stage the TestModule into a Modules directory so the REAL loader brings it up through the real
        // seams (ADR 0737's activation circle, ModuleControllerTests). Deliberately present for EVERY E2E
        // test, not just the module ones: an inactive module must be inert, and the whole suite running
        // beside it is the standing proof. Only the module's own dll is copied — the ABI resolves from the
        // default context, which is the deployment shape too.
        var modulesRoot = Path.Combine(Path.GetTempPath(), $"simplarchive-e2e-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(modulesRoot, "test-module"));
        var testModuleDll = typeof(SimplArchive.TestModule.TestModule).Assembly.Location;
        File.Copy(testModuleDll, Path.Combine(modulesRoot, "test-module", Path.GetFileName(testModuleDll)));
        Environment.SetEnvironmentVariable("Modules__Directory", modulesRoot);
        Environment.SetEnvironmentVariable("ObjectStorage__Region", "us-east-1");
        Environment.SetEnvironmentVariable("ObjectStorage__BucketName", Bucket);
        Environment.SetEnvironmentVariable("ObjectStorage__AccessKey", StorageUser);
        Environment.SetEnvironmentVariable("ObjectStorage__SecretKey", StoragePassword);
        // The IMAP endpoint (#562): on, plaintext, on an OS-assigned ephemeral port — tests read the bound
        // port back from the ImapServer singleton and drive it with a real mail-client library (MailKit).
        // The stub SIEM receiver the webhook tests stand up lives on loopback, which the outbound-address
        // policy refuses by default (ADR 0717) — so this fixture configures the allowlist exactly as an
        // operator with an on-premises collector would. It is the mechanism under test, not a bypass: the
        // cloud-metadata addresses stay refused underneath it, and the tests assert that they do.
        // Both families: "localhost" resolves to ::1 as well as 127.0.0.1 on a normal machine, and the policy
        // requires EVERY resolved address to be permitted — one public answer beside a private one is the shape
        // of a rebinding attack. Allowlisting only the IPv4 half would refuse the very receiver it stood up.
        Environment.SetEnvironmentVariable("OutboundHttp__AllowedNetworks__0", "127.0.0.0/8");
        Environment.SetEnvironmentVariable("OutboundHttp__AllowedNetworks__1", "::1/128");

        Environment.SetEnvironmentVariable("Lmtp__Enabled", "true");
        Environment.SetEnvironmentVariable("Lmtp__Port", "-1");
        Environment.SetEnvironmentVariable("Imap__Enabled", "true");
        Environment.SetEnvironmentVariable("Imap__Port", "-1");
        // A PUBLISHED port that cannot coincide with the ephemeral bound one, so the advertised-vs-bound
        // distinction (#682) is observable rather than a value that happens to match.
        Environment.SetEnvironmentVariable("Imap__PublicPort", "143");
        // Session-hygiene caps scaled for testability (ADR 0618): the pre-auth timeout short enough that a
        // test can wait it out, the caps low enough to hit with a handful of sockets — but all with headroom
        // over what the MailKit-driven tests actually hold open (≤3 concurrent sessions, logins < 1 s).
        // IdleTimeoutSeconds deliberately stays at its 30-minute default: the writes/Notes tests keep a
        // MailKit client idle while they poll HTTP APIs, which on a slow runner can exceed any value small
        // enough to be worth asserting on.
        Environment.SetEnvironmentVariable("Imap__PreAuthTimeoutSeconds", "10");
        Environment.SetEnvironmentVariable("Imap__MaxConnectionsPerUser", "5");
        Environment.SetEnvironmentVariable("Imap__MaxConnections", "8");
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

    /// <summary>
    /// The container's superuser connection string, for the one test that needs to make roles of its own and
    /// break them on purpose (#1287). Every other test reaches the database through the Api.
    /// </summary>
    public string PostgresSuperuserConnectionString => _postgres.GetConnectionString();

    /// <summary>
    /// The DNS the mail-domain challenge is checked against (#667) — a stub, so tests can publish a record.
    /// </summary>
    /// <remarks>
    /// The real lookup asks the host's resolvers, which CI has no way to answer for a domain a test invented.
    /// Shared across the collection like everything else here, so a test must use a domain of its own; they all
    /// build one from a GUID.
    /// </remarks>
    public TestDnsTxtLookup Dns { get; } = new();

    public sealed class TestDnsTxtLookup : SimplArchive.Application.Abstractions.IDnsTxtLookup
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _records =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Publishes a TXT value at a name, as an administrator would in their zone.</summary>
        public void Publish(string name, string value) =>
            _records.AddOrUpdate(name, _ => [value], (_, existing) => [.. existing, value]);

        public Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(_records.TryGetValue(name, out var values) ? values : []);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development so OpenIddict allows token requests over the in-process HTTP test server (its transport
        // security requirement is disabled only in Development). Ocr stays unset → searchable-PDF no-op (not
        // exercised here); OpenSearch/Tika (search) and Gotenberg (preview) are configured via env vars above.
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SimplArchive.Application.Abstractions.IDnsTxtLookup>();
            services.AddSingleton<SimplArchive.Application.Abstractions.IDnsTxtLookup>(Dns);
        });
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
        if (_encryptionStub is not null)
        {
            await _encryptionStub.DisposeAsync();
        }

        await _gotenberg.DisposeAsync();
        await _tika.DisposeAsync();
        await _openSearch.DisposeAsync();
        await _storage.DisposeAsync();
        await _valkey.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
