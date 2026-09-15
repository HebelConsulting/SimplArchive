namespace SimplArchive.SelfHosting;

/// <summary>
/// The third-party image versions the fixtures start, read from <c>images.env</c> — the same file the shipped
/// stack uses.
/// </summary>
/// <remarks>
/// <para>
/// A fixture that pins its own version is a fixture testing something other than what ships, and nobody learns
/// that until the upstream project releases. It has already cost a day here: <c>opensearchproject/opensearch:2</c>
/// floated, a green build went red with no commit on our side, and the guard written afterwards covered that
/// one image while <c>apache/tika:latest-full</c> floated on untouched — on the component that extracts the
/// text search depends on.
/// </para>
/// <para>
/// Reading the file rather than copying its values is what makes a bump ONE edit. The copies that remain —
/// compose's inline default, the chart's literal — exist because those formats cannot reference a file, and
/// <c>PinnedImageLockstepTests</c> fails the build when either drifts from this one.
/// </para>
/// <para>
/// It THROWS rather than falling back to a default. A missing or malformed pin must stop the run: a fixture
/// that quietly substitutes <c>latest</c> when it cannot find the file is exactly the silent drift this exists
/// to prevent, and it would look like a passing suite.
/// </para>
/// </remarks>
public static class ImagePins
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Pins = new(Load);

    /// <summary>The value of one pin, e.g. <c>Tag("POSTGRES_TAG")</c> → <c>16.15-alpine</c>.</summary>
    public static string Tag(string name) =>
        Pins.Value.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new InvalidOperationException(
                $"images.env has no value for '{name}'. Every image the fixtures start is pinned there, so the "
                + "suite runs the versions that ship; add it rather than hard-coding a tag here.");

    /// <summary><c>repo:tag</c> for an image, e.g. <c>Image("postgres", "POSTGRES_TAG")</c>.</summary>
    public static string Image(string repository, string name) => $"{repository}:{Tag(name)}";

    private static IReadOnlyDictionary<string, string> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (SimplArchive.slnx)."),
            "images.env");

        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            var split = text.IndexOf('=');
            if (split > 0)
            {
                pins[text[..split].Trim()] = text[(split + 1)..].Trim();
            }
        }

        return pins;
    }
}
