using SimplArchive.Theming;
using SimplArchive.ThemeGen;

// Writes the generated theme files from the shipped tokens (ADR 0578). Run it via scripts/generate-theme.sh.
//
// It is deliberately dumb: every decision — which variables exist, what they are worth, how each target spells
// them — belongs to SimplArchive.Theming, which the CLIENTS also use at runtime to apply a custom theme. A
// generator that knew anything of its own would be a third opinion about the design.

var root = RepoRoot();
var outputs = ThemeOutputs.For(root);

foreach (var (path, content) in outputs)
{
    var relative = Path.GetRelativePath(root, path);
    var unchanged = File.Exists(path) && File.ReadAllText(path) == content;

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    Console.WriteLine($"{(unchanged ? "unchanged" : "WRITTEN  ")}  {relative}");
}

Console.WriteLine($"\n{ThemeTokensReader.Shipped.Name}: {outputs.Count} file(s) from src/SimplArchive.Theming/tokens.json");
return 0;

static string RepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimplArchive.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
}
