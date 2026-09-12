using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// A mask choice in the mask-change dropdown (ADR "Editable mask on the detail pane"). MaskId null = "(No mask)".
// Mask is the catalogue row the server sent, carried so reading the mask's fields follows the address it
// advertised (ADR 0543/0555). Null for the "(No mask)" entry and for the designer-preview rows, which reach
// no server — every real, selectable mask has it.
public sealed record MaskChoiceViewModel(Guid? MaskId, string Name, SimplArchive.DesktopClient.Services.MasksClient.MaskOptionInfo? Mask = null)
{
    public override string ToString() => Name;
}

// One editable index field in the mask edit mode — renders a type-aware editor bound to the field's DataType
// (ADR "Editable mask on the detail pane"): a date picker for Date, a checkbox for Boolean, a multi-line box
// for a LIST (one value per line), else a single-line text box (Text / Number / SingleSelect / EmailAddress).
//
// The web client's EditField is the same shape and must stay it (ADR 0511) — one surface, two renderings.
public sealed partial class MaskFieldEditViewModel : ObservableObject
{
    public required Guid FieldDefinitionId { get; init; }

    public required string Name { get; init; }

    public required string DataType { get; init; }

    public bool IsRequired { get; init; }

    /// <summary>Whether this field holds many values (#703).</summary>
    public bool IsList { get; init; }

    /// <summary>The caller may see this field but not change it (#703) — a Mailbox's address list for a
    /// caller without the routing right. Rendered read-only, so the refusal happens here instead of on save.</summary>
    public bool Locked { get; init; }

    /// <summary>
    /// The address of the values already filed under this field, when the server offers them (#1127).
    /// </summary>
    /// <remarks>Absent means "no suggestions here" — the editor stays a plain text box (ADR 0543).</remarks>
    public string? ValuesHref { get; init; }

    /// <summary>
    /// Fetches the suggestions for what has been typed so far. Assigned by whoever builds the field, because
    /// this view-model owns no api client.
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>>? SuggestionSource { get; set; }

    /// <summary>Whether this field completes as you type — a single-line editor WITH an offered value list.</summary>
    /// <remarks>
    /// Reported from use of a module's Aerodrome field, and true of its METAR/TAF and NOTAM siblings: every
    /// textual field rendered as a bare text box, so a vocabulary the tenant already uses had to be retyped
    /// from memory. Not for the multi-line list editor, where a line is one of many and a dropdown over the
    /// caret would fight the editing.
    /// </remarks>
    public bool Completes => IsSingleLine && !Locked && SuggestionSource is not null;

    /// <summary>The plain text box, for a single-line field with nothing to complete from.</summary>
    public bool IsPlainSingleLine => IsSingleLine && !Completes;

    [ObservableProperty] private string _textValue = string.Empty;
    // DateTime?, NOT DateTimeOffset? — Avalonia ships TWO date controls with DIFFERENT property types, and
    // this one binds a CalendarDatePicker, whose SelectedDate is DateTime? (DatePicker's is DateTimeOffset?).
    // The mismatch does not fail the build or any C# test: it fails at BINDING time with "Could not cast
    // DateTimeOffset to DateTime?", leaving every Date and DateTime mask field silently uneditable — the
    // owner met it on the Enrollment mask. Every other date-bearing view-model here is DateTime? for exactly
    // this reason; this one was the outlier. (Same family as the NumericUpDown/decimal? trap.)
    [ObservableProperty] private System.DateTime? _dateValue;
    [ObservableProperty] private System.TimeSpan? _timeValue;
    [ObservableProperty] private bool _boolValue;

    /// <summary>The stored wire values, kept verbatim so a LOCKED field round-trips unchanged: the pane
    /// shows a readable rendering, but the full-replacement PUT must echo exactly what the server holds —
    /// the classifier-owned guard refuses anything else (ADR 0744-era pane fix).</summary>
    private IReadOnlyList<string> _originalValues = [];

    public bool IsDate => DataType == "Date" && !Locked;
    // A moment, not a day (#660): its editor is a date picker AND a time picker. Locked fields fall to the
    // read-only text rendering below regardless of type.
    public bool IsDateTime => DataType == "DateTime" && !Locked;
    public bool IsBoolean => DataType == "Boolean" && !Locked;
    // Either the field says it is a list, or its type already means it (MultiSelect, grandfathered) — #703.
    public bool IsMultiLine => !Locked && (IsList || DataType == "MultiSelect");
    public bool IsSingleLine => Locked || (!IsDate && !IsDateTime && !IsBoolean && !IsMultiLine);
    public string Label => IsRequired ? $"{Name} *" : Name;

    // ---- Machine proposals (ABI 0.11, ADR 0769) --------------------------------------------------------
    // A picker beside this field's editor, offered when a machine proposal fills it. Typing stays primary
    // (the typed-not-picked rule); each choice carries its own apply command so the flyout's menu items
    // need no cross-tree binding back to this view model.

    public ObservableCollection<ProposalChoice> Proposals { get; } = [];

    [ObservableProperty] private bool _hasProposals;

    [ObservableProperty] private string _proposalLabel = string.Empty;

    public sealed record ProposalChoice(string Display, System.Windows.Input.ICommand ApplyCommand, string Value);

    public void OfferProposals(string label, IEnumerable<(string Value, string Label, string? Detail)> items)
    {
        Proposals.Clear();
        foreach (var (value, itemLabel, detail) in items)
        {
            var display = string.IsNullOrEmpty(detail) ? itemLabel : $"{itemLabel} — {detail}";
            Proposals.Add(new ProposalChoice(display,
                new CommunityToolkit.Mvvm.Input.RelayCommand(() => TextValue = value), value));
        }

        ProposalLabel = label;
        HasProposals = Proposals.Count > 0;
    }

    public static MaskFieldEditViewModel Create(MasksClient.MaskFieldInfo definition, IReadOnlyList<string> values, bool mayRouteMail = true)
    {
        var field = new MaskFieldEditViewModel
        {
            FieldDefinitionId = definition.Id,
            Name = definition.Name,
            DataType = definition.DataType,
            IsRequired = definition.IsRequired,
            IsList = definition.IsList,
            // Read-only either for THIS caller (mail routing, #703) or for EVERY caller (the classifier
            // owns the value — Start/End/UIDs, ADRs 0743/0744; the real write path is the content editor).
            Locked = (definition.RequiresMailRouting && !mayRouteMail) || definition.ClassifierOwned,
            ValuesHref = definition.ValuesHref,
        };
        field._originalValues = [.. values];

        if (field.Locked)
        {
            // Readable, not raw: a DateTime shows its local wall clock instead of the ISO wire string.
            field.TextValue = string.Join(", ", values.Select(v =>
                definition.DataType == "DateTime" ? SimplArchive.Presentation.IndexInstant.Display(v) : v));
            return field;
        }

        // Multiplicity is decided BEFORE the type: a list of dates is a list first, so it gets the list editor
        // rather than a date picker that could only ever hold one of them.
        if (field.IsMultiLine)
        {
            field.TextValue = string.Join("\n", values);
            return field;
        }

        switch (definition.DataType)
        {
            case "Date":
                field.DateValue = values.Count > 0 && System.DateTime.TryParse(values[0], out var d)
                    ? d.Date : null;
                break;
            case "DateTime":
                var (day, time) = SimplArchive.Presentation.IndexInstant.Split(values.Count > 0 ? values[0] : null);
                field.DateValue = day;
                field.TimeValue = time;
                break;
            case "Boolean":
                field.BoolValue = values.Count > 0 && values[0].Equals("true", System.StringComparison.OrdinalIgnoreCase);
                break;
            default:
                field.TextValue = values.Count > 0 ? values[0] : "";
                break;
        }

        return field;
    }

    public IReadOnlyList<string> ToValues() => Locked
        // Echo the wire values verbatim: the PUT is a full replacement, and the server's classifier-owned
        // guard (rightly) reads any deviation — including a formatted rendering — as an attempted edit.
        ? _originalValues
        : IsMultiLine
            ? TextValue.Split('\n').Select(v => v.Trim()).Where(v => v.Length > 0).ToList()
            : DataType switch
            {
                "Date" => DateValue is { } d ? [d.ToString("yyyy-MM-dd")] : [],
                "DateTime" => SimplArchive.Presentation.IndexInstant.Compose(DateValue?.Date, TimeValue) is { } instant ? [instant] : [],
                "Boolean" => [BoolValue ? "true" : "false"],
                _ => string.IsNullOrWhiteSpace(TextValue) ? [] : [TextValue.Trim()],
            };
}
