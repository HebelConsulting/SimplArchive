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
    public const string AdminPassword = "SimplDemo2026!";
    public const string AdminDisplayName = "Demo Admin";

    // The SeaweedFS S3 identity config (mirrors scripts/seaweedfs-s3.json), ADR 0360.
    private const string SeaweedS3Config =
        """{"identities":[{"name":"storageadmin","credentials":[{"accessKey":"storageadmin","secretKey":"storageadmin"}],"actions":["Admin","Read","Write","List","Tagging"]}]}""";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    // SeaweedFS via the generic container builder (ADR 0360, replacing the EOL MinIO).
    private readonly IContainer _storage = new ContainerBuilder()
        .WithImage("chrislusf/seaweedfs@sha256:c7d6c721b30ae711db766bbbfd40192776e263d4e51e22f57baef7bef93c12c6")
        .WithResourceMapping(Encoding.UTF8.GetBytes(SeaweedS3Config), "/s3.json")
        // -volume.max: SeaweedFS defaults to only 8 volume slots, but per-tenant buckets make every tenant's
        // bucket its own collection consuming volumes; when the cap is hit an upload PUT gets a 500 ("no writable
        // volume"). Each bucket takes several volumes and the old cap of 500 sat right at the suite's peak — raised
        // well above it for headroom. Volumes are created on demand, so a high slot cap costs nothing. (Same fix
        // as E2EApiFactory.)
        .WithCommand("server", "-dir=/data", "-s3", "-s3.port=8333", "-s3.config=/s3.json", "-volume.max=5000")
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
    private IContainer? _ocr;
    private string _ocrUrl = "";

    public string BaseUrl { get; private set; } = "";

    // When set (an ISO-8601 instant), the app's demo seed + audit recorder resolve "now" from a fixed clock instead
    // of the wall clock (ADR 0510) — so the manual-capture harness gets byte-stable audit/tasks/my-work screens.
    // The E2E fixtures leave this null and run against the real clock, exactly as before. Must be set before StartAsync.
    public string? DemoClock { get; set; }

    // When true, the OCR sidecar is built and started too, and Ocr:Url points at it — which is what draws the
    // external-link landing page's thumbnail (issue #476). Opt-in because building that image is slow (Debian +
    // tesseract language packs) and only the MANUAL capture needs it: the UI and desktop suites would pay the
    // build on every run for a figure they never take. Must be set before StartAsync.
    public bool WithOcrSidecar { get; set; }

    // The self-hosted app's Postgres — exposed so a caller can clean up data it seeded.
    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async Task StartAsync()
    {
        if (WithOcrSidecar)
        {
            // Built from the repo's own ocr/ Dockerfile rather than pulled: the thumbnail route is ours, so
            // there is no published image to use. Docker layer-caches it, so the cost lands on the first run.
            var image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(new CommonDirectoryPath(RepoRoot()), "ocr")
                .WithDockerfile("Dockerfile")
                .WithName("simplarchive-ocr-capture:latest")
                .WithCleanUp(false)
                .Build();
            await image.CreateAsync();

            _ocr = new ContainerBuilder()
                .WithImage(image)
                .WithPortBinding(8080, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health").ForStatusCode(HttpStatusCode.OK)))
                .Build();
        }

        await Task.WhenAll(_postgres.StartAsync(), _storage.StartAsync(), _openSearch.StartAsync(), _tika.StartAsync(), _gotenberg.StartAsync());
        if (_ocr is not null)
        {
            await _ocr.StartAsync();
            _ocrUrl = $"http://{_ocr.Hostname}:{_ocr.GetMappedPublicPort(8080)}";
        }
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
        StageLibvipsNatives(repoRoot);

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
            // The retention sweep's first run must land beyond any test leg's lifetime. Its default
            // app-start+3min fired mid-leg on CI (a leg runs ~5–6 min there) and silently disposed a test's
            // overdue-at-birth document between its API setup and its first UI assertion — four consecutive
            // CI failures that never reproduced locally, because a local leg finishes in ~2.5 min. A test
            // that wants the sweep exercises IRetentionService.SweepAsync directly; no test wants it firing
            // on a wall clock it cannot see.
            ["Retention__InitialDelay"] = "02:00:00",
            ["App__BaseUrl"] = BaseUrl, // blazor-client redirect URIs must match the served origin for login
            // The IMAP endpoint (ADR 0594) on an ephemeral plaintext port, so the account dialog is fully
            // exercisable (available=true) without a fixed port racing parallel fixtures.
            ["Imap__Enabled"] = "true",
            ["Imap__Port"] = "-1",
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
        // Deterministic capture (ADR 0510): a fixed demo clock only when the caller asked for one.
        if (!string.IsNullOrWhiteSpace(_ocrUrl))
        {
            env["Ocr__Url"] = _ocrUrl;
        }

        if (!string.IsNullOrWhiteSpace(DemoClock))
        {
            env["Demo__Clock"] = DemoClock;
        }
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

    /// <summary>
    /// Copies this test host's version-matched libvips natives next to the Api's build output, so the Api
    /// SUBPROCESS can do TIFF renditions. The product deliberately ships only the linux-musl natives its
    /// Alpine image needs, which means a self-hosted Api on a developer Mac or a glibc CI runner has no
    /// libvips at all — every TIFF preview/preview-pages request logged a DllNotFoundException and answered
    /// 204, silently, until #522's sort-thumbnail test became the first to depend on one. The natives come
    /// from this project's own test-only NetVips.Native references (the accepted, version-pinned exception —
    /// never a distro libvips, which shipped an incompatible version that crashed the test host), and dlopen's
    /// first probe is exactly the Api's output directory, which sidesteps macOS stripping DYLD_* for hardened
    /// binaries. Copy-if-different keeps reruns cheap; bin/ is gitignored, so nothing ships.
    /// </summary>
    private static void StageLibvipsNatives(string repoRoot)
    {
        var rid = $"{(OperatingSystem.IsMacOS() ? "osx" : "linux")}-{(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64")}";
        var source = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native");
        if (!Directory.Exists(source))
        {
            return; // an unanticipated platform: the Api runs, TIFF renditions stay 204 — as before
        }

        var target = Path.Combine(repoRoot, "src", "SimplArchive.Api", "bin", "Debug", "net10.0");
        if (!Directory.Exists(target))
        {
            return; // no build output yet; the dotnet run below would fail anyway with a clearer message
        }

        foreach (var file in Directory.GetFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination) || new FileInfo(destination).Length != new FileInfo(file).Length)
            {
                File.Copy(file, destination, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Everything the Api subprocess has written so far — public because a test diagnosing a server-side "no
    /// content" answer needs the server's own account of why (the #522 empty-sort-dialog hunt: a 204 from
    /// preview-pages carries no reason, but the request log line does).
    /// </summary>
    public string ApiLog()
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
        if (_ocr is not null)
        {
            await _ocr.DisposeAsync();
        }
        await _tika.DisposeAsync();
        await _openSearch.DisposeAsync();
        await _storage.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
