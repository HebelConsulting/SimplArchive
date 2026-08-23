using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>A mask the caller may assign, as offered by the mask picker.</summary>
/// <remarks>
/// <para>
/// Shared because two surfaces edit index data against a mask: the Repositories index-data pane, and the Intray
/// staging form that fills a draft before a file is filed. One shape read two ways is what drifts (ADR 0558).
/// </para>
/// <para>
/// Named <c>MaskSummary</c>, not <c>MaskOption</c>: MudBlazor is imported globally and ships its own
/// <c>MaskOption</c>, so the obvious name is ambiguous in every file. It was fine as a private nested type and
/// became a compile error the moment it was promoted — the rename keeps it usable without a qualifier.
/// </para>
/// </remarks>
public record MaskSummary(Guid Id, string Name, bool IsFreelyAssignable = true)
{
    /// <summary>The row's advertised addresses — its <c>self</c> is where the mask's field definitions live,
    /// so a picker choice is followed rather than rebuilt from the id (ADR 0543, #416).</summary>
    public List<LinkResponse> Links { get; init; } = [];
}

/// <summary>The masks available to the current tenant.</summary>
public record MaskListResponse
{
    public List<MaskSummary> Masks { get; set; } = [];
}

/// <summary>The field definitions of one mask version, used to build the edit form.</summary>
public record MaskFieldsResponse
{
    public List<MaskFieldInfo> Fields { get; set; } = [];
}

/// <summary>One index-field definition: what to label it, how to edit it, and whether it must be filled.</summary>
public record MaskFieldInfo
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Text / Number / Date / DateTime / Boolean / SingleSelect / MultiSelect / EmailAddress.</summary>
    public string DataType { get; set; } = "Text";

    public bool IsRequired { get; set; }

    /// <summary>Whether the field holds many values rather than one (#703) — orthogonal to
    /// <see cref="DataType"/>, and decided by the server rather than inferred from the type.</summary>
    public bool IsList { get; set; }
}

/// <summary>An OCR language the tenant offers, for the per-item language picker.</summary>
public record OcrLanguageOption(string Code, string DisplayName);

/// <summary>The OCR languages available to choose from.</summary>
public record OcrCatalogResponse
{
    public List<OcrLanguageOption> Languages { get; set; } = [];
}

/// <summary>
/// One editable index field in a mask edit form — type-aware, mirroring the desktop's
/// <c>MaskFieldEditViewModel</c> (ADRs 0273/0276/0278).
/// </summary>
/// <remarks>
/// A mutable class rather than a record because the form binds its inputs straight to these properties and edits
/// a field in place. The <c>Is*</c> flags exist so the markup picks an editor without re-parsing
/// <see cref="DataType"/> at every use, and <see cref="ToValues"/> is the single place that turns whichever
/// editor was used back into the wire shape.
/// </remarks>
public sealed class EditField
{
    public Guid FieldDefinitionId { get; init; }

    public string Label { get; init; } = "";

    public string DataType { get; init; } = "Text";

    public bool Required { get; init; }

    /// <summary>Whether this field holds many values (#703).</summary>
    public bool IsList { get; init; }

    public string TextValue { get; set; } = "";

    public DateTime? DateValue { get; set; }

    public bool BoolValue { get; set; }

    public bool IsDate => DataType == "Date";

    public bool IsBoolean => DataType == "Boolean";

    /// <summary>
    /// Whether this field is edited as a LIST — a multi-line box, one value per line.
    /// </summary>
    /// <remarks>
    /// Either because the field says so (<see cref="IsList"/>, #703) or because its type already means it
    /// (MultiSelect, grandfathered). Asked once, here, so the two clients and the two surfaces that use them
    /// cannot answer it differently — and so the markup picks an editor without re-deriving the rule.
    /// </remarks>
    public bool IsMultiLine => IsList || DataType == "MultiSelect";

    public bool IsSingleLine => !IsDate && !IsBoolean && !IsMultiLine;

    public static EditField Create(MaskFieldInfo f, List<string> values)
    {
        var field = new EditField { FieldDefinitionId = f.Id, Label = f.IsRequired ? $"{f.Name} *" : f.Name, DataType = f.DataType, Required = f.IsRequired, IsList = f.IsList };

        // Multiplicity is decided BEFORE the type: a list of dates is a list first, so it gets the list
        // editor rather than a date picker that could only ever hold one of them.
        if (field.IsMultiLine)
        {
            field.TextValue = string.Join("\n", values);
            return field;
        }

        switch (f.DataType)
        {
            case "Date": field.DateValue = DateTime.TryParse(values.FirstOrDefault(), out var d) ? d.Date : null; break;
            case "Boolean": field.BoolValue = values.FirstOrDefault() == "true"; break;
            default: field.TextValue = values.FirstOrDefault() ?? ""; break;
        }

        return field;
    }

    public List<string> ToValues() => IsMultiLine
        ? TextValue.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        : DataType switch
        {
            "Date" => DateValue is { } d ? [d.ToString("yyyy-MM-dd")] : [],
            "Boolean" => [BoolValue ? "true" : "false"],
            _ => string.IsNullOrWhiteSpace(TextValue) ? [] : [TextValue.Trim()],
        };
}
