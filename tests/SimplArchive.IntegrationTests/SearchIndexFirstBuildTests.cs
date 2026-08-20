using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Search;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.IntegrationTests;

// When the search alias is published decides whether search works AT ALL while an index is being built.
//
// Every per-document write is gated on the alias existing, so for as long as a build withholds it, nothing is
// searchable and the indexing outbox simply spins. Blue-green is worth that on a REBUILD — a running search
// must never be served a half-built index — but on a FIRST build it is guarding an empty room, and the cost is
// the whole corpus's extraction time: ~1 s on a developer machine, over five minutes on a 2-core CI runner
// once the demo seed grew, which is what failed nine search tests in #660.
//
// So the rule is "publish early on a first build, late on a rebuild", and it is asserted by the ORDER of the
// HTTP calls, because ordering is the entire content of the decision — a test that merely checked the alias
// exists afterwards would pass just as happily on the behaviour this replaces.
public class SearchIndexFirstBuildTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    /// <summary>Records the path of every request, and answers the alias probe as a caller decides.</summary>
    private sealed class RecordingHandler(bool aliasExists) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Calls.Add($"{request.Method} {path}");

            // The alias probe: 404 means "no alias yet" — a first build. Once the rebuild has POSTed _aliases,
            // report it as present, so SwapAliasAsync's own lookup behaves like the real thing.
            var probing = request.Method == HttpMethod.Get && path == "_alias/documents";
            var published = Calls.Any(c => c == "POST _aliases");
            var code = probing && !(aliasExists || published) ? HttpStatusCode.NotFound : HttpStatusCode.OK;

            return Task.FromResult(new HttpResponseMessage(code)
            {
                // `_alias/documents` and `_cat/indices` are both read as JSON; an empty object/array satisfies both.
                Content = new StringContent(path.StartsWith("_cat/", StringComparison.Ordinal) ? "[]" : "{}",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    /// <summary>
    /// One tenant and one FOLDER. A folder is deliberate: it has no version, so the rebuild builds its body
    /// without reaching object storage or a text extractor, and the stubs below are never called rather than
    /// being made to lie.
    /// </summary>
    private async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor)> SeededAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };

        await using var db = Ctx(connection, accessor);
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Rebuild", Status = TenantStatus.Active });

        // A creator is required — CK_Documents_ExactlyOneCreator wants exactly one of user/service account.
        var creatorId = Guid.NewGuid();
        db.Users.Add(new User { Id = creatorId, TenantId = _tenantId, Email = "rebuild@e2e.local", DisplayName = "Rebuild" });
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Name = "A folder",
            ParentId = null,
            CreatedByUserId = creatorId,
        });
        await db.SaveChangesAsync();

        return (connection, accessor);
    }

    private static OpenSearchIndexRebuilder Rebuilder(HttpClient http, SimplArchiveDbContext db, CurrentTenantAccessor accessor) =>
        new(http, db, accessor,
            new InMemoryObjectStorage(), new NullTextExtractor(), new ZipArchiveReader(),
            new EffectiveRightsCalculator(db), NullLogger<OpenSearchIndexRebuilder>.Instance);

    private async Task<List<string>> RunAsync(bool aliasExists)
    {
        var (connection, accessor) = await SeededAsync();
        using var _ = connection;
        await using var db = Ctx(connection, accessor);

        var handler = new RecordingHandler(aliasExists);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://opensearch.invalid/") };
        await Rebuilder(http, db, accessor).RebuildAsync(CancellationToken.None);
        return handler.Calls;
    }

    [Fact]
    public async Task A_first_build_publishes_the_alias_before_it_indexes_anything()
    {
        var calls = await RunAsync(aliasExists: false);

        var published = calls.IndexOf("POST _aliases");
        var firstDocument = calls.FindIndex(c => c.Contains("/_doc/", StringComparison.Ordinal));

        Assert.True(published >= 0, $"the alias was never published: {string.Join(" | ", calls)}");
        Assert.True(firstDocument >= 0, $"nothing was indexed: {string.Join(" | ", calls)}");

        // The whole point: searchable while it fills, rather than only once it is full.
        Assert.True(
            published < firstDocument,
            $"the alias was published only after indexing began: {string.Join(" | ", calls)}");
    }

    [Fact]
    public async Task A_rebuild_of_a_live_index_still_swaps_only_at_the_end()
    {
        var calls = await RunAsync(aliasExists: true);

        var published = calls.IndexOf("POST _aliases");
        var lastDocument = calls.FindLastIndex(c => c.Contains("/_doc/", StringComparison.Ordinal));

        Assert.True(published >= 0, $"the alias was never swapped: {string.Join(" | ", calls)}");

        // Unchanged behaviour, and the half of the rule that is easy to break while fixing the other half: a
        // running search must never be pointed at an index that is still filling.
        Assert.True(
            published > lastDocument,
            $"a live index was swapped before it was fully built: {string.Join(" | ", calls)}");
    }

    // Never reached: a folder has no version, so the rebuild never asks for content. The shared in-memory store
    // and the real zip reader stand in rather than a hand-written double per interface.
    private sealed class NullTextExtractor : ITextExtractor
    {
        public Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }
}
