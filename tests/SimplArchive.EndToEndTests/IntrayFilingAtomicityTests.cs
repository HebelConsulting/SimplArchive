using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// Filing from the intray deleted the intray object BEFORE the database work that can still refuse the filing
// (#1171). The finalizer is where the destination's admission rules become answerable (#634/#644), so a refusal
// arrived after the item had already left the intray AND after the document row had committed: the user was
// told no, lost the thing they were filing, and gained a document nobody asked for.
//
// It is now one transaction, and the intray copy is deleted only once that transaction has committed. So a
// refusal leaves the user exactly where they started, which is the only honest answer to "no".
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class IntrayFilingAtomicityTests
{
    private readonly E2EApiFactory _factory;

    public IntrayFilingAtomicityTests(E2EApiFactory factory) => _factory = factory;

    private static async Task<string[]> IntrayNamesAsync(HttpClient user) =>
        [.. (await TestJson.Get(user, "/api/intray")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!)];

    private async Task<(HttpClient User, Guid PersonalRootId, string ItemName)> IntrayItemAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"intray-atom-{Guid.NewGuid():N}@e2e.local";
        const string password = "intrayatom1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Intray Atom User");
        var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalRootId = (await TestJson.Post(user, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();

        var name = $"atom-{Guid.NewGuid():N}"[..12] + ".txt";
        var upload = await TestJson.Post(user, "/api/intray", new { fileName = name });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(
                upload.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("filed-atomically")))).EnsureSuccessStatusCode();
        }

        Assert.Contains(name, await IntrayNamesAsync(user));
        return (user, personalRootId, name);
    }

    [Fact]
    public async Task A_refused_filing_leaves_the_item_in_the_intray()
    {
        // THE case this change exists for. A personal space's first level holds only the folders it was
        // provisioned with (#634), so filing straight into its root is refused — by the FINALIZER, after the
        // rows have been staged. Before the fix that refusal still consumed the intray item.
        var (user, personalRootId, name) = await IntrayItemAsync();
        using var _u = user;

        var refused = await user.PostAsJsonAsync($"/api/intray/{name}/file", new { folderId = personalRootId });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("PERSONAL_SPACE_STRUCTURE", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Told no, and still holding what they had: the item is still in the intray…
        Assert.Contains(name, await IntrayNamesAsync(user));

        // …and no half-filed document was left behind in the destination.
        var children = await TestJson.Get(user, $"/api/documents/{personalRootId}/children");
        Assert.DoesNotContain(
            children.GetProperty("children").EnumerateArray(),
            c => c.GetProperty("name").GetString() == Path.GetFileNameWithoutExtension(name));
    }

    [Fact]
    public async Task A_filed_item_leaves_the_intray_and_arrives_as_a_document()
    {
        // The success half — which is what proves the delete still happens at all, now that it runs after the
        // commit rather than before the work.
        var (user, personalRootId, name) = await IntrayItemAsync();
        using var _u = user;

        // Into a provisioned folder, which the personal space does admit.
        var target = (await TestJson.Get(user, $"/api/documents/{personalRootId}/children"))
            .GetProperty("children").EnumerateArray().First().GetProperty("id").GetGuid();

        var filed = await TestJson.Post(user, $"/api/intray/{name}/file", new { folderId = target });
        var documentId = filed.GetProperty("id").GetGuid();

        var document = await TestJson.Get(user, $"/api/documents/{documentId}");
        Assert.Equal(Path.GetFileNameWithoutExtension(name), document.GetProperty("name").GetString());
        Assert.DoesNotContain(name, await IntrayNamesAsync(user));
    }
}
