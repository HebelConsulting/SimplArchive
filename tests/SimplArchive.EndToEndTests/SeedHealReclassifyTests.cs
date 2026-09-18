using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// A document filed before auto-classification existed can be healed into the state a fresh filing produces
// (#1272) — and nothing else is touched.
//
// WHY THIS EXISTS. An idempotent seeder only ever touches rows it creates, so a long-lived volume keeps
// whatever shape it was first written with. The demo's e-mail showed a date with NO TIME for ten days: not a
// seeder bug — a fresh volume gets the eMail mask, five index fields and 07:14 from the header — but a row
// written before the seeder was routed through DocumentFinalizer, which nothing would ever revisit.
//
// WHAT IS ACTUALLY AT RISK HERE. Not the repair; the SCOPE. A heal that reached one document too far would
// overwrite something a person wrote, and that is a data-loss bug wearing a fix's clothes. So the second case
// matters at least as much as the first: an already-classified document must come back untouched.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SeedHealReclassifyTests
{
    private readonly E2EApiFactory _factory;

    public SeedHealReclassifyTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_unclassified_email_is_healed_into_what_a_fresh_filing_produces()
    {
        var (api, docId) = await UploadEmailAsync();

        // It arrives classified, which is the state the heal has to reach.
        var (maskBefore, fieldsBefore, timeBefore) = await InspectAsync(docId);
        Assert.NotEqual("Basic Entry", maskBefore);
        Assert.True(fieldsBefore > 0);
        Assert.NotNull(timeBefore);

        // Wind it back to the pre-classification shape a ten-day-old volume actually holds: a generic mask, no
        // index values, no time. Not a synthetic state — it is exactly what the demo's e-mail looked like.
        await UnclassifyAsync(docId);
        var (maskStripped, fieldsStripped, timeStripped) = await InspectAsync(docId);
        Assert.Equal("Basic Entry", maskStripped);
        Assert.Equal(0, fieldsStripped);
        Assert.Null(timeStripped);

        Assert.True(await HealAsync(docId), "the heal must report that it classified the document");

        var (mask, fields, time) = await InspectAsync(docId);
        Assert.Equal(maskBefore, mask);
        Assert.Equal(fieldsBefore, fields);

        // The VALUE, not merely its presence: 08:14 +0100 is 07:14 UTC, and only a non-zero offset can tell
        // "we read the header" from "we stamped something".
        Assert.Equal(new TimeOnly(7, 14), time);

        GC.KeepAlive(api);
    }

    [Fact]
    public async Task An_already_classified_document_is_left_alone()
    {
        // The half that protects data rather than fixing it. Re-running the heal over a document that is
        // already classified must be a no-op — if it reclassified, every heal would overwrite whatever the
        // current content says over whatever a person has since corrected.
        var (api, docId) = await UploadEmailAsync();

        Assert.False(await HealAsync(docId),
            "a classified document must not be reclassified — a heal that re-runs on healthy rows is not a "
            + "heal, it is an overwrite on a schedule.");

        GC.KeepAlive(api);
    }

    private async Task<bool> HealAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var finalizer = scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.DocumentFinalizer>();
        var version = await db.DocumentVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.DocumentId == documentId).OrderByDescending(v => v.VersionNumber).FirstAsync();

        // The ambient tenant, exactly as the seeder establishes it: classification reads the document through
        // the tenant query filter, so with no tenant set it matches nothing and throws from inside. Driving the
        // heal the way its real caller does is the point — a test that bypassed this would have passed while
        // the startup path threw.
        scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.CurrentTenantAccessor>()
            .TenantId = version.TenantId;

        return await finalizer.ReclassifyAsync(version, CancellationToken.None);
    }

    private async Task UnclassifyAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        var document = await db.Documents.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(d => d.Id == documentId);
        var basicEntry = await db.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(mv => mv.TenantId == document.TenantId
                && mv.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.BasicEntry && mv.IsCurrent)
            .Select(mv => mv.Id).SingleAsync();
        document.MaskVersionId = basicEntry;

        db.FieldValues.RemoveRange(await db.FieldValues.IgnoreQueryFilters(["TenantFilter"])
            .Where(f => f.DocumentId == documentId).ToListAsync());

        foreach (var version in await db.DocumentVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.DocumentId == documentId).ToListAsync())
        {
            version.DocumentTime = null;
        }

        await db.SaveChangesAsync();
    }

    private async Task<(string Mask, int Fields, TimeOnly? Time)> InspectAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        var document = await db.Documents.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(d => d.Id == documentId);
        var mask = await db.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(mv => mv.Id == document.MaskVersionId).Select(mv => mv.Name).SingleAsync();
        var fields = await db.FieldValues.IgnoreQueryFilters(["TenantFilter"])
            .CountAsync(f => f.DocumentId == documentId);
        var time = await db.DocumentVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.DocumentId == documentId).OrderByDescending(v => v.VersionNumber)
            .Select(v => v.DocumentTime).FirstAsync();

        return (mask, fields, time);
    }

    private async Task<(HttpClient Api, Guid DocumentId)> UploadEmailAsync()
    {
        var email = $"seedheal-{Guid.NewGuid():N}@e2e.local";
        const string password = "seedhealpw1234";
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        await _factory.SeedUserAsync(tenantId, email, password, "Seed Heal", canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Heal {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = $"heal-{Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".eml" });

        var rfc822 = "From: billing@citytransit.example\r\n"
            + "To: someone@e2e.local\r\n"
            + $"Subject: Heal probe {Guid.NewGuid():N}\r\n"
            + "Date: Mon, 09 Feb 2026 08:14:00 +0100\r\n"
            + $"Message-ID: <{Guid.NewGuid():N}@e2e.local>\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n\r\nBody.\r\n";

        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(rfc822)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        return (api, docId);
    }
}
