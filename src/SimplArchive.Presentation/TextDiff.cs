namespace SimplArchive.Presentation;

/// <summary>What a side-by-side row IS: two aligned cells and the kind of change between them.</summary>
public enum DiffRowKind
{
    /// <summary>Both sides carry the same line.</summary>
    Unchanged,

    /// <summary>Both sides carry a line, and the lines differ — the word-level segments say where.</summary>
    Changed,

    /// <summary>Only the new side carries a line.</summary>
    Added,

    /// <summary>Only the old side carries a line.</summary>
    Removed,
}

/// <summary>A piece of one line; <paramref name="Emphasized"/> marks the words that changed.</summary>
public sealed record DiffSegment(string Text, bool Emphasized);

/// <summary>One side's cell of a row: its 1-based line number in that version, and its text in segments.</summary>
public sealed record DiffCell(int LineNumber, IReadOnlyList<DiffSegment> Segments);

/// <summary>One aligned row of the side-by-side view. A missing cell is the gap the other side's change leaves.</summary>
public sealed record DiffRow(DiffRowKind Kind, DiffCell? Old, DiffCell? New);

/// <summary>
/// The one answer both clients must give identically to "what changed between these two texts?" (#803,
/// ADR 0712): which rows the side-by-side view has, how the lines align, and which words within a changed
/// pair carry the emphasis. Rendering — a CSS class on one side, a brush on the other — is each client's own.
/// </summary>
/// <remarks>
/// Line alignment is a Myers edit script over the lines; a run of removals followed by a run of additions is
/// zipped pairwise into <see cref="DiffRowKind.Changed"/> rows (the form code-review readers know), with the
/// overhang staying pure <see cref="DiffRowKind.Removed"/>/<see cref="DiffRowKind.Added"/>. Within a changed
/// pair, the same algorithm runs again over word-and-whitespace tokens to place the emphasis. Pure and
/// dependency-free — the algorithm is written out here rather than pulled in as a package.
/// </remarks>
public static class TextDiff
{
    /// <summary>Every input is split the same way: normalize line endings, then split on newline.</summary>
    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static IReadOnlyList<DiffRow> Compute(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var rows = new List<DiffRow>();

        var pendingOld = new List<int>();
        var pendingNew = new List<int>();

        void FlushPending()
        {
            var paired = Math.Min(pendingOld.Count, pendingNew.Count);
            for (var i = 0; i < paired; i++)
            {
                var (oldSegments, newSegments) = WordSegments(oldLines[pendingOld[i]], newLines[pendingNew[i]]);
                rows.Add(new DiffRow(
                    DiffRowKind.Changed,
                    new DiffCell(pendingOld[i] + 1, oldSegments),
                    new DiffCell(pendingNew[i] + 1, newSegments)));
            }

            for (var i = paired; i < pendingOld.Count; i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Removed, Plain(pendingOld[i] + 1, oldLines[pendingOld[i]]), null));
            }

            for (var i = paired; i < pendingNew.Count; i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Added, null, Plain(pendingNew[i] + 1, newLines[pendingNew[i]])));
            }

            pendingOld.Clear();
            pendingNew.Clear();
        }

        foreach (var (kind, oldIndex, newIndex) in EditScript(oldLines, newLines))
        {
            switch (kind)
            {
                case EditKind.Common:
                    FlushPending();
                    rows.Add(new DiffRow(
                        DiffRowKind.Unchanged,
                        Plain(oldIndex + 1, oldLines[oldIndex]),
                        Plain(newIndex + 1, newLines[newIndex])));
                    break;
                case EditKind.Delete:
                    pendingOld.Add(oldIndex);
                    break;
                default:
                    pendingNew.Add(newIndex);
                    break;
            }
        }

        FlushPending();
        return rows;
    }

    private static DiffCell Plain(int lineNumber, string text) =>
        new(lineNumber, [new DiffSegment(text, Emphasized: false)]);

    /// <summary>Word-level emphasis for a changed pair: tokens not common to both sides are emphasized.</summary>
    private static (IReadOnlyList<DiffSegment> Old, IReadOnlyList<DiffSegment> New) WordSegments(string oldLine, string newLine)
    {
        var oldTokens = Tokenize(oldLine);
        var newTokens = Tokenize(newLine);
        var oldSegments = new List<DiffSegment>();
        var newSegments = new List<DiffSegment>();

        foreach (var (kind, oldIndex, newIndex) in EditScript(oldTokens, newTokens))
        {
            switch (kind)
            {
                case EditKind.Common:
                    Append(oldSegments, oldTokens[oldIndex], emphasized: false);
                    Append(newSegments, newTokens[newIndex], emphasized: false);
                    break;
                case EditKind.Delete:
                    Append(oldSegments, oldTokens[oldIndex], emphasized: true);
                    break;
                default:
                    Append(newSegments, newTokens[newIndex], emphasized: true);
                    break;
            }
        }

        return (oldSegments, newSegments);
    }

    /// <summary>Adjacent same-emphasis tokens merge, so a changed phrase is one segment rather than five.</summary>
    private static void Append(List<DiffSegment> segments, string text, bool emphasized)
    {
        if (segments.Count > 0 && segments[^1].Emphasized == emphasized)
        {
            segments[^1] = new DiffSegment(segments[^1].Text + text, emphasized);
        }
        else
        {
            segments.Add(new DiffSegment(text, emphasized));
        }
    }

    /// <summary>Words and the whitespace between them, each its own token — so emphasis lands on words.</summary>
    private static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        var start = 0;
        for (var i = 1; i <= line.Length; i++)
        {
            if (i == line.Length || char.IsWhiteSpace(line[i]) != char.IsWhiteSpace(line[i - 1]))
            {
                tokens.Add(line[start..i]);
                start = i;
            }
        }

        return [.. tokens];
    }

    private enum EditKind
    {
        Common,
        Delete,
        Insert,
    }

    /// <summary>
    /// Myers' greedy O((N+M)·D) edit script. Emitted in order; Common carries both indices, Delete the old
    /// index, Insert the new index.
    /// </summary>
    private static List<(EditKind Kind, int OldIndex, int NewIndex)> EditScript(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var max = n + m;
        var script = new List<(EditKind, int, int)>(Math.Max(n, m));
        if (max == 0)
        {
            return script;
        }

        // Forward pass recording each round's frontier, then a backtrack over those snapshots.
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1])
                    ? v[max + k + 1]
                    : v[max + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[max + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        // Backtrack from (n, m) through the recorded frontiers to the origin, collecting moves in reverse.
        var reversed = new List<(EditKind, int, int)>();
        var cx = n;
        var cy = m;
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var frontier = trace[d];
            var k = cx - cy;
            var prevK = k == -d || (k != d && frontier[max + k - 1] < frontier[max + k + 1]) ? k + 1 : k - 1;
            var prevX = frontier[max + prevK];
            var prevY = prevX - prevK;

            while (cx > prevX && cy > prevY)
            {
                reversed.Add((EditKind.Common, cx - 1, cy - 1));
                cx--;
                cy--;
            }

            if (cx == prevX)
            {
                reversed.Add((EditKind.Insert, -1, --cy));
            }
            else
            {
                reversed.Add((EditKind.Delete, --cx, -1));
            }
        }

        while (cx > 0 && cy > 0)
        {
            reversed.Add((EditKind.Common, cx - 1, cy - 1));
            cx--;
            cy--;
        }

        reversed.Reverse();
        script.AddRange(reversed);
        return script;
    }
}
