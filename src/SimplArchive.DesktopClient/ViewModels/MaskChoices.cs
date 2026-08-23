using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// What the mask picker may offer for a document that is already classified.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue is filtered by the SERVER to the masks a user may freely choose (ADR 0653 / #671): a folder
/// mask types a folder, an extension-claimed mask is assigned by the classifier on upload. A document already
/// wearing one of those is therefore wearing a mask that is NOT in the list — which the picker used to handle
/// by falling through to its first entry, <c>(No mask)</c>.
/// </para>
/// <para>
/// That was silent data loss waiting to happen: opening the index editor on a typed folder — My Calendar, My
/// Addressbook, a Mailbox — pre-selected "no mask", so a save the user believed changed only a field would
/// propose stripping the folder's type. The web client showed the same document's mask as a bare GUID; neither
/// symptom was visible to any test, because both are what a control does with a value it was never given.
/// </para>
/// <para>
/// So the document's own mask is ADDED to the picker: named, selected, and sitting alongside the alternatives.
/// Added rather than replacing them — the catalogue's absence means "you may not CHOOSE this", not "you may
/// not choose anything". Narrowing the list to the current mask froze every folder and every extension-claimed
/// document, which is a rule nobody asked for: re-typing a Calendar costs only CalDAV subscribability. The
/// masks that genuinely cannot be re-typed are a smaller set, and are refused by the server.
/// Its own type, rather than eight more lines in a 7,000-line view-model that is on the standing-debt list.
/// </para>
/// </remarks>
public static class MaskChoices
{
    /// <summary>True when the document's current mask is one the catalogue does not carry.</summary>
    public static bool IsFixed(IEnumerable<MaskChoiceViewModel> catalogue, Guid? currentMaskId) =>
        currentMaskId is { } id && !catalogue.Any(c => c.MaskId == id);

    /// <summary>
    /// Adds the document's own mask to <paramref name="choices"/> when the catalogue does not carry it, and
    /// answers the choice to select. Leaves the catalogue untouched in the ordinary case.
    /// </summary>
    public static MaskChoiceViewModel Select(
        ObservableCollection<MaskChoiceViewModel> choices, DocumentsClient.MaskInfo current)
    {
        if (IsFixed(choices, current.MaskId) && current.MaskId is { } ownMaskId)
        {
            // Right after "(No mask)", so the document's own type reads first rather than last in a long list.
            choices.Insert(Math.Min(1, choices.Count), new MaskChoiceViewModel(ownMaskId, current.Name ?? string.Empty));
        }

        return choices.FirstOrDefault(c => c.MaskId == current.MaskId) ?? choices[0];
    }
}
