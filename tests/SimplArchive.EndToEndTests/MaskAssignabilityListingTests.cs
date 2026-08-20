using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// A mask says how it can be assigned (#671), over the wire — which is where it matters, since the whole point
// is that both clients read the SAME answer instead of each deriving one.
//
// #580 is the symptom this closes: the pickers listed every tenant mask, including ones the containment
// invariant refuses, so a user could choose a mask that cannot work and learn about it from a failed save.
[Collection(E2ECollection.Name)]
public class MaskAssignabilityListingTests
{
    private readonly E2EApiFactory _factory;

    public MaskAssignabilityListingTests(E2EApiFactory factory) => _factory = factory;

    private async Task<HttpClient> UserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"masks-{Guid.NewGuid():N}@e2e.local";
        const string password = "masks-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Mask Reader");
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
    }

    private static Dictionary<string, JsonElement> ByName(JsonElement listing) =>
        listing.GetProperty("masks").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m);

    [Fact]
    public async Task The_listing_says_which_masks_type_a_folder_and_which_are_claimed_by_an_extension()
    {
        using var api = await UserAsync();
        var masks = ByName(await TestJson.Get(api, "/api/masks"));

        // A folder mask says so — and, the half that gives it meaning, an item mask does not.
        Assert.True(masks["Addressbook"].GetProperty("isFolderMask").GetBoolean());
        Assert.True(masks["Calendar"].GetProperty("isFolderMask").GetBoolean());
        Assert.False(masks["Basic Entry"].GetProperty("isFolderMask").GetBoolean());
        Assert.False(masks["Contact"].GetProperty("isFolderMask").GetBoolean());

        // The extensions that make a mask automatic, visible to a picker for the first time — the mapping used
        // to live only inside the classifier, where nothing that draws a menu could reach it.
        var contact = masks["Contact"].GetProperty("fileExtensions").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal([".vcf"], contact);

        var mail = masks["eMail"].GetProperty("fileExtensions").EnumerateArray().Select(e => e.GetString()).Order().ToList();
        Assert.Equal([".eml", ".msg"], mail);

        // A note is stored as .eml too, and is told from a mail by WHERE it is filed. So .eml belongs to eMail
        // and Note claims nothing — otherwise the unique index on (tenant, extension) could not hold.
        Assert.Empty(masks["Note"].GetProperty("fileExtensions").EnumerateArray());
    }

    [Fact]
    public async Task Only_freely_assignable_masks_are_offered_to_a_picker()
    {
        using var api = await UserAsync();
        var masks = ByName(await TestJson.Get(api, "/api/masks"));

        var assignable = masks
            .Where(m => m.Value.GetProperty("isFreelyAssignable").GetBoolean())
            .Select(m => m.Key)
            .Order()
            .ToList();

        // Today that is exactly Basic Entry: every other well-known mask either types a folder or is claimed by
        // an extension. Asserted as the whole SET rather than as "contains Basic Entry", because the bug being
        // fixed is one of over-offering — a test that only checks what is present cannot see it.
        Assert.Equal(["Basic Entry"], assignable);

        // And the ones that must never be offered, named individually so a regression says which.
        foreach (var refused in new[] { "Addressbook", "Calendar", "Notebook", "Section", "Mailbox", "Contact", "Appointment", "eMail", "Note", "Folder" })
        {
            Assert.False(
                masks[refused].GetProperty("isFreelyAssignable").GetBoolean(),
                $"'{refused}' was offered as freely assignable — a user could choose it and the save would be refused.");
        }
    }

    [Fact]
    public async Task A_folder_mask_advertises_no_create_rel_of_its_own()
    {
        using var api = await UserAsync();
        var masks = ByName(await TestJson.Get(api, "/api/masks"));

        // Creating a typed folder needs a PARENT, which this resource does not know. An href carrying a
        // {parentId} placeholder would be a template the client substitutes into — composing a URL wearing a
        // rel's clothes (ADR 0543). The affordance belongs on the document that would hold the new folder.
        var rels = masks["Addressbook"].GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()).ToList();

        Assert.Equal(["self"], rels);
    }
}
