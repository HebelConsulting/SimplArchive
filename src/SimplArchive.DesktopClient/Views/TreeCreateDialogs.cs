using Avalonia.Controls;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The tree menu's RICH creates — the kinds that need a form rather than a name (#689, ADR 0665).
/// </summary>
/// <remarks>
/// <para>
/// It lives here rather than in <c>MainWindow.axaml.cs</c> because opening a dialog needs an owner window and
/// nothing else: given the owner, the view-model and the entry the folder advertised, this is a complete unit.
/// The code-behind was already on the over-limit list, and the standing rule is that adding to one of those is
/// a reason to find the thing a home rather than a licence to make it longer.
/// </para>
/// <para>
/// Every entry on that menu carries a <c>prompt</c> naming what to ask for. <c>name</c> and <c>note</c> stay in
/// the code-behind with the rest of the menu, because their dialogs are one line each; these two are here
/// because a person and an event are forms, and the forms already exist — they are the Contacts and Calendar
/// tabs' own. Reusing them is the point: two forms for one object is how they come to disagree about which
/// fields are required.
/// </para>
/// </remarks>
internal static class TreeCreateDialogs
{
    /// <summary>
    /// Opens the dialog this entry asks for and creates from the filled-in form. Does nothing when the user
    /// cancels — the whole resource goes in one request, so nothing exists until Save.
    /// </summary>
    public static async Task CreateAsync(
        Window owner, MainWindowViewModel vm, TreeNodeViewModel node, Services.CreatableChild admitted)
    {
        // The folder itself, as the one place this create can land: it fixes the destination AND hides the
        // dialog's collection picker, which both clients draw only above a single target.
        var targets = new[] { new CreateTarget(node.Name, admitted.Href) };

        object payload;
        string typed;
        string okKey;
        string errKey;

        if (admitted.Prompt == "contact")
        {
            var form = new ContactEditViewModel();
            form.OpenForCreate(targets);
            if (await new ContactDialog(form).ShowDialog<ContactEditViewModel?>(owner) is not { } filled)
            {
                return;
            }

            payload = filled.ToPayload();
            // What the user TYPED, for the message only. The server derives the filed name and may disambiguate
            // a sibling clash, so this is not something to address anything by later (ADR 0559) — it is the
            // answer to "did that work", and the words just entered are what make it one.
            typed = $"{filled.GivenName} {filled.FamilyName}".Trim() is { Length: > 0 } person
                ? person
                : filled.Organization;
            (okKey, errKey) = ("StContactCreated", "StErrCreateContact");
        }
        else
        {
            var form = new AppointmentEditViewModel();
            form.OpenForCreate(targets);
            if (await new AppointmentDialog(form).ShowDialog<AppointmentEditViewModel?>(owner) is not { } filled)
            {
                return;
            }

            payload = filled.ToPayload();
            typed = filled.Summary;
            (okKey, errKey) = ("StApptCreated", "StErrCreateAppt");
        }

        // The href the ENTRY carried, never one composed from the node (ADR 0543/0559).
        await vm.CreateStructuredChildAsync(node.Id, admitted.Href, payload, okKey, errKey, typed, node.Name);
    }
}
