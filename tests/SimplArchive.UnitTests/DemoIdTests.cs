using SimplArchive.Api.Imap;
using SimplArchive.Api.Provisioning;

namespace SimplArchive.UnitTests;

// The demo seed's identities are a PROMISE across releases (#781): the kiosk reseeds nightly, and a caching
// client (IMAP, DAV, WebDAV) survives that only while the same slugs keep producing the same GUIDs. So these
// tests pin GOLDEN values as literals — computed independently with a reference RFC 4122 v5 implementation,
// so they also cross-validate the byte-order handling, which is the classic way a home-grown v5 goes wrong.
//
// If a change here fails these tests, the test is not stale: the change breaks every client cache and every
// bookmarked id on the next kiosk reset, and needs to be exactly that deliberate.
public class DemoIdTests
{
    private static readonly Guid Root = DemoId.Root("Demo");

    [Theory]
    [InlineData("repository", "7e5f02af-67e7-51f0-9482-e0882d3e54bc")]
    [InlineData("user/admin", "08318556-9d37-58b4-8909-c7d8135c9c39")]
    [InlineData("doc/DemoInvoice", "b0a55acd-bd88-5446-a194-8526b6ea6a35")]
    [InlineData("folder/contracts/acme-corp", "39029ca2-4943-5a55-8674-a93aa8cc9033")]
    [InlineData("personal/admin/my-addressbook", "789ad67f-843c-5817-aa8a-d404777e0cff")]
    public void Slugs_produce_their_golden_ids(string slug, string expected)
        => Assert.Equal(Guid.Parse(expected), DemoId.For(Root, slug));

    [Fact]
    public void The_root_is_its_golden_id()
        => Assert.Equal(Guid.Parse("746a22de-2d1c-5b70-8888-ea12c0c8ffec"), Root);

    [Fact]
    public void A_second_tenant_name_is_a_different_id_family()
    {
        var other = DemoId.Root("Demo 2");
        Assert.NotEqual(Root, other);
        Assert.NotEqual(DemoId.For(Root, "repository"), DemoId.For(other, "repository"));
    }

    [Fact]
    public void The_ids_carry_the_version_5_marker()
    {
        // Version nibble 5, RFC variant — proof the derivation is the RFC one rather than an ad-hoc hash, which
        // is what lets the goldens be recomputed with any standard uuid5 implementation.
        var bytes = DemoId.For(Root, "repository").ToByteArray();
        Assert.Equal(0x50, bytes[7] & 0xF0);        // version (little-endian field order in ToByteArray)
        Assert.Equal(0x80, bytes[8] & 0xC0);        // variant
    }

    [Fact]
    public void Uidvalidity_is_positive_and_derived_from_the_folder()
    {
        var folder = Guid.Parse("39029ca2-4943-5a55-8674-a93aa8cc9033");
        var value = ImapMailboxes.UidValidityFor(folder);
        Assert.True(value > 0);
        Assert.Equal(value, ImapMailboxes.UidValidityFor(folder));
        Assert.NotEqual(value, ImapMailboxes.UidValidityFor(Guid.Parse("7e5f02af-67e7-51f0-9482-e0882d3e54bc")));
        Assert.True(ImapMailboxes.UidValidityFor(Guid.Empty) > 0); // the all-zero corner must still be a legal UIDVALIDITY
    }

    // The call-site half of the promise: an id minted with Guid.NewGuid() anywhere in the demo seeders is an
    // identity the nightly reseed will churn, invisible in any single run. Zero is the only number this scan
    // may find — a new seeded entity derives its id from a slug (see DemoId) or composes it from ids that do.
    [Theory]
    [InlineData("src/SimplArchive.Api/Provisioning/DemoDataSeeder.cs")]
    [InlineData("src/SimplArchive.Api/Provisioning/DemoArtistsSeeder.cs")]
    public void The_demo_seeders_mint_no_random_ids(string file)
    {
        var path = Path.Combine(RepoRoot(), file);
        Assert.True(File.Exists(path), $"{file} not found — if the seeder moved, update this test.");
        Assert.DoesNotContain("Guid.NewGuid", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent!;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
