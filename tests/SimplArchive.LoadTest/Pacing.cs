namespace SimplArchive.LoadTest;

/// <summary>
/// How long a simulated person pauses between actions — the difference between a load test and a flood.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two regimes, and a report is uninterpretable without knowing which produced it.</b> The default is
/// <see cref="Realistic"/>: 3–8 s per action, which is roughly what a person browsing a document archive does,
/// and it is what a "can ten people work?" question means. But the first kiosk run paced at 2 s per *iteration*
/// and generated ~12,000 requests a minute — about twenty times that — which is how it found a real defect by
/// hammering (issue #750).
/// </para>
/// <para>
/// Both are legitimate; they answer different questions. Realistic pacing measures the experience. Aggressive
/// pacing is how you deliberately drive a system to saturation to observe *how it fails* — and after a fix, to
/// confirm the failure mode changed. Reproducing a defect needs the load that produced it, so the pacing has to
/// be a knob rather than a constant. It is recorded in the report for the same reason: two runs at different
/// pacing are not comparable, and nothing on the page would otherwise say so.
/// </para>
/// <para>
/// <b>Browsers are the limiting resource, which is why this is pacing rather than population.</b> Reaching
/// ~12,000 req/min at realistic pacing would need on the order of 150 Chrome instances; the generator would
/// saturate its own CPU long first, and <see cref="GeneratorLoad"/> would correctly stamp the run INVALID. So
/// to hold offered load constant while changing the thing under test, change the pause, not the user count.
/// </para>
/// </remarks>
/// <param name="MinMs">Lower bound of the pause, in milliseconds.</param>
/// <param name="MaxMs">Upper bound. Randomised between the two so users do not march in lockstep.</param>
public sealed record Pacing(int MinMs, int MaxMs)
{
    /// <summary>What a person does: read the thing they just opened before clicking the next one.</summary>
    public static readonly Pacing Realistic = new(3_000, 8_000);

    /// <summary>
    /// Deliberately harder than any real user, to drive a system to saturation on purpose.
    /// </summary>
    /// <remarks>
    /// Approximately the pacing of the run that exhausted the kiosk's connection pool, so a post-fix run can
    /// offer the same load and ask whether the failure MODE changed — 500s, or merely slower.
    /// </remarks>
    public static readonly Pacing Aggressive = new(0, 200);

    public TimeSpan Next(Random random) =>
        TimeSpan.FromMilliseconds(MinMs + random.NextDouble() * Math.Max(0, MaxMs - MinMs));

    /// <summary>How the report names this regime, so a reader knows what they are looking at.</summary>
    public string Describe() =>
        this == Realistic || (MinMs, MaxMs) == (Realistic.MinMs, Realistic.MaxMs)
            ? $"realistic ({MinMs / 1000.0:0.#}–{MaxMs / 1000.0:0.#} s per action)"
            : $"**aggressive** ({MinMs}–{MaxMs} ms per action — harder than any real user, to provoke saturation)";
}
