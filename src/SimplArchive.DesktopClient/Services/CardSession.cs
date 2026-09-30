using CAManagement.Pkcs11;
using CAManagement.Pkcs11.Extensions;

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
    public static Func<CardBeingUnlocked, Task<string?>>? PinPrompt { get; set; }

    // PER TOKEN, because a reader may have two cards in it (#1500). A single held session meant the FIRST
    // card's session answered for every read: a document addressed to the second card prompted for a PIN,
    // opened slots[0], failed to unwrap and declined — telling the reader no card held their key while it sat
    // in the other reader. Worse, the PIN went to the wrong token, and by PKCS#11 semantics a wrong PIN
    // decrements THAT token's retry counter, so three such reads block an innocent card.
    //
    // Keyed by the token's SERIAL rather than by its slot: a slot id is a CK_ULONG, whose CLR type is uint on
    // Windows and ulong elsewhere, so it cannot appear in a signature or a field type that compiles for both
    // (ADR 0831). The serial is a string, identifies the card rather than the reader it happens to be in, and
    // is already what CardCertificates.Found carries.
    private static readonly Dictionary<string, (Pkcs11Session Session, LoginScope Login)> Sessions = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Declined = new(StringComparer.Ordinal);

    /// <summary>
    /// The live logged-in session on <paramref name="library"/>, opening one — and prompting for the PIN —
    /// if there is none.
    /// </summary>
    /// <remarks>
    /// Called only from inside <see cref="CardModule"/>'s gate, so it never races another opener. Answers null
    /// when there is no card, no reader, or the user declined: all ordinary answers rather than failures,
    /// because another opener may still hold the key.
    /// </remarks>
    /// <param name="tokenSerial">
    /// The card this session must be on — the serial of the token whose certificate the envelope named. Asking
    /// for a card by name is the whole point: the PIN must reach the token that can actually answer.
    /// </param>
    internal static async Task<Pkcs11Session?> OpenAsync(
        Pkcs11Library library, string tokenSerial, string tokenLabel, string objectLabel)
    {
        // A decline is remembered PER CARD. Without this, a user who cancels the PIN prompt is asked again by
        // the very next read — and a document's preview, pages and text layout are three reads, so cancelling
        // once would produce three more prompts. Per card, so declining for one does not disable the other.
        if (Declined.Contains(tokenSerial))
        {
            return null;
        }

        if (Sessions.TryGetValue(tokenSerial, out var held))
        {
            return held.Session;
        }

        // THE SLOT HOLDING THAT CARD, not the first slot with a token in it. Matched inline rather than
        // returned, because a slot id cannot be named in a signature (see Sessions above).
        foreach (var slot in library.GetSlotList(tokenPresent: true))
        {
            string serial;
            try
            {
                serial = library.GetTokenInfo(slot).SerialNumber.AsPkcs11String().Trim();
            }
            catch (Exception e)
            {
                // One unreadable token is not a reason to abandon the others — the same reasoning as the
                // per-slot guard in CardCertificates.
                DesktopLog.Warn(e, "Reading a slot's token info while looking for a card failed");
                continue;
            }

            if (!string.Equals(serial, tokenSerial, StringComparison.Ordinal))
            {
                continue;
            }

            if (PinPrompt is null
                || await PinPrompt(new CardBeingUnlocked(tokenLabel, tokenSerial, objectLabel))
                    is not { Length: > 0 } pin)
            {
                Declined.Add(tokenSerial);
                return null;
            }

            try
            {
                // Read-write is NOT needed: this session only decrypts. A read-only session is the smaller
                // request, and a token that refuses to be written to still serves it.
                var session = library.OpenSession(slot, readWrite: false);
                var login = session.Login(pin);
                Sessions[tokenSerial] = (session, login);

                return session;
            }
            catch (Exception e)
            {
                // A wrong PIN lands here, and so does a token that vanished between the slot list and the
                // login. Both mean "no card session"; which of card / reader / certificate is missing is said
                // by the opener, which has the fuller picture.
                DesktopLog.Warn(e, "Opening a logged-in card session failed");
                DropHeld();
                return null;
            }
        }

        // The card whose certificate was named is no longer in a reader. Not a failure: it may have been
        // pulled between reading its certificates and asking for the PIN.
        DesktopLog.Debug("No token with serial {Serial} is present, so no session was opened.", tokenSerial);
        return null;
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
        foreach (var (_, held) in Sessions)
        {
            try
            {
                held.Login.Dispose();
                held.Session.Dispose();
            }
            catch (Exception e)
            {
                DesktopLog.Warn(e, "Closing the card session did not complete cleanly");
            }
        }

        Sessions.Clear();
        Declined.Clear();
    }
}

/// <summary>
/// Which card the PIN is being asked for — what the prompt must say (#1500, owner-decided 2026-09-30).
/// </summary>
/// <remarks>
/// <para>
/// <b>The PIN is per card, so a prompt that does not name one is a credential asked blind.</b> With two
/// tokens in the reader a reader has two PINs, and entering the wrong card's decrements THAT card's retry
/// counter — three of those block a card that was never involved. So naming the card is not presentation, it
/// is the thing that makes the prompt answerable.
/// </para>
/// <para>
/// <b>A CHOOSER was considered and declined</b> (owner, 2026-09-30). Every valid recipient opens the same
/// document — a CMS envelope wraps one content key to each — so the choice has no outcome the user can see,
/// and a prompt whose answer changes nothing, in front of a prompt that does, is how people learn to click
/// through prompts. Naming the card and trying the next one on failure gives the same reach with no extra
/// interaction in the common case.
/// </para>
/// </remarks>
/// <param name="TokenLabel">The token as it names itself, e.g. <c>YubiKey PIV #36771502</c>.</param>
/// <param name="TokenSerial">Its serial, which is what distinguishes two of the same model.</param>
/// <param name="ObjectLabel">The certificate on it that the document is addressed to.</param>
public sealed record CardBeingUnlocked(string TokenLabel, string TokenSerial, string ObjectLabel);
