using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// An index field offers the values already filed under it, so an editor can complete rather than ask the user
// to remember (#1127).
//
// Reported from use, of a module's Aerodrome field: core renders every textual field as a free-text box, and a
// SingleSelect has no stored option list anywhere — its choices ARE the values in use, which is exactly what
// search already facets on. So the suggestions come from there rather than from a new concept on the mask.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class FieldValueSuggestionTests
{
    private readonly E2EApiFactory _factory;

    public FieldValueSuggestionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_text_field_suggests_the_values_already_filed_under_it()
    {
        // A USER promoted to tenant admin: authoring a mask needs CanManageMasks, which the service-account
        // seed does not grant.
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"fv-{Guid.NewGuid():N}@e2e.local";
        const string password = "fieldval-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Field Values", canManageRepositories: true);
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var maskName = $"Aerodrome {Guid.NewGuid():N}"[..20];
        var mask = await TestJson.Post(api, "/api/masks", new
        {
            name = maskName,
            // The enum goes over the wire as its NUMBER, and the order is
            // Text=0, Number=1, Date=2, DateTime=3, Boolean=4, SingleSelect=5, MultiSelect=6 — DateTime was
            // inserted in the middle (#660), which shifted everything after it. Written out because the first
            // version of this test used the pre-#660 mapping, asked for a Boolean, and got no suggestions rel
            // — correctly, since completing a boolean means nothing.
            fields = new[] { new { name = "ICAO", dataType = 5, isRequired = false } },
        });
        var maskId = mask.GetProperty("id").GetGuid();

        // The rel IS the affordance (ADR 0543) — an editor gates its autocomplete on the field advertising it.
        var field = mask.GetProperty("fields").EnumerateArray().Single();
        var fieldId = field.GetProperty("id").GetGuid();
        var valuesHref = field.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "values")
            .GetProperty("href").GetString()!;

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Ops {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        foreach (var icao in new[] { "LSZB", "LSZH", "EDDF" })
        {
            // Created bare, then MASKED: the children endpoint accepts only folder masks, and this is an
            // ordinary document wearing a tenant-authored one. Mask last is also the order required
            // elsewhere, because required-field validation fires on assignment (ADR 0176).
            var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children",
                new { name = $"Flight {icao} {Guid.NewGuid():N}"[..20] })).GetProperty("id").GetGuid();
            await TestJson.Put(api, $"/api/documents/{docId}/mask", new { maskId });
            await TestJson.Put(api, $"/api/documents/{docId}/index-data",
                new { fields = new[] { new { fieldDefinitionId = fieldId, values = new[] { icao } } } });
        }

        var all = (await TestJson.Get(api, valuesHref)).GetProperty("values").EnumerateArray()
            .Select(v => v.GetString()).ToList();
        Assert.Equal(["EDDF", "LSZB", "LSZH"], all);

        // ...and narrowed as the user types, which is the whole point: a vocabulary of hundreds is not a list
        // anybody scrolls.
        var narrowed = (await TestJson.Get(api, $"{valuesHref}?q=LSZ")).GetProperty("values").EnumerateArray()
            .Select(v => v.GetString()).ToList();
        Assert.Equal(["LSZB", "LSZH"], narrowed);

        api.Dispose();
    }
}
