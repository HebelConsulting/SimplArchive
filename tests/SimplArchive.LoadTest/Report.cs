using System.Globalization;
using System.Text;

namespace SimplArchive.LoadTest;

/// <summary>What a scenario concluded, and the evidence for it (#705).</summary>
/// <param name="Baseline">Per-action p95 measured by a single user at run start — the yardstick.</param>
/// <param name="Steady">
/// Per-action p95 during steady state. An action absent from this map produced NO steady-state samples and is
/// reported as such rather than as a zero — see the verdict rule.
/// </param>
public sealed record ScenarioResult(
    string Scenario,
    string Target,
    int Users,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyDictionary<string, TimeSpan> Baseline,
    IReadOnlyDictionary<string, TimeSpan> Steady,
    int Failures,
    IReadOnlyDictionary<string, int> FailuresByAction,
    int UsersAffected,
    IReadOnlyList<string> FailureExamples,
    double GeneratorPeakCpu,
    bool GeneratorValid);

/// <summary>
/// Writes the run up: a markdown verdict a person reads, and a CSV of every timed action.
/// </summary>
/// <remarks>
/// Both, deliberately. The markdown answers the question that was asked; the CSV is what lets somebody
/// disagree with the answer — the raw samples, so a reader can re-percentile them, plot them, or notice that
/// one action carried the whole result. A verdict without its evidence is an opinion.
/// </remarks>
public static class Report
{
    /// <summary>PASS iff nothing failed and no action's p95 exceeded twice its baseline (#705's rule).</summary>
    public const double SteadyP95Multiple = 2.0;

    /// <summary>
    /// Below this, a ratio is not evidence of anything and is not allowed to decide the verdict.
    /// </summary>
    /// <remarks>
    /// A purely relative rule breaks down when the baseline is tiny: calibration produced "open document"
    /// 0.04 s → 0.09 s, a 2.01× that failed the run over FIFTY MILLISECONDS. Nobody experiences 90 ms as
    /// degradation, and a harness that reports it will be ignored the third time it cries — which costs the
    /// verdict its authority for the cases that matter. The ratio is still SHOWN for such an action, so a
    /// reader can see a trend forming; it just cannot fail the run on its own.
    /// </remarks>
    public static readonly TimeSpan RatioFloor = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether an action's steady p95 is a degradation worth failing over.</summary>
    public static bool Degraded(TimeSpan baseline, TimeSpan steady) =>
        steady > RatioFloor && baseline > TimeSpan.Zero && steady > baseline * SteadyP95Multiple;

    public static bool Passed(ScenarioResult result) =>
        result.GeneratorValid
        && result.Failures == 0
        && result.Steady.All(kv =>
            !result.Baseline.TryGetValue(kv.Key, out var baseline) || !Degraded(baseline, kv.Value));

    public static string Markdown(ScenarioResult r)
    {
        var sb = new StringBuilder();
        var verdict = !r.GeneratorValid ? "INVALID" : Passed(r) ? "PASS" : "FAIL";

        sb.AppendLine(CultureInfo.InvariantCulture, $"# Load test — {r.Scenario} — {verdict}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Target**: {r.Target}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Users**: {r.Users}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Started**: {r.StartedAt:u}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Duration**: {r.Duration:hh\\:mm\\:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **Generator CPU**: peak {r.GeneratorPeakCpu:F0}% ({(r.GeneratorValid ? "within limits" : $"OVER {GeneratorLoad.InvalidAbovePercent:F0}% — this run is not evidence about the server")})");
        sb.AppendLine();

        if (!r.GeneratorValid)
        {
            // First, before any number: a reader who skims must not carry away a latency figure from a run that
            // cannot support one.
            sb.AppendLine("> **This run is INVALID.** The generator saturated its own CPU, so rising latency here");
            sb.AppendLine("> is indistinguishable from the generator queueing on itself. Re-run on a bigger machine,");
            sb.AppendLine("> or with fewer browser users, before drawing any conclusion about the target.");
            sb.AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **Failed actions**: {r.Failures}{(r.Failures > 0 ? $" — across {r.UsersAffected} of {r.Users} users" : string.Empty)}");
        foreach (var example in r.FailureExamples.Take(5))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - {example}");
        }

        if (r.Failures > 0 && r.UsersAffected >= r.Users)
        {
            // EVERY user failing is a different finding from one user failing repeatedly: the first is an
            // outage, the second is a stuck browser. The first kiosk run could not tell them apart, because
            // the samples carried no user — so when the answer is available, the report states it.
            sb.AppendLine();
            sb.AppendLine("> Every simulated user hit a failure, so this is the target being unavailable rather");
            sb.AppendLine("> than one browser getting stuck. Check the server's own logs for the window above.");
        }

        sb.AppendLine();
        sb.AppendLine("## Per action");
        sb.AppendLine();
        sb.AppendLine("| Action | Baseline p95 | Steady p95 | Ratio | Failures | Verdict |");
        sb.AppendLine("|---|---:|---:|---:|---:|:--|");

        foreach (var action in r.Baseline.Keys.Union(r.Steady.Keys).Union(r.FailuresByAction.Keys).Order())
        {
            r.Baseline.TryGetValue(action, out var baseline);
            r.FailuresByAction.TryGetValue(action, out var failed);
            var failures = failed == 0 ? "—" : failed.ToString(CultureInfo.InvariantCulture);

            // NO STEADY SAMPLES is its own answer, never a zero. Login is the standing example: every user signs
            // in once, during warm-up, so steady state contains none — and reporting that as "0.00 s, within 2×"
            // is a healthy-looking row for an action nobody measured. It read exactly that way in calibration.
            if (!r.Steady.TryGetValue(action, out var steady))
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {action} | {baseline.TotalSeconds:F2} s | — | — | {failures} | no steady-state samples |");
                continue;
            }

            if (baseline <= TimeSpan.Zero)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {action} | — | {steady.TotalSeconds:F2} s | — | {failures} | no baseline |");
                continue;
            }

            var ratio = steady.TotalMilliseconds / baseline.TotalMilliseconds;

            // A row with FAILURES is never "ok", whatever its percentile says. A timeout is excluded from the
            // percentile (its duration is a ceiling, not a latency), so on the first kiosk run an eight-minute
            // outage rendered as two rows reading "ok" with a failure count only in the summary above. The
            // failure is the more important fact about the row, so it wins the verdict cell.
            var note = failed > 0
                ? $"**{failed} failed**"
                : !Degraded(baseline, steady)
                    ? (ratio > SteadyP95Multiple ? $"within 2× of nothing — under the {RatioFloor.TotalMilliseconds:F0} ms floor" : "ok")
                    : "**degraded**";

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {action} | {baseline.TotalSeconds:F2} s | {steady.TotalSeconds:F2} s | {ratio:F2}× | {failures} | {note} |");
        }

        sb.AppendLine();
        sb.AppendLine("The baseline is measured by a single user at the start of this same run, against this same");
        sb.AppendLine("target — so it stays valid across hardware and dataset changes, and there are no absolute");
        sb.AppendLine("thresholds to go stale. It does carry cold-start cost (first render, WASM boot), so an");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"action getting *faster* under load is normal rather than suspicious. A ratio on an action under {RatioFloor.TotalMilliseconds:F0} ms is");
        sb.AppendLine("shown but cannot fail the run: at that size it is noise wearing a percentage.");
        return sb.ToString();
    }

    /// <summary>Every timed attempt, so the verdict above can be checked rather than believed.</summary>
    public static string Csv(IEnumerable<ActionSample> samples)
    {
        // `user` is here so a reader can tell an outage from one stuck browser — see ActionSample.User.
        var sb = new StringBuilder("started_at,user,action,duration_ms,failed,error\n");
        foreach (var s in samples)
        {
            var error = (s.Error ?? string.Empty).Replace('"', '\'').Replace(',', ';');
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{s.StartedAt:O},{s.User},{s.Action},{s.Duration.TotalMilliseconds:F0},{(s.Failed ? 1 : 0)},\"{error}\"");
        }

        return sb.ToString();
    }
}
