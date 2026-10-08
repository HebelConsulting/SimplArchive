using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.TestModule;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// A document can be created WITH its index data in one request, and a refused create says why (#1634).
/// </summary>
/// <remarks>
/// A mask with a REQUIRED field inside an exclusive module folder could not be created through the API at all: the
/// child can only be created with its mask, and a create carried no values. The refusal also arrived as
/// <c>409 DOCUMENT_NAME_CONFLICT</c> for a name that existed nowhere, because the create mapped every SaveChanges
/// invariant to a name clash.
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class CreateWithIndexDataTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_case_with_a_required_field_is_created_with_its_value_and_refused_for_the_right_reason_without()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        var rig = await TestModuleRig.CreateAsync(factory);
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            var register = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Register {Guid.NewGuid():N}", maskId = TestModule.TestModule.RegisterMaskId })).GetProperty("id").GetGuid();
            var caseNumber = await FieldAsync(rig.TenantId, TestModule.TestModule.CaseMaskId, "Case number");

            // Without the required value: refused AS a missing required field, not as a name clash.
            var refused = await rig.Owner.PostAsJsonAsync($"/api/documents/{register}/children",
                new { name = "Case 1", maskId = TestModule.TestModule.CaseMaskId });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("REQUIRED_FIELD_MISSING", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

            // With it, in the same request: created, and the value is stored.
            var created = await TestJson.Post(rig.Owner, $"/api/documents/{register}/children", new
            {
                name = "Case 1",
                maskId = TestModule.TestModule.CaseMaskId,
                fields = new[] { new { fieldDefinitionId = caseNumber, values = new[] { "C-42" } } },
            });
            var caseId = created.GetProperty("id").GetGuid();
            var stored = (await TestJson.Get(rig.Owner, $"/api/documents/{caseId}/index-data")).GetProperty("fields")
                .EnumerateArray().Single(f => f.GetProperty("fieldDefinitionId").GetGuid() == caseNumber);
            Assert.Equal(["C-42"], stored.GetProperty("values").EnumerateArray().Select(v => v.GetString()));

            // A real duplicate is still the one name conflict.
            var duplicate = await rig.Owner.PostAsJsonAsync($"/api/documents/{register}/children", new
            {
                name = "Case 1",
                maskId = TestModule.TestModule.CaseMaskId,
                fields = new[] { new { fieldDefinitionId = caseNumber, values = new[] { "C-43" } } },
            });
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.Equal("DOCUMENT_NAME_CONFLICT", (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    private async Task<Guid> FieldAsync(Guid tenantId, Guid maskId, string name)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        return await db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent)
            .Join(db.FieldDefinitions, v => v.Id, f => f.MaskVersionId, (v, f) => f)
            .Where(f => f.Name == name).Select(f => f.Id).SingleAsync();
    }
}
