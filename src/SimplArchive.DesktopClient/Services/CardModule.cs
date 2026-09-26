using CAManagement.Pkcs11;
using CAManagement.Pkcs11.Configuration;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The one loaded PKCS#11 module in this process, and the gate that serialises access to it (#1353, ADR 0832).
/// </summary>
/// <remarks>
/// <para>
/// <b>`C_Initialize` is PER PROCESS, not per handle.</b> A second <c>Pkcs11Library</c> over the same module
/// while a first is alive answers <c>CKR_CRYPTOKI_ALREADY_INITIALIZED</c> — and there were two creators: the
/// certificate reader (used by registration, and again to check the addressee) and the login session. They
/// never collided in a single scripted read, which is exactly why this survived every check I ran and failed
/// the moment a real preview fired its page render, its thumbnails and its text layout at once:
/// </para>
/// <code>
/// Could not load 'Invoice 2026-003': PKCS#11 call 'C_Initialize' failed with
/// CKR_CRYPTOKI_ALREADY_INITIALIZED (0x00000191).
/// </code>
/// <para>
/// So the module is owned HERE, loaded once, and every card operation runs through <see cref="UseAsync"/> or
/// <see cref="Use"/>. The gate is not only about initialisation: PKCS#11 sessions carry operation state
/// (<c>C_DecryptInit</c> then <c>C_Decrypt</c>), so two interleaved unwraps on one session would corrupt each
/// other even with the module loaded exactly once.
/// </para>
/// </remarks>
public static class CardModule
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Pkcs11Library? _library;
    private static string? _loadedFrom;

    /// <summary>
    /// Whether a token was there the last time anything actually looked — null if nothing ever has.
    /// </summary>
    /// <remarks>
    /// So that "which of the three things is missing?" can be ANSWERED FROM THE ATTEMPT THAT JUST FAILED
    /// rather than by making a fresh call to the reader. Two reasons, and the second is the one that bit:
    /// the remembered answer describes the read the user is being told about, and asking again means the
    /// client talks to whatever token is plugged in every time it composes an error message — including
    /// inside the test suite, which has no business touching a developer's card at all.
    /// </remarks>
    public static bool? LastSawToken { get; private set; }

    /// <summary>Records what a slot query saw, for <see cref="LastSawToken"/>.</summary>
    internal static void Observed(bool tokenPresent) => LastSawToken = tokenPresent;

    /// <summary>Runs <paramref name="work"/> against the shared module, or answers <paramref name="whenUnavailable"/>.</summary>
    public static async Task<T> UseAsync<T>(Func<Pkcs11Library, Task<T>> work, T whenUnavailable)
    {
        await Gate.WaitAsync();
        try
        {
            return Library() is { } library ? await work(library) : whenUnavailable;
        }
        catch (Exception e)
        {
            DesktopLog.Warn(e, "A card operation failed");
            return whenUnavailable;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The synchronous form, for callers with nothing to await.</summary>
    public static T Use<T>(Func<Pkcs11Library, T> work, T whenUnavailable) =>
        UseAsync(library => Task.FromResult(work(library)), whenUnavailable).GetAwaiter().GetResult();

    /// <summary>
    /// The same, but SKIPPED rather than queued when the card is busy.
    /// </summary>
    /// <remarks>
    /// For the presence watcher. A read that is waiting for the user to type their PIN holds the gate for as
    /// long as they take, and a timer that blocked on it would pile its ticks up behind the prompt and then
    /// fire them all at once. "Somebody is using the card" is also a perfectly good answer to "is the card
    /// still there?", so the tick is simply dropped.
    /// </remarks>
    public static bool TryUse(Func<Pkcs11Library, bool> work, bool whenBusyOrUnavailable)
    {
        if (!Gate.Wait(0))
        {
            return whenBusyOrUnavailable;
        }

        try
        {
            return Library() is { } library ? work(library) : whenBusyOrUnavailable;
        }
        catch (Exception e)
        {
            DesktopLog.Warn(e, "A card presence check failed");
            return whenBusyOrUnavailable;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Drops the logged-in session but KEEPS the module loaded — the answer to card removal and sign-out.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <c>C_Finalize</c>.</b> Unloading and re-initialising inside one process is a known
    /// PKCS#11 hazard, and with OpenSC it is worse than academic: after a finalize, a later
    /// <c>C_Initialize</c> in the same process could no longer see the token, so a single card-removal event
    /// left card reading broken for the whole session while a freshly started client saw the card perfectly.
    /// Observed exactly that way — and the invalid-handle <c>C_Logout</c> it left in the log was the symptom,
    /// not the cause.
    ///
    /// What removal actually requires is that the KEY stops being usable and decrypted content is discarded.
    /// Closing the session does both. The module is a loaded library, and keeping it costs nothing.
    /// </remarks>
    public static void DropSession()
    {
        Gate.Wait();
        try
        {
            CardSession.DropHeld();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Unloads the module entirely — for shutdown, and when a different module path is chosen.</summary>
    /// <remarks>
    /// Callers hold the gate through <see cref="UseAsync"/>, so this takes it too rather than tearing the
    /// module out from under a decryption in flight. Not the card-removal path: see <see cref="DropSession"/>.
    /// </remarks>
    public static void Close()
    {
        Gate.Wait();
        try
        {
            Unload();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Pkcs11Library? Library()
    {
        if (CardCertificates.FindModule() is not { } path)
        {
            Unload();
            return null;
        }

        // A different module than the one loaded — the Browse fallback was used — means unloading the old one
        // first, because the second C_Initialize is what this class exists to prevent.
        if (_library is not null && !string.Equals(_loadedFrom, path, StringComparison.Ordinal))
        {
            Unload();
        }

        if (_library is null)
        {
            _library = new Pkcs11Library(new Pkcs11Options { ModulePath = path });
            _loadedFrom = path;
        }

        return _library;
    }

    private static void Unload()
    {
        try
        {
            CardSession.DropHeld();
            _library?.Dispose();
        }
        catch (Exception e)
        {
            DesktopLog.Warn(e, "Unloading the PKCS#11 module did not complete cleanly");
        }
        finally
        {
            _library = null;
            _loadedFrom = null;
        }
    }
}
