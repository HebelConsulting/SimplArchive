using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The roots a user can file into (ADR 0689). Written against the shared rule rather than either client,
// because the defect was that the two clients answered it in four places and three of them left the personal
// space out entirely — so the Move dialog offered every shared repository and not the user's own.
public class FilingRootsTests
{
    private sealed record Root(string Name);

    [Fact]
    public void The_personal_space_comes_first_and_the_shared_ones_are_alphabetical()
    {
        var roots = FilingRoots.Compose(
            new Root("Demo Admin"), [new Root("Zebra"), new Root("acme"), new Root("Contracts")], r => r.Name);

        // Case-insensitively alphabetical, and the personal space pinned above them rather than sorted among
        // them — it is the one root that is always the same user's, so it is always in the same place.
        Assert.Equal(["Demo Admin", "acme", "Contracts", "Zebra"], roots.Select(r => r.Node.Name));
    }

    [Fact]
    public void The_personal_root_is_not_itself_a_target()
    {
        var roots = FilingRoots.Compose(new Root("Demo Admin"), [new Root("Contracts")], r => r.Name);

        // A personal space's first level is provisioned, not user-filled (#634): filing into the root itself is
        // refused by the server, so the picker must not offer it as a destination. It is still SHOWN and still
        // expands — what a user wants is one of the folders inside it.
        Assert.False(roots[0].Selectable);
        Assert.True(roots[1].Selectable);
    }

    [Fact]
    public void A_user_without_a_personal_space_simply_gets_the_shared_ones()
    {
        var roots = FilingRoots.Compose<Root>(null, [new Root("Contracts")], r => r.Name);

        // The control: no personal space must not mean no roots, and must not leave a hole at the top.
        Assert.Equal(["Contracts"], roots.Select(r => r.Node.Name));
        Assert.True(roots[0].Selectable);
    }
}
