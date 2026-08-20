using SimplArchive.Client.Services;
using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests;

// The icon vocabulary is a WIRE CONTRACT with two independent implementations — the server names a thing
// ("calendar") and each client answers from its own icon set, because Material and Material Design Icons share
// no glyph name at all. Two tables that must agree is exactly the shape that drifts, and the drift is silent:
// a token with no glyph in one client draws the generic folder there and the right icon in the other, which
// nobody notices until they use both.
//
// So this asserts the WEB half against the server's vocabulary. The desktop half is asserted in its own suite
// (DesktopMaskIconTests) because this project cannot reference the Avalonia client.
//
// The failure mode this exists for is adding a mask with a token and forgetting one client, which compiles
// clean and produces no warning — the same class of thing as the two MudBlazor traps in #673.
public class MaskIconVocabularyTests
{
    [Fact]
    public void Every_token_the_server_ships_has_a_glyph_in_the_web_client()
    {
        var missing = WellKnownMaskIds.IconTokens.Values
            .Distinct()
            .Where(token => MaskIcon.Filled(token) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            $"The server ships icon token(s) the web client has no glyph for: {string.Join(", ", missing)}. "
            + "Add them to SimplArchive.Client/Services/MaskIcon.cs — an unmapped token silently falls back to "
            + "the generic folder glyph, so the mask looks right in the desktop client and wrong here.");
    }

    // The outline half is not decoration: an empty folder drops to it so "nothing here" is carried by shape
    // rather than colour alone (ADR "Folder icon scheme"). A token with a filled glyph and no outline one would
    // make an empty typed folder fall back to a plain outline folder — losing what the node IS, which is the
    // one thing that rule says emptiness must not cost.
    [Fact]
    public void Every_web_glyph_has_an_outline_partner()
    {
        var missing = WellKnownMaskIds.IconTokens.Values
            .Distinct()
            .Where(token => MaskIcon.Outlined(token) is null)
            .ToList();

        Assert.Empty(missing);
    }

    // Not every folder needs a token — Folder, My Documents and Basic Entry ARE the generic shapes, so a token
    // for them would be a second way to say what the default already says. Asserted so that "absent" reads as a
    // decision rather than as three masks somebody forgot.
    [Theory]
    [InlineData("Folder")]
    [InlineData("MyDocuments")]
    [InlineData("BasicEntry")]
    public void The_generic_shapes_deliberately_have_no_token(string mask)
    {
        var maskId = mask switch
        {
            "Folder" => WellKnownMaskIds.Folder,
            "MyDocuments" => WellKnownMaskIds.MyDocuments,
            _ => WellKnownMaskIds.BasicEntry,
        };

        Assert.False(WellKnownMaskIds.IconTokens.ContainsKey(maskId));
    }

    // An unknown token is the designed degradation, not an error: it is what lets the vocabulary grow without a
    // migration and without a CHECK constraint, and what stops a newer server blanking an older client's rows.
    [Fact]
    public void An_unknown_token_falls_back_rather_than_failing()
    {
        Assert.Null(MaskIcon.Filled("a-token-from-a-newer-server"));
        Assert.Null(MaskIcon.Outlined("a-token-from-a-newer-server"));
        Assert.Null(MaskIcon.Filled(null));
    }

    // Two masks drawn identically is a bug the eye catches and no other test does: an Addressbook and a
    // Calendar that share a glyph are two different things the user cannot tell apart in a tree.
    [Fact]
    public void No_two_masks_are_drawn_the_same()
    {
        var byGlyph = WellKnownMaskIds.IconTokens
            .GroupBy(t => MaskIcon.Filled(t.Value))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" + ", g.Select(t => t.Value)))
            .ToList();

        Assert.True(byGlyph.Count == 0, $"Masks sharing one glyph: {string.Join("; ", byGlyph)}");
    }
}
