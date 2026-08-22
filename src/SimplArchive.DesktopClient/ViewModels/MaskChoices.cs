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
/// So a fixed mask becomes the ONLY choice: named, selectable, and with nothing to change it to — every
/// alternative is a refusal the containment invariant would deliver after the save rather than before it.
/// Its own type, rather than eight more lines in a 7,000-line view-model that is on the standing-debt list.
/// </para>
/// </remarks>
public static class MaskChoices
{
    /// <summary>True when the document's current mask is one the catalogue does not offer.</summary>
    public static bool IsFixed(IEnumerable<MaskChoiceViewModel> catalogue, Guid? currentMaskId) =>
        currentMaskId is { } id && !catalogue.Any(c => c.MaskId == id);

    /// <summary>
    /// Narrows <paramref name="choices"/> to the document's own mask when that mask cannot be chosen freely,
    /// and answers the choice to select. Leaves the catalogue untouched in the ordinary case.
    /// </summary>
    public static MaskChoiceViewModel Select(
        ObservableCollection<MaskChoiceViewModel> choices, DocumentsClient.MaskInfo current)
    {
        if (IsFixed(choices, current.MaskId) && current.MaskId is { } fixedId)
        {
            choices.Clear();
            choices.Add(new MaskChoiceViewModel(fixedId, current.Name ?? string.Empty));
        }

        return choices.FirstOrDefault(c => c.MaskId == current.MaskId) ?? choices[0];
    }
}
