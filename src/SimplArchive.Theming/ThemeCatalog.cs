using System.Reflection;

namespace SimplArchive.Theming;

/// <summary>
/// Every style a user can pick from: the ones bundled with the application, plus any dropped into a
/// <c>themes/</c> folder beside it (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// A picker rather than renaming a file on disk. Renaming is a developer's affordance — it cannot be discovered,
/// it can be mistyped, and a week later nobody remembers it was done. A drop-down states the choice and can be
/// changed back.
/// </para>
/// <para>
/// <b>Bundled is not the whole list, deliberately.</b> A picker offering only what we shipped can never show a
/// customer their own brand, which is the entire point of the feature. So a <c>themes/</c> folder beside the
/// executable is scanned too and its files appear in the same drop-down — the same gesture as dropping a file
/// in, minus the rename.
/// </para>
/// <para>
/// <b>A missing style is not an error.</b> Styles come and go — a folder is not copied to a new machine, a
/// profile is synced from elsewhere, a file is deleted. The application falls back to the shipped design and
/// carries on, because a colour scheme is never worth blocking somebody's work over. It is not <em>silent</em>
/// though: the fallback is reported, or "why did our colours change?" has no answer anywhere.
/// </para>
/// </remarks>
public static class ThemeCatalog
{
    /// <summary>The id of the shipped design — what an empty or unknown selection resolves to.</summary>
    public const string DefaultId = "default";

    private const string BundledPrefix = "SimplArchive.Theming.presets.";
    private const string FolderName = "themes";

    /// <summary>One pickable style.</summary>
    /// <param name="Id">Stable across renames of the display name — this is what a profile stores.</param>
    /// <param name="Name">What the drop-down shows.</param>
    /// <param name="Bundled">False for a style found beside the executable.</param>
    public sealed record Entry(string Id, string Name, bool Bundled);

    /// <summary>
    /// The shipped design first, then the bundled styles, then anything found on disk — each group by name.
    /// </summary>
    /// <param name="directory">Where to look for extra styles; defaults to <c>themes/</c> beside the app.</param>
    public static IReadOnlyList<Entry> Available(string? directory = null)
    {
        var entries = new List<Entry> { new(DefaultId, ThemeTokensReader.Shipped.Name, Bundled: true) };

        entries.AddRange(BundledIds()
            .Select(id => new Entry(id, NameOf(ReadBundled(id)) ?? id, Bundled: true))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));

        entries.AddRange(OnDisk(directory)
            .Select(file => new Entry(
                Path.GetFileNameWithoutExtension(file),
                NameOf(SafeRead(file)) ?? Path.GetFileNameWithoutExtension(file),
                Bundled: false))
            // A file beside the app that shadows a bundled id would make the drop-down show two identical
            // entries, so the bundled one wins and the file is simply not offered twice.
            .Where(e => !BundledIds().Contains(e.Id, StringComparer.OrdinalIgnoreCase) && e.Id != DefaultId)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));

        return entries;
    }

    /// <summary>
    /// Resolves a stored selection into a usable theme. Anything unknown, unreadable or unreadable-on-screen
    /// comes back as the shipped design with a note saying so.
    /// </summary>
    public static ThemeTokensReader.ThemeLoad Load(string? id, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id == DefaultId)
        {
            return new ThemeTokensReader.ThemeLoad(ThemeTokensReader.Shipped, Applied: false, []);
        }

        if (ReadBundled(id) is { } bundled)
        {
            return ThemeTokensReader.Load(bundled);
        }

        var file = OnDisk(directory).FirstOrDefault(
            f => string.Equals(Path.GetFileNameWithoutExtension(f), id, StringComparison.OrdinalIgnoreCase));

        if (file is not null && SafeRead(file) is { } content)
        {
            return ThemeTokensReader.Load(content);
        }

        return new ThemeTokensReader.ThemeLoad(
            ThemeTokensReader.Shipped,
            Applied: false,
            [$"The style '{id}' is no longer available, so the shipped design is in use."]);
    }

    private static IEnumerable<string> BundledIds() =>
        typeof(ThemeCatalog).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(BundledPrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[BundledPrefix.Length..^".json".Length])
            .OrderBy(n => n, StringComparer.Ordinal);

    private static string? ReadBundled(string id)
    {
        var name = BundledIds().FirstOrDefault(n => string.Equals(n, id, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        using var stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream(BundledPrefix + name + ".json");
        return stream is null ? null : new StreamReader(stream).ReadToEnd();
    }

    private static IEnumerable<string> OnDisk(string? directory)
    {
        var folder = directory ?? Path.Combine(AppContext.BaseDirectory, FolderName);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return []; // an unreadable folder costs the extra styles, never the application
        }
    }

    private static string? SafeRead(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The display name without paying to validate the whole theme — the picker has to list styles it may well
    // refuse later, and refusing at list time would hide them instead of explaining them.
    private static string? NameOf(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
