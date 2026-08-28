using System.Collections.Generic;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Maps a mask's icon TOKEN, as the server sends it, to this client's Material Design Icons glyph.
/// </summary>
/// <remarks>
/// <para>
/// The twin of the web client's map, and deliberately a separate table rather than a shared one: the server
/// names the thing (<c>calendar</c>, <c>mailbox</c>) and each client answers from its own icon set. These two
/// sets share no name at all — the web draws Material, this draws MDI — so a single table could only have
/// held one of them.
/// </para>
/// <para>
/// <b>Every glyph here must have an <c>-outline</c> variant</b>, because the tree builds the empty-folder icon
/// by appending that suffix rather than listing outline names separately (see
/// <c>TreeNodeViewModel.IconValue</c>). All twelve were checked against the packaged set; a name without one
/// would render nothing at all, silently, and only for empty folders — a bug that hides until a user makes a
/// folder and looks at it before putting anything in.
/// </para>
/// <para>
/// An unknown token returns null and the caller keeps the glyph it already had, so a server that learns a new
/// token never leaves this client with a blank row.
/// </para>
/// </remarks>
public static class MaskIcon
{
    private static readonly Dictionary<string, string> Glyphs = new(System.StringComparer.Ordinal)
    {
        ["repository"] = "mdi-archive",
        // The glyph the personal space already wore here, now reached through the token so the tree, the list
        // pane and search cannot disagree. A contact gets a different one: a personal space and a person in an
        // addressbook are not the same thing.
        ["person"] = "mdi-account",
        ["mailbox"] = "mdi-mailbox",
        // The INBOX and its future siblings — "mail-folder", not "inbox", because SENT/DRAFTS/JUNK wear the
        // same mask and naming the token for today's only instance would be wrong when the second arrives.
        ["mail-folder"] = "mdi-inbox",
        // A user-created mail folder (#802) — stacked trays beside the standing tray, same distinction the
        // web client draws.
        ["mail-user-folder"] = "mdi-inbox-multiple",
        ["notebook"] = "mdi-notebook",
        ["section"] = "mdi-folder-text",
        // A BOOK with a person, not a contact card: mdi-contacts is person-shaped at 16 px, and so are the
        // personal space (mdi-account) and a Contact (mdi-card-account-details). Three person shapes in one
        // tree is three things the eye cannot separate.
        ["addressbook"] = "mdi-book-account",
        ["calendar"] = "mdi-calendar",
        ["email"] = "mdi-email",
        ["note"] = "mdi-note-text",
        ["contact"] = "mdi-card-account-details",
        ["appointment"] = "mdi-calendar-clock",
    };

    /// <summary>This client's glyph for a token, or null to keep whatever the caller would have drawn.</summary>
    public static string? For(string? token) =>
        token is not null && Glyphs.TryGetValue(token, out var glyph) ? glyph : null;
}
