namespace SimplArchive.Presentation;

/// <summary>
/// One system row of a document's details, named once so every surface that presents a document presents the
/// same list in the same order.
/// </summary>
public enum DocumentDetailRow
{
    Name,
    FileExtension,
    WorkflowStatus,
    DocumentDate,
    OcrLanguages,
    OcrStatus,
    Created,
    CreatedBy,
    CurrentVersion,
    Size,
    Retention,
}

/// <summary>
/// The ordered system rows a document's details are presented as — the SAME list the index-data pane draws and
/// IMAP serves in a synthetic message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Standing principle: IMAP shows exactly the information from the details pane.</b> A mail client browsing
/// the archive is looking at the same documents as the workbench, and a reader who compares the two is entitled
/// to see one list twice rather than two overlapping ones. Before this, they overlapped and disagreed: IMAP
/// omitted the name, the file extension, the workflow status, the OCR status and the retention, ordered what
/// remained differently, and showed a Size row the pane did not have — so "what does the archive say about this
/// document?" had two answers depending on which surface you asked.
/// </para>
/// <para>
/// <b>Why the order lives here and not in either surface.</b> Two lists that must agree will not, if each keeps
/// its own copy — the question is only how long the drift takes to be noticed, and a mail client is exactly the
/// place nobody looks. This type is the one place the order and the label keys are written down; the pane and
/// the IMAP builder both read it, so adding a row means adding it here and the compiler finds the rest.
/// </para>
/// <para>
/// <b>Sensitivity is deliberately NOT in this list.</b> It renders after the mask and its index fields on both
/// surfaces, not among the system rows, so it is sequenced by the renderer rather than by this order. Putting
/// it here would have made the list wrong in a way that looks right.
/// </para>
/// <para>
/// It lives in <c>SimplArchive.Presentation</c> for the reason that project exists: the pure answer to a
/// question two surfaces must answer identically. Note that the two surfaces render DIFFERENTLY — the pane
/// draws a table with editors and buttons, IMAP writes label-padded lines and <c>X-</c> headers — which is
/// their own business. What may not differ is which rows exist and in what order.
/// </para>
/// </remarks>
public static class DocumentDetailRows
{
    /// <summary>The system rows, in the order both surfaces present them.</summary>
    public static IReadOnlyList<DocumentDetailRow> InOrder { get; } =
    [
        DocumentDetailRow.Name,
        DocumentDetailRow.FileExtension,
        DocumentDetailRow.WorkflowStatus,
        DocumentDetailRow.DocumentDate,
        DocumentDetailRow.OcrLanguages,
        DocumentDetailRow.OcrStatus,
        DocumentDetailRow.Created,
        DocumentDetailRow.CreatedBy,
        DocumentDetailRow.CurrentVersion,
        DocumentDetailRow.Size,
        DocumentDetailRow.Retention,
    ];

    /// <summary>
    /// The localization key for a row's label, so both surfaces print the same word for the same fact — in the
    /// reader's language on one, and in the message on the other.
    /// </summary>
    public static string LabelKey(DocumentDetailRow row) => row switch
    {
        DocumentDetailRow.Name => "Name",
        DocumentDetailRow.FileExtension => "SysFileExtension",
        DocumentDetailRow.WorkflowStatus => "WorkflowStatus",
        DocumentDetailRow.DocumentDate => "VerColDocDate",
        DocumentDetailRow.OcrLanguages => "SysOcrLanguages",
        DocumentDetailRow.OcrStatus => "SysOcrStatus",
        DocumentDetailRow.Created => "SysCreated",
        DocumentDetailRow.CreatedBy => "SysCreatedBy",
        DocumentDetailRow.CurrentVersion => "SysCurrentVersion",
        DocumentDetailRow.Size => "SysSize",
        DocumentDetailRow.Retention => "SysRetention",
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Every row must name its label key."),
    };

    /// <summary>
    /// The <c>X-</c> header name IMAP carries a row as, so a client can filter on it. Derived from the row
    /// rather than from the label, because a label is translated and a header name must not be.
    /// </summary>
    public static string HeaderName(DocumentDetailRow row) => $"X-SimplArchive-{row}";
}
