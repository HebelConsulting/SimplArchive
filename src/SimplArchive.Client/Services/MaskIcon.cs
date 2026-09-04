using MudBlazor;

namespace SimplArchive.Client.Services;

/// <summary>
/// Maps a mask's icon TOKEN, as the server sends it, to this client's Material glyph.
/// </summary>
/// <remarks>
/// <para>
/// The server names the thing (<c>calendar</c>, <c>mailbox</c>) and each client picks the glyph its own set
/// has for it — the desktop draws from Material Design Icons and shares not one icon name with Material, so a
/// concrete name on the wire could only ever have been right for one of the two.
/// </para>
/// <para>
/// <b>An unknown token is not an error.</b> Both lookups return null for a token this client has never heard
/// of, and the caller falls back to the generic folder/document glyph — so a server that learns a new token
/// degrades an older client to the icon it drew before, rather than breaking it. That is the whole reason the
/// column carries no CHECK constraint.
/// </para>
/// <para>
/// Every folder token needs BOTH a filled and an outline glyph, because an empty folder drops to the outline
/// form so "nothing here" is carried by shape rather than colour alone (ADR "Folder icon scheme"). The item
/// tokens are given a pair too, though nothing reads it: an item is never an empty folder. A uniform table is
/// cheaper to read than one with holes and a rule about which entries may have them.
/// </para>
/// </remarks>
public static class MaskIcon
{
    private static readonly Dictionary<string, (string Filled, string Outlined)> Glyphs = new(StringComparer.Ordinal)
    {
        // Folder masks — the outline half of each pair is load-bearing.
        ["repository"] = (Icons.Material.Filled.Inventory2, Icons.Material.Outlined.Inventory2),
        // The glyph the personal space has always worn in the tree, now sourced from here so the tree, the
        // contents list and search cannot disagree about it. Contact deliberately gets a DIFFERENT one below:
        // a personal space and a person in an addressbook are not the same thing.
        ["person"] = (Icons.Material.Filled.Person, Icons.Material.Outlined.Person),
        ["mailbox"] = (Icons.Material.Filled.MarkunreadMailbox, Icons.Material.Outlined.MarkunreadMailbox),
        // The INBOX and its future siblings. A tray rather than a folder-with-envelope, which Material has no
        // glyph for; the token is "mail-folder" because SENT/DRAFTS/JUNK will wear the same mask.
        ["mail-folder"] = (Icons.Material.Filled.Inbox, Icons.Material.Outlined.Inbox),
        // A user-created mail folder (#802): stacked trays, not the single tray — it sits beside the standing
        // folders under My Mailbox and must read as "mine, holds mail" at 16 px.
        ["mail-user-folder"] = (Icons.Material.Filled.AllInbox, Icons.Material.Outlined.AllInbox),
        ["notebook"] = (Icons.Material.Filled.MenuBook, Icons.Material.Outlined.MenuBook),
        ["section"] = (Icons.Material.Filled.Topic, Icons.Material.Outlined.Topic),
        // A BOOK with a person, not a contact card. Contacts/ContactPage are person-shaped at 16 px and so is
        // the personal space's Person glyph, and three person shapes in one tree is three things the eye cannot
        // separate. Found by looking at the rendered tree, which no name-comparison test can do.
        ["addressbook"] = (Icons.Material.Filled.ImportContacts, Icons.Material.Outlined.ImportContacts),
        ["calendar"] = (Icons.Material.Filled.CalendarMonth, Icons.Material.Outlined.CalendarMonth),
        // A room's booking calendar (ADR 0744): a calendar with a check — bookings, not appointments —
        // because a month grid beside a month grid is two things the eye cannot separate.
        ["schedule"] = (Icons.Material.Filled.EventAvailable, Icons.Material.Outlined.EventAvailable),

        // Item masks — the outline half is never read (an item is not a folder, so it is never empty).
        ["email"] = (Icons.Material.Filled.Email, Icons.Material.Outlined.Email),
        ["note"] = (Icons.Material.Filled.StickyNote2, Icons.Material.Outlined.StickyNote2),
        ["contact"] = (Icons.Material.Filled.ContactPage, Icons.Material.Outlined.ContactPage),
        ["appointment"] = (Icons.Material.Filled.Event, Icons.Material.Outlined.Event),
    };

    /// <summary>This client's filled glyph for a token, or null to fall back to the shape default.</summary>
    public static string? Filled(string? token) =>
        token is not null && Glyphs.TryGetValue(token, out var g) ? g.Filled : null;

    /// <summary>This client's outline glyph for a token, or null to fall back to the shape default.</summary>
    public static string? Outlined(string? token) =>
        token is not null && Glyphs.TryGetValue(token, out var g) ? g.Outlined : null;
}
