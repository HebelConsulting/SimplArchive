namespace SimplArchive.UnitTests;

/// <summary>
/// Where the repository is, for the guards that read files out of the working tree rather than the build output.
/// </summary>
/// <remarks>
/// <para>
/// It exists because there were <b>28 copies</b> of this six-line walk in this project alone (35 across
/// <c>tests/</c>), in six variants — issue #955. They had drifted: five different exception messages, one
/// returning <c>string?</c>, one using <c>dir.Parent!</c>, and one anchoring on <b>a directory containing
/// <c>src</c></b> rather than on the solution file. That last pair answer different questions; they agreed on
/// the day it was written and nothing made them agree afterwards.
/// </para>
/// <para>
/// CLAUDE.md asks for the shared implementation at the SECOND occurrence, "because copies drift — the fourth one
/// gets the bug fix and the first three do not, and nothing points that out". #583 is what that looks like when
/// it matters: the private-repository gate was duplicated three times and every copy carried the same blind
/// spot, so the fix could only reach the copies someone remembered to look at.
/// </para>
/// </remarks>
public static class RepoPaths
{
    /// <summary>The directory holding <c>SimplArchive.slnx</c>. Throws when it cannot be found.</summary>
    /// <remarks>
    /// The throw is a defensive assertion rather than a modelled error: a test run whose working tree has gone
    /// missing has nothing to assert about, and a guard that quietly returned "" would pass while reading
    /// nothing — the exact failure #583 and #935 were both about.
    /// </remarks>
    public static string Root() =>
        RootOrNull() ?? throw new InvalidOperationException(
            "Could not locate the repository root (the directory holding SimplArchive.slnx) from "
            + $"{AppContext.BaseDirectory}.");

    /// <summary>The repository root, or null — for callers that legitimately stand down without one.</summary>
    public static string? RootOrNull()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}
