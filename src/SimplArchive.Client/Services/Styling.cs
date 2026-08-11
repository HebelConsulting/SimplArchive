namespace SimplArchive.Client.Services;

/// <summary>Small presentation helpers shared by more than one workbench pane.</summary>
public static class Styling
{
    /// <summary>
    /// Inline style for a colour-carrying chip (a tag, a sensitivity label). Empty when the catalog gave the
    /// entry no colour, so the chip falls back to MudBlazor's default rather than rendering an invalid rule.
    /// </summary>
    /// <remarks>
    /// Shared rather than copied: the contents list draws the sensitivity badge and the detail pane draws the
    /// same label plus the tag chips, and the two panes are being separated (ADR 0558).
    /// </remarks>
    public static string ChipStyle(string? color) => string.IsNullOrEmpty(color) ? "" : $"background:{color}; color:#fff;";
}
