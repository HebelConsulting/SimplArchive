using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The detail pane's always-shown SYSTEM fields (ADR "System fields on the detail pane"): loading them for the
// selected document, and staging an OCR-language change for the pane's Save to persist.
//
// A folder advertises no versions, so the fields below the sensitivity/tags block -- which belong to a current
// VERSION -- are simply absent there rather than blank; OCR languages apply only to a TIFF-sourced document.
//
// Separate from DetailEdit, which owns the edit MODE: this is what the pane shows, that is what happens when
// you press Edit. The one overlap is StageOcrLanguages, which is staging rather than display -- it lives with
// the field it stages, because the view hands it a code list and nothing else touches it.
public sealed partial class MainWindowViewModel
{
    // Loads the always-shown system fields for the selected document (ADR "System fields + OCR-language mask
    // field"). OCR languages only apply to a TIFF-sourced document.
    /// <param name="versionsHref">Null for a FOLDER, which advertises no versions (#686) — the system fields
    /// below the sensitivity/tags block are a current version's, so there are none to read.</param>
    private async Task LoadSystemFieldsAsync(string documentSelfHref, string? versionsHref, string name)
    {
        SysName = name;
        // Cleared with everything else on subject change (ADR 0559): an inherited action would execute
        // against the wrong document.
        SetDetailGenericActions(null);
        _detailLinks = null;
        OnPropertyChanged(nameof(CanOpenBookings)); // the affordance must not outlive its subject (ADR 0559)
        SysDocumentDate = null;
        SysCreated = string.Empty;
        SysCreatedBy = string.Empty;
        SysWorkflowStatus = null;
        WorkflowTransitions.Clear();
        SysFileExtension = string.Empty;
        SysHasTiff = false;
        SysOcrLanguages = string.Empty;
        SysCurrentVersion = string.Empty;
        _sysOcrCodes = [];
        _stagedOcrCodes = [];

        if (_api is null)
        {
            return;
        }

        // Sensitivity label applies to any document (ADR "Configurable sensitivity labels + upload defaults") —
        // load it before the version-less early-return so a folder can show/edit it too.
        try
        {
            // One read of the document resource serves the label AND the external-links rel (issue #385).
            var detail = await _api.Documents.GetDocumentDetailAsync(documentSelfHref);
            var s = detail.Sensitivity;
            _detailSensitivityName = s.Name;
            _detailSensitivityColor = s.Color;
            _detailSensitivityWatermark = s.Watermark;
            DetailSensitivityId = s.LabelId;
            _detailExternalLinksHref = detail.ExternalLinksHref;
            // The rels this resource advertised, so the calls below follow addresses instead of composing
            // them from the id (ADR 0543, issue #416). Captured here because `detail` is scoped to this try.
            _detailLinks = detail.Links;
            SetDetailGenericActions(detail.GenericActions);
            OnPropertyChanged(nameof(CanOpenBookings));
            _detailDocumentName = detail.Name;
            CanShareDocument = detail.ExternalLinksHref is not null;
            // Folder-only, and read from the resource because a child folder's order is never fetched by the
            // parent's listing that opened this pane (issue #408).
            _detailSortOrder = detail.ContentsSortOrder;
            OnPropertyChanged(nameof(DetailSortText));
        }
        catch (Exception) { _detailSensitivityName = string.Empty; _detailSensitivityColor = null; _detailSensitivityWatermark = false; DetailSensitivityId = null; _detailExternalLinksHref = null; CanShareDocument = false; }
        // Sensitivity watermark on the preview (ADR "Document watermarking") — when the label's watermark flag is set.
        Preview.WatermarkText = _detailSensitivityWatermark ? $"{_detailSensitivityName} · {UserDisplayName}" : "";
        // Whether the current user follows this document (ADR "Document subscriptions").
        try { DetailSubscribed = await _api.Reminders.GetSubscriptionAsync(DetailHref("subscription")); } catch (Exception) { DetailSubscribed = false; }

        // Free-form tags (ADR "Document tags").
        DetailTags.Clear();
        try { foreach (var t in await _api.Tags.GetTagsAsync(DetailHref("tags"))) DetailTags.Add(t); } catch (Exception) { /* leave empty */ }
        HasDetailTags = DetailTags.Count > 0;

        if (versionsHref is null)
        {
            return; // a folder: no versions rel at all, so nothing below this line applies
        }

        var fields = await _api.Documents.GetSystemFieldsAsync(versionsHref);
        if (fields is null)
        {
            return; // no confirmed version yet
        }

        _sysCurrentVersionId = fields.CurrentVersionId;
        _sysDocumentDateHref = fields.DocumentDateHref;
        SysCurrentVersion = fields.CurrentVersionNumber.ToString();
        SysCreated = fields.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        SysCreatedBy = fields.CreatedByName;
        SysWorkflowStatus = fields.WorkflowStatus;
        // The transitions the detail pane may offer (#691) — skipped for the states that offer nothing.
        await LoadWorkflowTransitionsAsync(fields.WorkflowStatus, versionsHref);
        SysFileExtension = fields.FileExtension;
        SysDocumentDate = DateTime.TryParse(fields.DocumentDate, out var d) ? d.Date : null;
        SysHasTiff = fields.HasTiffVersion;

        if (SysHasTiff)
        {
            await (_ocrLanguages?.EnsureLoadedAsync() ?? Task.CompletedTask);

            _sysOcrCodes = string.IsNullOrWhiteSpace(fields.OcrLanguages) ? [] : fields.OcrLanguages.Split('+', StringSplitOptions.RemoveEmptyEntries);
            _stagedOcrCodes = _sysOcrCodes;
            SysOcrLanguages = (_ocrLanguages?.Describe(_sysOcrCodes) ?? "");
        }
    }

    // Exposes the catalog + the currently staged ordered selection to the picker dialog (the view owns the
    // dialog). The picker stages into the pane; the pane's single Save persists it.
    public (IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> Catalog, IReadOnlyList<string> Selected) OcrLanguagePickerState() =>
        (_ocrLanguages?.Options ?? [], _stagedOcrCodes);

    // Stages the picker's ordered selection (no API call) — persisted by SaveDetail, discarded by cancel.
    public void StageOcrLanguages(IReadOnlyList<string> codes)
    {
        _stagedOcrCodes = codes;
        SysOcrLanguages = (_ocrLanguages?.Describe(codes) ?? "");
    }
}
