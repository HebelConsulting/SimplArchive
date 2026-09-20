using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

/// <summary>
/// A client renders an instant on the VIEWER's clock, never the device's (ADR 0801, ADR 0812).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a ratchet and not a rule in prose.</b> `ToLocalTime()` and `.LocalDateTime` are the obvious thing
/// to write, they compile, and they look right on the machine of whoever wrote them — a developer whose
/// device zone equals their preference sees nothing wrong, forever. That is how 29 call sites accumulated
/// across two clients while the document-date row beside them was correct: nothing failed, and the symptom
/// only appears to a user whose device and preference differ (#1315).
/// </para>
/// <para>
/// <b>The allowlist is the interesting part.</b> Three kinds of use are legitimate and must stay, and they
/// are listed here with the reason rather than excluded by a pattern that would also hide a new mistake.
/// </para>
/// </remarks>
public partial class DisplayZoneRatchetTests
{
    [GeneratedRegex(@"\.ToLocalTime\(\)|\.LocalDateTime\b|TimeZoneInfo\.Local\b")]
    private static partial Regex DeviceZone();

    // Every legitimate device-zone use, by file, with why it is one. A file appears here because the DEVICE
    // is genuinely the right answer there — never because converting it looked like work.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // The fallback itself: before a session exists there is no preference to read, and the device zone is
        // the only honest default. This IS the seam every other site now goes through.
        ["Client/Services/SessionTimeZone.cs"] = "the fallback when no preference is known",
        ["DesktopClient/Services/SessionTimeZone.cs"] = "the fallback when no preference is known",

        // Reporting the device's zone TO the server as the header fallback (ADR 0801's preference → header →
        // none). Here the device zone is the subject, not an accident.
        ["Client/Program.cs"] = "the X-Time-Zone header reports the device zone",
        ["DesktopClient/Services/ApiCore.cs"] = "the X-Time-Zone header reports the device zone",
        ["Presentation/DisplayZone.cs"] = "resolves a preference, falling back to the device",
        ["Presentation/TimeZoneChoices.cs"] = "offers the device zone as a choice",

        // APPOINTMENTS are a different decision, not an oversight (ADR 0690, "one instant, three readings").
        // A booking carries its own zone and an all-day entry is a floating date; converting those by the
        // viewer's offset would be a redesign of that ADR, made silently, inside a bug fix. Left alone
        // deliberately — and named here so the next reader finds a decision rather than a gap.
        ["Client/Components/Tabs/CalendarTab.razor"] = "appointments — ADR 0690",
        ["Client/Components/CalendarMonth.razor"] = "appointments — ADR 0690",
        ["Client/Dialogs/BookingsDialog.razor"] = "appointments — ADR 0690",
        ["Client/Services/DavCollections.cs"] = "appointments — ADR 0690",
        ["DesktopClient/ViewModels/CalendarTabViewModel.cs"] = "appointments — ADR 0690",
        ["DesktopClient/ViewModels/BookingDialogViewModel.cs"] = "appointments — ADR 0690",
        ["DesktopClient/ViewModels/ReminderDialogViewModel.cs"] = "appointments — ADR 0690",
        ["DesktopClient/Services/DavCollectionsClient.cs"] = "appointments — ADR 0690",
        ["Presentation/AppointmentCoverage.cs"] = "appointments — ADR 0690",
        ["Presentation/OfferedHours.cs"] = "appointments — ADR 0690",

        // Headless screenshot fixtures: a manual figure must be byte-stable across machines, and the demo
        // clock is synthetic anyway (ADR 0502).
        ["DesktopClient/ViewModels/MainWindowViewModel.Screenshots.cs"] = "synthetic screenshot clock",
    };

    /// <summary>The device-zone uses in one file, EXCLUDING comments.</summary>
    /// <remarks>
    /// Shared by both tests on purpose. They disagreed at first — the scan skipped comment lines while the
    /// staleness check read the whole file — so an exemption for a file whose only remaining mention was in
    /// its own DOCUMENTATION looked live and survived. That is the same failure this codebase has recorded
    /// before: a guard that ends up measuring prose.
    /// </remarks>
    private static List<string> DeviceZoneLines(string file) =>
        [.. File.ReadAllLines(file)
            .Select(l => l.TrimStart())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal)
                && !l.StartsWith("///", StringComparison.Ordinal)
                && !l.StartsWith("<!--", StringComparison.Ordinal)
                && !l.StartsWith("@*", StringComparison.Ordinal)
                && !l.StartsWith("*", StringComparison.Ordinal))
            .Where(l => DeviceZone().IsMatch(l))];

    [Fact]
    public void No_client_renders_an_instant_on_the_device_clock()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var dir in new[] { "src/SimplArchive.Client", "src/SimplArchive.DesktopClient", "src/SimplArchive.Presentation" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || Path.GetExtension(file) is not (".cs" or ".razor" or ".axaml"))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(Path.Combine(root, "src"), file).Replace('\\', '/')
                    .Replace("SimplArchive.", string.Empty, StringComparison.Ordinal);
                if (Allowed.ContainsKey(relative))
                {
                    continue;
                }

                // Comments describing the trap are not the trap — a guard that punished documenting the rule
                // would teach people to stop documenting it (ADR 0727's scanner learned this).
                offenders.AddRange(DeviceZoneLines(file).Select(l => $"  {relative}: {l.Trim()}"));
            }
        }

        Assert.True(offenders.Count == 0,
            "These render an instant on the DEVICE's clock instead of the viewer's (ADR 0801/0812):\n"
            + string.Join("\n", offenders)
            + "\n\nUse `.InZone(SessionTimeZone.Current)` (SimplArchive.Presentation.InstantFormat), or "
            + "`InstantFormat.Display(...)` for a row that should carry the offset marker.\n"
            + "If the DEVICE really is the right answer here, add the file to this test's Allowed list WITH "
            + "the reason — the list is the record of which uses are deliberate.");
    }

    // The allowlist must not outlive its entries: a file that no longer exists, or no longer contains a
    // device-zone use, leaves an exemption lying around for whatever is written there next.
    [Fact]
    public void Every_allowed_file_still_needs_its_exemption()
    {
        var root = Path.Combine(RepoRoot(), "src");

        foreach (var (relative, reason) in Allowed)
        {
            var file = Path.Combine(root, "SimplArchive." + relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"{relative} is allowed ({reason}) but does not exist — drop the entry.");

            Assert.True(DeviceZoneLines(file).Count > 0,
                $"{relative} is allowed ({reason}) but no longer uses the device zone. Remove the entry, or the "
                + "exemption silently covers whatever is written there next.");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
