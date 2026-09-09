using System.Net.Http.Json;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// The DocumentReference field type over the wire: a field whose value names another document, resolved
// server-side into a name and a followable address.
//
// The persistence tests cover what may be WRITTEN. What only a wire test can show is the READ half, and the
// read half is a permission surface: the same stored value must resolve to a name and a link for someone who
// may open the target, and to neither for someone who may not — while still showing that the field IS set,
// because the id is already in a field they are allowed to read.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentReferenceFieldApiTests
{
    private readonly E2EApiFactory _factory;

    public DocumentReferenceFieldApiTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_reference_resolves_to_a_name_and_a_followable_address()
    {
        var (owner, _, _) = await SchoolAsync();
        var (lessonId, flightId, fieldId, flightName, _, _) = await LessonPointingAtAFlightAsync(owner);

        var target = await TargetAsync(owner, lessonId);

        Assert.Equal(flightId, target.GetProperty("id").GetGuid());
        Assert.Equal(flightName, target.GetProperty("name").GetString());

        // The client follows this rel — it must never compose the address from the id (ADR 0543), which is
        // the whole reason the target is resolved here rather than there.
        var href = target.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "document").GetProperty("href").GetString()!;
        Assert.Equal(flightName, (await TestJson.Get(owner, href)).GetProperty("name").GetString());

        // The raw value is still the id: resolution ADDS to the wire shape, it does not replace it, so a
        // client that only knows about Values keeps working.
        Assert.Equal(flightId.ToString(), (await FieldAsync(owner, lessonId, fieldId)).GetProperty("values")
            .EnumerateArray().Single().GetString());
    }

    // The assertion this file exists for. A reader who may open the lesson but not the flight sees that the
    // field is set and nothing else — no name, no link. The NAME is what would leak: a person's name or a
    // case number is usually the sensitive part of the document the ACL is protecting.
    [Fact]
    public async Task A_target_the_caller_may_not_open_carries_no_name_and_no_link()
    {
        var (owner, tenantId, _) = await SchoolAsync();
        var (lessonId, flightId, _, flightName, lessonRepo, _) = await LessonPointingAtAFlightAsync(owner);

        // A reader granted rights on the LESSON's repository only — the flight lives in its own, ungranted.
        var email = $"reader-{Guid.NewGuid():N}@e2e.local";
        const string password = "reader1234";
        var readerId = await _factory.SeedUserAsync(tenantId, email, password, "Reader");
        await TestJson.Put(owner, $"/api/documents/{lessonRepo}/acl-entries/users/{readerId}",
            new { canSee = true, canReadContent = true });
        var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var target = await TargetAsync(reader, lessonId);

        // Still the id — they already hold it, it is in a field they may read — and that is the honest fact
        // that the field is set. Anything more would be the leak.
        Assert.Equal(flightId, target.GetProperty("id").GetGuid());
        Assert.True(target.GetProperty("name").ValueKind == System.Text.Json.JsonValueKind.Null,
            "the name of a document the caller may not open must not travel");
        Assert.Empty(target.GetProperty("links").EnumerateArray());

        // And not by any other route in the same response: the flight's name must appear nowhere in it.
        var whole = (await TestJson.Get(reader, $"/api/documents/{lessonId}/index-data")).GetRawText();
        Assert.DoesNotContain(flightName, whole, StringComparison.Ordinal);
    }

    // Anti-vacuous: the previous test would pass against an implementation that resolved NOTHING for anyone.
    // Same reader, same request shape, a target they CAN open — so the two together show the difference is
    // the rights and not the code path.
    [Fact]
    public async Task The_same_reader_sees_a_target_they_are_allowed_to_open()
    {
        var (owner, tenantId, _) = await SchoolAsync();
        var (lessonId, _, _, flightName, lessonRepo, flightRepo) = await LessonPointingAtAFlightAsync(owner);

        var email = $"reader-{Guid.NewGuid():N}@e2e.local";
        const string password = "reader1234";
        var readerId = await _factory.SeedUserAsync(tenantId, email, password, "Reader");
        await TestJson.Put(owner, $"/api/documents/{lessonRepo}/acl-entries/users/{readerId}", new { canSee = true, canReadContent = true });
        await TestJson.Put(owner, $"/api/documents/{flightRepo}/acl-entries/users/{readerId}", new { canSee = true, canReadContent = true });
        var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var target = await TargetAsync(reader, lessonId);

        Assert.Equal(flightName, target.GetProperty("name").GetString());
        Assert.Single(target.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task A_value_that_does_not_name_a_document_is_refused_at_the_edge()
    {
        var (owner, _, _) = await SchoolAsync();
        var (lessonId, _, fieldId, _, _, _) = await LessonPointingAtAFlightAsync(owner);

        var response = await owner.PutAsJsonAsync($"/api/documents/{lessonId}/index-data", new
        {
            fields = new object[] { new { fieldDefinitionId = fieldId, values = new[] { Guid.NewGuid().ToString() } } },
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(HttpClient Owner, Guid TenantId, string Email)> SchoolAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"school-{Guid.NewGuid():N}@e2e.local";
        const string password = "school1234";
        await _factory.SeedUserAsync(tenantId, email, password, "School", canManageRepositories: true);
        await _factory.GrantTenantAdminAsync(email);   // carries CanManageMasks
        return (_factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password)), tenantId, email);
    }

    /// <summary>A lesson record whose Flight field names a flight filed in a DIFFERENT repository — which is
    /// what lets the ACL half be tested by granting one and not the other.</summary>
    private static async Task<(Guid LessonId, Guid FlightId, Guid FieldId, string FlightName, Guid LessonRepo, Guid FlightRepo)> LessonPointingAtAFlightAsync(HttpClient api)
    {
        // The create endpoint takes the data type as its enum ORDINAL while the read returns its name — the
        // pre-existing asymmetry the list/e-mail tests document, hence the cast.
        var mask = await TestJson.Post(api, "/api/masks", new
        {
            name = $"Lesson record {Guid.NewGuid():N}",
            fields = new object[] { new { name = "Flight", dataType = (int)FieldDataType.DocumentReference, isRequired = false, isList = false } },
        });
        var fieldId = mask.GetProperty("fields").EnumerateArray().Single().GetProperty("id").GetGuid();

        var lessonRepo = (await TestJson.Post(api, "/api/repositories", new { name = $"Training {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var flightRepo = (await TestJson.Post(api, "/api/repositories", new { name = $"Fleet {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var flightName = $"HB-PHG {Guid.NewGuid():N}"[..16];
        var flightId = (await TestJson.Post(api, $"/api/documents/{flightRepo}/children", new { name = flightName })).GetProperty("id").GetGuid();
        var lessonId = (await TestJson.Post(api, $"/api/documents/{lessonRepo}/children", new { name = $"Lesson {Guid.NewGuid():N}"[..12] })).GetProperty("id").GetGuid();

        await TestJson.Put(api, $"/api/documents/{lessonId}/mask", new { maskId = mask.GetProperty("id").GetGuid() });
        await TestJson.Put(api, $"/api/documents/{lessonId}/index-data", new
        {
            fields = new object[] { new { fieldDefinitionId = fieldId, values = new[] { flightId.ToString() } } },
        });

        // The repositories come back with the rest: a document resource advertises its parent as a REL, not
        // as a parentId field, so a caller that wants the id must either follow that rel or be handed it.
        return (lessonId, flightId, fieldId, flightName, lessonRepo, flightRepo);
    }

    private static async Task<System.Text.Json.JsonElement> FieldAsync(HttpClient api, Guid documentId, Guid fieldId) =>
        (await TestJson.Get(api, $"/api/documents/{documentId}/index-data")).GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("fieldDefinitionId").GetGuid() == fieldId);

    private static async Task<System.Text.Json.JsonElement> TargetAsync(HttpClient api, Guid documentId) =>
        (await TestJson.Get(api, $"/api/documents/{documentId}/index-data")).GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("dataType").GetString() == nameof(FieldDataType.DocumentReference))
            .GetProperty("targets").EnumerateArray().Single();
}
