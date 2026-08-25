using System.Diagnostics;

namespace SimplArchive.LoadTest;

/// <summary>
/// Watches how hard THIS machine is working while it generates load (#705).
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-optional, and the reason is the whole failure mode of load testing.</b> "The server degraded at N
/// users" and "my laptop degraded at N users" produce identical-looking reports — rising latency, then errors —
/// and only one of them is a finding. A run whose generator was saturated cannot tell you which it measured, so
/// it is stamped INVALID rather than published with a caveat nobody reads.
/// </para>
/// <para>
/// Measured as process CPU across all cores, sampled on an interval: the harness is this process plus the
/// browsers it spawns, so the machine-wide figure is the honest one — a Chrome pegging four cores is this
/// harness's doing even though it is not this process.
/// </para>
/// </remarks>
public sealed class GeneratorLoad
{
    /// <summary>Above this, the run is not evidence about the server. Deliberately conservative.</summary>
    /// <remarks>
    /// 80 % leaves headroom for the sampling itself and for the bursts a browser makes when a page renders. A
    /// generator at 80 % is already competing with itself for CPU, so its own latencies stop being the server's.
    /// </remarks>
    public const double InvalidAbovePercent = 80.0;

    private readonly List<double> _samples = [];
    private readonly Lock _gate = new();

    public double PeakPercent
    {
        get { lock (_gate) { return _samples.Count == 0 ? 0 : _samples.Max(); } }
    }

    public double MeanPercent
    {
        get { lock (_gate) { return _samples.Count == 0 ? 0 : _samples.Average(); } }
    }

    /// <summary>Whether the run may be believed at all.</summary>
    public bool RunIsValid => PeakPercent <= InvalidAbovePercent;

    /// <summary>Samples until cancelled. Cheap enough to leave running for the whole scenario.</summary>
    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        var cores = Environment.ProcessorCount;
        var previous = TotalProcessorTime();
        var clock = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var elapsed = clock.Elapsed;
            clock.Restart();
            var current = TotalProcessorTime();

            // CPU-seconds consumed over wall-seconds, as a share of all cores.
            var used = (current - previous).TotalSeconds;
            previous = current;
            var percent = elapsed.TotalSeconds <= 0 ? 0 : used / (elapsed.TotalSeconds * cores) * 100.0;

            lock (_gate)
            {
                _samples.Add(Math.Clamp(percent, 0, 100));
            }
        }
    }

    /// <summary>
    /// This process and every browser it spawned.
    /// </summary>
    /// <remarks>
    /// Chrome does the rendering, so measuring only this process would report a harness at 5 % while the machine
    /// is on its knees — precisely the reading that would let a saturated generator pass as a valid run.
    /// </remarks>
    private static TimeSpan TotalProcessorTime()
    {
        var total = TimeSpan.Zero;
        foreach (var name in new[] { "chrome", "Google Chrome", "chromium", "Chromium", "headless_shell" })
        {
            foreach (var process in SafeProcesses(name))
            {
                try
                {
                    total += process.TotalProcessorTime;
                }
                catch
                {
                    // A browser that exited between enumeration and reading — nothing to add, and nothing wrong.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return total + Process.GetCurrentProcess().TotalProcessorTime;
    }

    private static Process[] SafeProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch
        {
            return [];
        }
    }
}
