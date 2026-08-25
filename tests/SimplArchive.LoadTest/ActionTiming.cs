using System.Diagnostics;

namespace SimplArchive.LoadTest;

/// <summary>One attempt at one named action: how long it took, and whether it worked.</summary>
/// <param name="Action">The workload step — "login", "open preview", "search", … — as reported.</param>
/// <param name="User">
/// Which simulated user made the attempt.
/// </param>
/// <param name="StartedAt">When it began, so a run can be sliced into warm-up and steady state.</param>
/// <param name="Duration">Wall-clock, including whatever the server took to be slow.</param>
/// <param name="Failed">
/// True only for an action that did not complete: an exception, a non-success status, an element that never
/// appeared inside the harness's generous ceiling. A SLOW action is not a failure — it is the measurement.
/// </param>
/// <param name="Error">The failure's first line, so a report can say what broke rather than how many did.</param>
/// <remarks>
/// <b><paramref name="User"/> exists because the first kiosk run could not answer a first-order question.</b>
/// It produced 32 timeouts in four clusters, and "all ten users are wedged" and "one user is wedged and
/// retrying" are completely different findings — one is an outage, the other is a stuck browser. Without a
/// per-user column the data could not distinguish them, and the write-up had to say so.
/// </remarks>
public sealed record ActionSample(
    string Action, int User, DateTimeOffset StartedAt, TimeSpan Duration, bool Failed, string? Error = null);

/// <summary>
/// Times workload actions. It never fails one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole point of this class is that it measures rather than asserts (#705).</b> A test harness turns a
/// slow action into a red test; a load harness must turn it into a data point, because "it took 9 seconds" is
/// the finding. An action that throws is recorded as failed and the loop continues — a run that stops at the
/// first error cannot answer "how long did it take to recover", which is half the question.
/// </para>
/// <para>
/// This is also why the Playwright timeouts here are set wide (see <see cref="Runner"/>): the suite's CI-tuned
/// values would convert exactly the degradation this exists to measure into a harness crash. The action-vs-
/// assertion timeout split has bitten this repository before.
/// </para>
/// </remarks>
public sealed class ActionLog
{
    private readonly List<ActionSample> _samples = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<ActionSample> Samples
    {
        get
        {
            lock (_gate)
            {
                return [.. _samples];
            }
        }
    }

    /// <summary>Runs <paramref name="action"/>, timing it, and records the outcome either way.</summary>
    public async Task<ActionSample> TimeAsync(string name, int user, Func<Task> action)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        string? error = null;

        try
        {
            await action();
        }
        catch (Exception failure)
        {
            // First line only: a Playwright timeout's message is a whole call log, and a report wants the
            // failure's identity, not its transcript.
            error = failure.Message.Split('\n')[0].Trim();
        }

        stopwatch.Stop();
        var sample = new ActionSample(name, user, startedAt, stopwatch.Elapsed, error is not null, error);
        lock (_gate)
        {
            _samples.Add(sample);
        }

        return sample;
    }

    /// <summary>The p95 of the completed attempts at one action, or null when there are none.</summary>
    /// <remarks>
    /// FAILED attempts are excluded on purpose. A failure's duration is the timeout ceiling, not a latency, and
    /// letting it into the percentile would make an outage look like slowness — the report counts failures
    /// separately, where they carry more weight than any percentile.
    /// </remarks>
    public TimeSpan? P95(string action, DateTimeOffset? since = null) => Percentile(action, 0.95, since);

    public TimeSpan? Median(string action, DateTimeOffset? since = null) => Percentile(action, 0.50, since);

    private TimeSpan? Percentile(string action, double quantile, DateTimeOffset? since)
    {
        var durations = Samples
            .Where(s => s.Action == action && !s.Failed && (since is null || s.StartedAt >= since))
            .Select(s => s.Duration)
            .OrderBy(d => d)
            .ToList();

        if (durations.Count == 0)
        {
            return null;
        }

        // Nearest-rank: with the handful of samples a short run produces, interpolation invents precision the
        // data does not have.
        var rank = (int)Math.Ceiling(quantile * durations.Count) - 1;
        return durations[Math.Clamp(rank, 0, durations.Count - 1)];
    }

    public IReadOnlyList<string> Actions() => [.. Samples.Select(s => s.Action).Distinct().Order()];

    public int Failures(DateTimeOffset? since = null) =>
        Samples.Count(s => s.Failed && (since is null || s.StartedAt >= since));

    /// <summary>Failures per action, so the per-action table can carry them beside the percentile.</summary>
    /// <remarks>
    /// The first kiosk run failed with `open repository` and `search` both reading "ok" in the table, because
    /// a timeout is excluded from the percentile — so a TOTAL OUTAGE rendered as two healthy rows and one
    /// number above them. The count belongs on the row it describes.
    /// </remarks>
    public IReadOnlyDictionary<string, int> FailuresByAction(DateTimeOffset? since = null) =>
        Samples
            .Where(s => s.Failed && (since is null || s.StartedAt >= since))
            .GroupBy(s => s.Action)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>How many distinct users hit a failure, since one wedged user is not an outage.</summary>
    public int UsersAffected(DateTimeOffset? since = null) =>
        Samples
            .Where(s => s.Failed && (since is null || s.StartedAt >= since))
            .Select(s => s.User)
            .Distinct()
            .Count();
}
