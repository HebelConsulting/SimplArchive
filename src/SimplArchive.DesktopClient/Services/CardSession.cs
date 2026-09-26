using CAManagement.Pkcs11;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The logged-in session on the user's card, held for as long as the card is in the reader (#1353, ADR 0832).
/// </summary>
/// <remarks>
/// <para>
/// <b>The PIN is asked once and never kept.</b> What is held is the PKCS#11 LOGIN SESSION, not the secret that
/// opened it — so nothing the user typed stays in this process's memory, and there is no value here for a
/// memory dump to find. A prompt per decryption was rejected on use (owner, 2026-09-25): one document's
/// preview, page image and text extraction are three reads, so per-read prompting is three prompts for one
/// file.
/// </para>
/// <para>
/// <b>The module itself belongs to <see cref="CardModule"/>, not here.</b> <c>C_Initialize</c> is per process,
/// so this class owning a library of its own was the second initialiser that produced
/// <c>CKR_CRYPTOKI_ALREADY_INITIALIZED</c> the first time a real preview read the card from several tasks at
/// once. Everything below therefore runs inside the module's gate, which also serialises the decrypt itself —
/// <c>C_DecryptInit</c> and <c>C_Decrypt</c> are two calls carrying state between them.
/// </para>
/// </remarks>
public static class CardSession
{
    /// <summary>Asks the user for their PIN. Returns null or empty when they decline.</summary>
    /// <remarks>
    /// A property the view assigns rather than a constructor argument (ADR 0730): the prompt is a DIALOG, which
    /// belongs to a window that does not exist when this static is first touched. And the omission is loud —
    /// with no prompt the card simply cannot be used and the reader is told so by name — which is the case that
    /// rule says does not need construction-time enforcement.
    /// </remarks>
    public static Func<Task<string?>>? PinPrompt { get; set; }

    private static Pkcs11Session? _session;
    private static LoginScope? _login;
    private static bool _declined;

    /// <summary>
    /// The live logged-in session on <paramref name="library"/>, opening one — and prompting for the PIN —
    /// if there is none.
    /// </summary>
    /// <remarks>
    /// Called only from inside <see cref="CardModule"/>'s gate, so it never races another opener. Answers null
    /// when there is no card, no reader, or the user declined: all ordinary answers rather than failures,
    /// because another opener may still hold the key.
    /// </remarks>
    internal static async Task<Pkcs11Session?> OpenAsync(Pkcs11Library library)
    {
        // A decline is remembered for the session. Without this, a user who cancels the PIN prompt is asked
        // again by the very next read — and a document's preview, pages and text layout are three reads, so
        // cancelling once would produce three more prompts.
        if (_declined || _session is not null)
        {
            return _session;
        }

        var slots = library.GetSlotList(tokenPresent: true);
        if (slots.Length == 0)
        {
            return null;
        }

        if (PinPrompt is null || await PinPrompt() is not { Length: > 0 } pin)
        {
            _declined = true;
            return null;
        }

        try
        {
            // Read-write is NOT needed: this session only decrypts. A read-only session is the smaller request,
            // and a token that refuses to be written to still serves it.
            var session = library.OpenSession(slots[0], readWrite: false);
            _login = session.Login(pin);
            _session = session;

            return session;
        }
        catch (Exception e)
        {
            // A wrong PIN lands here, and so does a token that vanished between the slot list and the login.
            // Both mean "no card session"; which of card / reader / certificate is missing is said by the
            // opener, which has the fuller picture.
            DesktopLog.Warn(e, "Opening a logged-in card session failed");
            DropHeld();
            return null;
        }
    }

    /// <summary>Whether a token is still present — skipped rather than queued while the card is in use.</summary>
    /// <remarks>
    /// This ASKS the reader, so it belongs to the presence watcher and to nothing else. A caller that merely
    /// wants to describe a failure should read <see cref="CardModule.LastSawToken"/> instead, which is what the
    /// last real attempt saw.
    /// </remarks>
    public static bool CardIsPresent() =>
        CardModule.TryUse(
            library =>
            {
                var present = library.GetSlotList(tokenPresent: true).Length > 0;
                CardModule.Observed(present);
                return present;
            },
            whenBusyOrUnavailable: true);

    /// <summary>
    /// Drops the logged-in session — on card removal and at sign-out. The module stays loaded.
    /// </summary>
    /// <remarks>
    /// The key stops being usable and the PIN must be given again, which is what removal and sign-out require.
    /// Unloading the module as well was tried and is WRONG: re-initialising OpenSC in the same process could
    /// not see the token afterwards, so one removal broke card reading until the client was restarted.
    /// </remarks>
    public static void Close() => CardModule.DropSession();

    /// <summary>
    /// Drops the session without touching the gate — for <see cref="CardModule"/>, which already holds it.
    /// </summary>
    /// <remarks>
    /// Swallowing on purpose: this runs on the path where something has already gone wrong (the card was
    /// pulled mid-operation), and a throw here would replace a recoverable state with a crash.
    /// </remarks>
    internal static void DropHeld()
    {
        try
        {
            _login?.Dispose();
            _session?.Dispose();
        }
        catch (Exception e)
        {
            DesktopLog.Warn(e, "Closing the card session did not complete cleanly");
        }
        finally
        {
            _login = null;
            _session = null;
            _declined = false;
        }
    }
}
