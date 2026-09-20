using SimplArchive.DesktopClient.Services;
using SimplArchive.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;

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
    // The filing INSTANT, stored raw and rendered derived — the same shape as SysDocumentDate rather than a
    // pre-formatted string (#1315). A string materialised once at load cannot re-render when the viewer
    // changes their display zone, so the two rows of this pane would disagree until a reload: the document
    // date would move and the filing date would sit there, which is exactly how the defect was reported.
    //
    // Here rather than beside the other Sys* fields in MainWindowViewModel.cs because that file is on the
    // 1000-line standing-debt list and may only shrink — and this partial is where the detail fields are
    // loaded anyway, so the field and its only writer now sit together.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SysCreated))] private DateTimeOffset? _sysCreatedAt;

    // With the zone marker, so it reads alike to the document date directly above it in the pane. Without
    // it a viewer whose device and preference differ cannot tell which clock either row is on, which is what
    // let the wrong one go unnoticed.
    public string SysCreated => SysCreatedAt is { } at
        ? SimplArchive.Presentation.InstantFormat.Display(at, Services.SessionTimeZone.Current)
        : string.Empty;

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
        SetDetailMachineStatuses(null);
        SetDetailModuleActions(null);
        _detailLinks = null;
        OnPropertyChanged(nameof(CanOpenBookings)); // the affordance must not outlive its subject (ADR 0559)
        SysDocumentDate = null;
        SysCreatedAt = null;
        SysCreatedBy = string.Empty;
        SysWorkflowStatus = null;
        WorkflowTransitions.Clear();
        SysFileExtension = string.Empty;
        SysOcrCandidate = false;
        SetOcrStatus(null, null); // an address must not outlive its subject (ADR 0559)
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
            SetDetailMachineStatuses(detail.MachineStatuses);
            SetDetailModuleActions(detail.ModuleActions);
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
        SysCreatedAt = fields.CreatedAt;
        SysCreatedBy = fields.CreatedByName;
        SysWorkflowStatus = fields.WorkflowStatus;
        // The transitions the detail pane may offer (#691) — skipped for the states that offer nothing.
        await LoadWorkflowTransitionsAsync(fields.WorkflowStatus, versionsHref);
        SysFileExtension = fields.FileExtension;
        SysDocumentDate = DateTime.TryParse(fields.DocumentDate, out var d) ? d.Date : null;
        SysDocumentTime = fields.DocumentTime;
        DocumentTimeEntry = fields.DocumentTime ?? string.Empty;
        SysOcrCandidate = fields.IsOcrCandidate;
        SetOcrStatus(fields.OcrVerdict, fields.MakeSearchableHref);

        if (SysOcrCandidate)
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

    // Loads the selected node's mask line and index fields — or leaves "No mask" when the node advertises no
    // `mask` rel, the personal-space root among them. That absence is the ANSWER (like `versions` for a folder),
    // not a failure: following a rel the resource never advertised is what threw the ADR 0543 "rel not
    // advertised for 'Demo Admin'" status line on the kiosk. Its own superseded checks mirror LoadDetailAsync's
    // (ADR 0559), so a stale load stops rather than repainting the pane for the previous subject.
    private async Task LoadMaskAndIndexAsync(NodeViewModel document)
    {
        if (document.TryHref("mask") is not { } maskHref)
        {
            MaskLine = "No mask";
            return;
        }

        var mask = await _api!.Documents.GetMaskAsync(maskHref);
        if (_selectedDocumentId != document.Id) { return; }

        MaskLine = mask.Name is null ? "No mask" : $"Mask: {mask.Name}" + (mask.VersionNumber is { } v ? $" · version {v}" : "");

        if (document.TryHref("index-data") is not { } indexHref) { return; }

        var indexData = await _api!.Documents.GetIndexDataAsync(indexHref);
        if (_selectedDocumentId != document.Id) { return; }

        foreach (var field in indexData)
        {
            // Through the one factory (its own comment warns about exactly this copy): this site hand-rolled
            // the row, so the MAIN tab showed a DateTime as the raw wire instant while every other tab
            // rendered it — and it is what gives Url fields their link rows (ADR 0763).
            IndexFields.Add(IndexFieldViewModel.From(field, OpenDocumentByIdAsync));
        }
    }
}
