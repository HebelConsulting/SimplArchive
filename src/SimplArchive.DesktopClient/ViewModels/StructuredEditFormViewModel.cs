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

    partial void OnCanEditChanged(bool value)
    {
        OnPropertyChanged(nameof(StructuredEnabled));
        OnPropertyChanged(nameof(CanCommit));
    }

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
    /// <summary>
    /// Whether to offer the collection picker — on CREATE it chooses where the entry lands, on EDIT it MOVES
    /// it (#1122).
    /// </summary>
    /// <remarks>
    /// No longer <c>IsCreate &amp;&amp;</c>: an entry filed in the wrong calendar could be edited but never
    /// re-filed, so the fix was to open somewhere else and retype it. The caller decides which targets are
    /// offered — on edit, only collections that admit this entry's kind, so the picker cannot propose a move
    /// the server refuses on containment (ADR 0543).
    /// </remarks>
    /// <summary>Whether to offer the collection PICKER — more than one candidate to choose between.</summary>
    public bool ShowTargetPicker => Targets.Count > 1;

    /// <summary>
    /// Whether to state the destination as plain text — exactly one candidate, so there is nothing to choose
    /// but the user still has to be able to see where this will go (#1125).
    /// </summary>
    /// <remarks>
    /// It used to show nothing at all in this case, and that is how an entry meant for a room's Schedule was
    /// filed as an availability window: the dialog said "New appointment" and never named the collection.
    /// A read-only line rather than a one-item dropdown, so nothing implies a choice that does not exist.
    /// </remarks>
    public bool ShowTargetName => Targets.Count == 1;

    /// <summary>The single destination, for <see cref="ShowTargetName"/>.</summary>
    public string TargetName => Targets.Count == 1 ? Targets[0].DisplayName : string.Empty;

    /// <summary>
    /// A create cannot be committed until a destination is chosen — which matters only when nothing could be
    /// pre-selected, i.e. when the candidates differ in kind (#1125).
    /// </summary>
    public bool HasTarget => !IsCreate || SelectedTarget is not null;

    /// <summary>
    /// Whether Save may be pressed: the caller may edit AND a destination is settled.
    /// </summary>
    /// <remarks>
    /// BOTH, deliberately. Binding Save to <see cref="HasTarget"/> alone would drop the read-only gate and let
    /// an entry the caller cannot edit be saved — which is what the first version of this change did.
    /// </remarks>
    public bool CanCommit => CanEdit && HasTarget;

    partial void OnSelectedTargetChanged(CreateTarget? value)
    {
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(CanCommit));
    }

    partial void OnIsCreateChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTargetPicker));
        OnPropertyChanged(nameof(ShowTargetName));
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(ShowRaw));
    }

    /// <summary>Puts the form into create mode over <paramref name="targets"/>, selecting the first.</summary>
    /// <remarks>
    /// The first is the tab's own ordering, which lists the caller's personal collection ahead of shared ones —
    /// so the default is the one a person filing something of their own almost always means.
    /// </remarks>
    /// <summary>
    /// Puts the form into EDIT mode over the collections this entry may be moved between, selecting the one it
    /// is in now (#1122).
    /// </summary>
    /// <remarks>
    /// <paramref name="current"/> is matched by collection id rather than by reference: the targets are rebuilt
    /// from the tab's listing each time the dialog opens, so the instance is never the same object.
    /// </remarks>
    public void OpenForMove(IEnumerable<CreateTarget> targets, Guid current)
    {
        Targets.Clear();
        foreach (var target in targets)
        {
            Targets.Add(target);
        }

        SelectedTarget = Targets.FirstOrDefault(t => t.CollectionId == current);
        OriginalTargetId = SelectedTarget?.CollectionId ?? current;
        OnPropertyChanged(nameof(ShowTargetPicker));
        OnPropertyChanged(nameof(ShowTargetName));
        OnPropertyChanged(nameof(TargetName));
    }

    /// <summary>Where the entry was when the form opened, so a save knows whether it must also move it.</summary>
    public Guid OriginalTargetId { get; private set; }

    /// <summary>True when the user picked a different collection than the entry is filed in.</summary>
    public bool TargetChanged =>
        SelectedTarget is { } target && target.CollectionId != Guid.Empty && target.CollectionId != OriginalTargetId;

    public void OpenForCreate(IEnumerable<CreateTarget> targets)
    {
        Targets.Clear();
        foreach (var target in targets)
        {
            Targets.Add(target);
        }

        // Pre-select ONLY when every candidate means the same thing (#1125).
        //
        // The first was always taken before, on the reasoning that the tab lists the caller's personal
        // collection ahead of shared ones — sound for a set of calendars, and wrong the moment a bookable
        // resource contributes three collections of DIFFERENT meaning. Those sort alphabetically, so
        // "Availability" came first and a booking meant for the Schedule was silently filed as an offer of
        // free time. Where the candidates disagree about what they are, guessing is not a convenience; the
        // dialog asks, and Save waits.
        SelectedTarget = Targets.Select(t => t.CollectionKind).Distinct(StringComparer.Ordinal).Count() > 1
            ? null
            : Targets.FirstOrDefault();
        IsCreate = true;
        OnPropertyChanged(nameof(ShowTargetPicker));
        OnPropertyChanged(nameof(ShowTargetName));
        OnPropertyChanged(nameof(TargetName));
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(CanCommit));
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
