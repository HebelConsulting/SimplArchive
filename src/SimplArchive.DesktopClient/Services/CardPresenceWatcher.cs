using System.Timers;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Notices when the card leaves the reader, so what it decrypted can be discarded (#1353, ADR 0832).
/// </summary>
/// <remarks>
/// <para>
/// <b>A timer is the point, not an optimisation.</b> The threat this addresses is an UNATTENDED session — the
/// user has walked away and taken their card — and nobody is clicking, so nothing else in the client would ever
/// notice. A check only at the next read would leave a machine showing a decrypted document for as long as it
/// sits there, which is the case the rule exists for.
/// </para>
/// <para>
/// The per-read check in <see cref="CardEnvelopeOpener"/> is the other half and is not redundant: this one has
/// an interval, and a read can fall inside it. The owner chose both (2026-09-26).
/// </para>
/// <para>
/// <b>It only watches once a card session exists.</b> Polling a PKCS#11 module on a machine that has never
/// touched a card would load native code and talk to readers for no reason, on every installation, for a
/// feature most of them do not use.
/// </para>
/// </remarks>
public sealed class CardPresenceWatcher : IDisposable
{
    /// <summary>How often to ask. Short enough that "walked away" is seconds, cheap enough to ignore.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many consecutive absent readings mean the card is really gone.
    /// </summary>
    /// <remarks>
    /// <b>Not one.</b> A reader can momentarily report no token without anybody touching it — PCSC contention
    /// with another process, a power-management blip — and the response here is disruptive: the open document
    /// closes and its pages are discarded. Acting on a single reading made that happen to a card that had never
    /// left the reader, and it was observed live, caused by another process on the same machine talking to the
    /// same token.
    ///
    /// Two readings at a five-second interval still notices a real removal inside about ten seconds, which is
    /// well within what "somebody walked away" needs. The cost of waiting one extra tick is far smaller than the
    /// cost of being wrong.
    /// </remarks>
    public static int ConsecutiveAbsences { get; set; } = 2;

    private readonly Action _onRemoved;
    private readonly System.Timers.Timer _timer;
    private bool _sawCard;
    private int _absences;

    public CardPresenceWatcher(Action onRemoved)
    {
        _onRemoved = onRemoved;
        _timer = new System.Timers.Timer(Interval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += Tick;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Elapsed -= Tick;
        _timer.Dispose();
    }

    /// <summary>The check itself, separated so a test can drive it without waiting for a timer.</summary>
    /// <remarks>
    /// Edge-triggered on purpose: it fires when a card that WAS there is gone, once. A level check would
    /// re-fire every interval for a machine that simply has no card, which is most of them.
    /// </remarks>
    internal void Check(Func<bool> cardIsPresent)
    {
        if (cardIsPresent())
        {
            _sawCard = true;
            _absences = 0;
            return;
        }

        if (!_sawCard)
        {
            return;
        }

        // One absent reading is not a removal — see ConsecutiveAbsences. The counter resets on any sighting,
        // so a blip costs a tick rather than the user's open document.
        if (++_absences < ConsecutiveAbsences)
        {
            return;
        }

        _sawCard = false;
        _absences = 0;
        CardSession.Close();
        _onRemoved();
    }

    private void Tick(object? sender, ElapsedEventArgs e)
    {
        try
        {
            Check(CardSession.CardIsPresent);
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the process down through the unobserved-exception path. This
            // one runs every few seconds forever, so it is the last place to let that happen.
            DesktopLog.Warn(ex, "The card presence check failed");
        }
    }
}
