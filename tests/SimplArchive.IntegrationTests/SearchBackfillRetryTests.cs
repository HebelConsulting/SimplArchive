using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Search;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.IntegrationTests;

// A rebuild that fails used to become one log line and stop there. The request had already been taken from the
// capacity-1 channel, so nothing retried — ever.
//
// When the failing attempt is the STARTUP backfill, that consequence is total and permanent: the alias is never
// created, every per-document write is gated off waiting for it, and the process serves an empty search for the
// rest of its life while answering each query with a cheerful zero hits. One 403 from OpenSearch on index
// creation wedged five CI legs exactly that way, and it presented as search tests timing out — which reads as
// flakiness rather than as "search never started" (#660/#661).
//
// So: a transient refusal must not be a permanent outage.
public class SearchBackfillRetryTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    /// <summary>Refuses the first <c>n</c> index creations with a 403, then behaves.</summary>
    private sealed class RefusingHandler(int refusals) : HttpMessageHandler
    {
        public int CreateAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var creating = request.Method == HttpMethod.Put && path.StartsWith("documents-", StringComparison.Ordinal)
                           && !path.Contains("/_doc/", StringComparison.Ordinal);

            if (creating && ++CreateAttempts <= refusals)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    // A real OpenSearch refusal carries its reason in the body — the part
                    // EnsureSuccessStatusCode used to throw away.
                    Content = new StringContent(
                        """{"error":{"type":"cluster_block_exception","reason":"blocked by: [FORBIDDEN/12/index read-only]"}}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
            }

            // The alias probe answers 404 until the swap has been POSTed, like the real thing.
            var probing = request.Method == HttpMethod.Get && path == "_alias/documents";
            var code = probing && CreateAttempts <= refusals ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(
                    path.StartsWith("_cat/", StringComparison.Ordinal) ? "[]" : "{}",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private async Task<ServiceProvider> ProviderAsync(HttpMessageHandler handler)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        await using (var seed = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, accessor))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Tenants.Add(new Tenant { Id = _tenantId, Name = "Retry", Status = TenantStatus.Active });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        services.AddSingleton<ICurrentTenantAccessor>(accessor);
        services.AddSingleton<SearchReindexState>();
        services.AddDbContext<SimplArchiveDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<IObjectStorageClient, InMemoryObjectStorage>();
        services.AddSingleton<ITextExtractor, NoText>();
        services.AddSingleton<IArchiveReader, ZipArchiveReader>();
        services.AddScoped<IEffectiveRightsCalculator, EffectiveRightsCalculator>();
        services.AddScoped(sp => new OpenSearchIndexRebuilder(
            new HttpClient(handler) { BaseAddress = new Uri("http://opensearch.invalid/") },
            sp.GetRequiredService<SimplArchiveDbContext>(),
            accessor,
            sp.GetRequiredService<IObjectStorageClient>(),
            sp.GetRequiredService<ITextExtractor>(),
            sp.GetRequiredService<IArchiveReader>(),
            sp.GetRequiredService<IEffectiveRightsCalculator>(),
            NullLogger<OpenSearchIndexRebuilder>.Instance));

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_refused_first_build_is_retried_until_it_succeeds()
    {
        var handler = new RefusingHandler(refusals: 2);
        await using var provider = await ProviderAsync(handler);

        var state = provider.GetRequiredService<SearchReindexState>();
        var service = new SearchReindexService(
            provider.GetRequiredService<IServiceScopeFactory>(), state, NullLogger<SearchReindexService>.Instance);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await service.StartAsync(stop.Token);

        // The startup backfill requests itself; wait for the third attempt — the one that is allowed to work.
        while (handler.CreateAttempts < 3 && !stop.IsCancellationRequested)
        {
            await Task.Delay(200, CancellationToken.None);
        }

        await service.StopAsync(CancellationToken.None);

        // Before this fix the count stuck at 1 forever, and search stayed empty for the life of the process.
        Assert.True(
            handler.CreateAttempts >= 3,
            $"the backfill gave up after {handler.CreateAttempts} attempt(s); a transient refusal became a permanent outage.");
    }

    [Fact]
    public async Task The_refusal_reports_what_OpenSearch_actually_said()
    {
        var handler = new RefusingHandler(refusals: 1);
        await using var provider = await ProviderAsync(handler);
        using var scope = provider.CreateScope();

        var thrown = await Assert.ThrowsAsync<SearchIndexOperationException>(
            () => scope.ServiceProvider.GetRequiredService<OpenSearchIndexRebuilder>().RebuildAsync(CancellationToken.None));

        // EnsureSuccessStatusCode threw "403 (Forbidden)" and discarded the only part that says WHY. That cost a
        // full CI round trip and two wrong diagnoses, because a refusal with no reason is indistinguishable from
        // any other refusal.
        Assert.Contains("403", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("cluster_block_exception", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("index read-only", thrown.Message, StringComparison.Ordinal);
    }

    private sealed class NoText : ITextExtractor
    {
        public Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }
}
