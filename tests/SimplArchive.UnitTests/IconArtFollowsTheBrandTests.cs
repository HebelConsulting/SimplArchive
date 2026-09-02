using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

/// <summary>
/// The launcher icon takes its colours from the design tokens, and keeps doing so (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// The icon is the half of a rebrand nobody remembers. An application whose window is one colour and whose Dock
/// tile is another looks unfinished in a way users notice before they can name it — and the failure is silent,
/// because the icon still renders perfectly, just in last year's colour.
/// </para>
/// <para>
/// <b>Deliberately not a byte comparison of the generated PNGs.</b> They come out of Skia, whose output moves
/// with its version, so a guard on the bytes would fail on an unrelated dependency bump — and a flaky guard is
/// worse than none, because the first thing anyone does with one is stop reading it. What is asserted instead
/// is the property that actually matters: the art names no colour of its own.
/// </para>
/// </remarks>
public partial class IconArtFollowsTheBrandTests
{
    [GeneratedRegex(@"#[0-9a-fA-F]{6,8}")]
    private static partial Regex HexColour();

    [Fact]
    public void The_icon_art_names_no_colour_of_its_own()
    {
        var path = Path.Combine(RepoPaths.Root(), "src", "SimplArchive.DesktopClient", "IconArt.cs");
        Assert.True(File.Exists(path), "IconArt.cs not found — if it moved, update this guard.");

        var offenders = HexColour().Matches(File.ReadAllText(path)).Select(m => m.Value).Distinct().ToList();

        Assert.True(
            offenders.Count == 0,
            "IconArt hardcodes " + string.Join(", ", offenders) + ". The launcher icon must take its colours "
            + "from SimplArchive.Theming's tokens (ADR 0578), or a brand change leaves the Dock tile wearing "
            + "the previous accent while the application wears the new one.");
    }

    /// <summary>Every icon the packaging scripts copy exists — they read committed artefacts, not a generator.</summary>
    [Theory]
    [InlineData("Assets/cabinet.png")]
    [InlineData("Assets/cabinet-1024.png")]
    [InlineData("Assets/app.ico")]
    [InlineData("Assets/SimplArchive.icns")]
    [InlineData("Assets/linux-icons/48.png")]
    [InlineData("Assets/linux-icons/64.png")]
    [InlineData("Assets/linux-icons/128.png")]
    [InlineData("Assets/linux-icons/256.png")]
    public void Every_packaged_icon_is_present(string relative)
    {
        var path = Path.Combine(RepoPaths.Root(), "src", "SimplArchive.DesktopClient", relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"{relative} is missing — run scripts/generate-icons.sh.");
        Assert.True(new FileInfo(path).Length > 0, $"{relative} is empty.");
    }

}
