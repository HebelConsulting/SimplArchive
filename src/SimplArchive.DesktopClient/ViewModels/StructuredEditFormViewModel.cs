using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// What the contact and appointment forms hold in common: whether they may be saved at all, and — when opened
/// for a NEW item (#631) — which collection it will be filed into.
/// </summary>
/// <remarks>
/// <para>
/// A base rather than the same four members written twice. They are not similar, they are identical, and the
/// cost of the copy is not the lines but the divergence nobody sees when only one of them is fixed.
/// </para>
/// <para>
/// The same dialog serves New and Edit, which is the point: a create form that models fewer fields than the
/// editor is a funnel that drops whatever the user typed into the missing ones, and a second dialog is a second
/// place for every later field to be added — or forgotten.
/// </para>
/// </remarks>
public abstract partial class StructuredEditFormViewModel : ObservableObject
{
    /// <summary>False when the caller may read but not save — the form opens with Save disabled.</summary>
    [ObservableProperty] private bool _canEdit = true;

    /// <summary>True when the dialog is composing a NEW item rather than editing a stored one.</summary>
    [ObservableProperty] private bool _isCreate;

    /// <summary>Where the new item will be filed. Null only when there is nowhere to file it.</summary>
    [ObservableProperty] private CreateTarget? _selectedTarget;

    /// <summary>The collections the server said the caller may create in — never a client-side guess.</summary>
    public ObservableCollection<CreateTarget> Targets { get; } = [];

    /// <summary>The stored item verbatim, once the disclosure has been opened; empty until then (#648).</summary>
    [ObservableProperty] private string _rawText = string.Empty;

    /// <summary>What was loaded, so a dirty check compares against the SERVER's text rather than a guess.</summary>
    private string _rawOriginal = string.Empty;

    /// <summary><c>vCard</c> or <c>iCalendar</c> — what the disclosure says it is showing.</summary>
    [ObservableProperty] private string _rawFormat = string.Empty;

    /// <summary>The token the raw save goes back under; its own read's, not the structured read's.</summary>
    public string RawETag { get; private set; } = string.Empty;

    /// <summary>False before the disclosure has been opened — the text is fetched on demand, not up front.</summary>
    [ObservableProperty] private bool _rawLoaded;

    /// <summary>
    /// Whether the disclosure is open. Bound two-way rather than left to the control, so the state is reachable
    /// without a visual tree — which is what lets the headless render open it and photograph the box.
    /// </summary>
    [ObservableProperty] private bool _rawExpanded;

    /// <summary>
    /// True once the user has actually changed the raw text. This decides WHICH save happens: a raw save
    /// replaces the whole item, so it must not run merely because somebody opened the box to look.
    /// </summary>
    public bool RawIsDirty => RawLoaded && !string.Equals(RawText, _rawOriginal, StringComparison.Ordinal);

    /// <summary>
    /// Whether the structured fields accept input. They go read-only while the raw text is dirty, because the
    /// two describe the same item and only one of them is about to be saved — leaving both live would let a
    /// user type into fields that are then discarded without a word (ADR 0550: a control that cannot do
    /// anything is noise that hides the one that can).
    /// </summary>
    public bool StructuredEnabled => CanEdit && !RawIsDirty;

    /// <summary>Hidden while composing a NEW item: there is no stored source to show yet.</summary>
    public bool ShowRaw => !IsCreate;

    partial void OnRawTextChanged(string value)
    {
        OnPropertyChanged(nameof(RawIsDirty));
        OnPropertyChanged(nameof(StructuredEnabled));
    }

    partial void OnCanEditChanged(bool value) => OnPropertyChanged(nameof(StructuredEnabled));

    /// <summary>Takes the loaded source as the baseline — so opening the box is not itself an edit.</summary>
    public void SetRaw(string text, string format, string etag)
    {
        _rawOriginal = text;
        RawFormat = format;
        RawETag = etag;
        RawText = text;
        RawLoaded = true;
        OnPropertyChanged(nameof(RawIsDirty));
        OnPropertyChanged(nameof(StructuredEnabled));
    }

    /// <summary>
    /// Whether to show the "file it into…" picker at all. Hidden for a single candidate: a chooser with one
    /// entry asks a question that has no second answer, and the status line names the collection afterwards
    /// regardless, so nothing is concealed by leaving it out.
    /// </summary>
    public bool ShowTargetPicker => IsCreate && Targets.Count > 1;

    partial void OnIsCreateChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTargetPicker));
        OnPropertyChanged(nameof(ShowRaw));
    }

    /// <summary>Puts the form into create mode over <paramref name="targets"/>, selecting the first.</summary>
    /// <remarks>
    /// The first is the tab's own ordering, which lists the caller's personal collection ahead of shared ones —
    /// so the default is the one a person filing something of their own almost always means.
    /// </remarks>
    public void OpenForCreate(IEnumerable<CreateTarget> targets)
    {
        Targets.Clear();
        foreach (var target in targets)
        {
            Targets.Add(target);
        }

        SelectedTarget = Targets.FirstOrDefault();
        IsCreate = true;
        OnPropertyChanged(nameof(ShowTargetPicker));
        OnOpenedForCreate();
    }

    /// <summary>
    /// A form's chance to seed the values a blank one has no sensible default for. Empty by default: a contact
    /// genuinely starts blank, while an appointment with no date at all is a form the user must fill twice
    /// before it means anything.
    /// </summary>
    protected virtual void OnOpenedForCreate()
    {
    }
}
