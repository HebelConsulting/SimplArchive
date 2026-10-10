namespace SimplArchive.Presentation;

/// <summary>
/// Which text a document's machine states are shown with, as localization KEYS, so the desktop pane and the phone's
/// details answer identically (ADR 0917 and mobile ADR 0002: the phone mirrors the desktop pane).
/// </summary>
public static class DocumentStateText
{
    /// <summary>The workflow state's key; an unknown state is shown as it came (null means: show the raw value).</summary>
    public static string? WorkflowKey(string? status) => status switch
    {
        null or "" => "WfNotStarted",
        "Draft" => "WfStateDraft",
        "InReview" => "WfStateInReview",
        "Approved" => "WfStateApproved",
        "Rejected" => "WfStateRejected",
        "Released" => "WfStateReleased",
        _ => null,
    };

    /// <summary>The OCR verdict's key, or null when there is no verdict to show.</summary>
    public static string? OcrVerdictKey(string? verdict) => verdict switch
    {
        "ConvertibleScan" => "OcrVerdictConvertibleScan",
        "NotAScan" => "OcrVerdictNotAScan",
        "Unreadable" => "OcrVerdictUnreadable",
        _ => null,
    };
}
