using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

// The detail pane's ONE edit mode (ADR "Single pane-level edit toggle on the detail pane"): read-only until
// Edit, then every read-write field -- name, document date, OCR languages, mask and index data -- becomes
// editable at once; one Save persists only what changed, one Cancel discards it.
//
// Unusually for this file, the heading this arrived under was TRUE. It is the first section of the five taken
// out of this view model that did not have to be dealt out first (#941), so this is a plain contiguous move
// rather than a redistribution -- worth saying, because the previous four each looked like this until their
// members were listed.
//
// A partial rather than a type of its own: the pane edits the view model's own detail state and re-reads it on
// save, so a separate type would take the view model as a parameter and be a partial wearing a constructor.
public sealed partial class MainWindowViewModel
{
    // One edit mode governs the whole pane: read-only until Edit; every read-write field (Name, Document date,
    // OCR languages, mask + index data) becomes editable at once; one Save persists only the changed fields,
    // one Cancel discards them.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOcr))]
    [NotifyPropertyChangedFor(nameof(CanBeginEdit))]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeginEdit))]
    private bool _canEditDetail; // a document detail (not the repository-list root) is loaded

    [ObservableProperty] private MaskChoiceViewModel? _selectedMaskChoice;

    // Edit affordances: begin only when a detail is loaded and not already editing; the OCR picker only for a
    // TIFF-sourced document and only while editing.
    public bool CanBeginEdit => CanEditDetail && !IsEditing;

    // Seeds the staged contents order from the subject, so opening the edit does not silently change it.
    private void StageSortOrder() => EditSortOrder = _detailSortOrder;
    public bool CanEditOcr => IsEditing && SysOcrCandidate;

    public ObservableCollection<MaskChoiceViewModel> AvailableMasks { get; } = [];
    public ObservableCollection<MaskFieldEditViewModel> MaskEditFields { get; } = [];

    private Guid? _originalMaskId;
    private string _originalName = string.Empty;

    // The detail as it stood when the pencil opened, kept WHOLE rather than field by field: the save is a PUT
    // of the full detail, so an aspect this pane does not show must still be echoed back — omitting it would
    // clear it.
    private JsonElement? _detailBaseline;
    private string? _detailEtag;
    private DateTime? _originalDocumentDate;
    private string? _originalDocumentTime;
    private bool _loadingMaskEdit;

    [RelayCommand]
    private async Task BeginEditAsync()
    {
        if (_api is null || _selectedDocumentId is not { } documentId)
        {
            return;
        }

        try
        {
            _loadingMaskEdit = true;
            StageSortOrder();

            // Snapshot the read-write system fields so Save can persist only what actually changed and Cancel
            // can restore them.
            // The read that fills the form, and the tag a save is measured against (ADR 0794). Taken HERE
            // because the user cannot have edited anything before opening the pencil, so "as it was when I
            // opened the form" is exactly what this asserts.
            (_detailBaseline, _detailEtag) = await _api.Documents.GetDetailAsync(DetailHref("detail"));

            _originalName = SysName;
            // The originals stay the STORED (UTC) pair, because Cancel restores them verbatim. The FIELDS are
            // filled with the same instant in the viewer's zone (#1254): the pane shows a local time read-only,
            // so opening the pencil on the UTC value would jump the clock the moment it was clicked.
            _originalDocumentDate = SysDocumentDate;
            _originalDocumentTime = SysDocumentTime;
            var (localDate, localTime) =
                DocumentDateFormat.FieldsInZone(SysDocumentDate, SysDocumentTime, Services.SessionTimeZone.Current);
            SysDocumentDate = localDate;
            DocumentTimeEntry = localTime ?? string.Empty;
            _stagedOcrCodes = _sysOcrCodes;
            RebuildSensitivityPicker();
            SelectedSensitivityItem = SensitivityPickerItems.FirstOrDefault(i => i.Id == DetailSensitivityId) ?? SensitivityPickerItems.FirstOrDefault();

            // Tags (ADR "Document tags"): working copy + original for change detection + the tenant catalog.
            EditTags.Clear();
            foreach (var t in DetailTags) EditTags.Add(t);
            _origTags = [.. DetailTags];
            NewTag = string.Empty;
            if (TagCatalog.Count == 0)
            {
                try { foreach (var t in await _api.Tags.GetTagCatalogAsync()) TagCatalog.Add(t); } catch (Exception) { /* optional */ }
            }

            AvailableMasks.Clear();
            AvailableMasks.Add(new MaskChoiceViewModel(null, "(No mask)"));
            foreach (var mask in await _api.Masks.GetMasksAsync())
            {
                AvailableMasks.Add(new MaskChoiceViewModel(mask.Id, mask.Name, mask));
            }

            SelectedMaskChoice = MaskChoices.Select(AvailableMasks, await _api.Documents.GetMaskAsync(DetailHref("mask")));
            _originalMaskId = SelectedMaskChoice.MaskId; // Select always answers the document's own mask
            await LoadMaskEditFieldsAsync(SelectedMaskChoice.Mask, withCurrentValues: true);

            // Machine proposals (ABI 0.11, ADR 0769): the rels rode in with the document; the items are
            // fetched NOW so the candidate list is as fresh as the form. Best-effort like the tag catalog —
            // a failed fetch costs the picker, never the edit.
            foreach (var rel in (_detailLinks?.Rels ?? [])
                .Where(r => r.StartsWith("machine-proposal:", StringComparison.Ordinal)).ToList())
            {
                try
                {
                    var proposal = await _api.Documents.GetProposalAsync(_detailLinks!.Href(rel)!);
                    MaskEditFields.FirstOrDefault(f => f.Name == proposal.FillsField)
                        ?.OfferProposals(proposal.Label, proposal.Items.Select(i => (i.Value, i.Label, i.Detail)));
                }
                catch (Exception) { /* optional affordance */ }
            }

            IsEditing = true;
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrStartEdit"), e.Message));
        }
        finally
        {
            _loadingMaskEdit = false;
        }
    }

    // Re-load the field editors when the user picks a different mask (empty values — a different mask has
    // different fields). Suppressed during the initial edit load, which fills the current values instead.
    partial void OnSelectedMaskChoiceChanged(MaskChoiceViewModel? value) => Services.Safe.Fire(() => OnSelectedMaskChoiceChangedAsync(value));

    private async Task OnSelectedMaskChoiceChangedAsync(MaskChoiceViewModel? value)
    {
        if (_loadingMaskEdit || _selectedDocumentId is not { } documentId)
        {
            return;
        }

        await LoadMaskEditFieldsAsync(value?.Mask, withCurrentValues: false);
    }

    private async Task LoadMaskEditFieldsAsync(MasksClient.MaskOptionInfo? mask, bool withCurrentValues)
    {
        MaskEditFields.Clear();
        if (_api is null || mask is not { } chosen)
        {
            return;
        }

        var fields = await _api.Masks.GetMaskFieldsAsync(chosen);
        var valuesByName = withCurrentValues
            ? (await _api.Documents.GetIndexDataAsync(DetailHref("index-data"))).ToDictionary(f => f.FieldName, f => f.Values)
            : new Dictionary<string, IReadOnlyList<string>>();

        foreach (var field in fields)
        {
            var values = valuesByName.GetValueOrDefault(field.Name) is { } v ? v : [];
            var editor = MaskFieldEditViewModel.Create(field, values, CanManageMailRouting);

            // What the field completes from (#1127) — the values already filed under it. Assigned here rather
            // than resolved by the field itself, which owns no api client; absent when the server offered no
            // `values` rel, and the editor then stays a plain text box.
            if (editor.ValuesHref is { } valuesHref && _api is { } api)
            {
                editor.SuggestionSource = async (typed, cancellationToken) =>
                    await api.Masks.FieldValuesAsync(valuesHref, typed, cancellationToken);
            }

            MaskEditFields.Add(editor);
        }
    }

    // Persists every read-write field that actually changed. Each field is independent, so one failure doesn't
    // abort the others; if anything failed we stay in edit mode and report it, otherwise we drop to read-only.
    [RelayCommand]
    private async Task SaveDetailAsync()
    {
        // IsEditing guards the KEYBOARD path (ADR 0550): Ctrl/Cmd+S is bound window-wide, so without this a
        // save would fire while the pane is merely displaying a document.
        if (_api is null || !IsEditing || _selectedDocumentId is not { } documentId)
        {
            return;
        }

        // The typed time is the one thing that can be wrong before anything is sent (ADR 0758), so it is
        // checked here rather than becoming a refusal from the server.
        if (!DocumentDateFormat.TryParseTypedTime(DocumentTimeEntry, out _))
        {
            ReportError(string.Format(Strings.Get("StErrSaveJoin"), Strings.Get("SaveFailDocumentTime")));
            return;
        }

        var newName = SysName?.Trim() ?? string.Empty;
        var nameChanged = newName.Length > 0 && newName != _originalName;
        // Back out of the viewer's zone into the UTC the API stores (#1254). The DATE moves with the time — a
        // 00:30 entry typed in Zurich is the previous day in UTC — so both come from one conversion rather
        // than the date being sent as typed.
        var (utcDateStr, timeStr) =
            DocumentDateFormat.FieldsInUtc(SysDocumentDate, DocumentTimeEntry, Services.SessionTimeZone.Current);
        var editTags = EditTags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 100).Distinct().ToList();
        var chosenLabelId = SelectedSensitivityItem?.Id;
        var newMaskId = SelectedMaskChoice?.MaskId;
        var sortOrderChanged = _detailIsFolder && EditSortOrder != _detailSortOrder;

        // ONE request for the whole pencil (ADR 0794). It is a PUT of the full detail, so every aspect is
        // stated: the edited ones from the form, the rest echoed from the baseline this edit opened with — an
        // omitted aspect is a request to CLEAR it, not to leave it alone.
        string? Baseline(string property) =>
            _detailBaseline?.TryGetProperty(property, out var value) == true && value.ValueKind != JsonValueKind.Null
                ? value.GetString()
                : null;

        object Body(bool confirmDuplicateClaims) => new
        {
            name = newName.Length > 0 ? newName : Baseline("name"),
            documentDate = utcDateStr ?? Baseline("documentDate"),
            documentTime = timeStr ?? Baseline("documentTime"),
            ocrLanguages = _stagedOcrCodes,
            sensitivityLabelId = chosenLabelId,
            tags = editTags,
            fields = MaskEditFields.Select(f => new { fieldDefinitionId = f.FieldDefinitionId, values = f.ToValues() }),
            maskId = newMaskId,
            contentsSortOrder = sortOrderChanged ? (int?)EditSortOrder : null,
            confirmDuplicateClaims,
        };

        JsonElement saved;
        try
        {
            saved = await _api.Documents.SaveDetailAsync(
                DetailHref("detail"), Body, _detailEtag, ConfirmDuplicateClaimDialog);
        }
        catch (Exception e) when (e is ApiActionException or DuplicateAddressClaimException or DetailChangedElsewhereException)
        {
            // Stay in edit mode so the rejected value can be corrected — and so a 412 leaves the user's typing
            // in front of them rather than discarding it along with the save.
            ReportError(string.Format(Strings.Get("StErrSaveJoin"), e.Message));
            return;
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrSaveJoin"), e.Message));
            return;
        }

        AdoptSavedDetail(saved, chosenLabelId, utcDateStr, timeStr, newMaskId, editTags, sortOrderChanged, documentId);

        IsEditing = false;
        Status = Strings.Get("StSaved");
        await ReloadDetailAsync();
        if (nameChanged)
        {
            await ReloadTreeAsync();
            await LoadFolderContentsAsync(_currentFolderId ?? documentId);
        }
    }

    // Takes the SAVED detail as the pane's new truth, so nothing is left describing what was merely sent.
    private void AdoptSavedDetail(
        JsonElement saved, Guid? labelId, string? utcDateStr, string? timeStr, Guid? maskId, List<string> tags, bool sortOrderChanged, Guid documentId)
    {
        DetailTitle = _originalName = saved.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null
            ? name.GetString() ?? _originalName
            : _originalName;

        // Back to the STORED pair the read-only line converts for display (#1254). Leaving the picker's local
        // date here would have the display convert an already-local value a second time, shifting the document
        // by the offset on every save — a drift that compounds and that no single save looks wrong in.
        SysDocumentDate = _originalDocumentDate =
            DateTime.TryParse(utcDateStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var storedDay) ? storedDay : _originalDocumentDate;
        SysDocumentTime = _originalDocumentTime = timeStr;
        DocumentTimeEntry = timeStr ?? string.Empty;
        _sysOcrCodes = _stagedOcrCodes;

        var label = SensitivityCatalog.FirstOrDefault(l => l.Id == labelId);
        _detailSensitivityName = label?.Name ?? string.Empty;
        _detailSensitivityColor = label?.Color;
        _detailSensitivityWatermark = label?.Watermark ?? false;
        DetailSensitivityId = labelId;
        Preview.WatermarkText = _detailSensitivityWatermark ? $"{_detailSensitivityName} · {UserDisplayName}" : "";

        DetailTags.Clear();
        foreach (var tag in saved.TryGetProperty("tags", out var stored) && stored.ValueKind == JsonValueKind.Array
                     ? stored.EnumerateArray().Select(t => t.GetString() ?? string.Empty)
                     : tags)
        {
            DetailTags.Add(tag);
        }
        HasDetailTags = DetailTags.Count > 0;
        _origTags = [.. DetailTags];

        _originalMaskId = maskId;

        if (sortOrderChanged)
        {
            _detailSortOrder = EditSortOrder;
            OnPropertyChanged(nameof(DetailSortText));

            // The OPEN folder's listing re-sorts only when it is the folder that changed.
            if (_currentFolderId == documentId)
            {
                _folderSortOrder = EditSortOrder;
                _headerSortActive = false;
            }
        }
    }

    [RelayCommand]
    private async Task CancelEditAsync()
    {
        // Esc is bound window-wide and also exits the preview full-screen (ADR 0550), so this must do nothing
        // unless the pane is actually editing — the two states never coexist, and each command ignores the one
        // it does not own.
        if (!IsEditing)
        {
            return;
        }

        IsEditing = false;

        // Restore the staged system fields to their loaded values.
        SysName = _originalName;
        SysDocumentDate = _originalDocumentDate;
        SysDocumentTime = _originalDocumentTime;
        DocumentTimeEntry = _originalDocumentTime ?? string.Empty;
        _stagedOcrCodes = _sysOcrCodes;
        SysOcrLanguages = (_ocrLanguages?.Describe(_sysOcrCodes) ?? "");

        if (_selectedDocumentId is not null)
        {
            await ReloadDetailAsync();
        }
    }

    // Reloads the read-only mask line + index fields after a save/cancel.
    private async Task ReloadDetailAsync()
    {
        if (_api is null)
        {
            return;
        }

        var mask = await _api.Documents.GetMaskAsync(DetailHref("mask"));
        MaskLine = mask.Name is null ? "No mask" : $"Mask: {mask.Name}" + (mask.VersionNumber is { } v ? $" · version {v}" : "");

        IndexFields.Clear();
        foreach (var field in await _api.Documents.GetIndexDataAsync(DetailHref("index-data")))
        {
            IndexFields.Add(IndexFieldViewModel.From(field, OpenDocumentByIdAsync));
        }
    }

    // The Repositories/Intray preview render goes through the shared Preview surface (ADR "Desktop recycle bin
    // parity" — the Recycle bin has its own).
    private async Task LoadPreviewAsync(string versionsHref) => await Preview.RenderAsync(await _api!.Documents.GetPreviewAsync(versionsHref));
}
