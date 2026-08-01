using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace SimplArchive.SelfHosting;

// Boots the REAL application for out-of-process, browser-drivable end-to-end use: Postgres + SeaweedFS +
// OpenSearch + Tika + Gotenberg via Testcontainers, then the real API launched as a subprocess on a real Kestrel
// port (a browser can't reach WebApplicationFactory's in-memory TestServer), serving the Blazor WASM client. A demo
// tenant + admin + sample tree is seeded via the app's own Demo:* startup seed (ADR 0214), so a client/browser logs
// straight in and has content.
//
// This is the single source of truth for the self-host boot (ADR 0502): the web + desktop E2E fixtures and the
// manual-capture harness all wrap it. It is deliberately **browser-agnostic** (no Playwright reference) so the
// desktop suite stays light — the web fixture and the capture harness launch their own Chrome on top of BaseUrl.
public sealed class SelfHostedApp : IAsyncDisposable
{
    public const string Bucket = "simplarchive";
    public const string StorageUser = "storageadmin";
    public const string StoragePassword = "storageadmin";

    public const string AdminEmail = "demo@simplarchive.local";
    public const string AdminPassword = "demo1234";
    public const string AdminDisplayName = "Demo Admin";

    // The SeaweedFS S3 identity config (mirrors scripts/seaweedfs-s3.json), ADR 0360.
    private const string SeaweedS3Config =
        """{"identities":[{"name":"storageadmin","credentials":[{"accessKey":"storageadmin","secretKey":"storageadmin"}],"actions":["Admin","Read","Write","List","Tagging"]}]}""";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    // SeaweedFS via the generic container builder (ADR 0360, replacing the EOL MinIO).
    private readonly IContainer _storage = new ContainerBuilder()
        .WithImage("chrislusf/seaweedfs@sha256:c7d6c721b30ae711db766bbbfd40192776e263d4e51e22f57baef7bef93c12c6")
        .WithResourceMapping(Encoding.UTF8.GetBytes(SeaweedS3Config), "/s3.json")
        // -volume.max=500: SeaweedFS defaults to only 8 volume slots, but per-tenant buckets make every tenant's
        // bucket its own collection → its own volume; past the 8th, an upload PUT gets a 500 ("no writable
        // volume"). Volumes are created on demand, so a high slot cap costs nothing. (Same fix as E2EApiFactory.)
        .WithCommand("server", "-dir=/data", "-s3", "-s3.port=8333", "-s3.config=/s3.json", "-volume.max=500")
        .WithPortBinding(8333, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S3 API Server"))
        .Build();

    // OpenSearch + Tika so the real full-text path is active (the Postgres fallback ignores field/date filters).
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
        // Cap the Tika JVM heap so the fleet fits a memory-constrained runner. JAVA_TOOL_OPTIONS, not JAVA_OPTS —
        // the image entrypoint runs `exec java …` and never references JAVA_OPTS, whereas the JVM auto-reads
        // JAVA_TOOL_OPTIONS at startup.
        .WithEnvironment("JAVA_TOOL_OPTIONS", "-Xmx512m")
        .WithPortBinding(9998, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9998).ForPath("/version").ForStatusCode(HttpStatusCode.OK)))
        .Build();

    // Gotenberg — office/markdown/html → PDF renditions.
    private readonly IContainer _gotenberg = new ContainerBuilder()
        .WithImage("gotenberg/gotenberg:8")
        .WithPortBinding(3000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/health").ForStatusCode(HttpStatusCode.OK)))
        .Build();

    private readonly StringBuilder _apiLog = new();
    private Process? _api;
    private string _openSearchUrl = "";
    private string _tikaUrl = "";
    private string _gotenbergUrl = "";

    public string BaseUrl { get; private set; } = "";

    // The self-hosted app's Postgres — exposed so a caller can clean up data it seeded.
    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async Task StartAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _storage.StartAsync(), _openSearch.StartAsync(), _tika.StartAsync(), _gotenberg.StartAsync());
        var storageUrl = $"http://{_storage.Hostname}:{_storage.GetMappedPublicPort(8333)}";
        _openSearchUrl = $"http://{_openSearch.Hostname}:{_openSearch.GetMappedPublicPort(9200)}";
        _tikaUrl = $"http://{_tika.Hostname}:{_tika.GetMappedPublicPort(9998)}";
        _gotenbergUrl = $"http://{_gotenberg.Hostname}:{_gotenberg.GetMappedPublicPort(3000)}";

        using (var s3 = new AmazonS3Client(
            new BasicAWSCredentials(StorageUser, StoragePassword),
            new AmazonS3Config { ServiceURL = storageUrl, ForcePathStyle = true, UseHttp = true, AuthenticationRegion = "us-east-1" }))
        {
            for (var attempt = 1; ; attempt++)
            {
                try { await s3.PutBucketAsync(Bucket); break; }
                catch (Exception) when (attempt < 10) { await Task.Delay(500); }
            }

            // SeaweedFS enforces CORS per bucket (MinIO used a server-wide env), so the browser can PUT/GET the
            // presigned upload/download URLs cross-origin (the SPA origin ≠ the object-store origin).
            await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
            {
                BucketName = Bucket,
                Configuration = new CORSConfiguration
                {
                    Rules = [new CORSRule { AllowedOrigins = ["*"], AllowedMethods = ["GET", "PUT", "HEAD"], AllowedHeaders = ["*"] }],
                },
            });
        }

        var port = FreeTcpPort();
        // Serve on "localhost" (not 127.0.0.1): browsers special-case localhost as a secure context AND accept it
        // as a WebAuthn RP ID, whereas a bare IP address is rejected (rp.id must be a registrable domain).
        BaseUrl = $"http://localhost:{port}";
        await StartApiAsync(storageUrl);
    }

    private async Task StartApiAsync(string storageUrl)
    {
        var repoRoot = RepoRoot();
        var apiCsproj = Path.Combine(repoRoot, "src", "SimplArchive.Api", "SimplArchive.Api.csproj");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "run", "--project", apiCsproj, "--no-build", "--no-restore", "--no-launch-profile", "--urls", BaseUrl })
        {
            psi.ArgumentList.Add(a);
        }

        var env = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__Default"] = _postgres.GetConnectionString(),
            ["App__ApplyMigrationsAtStartup"] = "true",
            ["App__BaseUrl"] = BaseUrl, // blazor-client redirect URIs must match the served origin for login
            // Hermetic in-memory OpenIddict keys — the dev-cert store fails in a headless CI runner environment
            // (ADR "Continuous integration"); ephemeral keys need no store.
            ["OpenIddict__UseEphemeralKeys"] = "true",
            ["ObjectStorage__ServiceUrl"] = storageUrl,
            ["ObjectStorage__PublicServiceUrl"] = storageUrl,
            ["ObjectStorage__Region"] = "us-east-1",
            ["ObjectStorage__BucketName"] = Bucket,
            ["ObjectStorage__AccessKey"] = StorageUser,
            ["ObjectStorage__SecretKey"] = StoragePassword,
            ["OpenSearch__Url"] = _openSearchUrl,
            ["Tika__Url"] = _tikaUrl,
            ["Gotenberg__Url"] = _gotenbergUrl,
            // Seed a demo tenant + admin (known password) + sample tree so a client/browser has data to work with.
            ["Demo__Tenant__Name"] = "Demo",
            ["Demo__Administrator__Email"] = AdminEmail,
            ["Demo__Administrator__Password"] = AdminPassword,
            ["Demo__Administrator__DisplayName"] = AdminDisplayName,
            ["Demo__RepositoryName"] = "Demo Repository",
        };
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }

        _api = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the API process.");
        _api.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_apiLog) _apiLog.AppendLine(e.Data); };
        _api.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_apiLog) _apiLog.AppendLine(e.Data); };
        _api.BeginOutputReadLine();
        _api.BeginErrorReadLine();

        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var deadline = DateTime.UtcNow.AddSeconds(150);
        while (DateTime.UtcNow < deadline)
        {
            if (_api.HasExited)
            {
                throw new InvalidOperationException($"API exited early (code {_api.ExitCode}):\n{ApiLog()}");
            }

            try
            {
                if ((await http.GetAsync("/health/ready")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // not up yet
            }

            await Task.Delay(1000);
        }

        throw new InvalidOperationException($"API did not become ready within 150s:\n{ApiLog()}");
    }

    private string ApiLog()
    {
        lock (_apiLog)
        {
            return _apiLog.ToString();
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (SimplArchive.slnx).");
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is { HasExited: false })
        {
            try
            {
                _api.Kill(entireProcessTree: true);
                _api.WaitForExit(10000);
            }
            catch
            {
                // best effort
            }
        }

        _api?.Dispose();
        await _gotenberg.DisposeAsync();
        await _tika.DisposeAsync();
        await _openSearch.DisposeAsync();
        await _storage.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
