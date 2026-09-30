using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// A module's projection hook must be able to SEE the document it was called about (ABI 1.1, ADR 0848).
//
// WHY THIS EXISTS, and why it had to be an E2E test. The hook runs inside the core's SaveChangesAsync, and the
// dispatcher gives it a DI scope of its own — it must, or a module would get the core context whose
// ChangeTracker holds the save in progress. A fresh scope means a fresh DbContext, which takes its own
// connection from the pool; the save's rows are written but NOT committed, so on any other connection they do
// not exist. The hook asked the facade for its own subject, was told there is no such document, took its
// "nothing to derive" branch, and returned having written nothing and logged nothing.
//
// Nothing caught it because every existing test stops at a FAKE dispatcher (ModuleProjectionHookTests asserts
// that a change is COLLECTED, which was never the broken half) and because the SQLite integration rig has no
// DI scope to create — the defect is in the scope, so only a host can reproduce it. Measured on the demo
// stack: a certificate enrolled through the encryption module's own surface never reached its read model, so
// the module answered "this reader holds no certificate" — and because a module that answers is the ONLY
// source (ADR 0842), activating the module REMOVED the reader's access. The tell was that a rebuild fixed it
// every single time: a rebuild is its own request, after the commit, where the rows are simply there.
//
// It asserts the NAME the hook read back, not that a row appeared. "The hook ran" and "the hook could do its
// job" are different facts, and the whole defect lives between them.
[Collection(E2ECollection.Name)]
// e2e-1, the lighter leg — UiLegBalanceTests requires every class in a split suite to name its leg.
[Trait("Area", "e2e-1")]
public class ModuleProjectionDispatchTests
{
    private readonly E2EApiFactory _factory;

    public ModuleProjectionDispatchTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_projection_hook_sees_the_document_that_triggered_it_with_no_rebuild()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Proj {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"projadmin-{Guid.NewGuid():N}@e2e.local";
        const string password = "projadmin-1234";
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Projection Admin",
            isTenantAdmin: true, canManageServiceAccounts: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(owner, admin, tenantId, repoId, vendorKey);

            // CONSENT for the module principal (ADR 0736). The facade filters by ordinary ACL grants, and an
            // ungranted read answers EMPTY rather than failing — so without this the hook would read null for
            // a perfectly visible document and this test would pass for the wrong reason once, then stop
            // being able to tell the two causes apart. Granted deliberately, so a null here means the
            // CONNECTION, not the consent gate.
            var principalId = (await TestJson.Get(admin, "/api/service-accounts"))
                .GetProperty("serviceAccounts").EnumerateArray()
                .Single(sa => sa.GetProperty("name").GetString() == "Module: Test Module")
                .GetProperty("id").GetGuid();
            await TestJson.Put(admin, $"/api/documents/{repoId}/acl-entries/service-accounts/{principalId}",
                new { canSee = true, canReadContent = true, canCreateSubItems = true });

            // THE EXPERIMENT: a document created with its mask in ONE save — the shape every create endpoint
            // uses, and the one where nothing is committed yet when the hook runs.
            var name = $"Projected cert {Guid.NewGuid():N}";
            var documentId = (await TestJson.Post(admin,
                $"/api/test-module/certificates?parentId={repoId}&name={Uri.EscapeDataString(name)}", new { }))
                .GetProperty("id").GetGuid();

            var probe = await ProbeAsync(documentId);

            Assert.NotNull(probe);
            Assert.Equal(name, probe!.Value.SawName);
            Assert.False(probe.Value.Removed);

            // AND THE OTHER DIRECTION, because a removal is the case ADR 0848 was actually written for: a
            // revoked certificate that goes on being addressed. The soft delete modifies a COMMITTED row, so
            // it would pass even with the connection defect — it is here to pin the removed flag, not the
            // visibility.
            var etag = (await owner.GetAsync($"/api/documents/{documentId}")).Headers.ETag!.Tag;
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{documentId}");
            delete.Headers.TryAddWithoutValidation("If-Match", etag);
            (await owner.SendAsync(delete)).EnsureSuccessStatusCode();

            var afterDelete = await ProbeAsync(documentId);
            Assert.NotNull(afterDelete);
            Assert.True(afterDelete!.Value.Removed,
                "A soft-deleted projected document did not reach the hook as removed, so a module cannot "
                + "learn that a certificate it addresses has been withdrawn (ADR 0848).");
            Assert.True(afterDelete.Value.Calls >= 2,
                $"The hook was called {afterDelete.Value.Calls} time(s); the create and the delete are two.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    /// <summary>What the hook recorded for one document, or null when it recorded nothing.</summary>
    /// <remarks>
    /// <para>
    /// Read with SQL rather than through <c>TestReadModelContext</c>, and that is forced rather than chosen:
    /// the loader gives a module its own <c>AssemblyLoadContext</c> (ADR 0741), so the context type the HOST
    /// registered is a different <c>Type</c> from the one this test project references — asking DI for it
    /// answers "no service for type TestReadModelContext" while the module's own is registered and working.
    /// The table is in the same database on the core's connection (ADR 0738), so the honest way in is the
    /// table.
    /// </para>
    /// <para>
    /// Deliberately NOT through a rebuild endpoint: a rebuild is what masked this defect for weeks, so a test
    /// that triggers one cannot see it.
    /// </para>
    /// </remarks>
    private async Task<(string SawName, bool Removed, int Calls)?> ProbeAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"SawName\", \"Removed\", \"Calls\" FROM tm_projection_probes WHERE \"DocumentId\" = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = documentId;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetString(0), reader.GetBoolean(1), reader.GetInt32(2))
            : null;
    }

    private async Task ActivateAsync(HttpClient owner, HttpClient admin, Guid tenantId, Guid repoId, ECDsa vendorKey)
    {
        var license = new TenantLicense(["test-module"], tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
            ModuleAbiVersion.Major, string.Empty).Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        await TestJson.Put(admin, "/api/modules/test-module/license", new { licenseDocumentId = docId });
    }
}
