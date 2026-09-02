using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

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
    public bool CanEditOcr => IsEditing && SysHasTiff;

    public ObservableCollection<MaskChoiceViewModel> AvailableMasks { get; } = [];
    public ObservableCollection<MaskFieldEditViewModel> MaskEditFields { get; } = [];

    private Guid? _originalMaskId;
    private string _originalName = string.Empty;
    private DateTime? _originalDocumentDate;
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
            _originalName = SysName;
            _originalDocumentDate = SysDocumentDate;
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

            IsEditing = true;
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrStartEdit"), e.Message);
        }
        finally
        {
            _loadingMaskEdit = false;
        }
    }

    // Re-load the field editors when the user picks a different mask (empty values — a different mask has
    // different fields). Suppressed during the initial edit load, which fills the current values instead.
    async partial void OnSelectedMaskChoiceChanged(MaskChoiceViewModel? value)
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
            var values = valuesByName.TryGetValue(field.Name, out var v) ? v : [];
            MaskEditFields.Add(MaskFieldEditViewModel.Create(field, values, CanManageMailRouting));
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

        var failures = new List<string>();
        var nameChanged = false;

        // Name (rename).
        var newName = SysName?.Trim() ?? "";
        if (newName.Length > 0 && newName != _originalName)
        {
            try
            {
                await _api.Documents.RenameAsync(DetailHref("self"), newName);
                DetailTitle = newName;
                _originalName = newName;
                nameChanged = true;
            }
            catch (Exception e) { failures.Add($"name ({e.Message})"); }
        }

        // Document date (on the current version).
        if (_sysDocumentDateHref is { } dateHref && SysDocumentDate is { } date && date != _originalDocumentDate)
        {
            try
            {
                await _api.Versions.SetDocumentDateAsync(dateHref, date.ToString("yyyy-MM-dd"));
                _originalDocumentDate = date;
            }
            catch (Exception e) { failures.Add($"document date ({e.Message})"); }
        }

        // OCR languages (only if the ordered selection changed — this re-runs the searchable-PDF conversion).
        if (SysHasTiff && !_stagedOcrCodes.SequenceEqual(_sysOcrCodes))
        {
            try
            {
                await _api.Documents.SetOcrLanguagesAsync(DetailHref("ocr-languages"), _stagedOcrCodes);
                _sysOcrCodes = _stagedOcrCodes;
            }
            catch (Exception e) { failures.Add($"OCR languages ({e.Message})"); }
        }

        // Sensitivity label (ADR "Configurable sensitivity labels + upload defaults").
        var chosenLabelId = SelectedSensitivityItem?.Id;
        if (chosenLabelId != DetailSensitivityId)
        {
            try
            {
                await _api.Documents.SetSensitivityAsync(DetailHref("sensitivity"), chosenLabelId);
                var lbl = SensitivityCatalog.FirstOrDefault(l => l.Id == chosenLabelId);
                _detailSensitivityName = lbl?.Name ?? "";
                _detailSensitivityColor = lbl?.Color;
                _detailSensitivityWatermark = lbl?.Watermark ?? false;
                DetailSensitivityId = chosenLabelId;
                Preview.WatermarkText = _detailSensitivityWatermark ? $"{_detailSensitivityName} · {UserDisplayName}" : "";
            }
            catch (Exception e) { failures.Add($"sensitivity ({e.Message})"); }
        }

        // Free-form tags (ADR "Document tags"): PUT-replaces the whole set (the server normalizes/dedupes).
        var editTags = EditTags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 100).Distinct().ToList();
        if (!editTags.OrderBy(t => t).SequenceEqual(_origTags.OrderBy(t => t)))
        {
            try
            {
                var stored = await _api.Tags.SetTagsAsync(DetailHref("tags"), editTags);
                DetailTags.Clear();
                foreach (var t in stored) DetailTags.Add(t);
                HasDetailTags = DetailTags.Count > 0;
                _origTags = [.. DetailTags];
            }
            catch (Exception e) { failures.Add($"tags ({e.Message})"); }
        }

        // Mask + index data.
        try
        {
            var newMaskId = SelectedMaskChoice?.MaskId;
            if (newMaskId is null)
            {
                if (_originalMaskId is not null)
                {
                    await _api.Masks.ClearMaskAsync(DetailHref("mask"));
                    _originalMaskId = null;
                }
            }
            else
            {
                // Fill index data first, then (re)assign the mask — assigning re-checks required fields, so
                // the values must already be in place (ADR "Document metadata (index data) endpoints").
                // The duplicate-claim ask-and-retry (#703) is the client's; this only wires the dialog in. A
                // decline throws, skipping the mask assignment below — its re-check of required fields would
                // run against index data that never landed.
                await _api.Documents.SetIndexDataAsync(DetailHref("index-data"), MaskEditFields.Select(f => (f.FieldDefinitionId, f.ToValues())), ConfirmDuplicateClaimDialog);
                if (newMaskId != _originalMaskId)
                {
                    await _api.Masks.SetMaskAsync(DetailHref("mask"), newMaskId.Value);
                    _originalMaskId = newMaskId;
                }
            }
        }
        catch (ApiActionException e) { failures.Add(e.Message); } // required field missing / invalid value
        catch (DuplicateAddressClaimException e) { failures.Add(e.Message); } // declined the fan-out question
        catch (Exception e) { failures.Add($"mask ({e.Message})"); }

        // A folder's contents order commits with everything else, from the same Save (issue #408). Skipped for a
        // document, which lists nothing, and when unchanged — so an ordinary edit sends no extra request.
        if (_detailIsFolder && EditSortOrder != _detailSortOrder)
        {
            try
            {
                await _api.Documents.SetContentsSortOrderAsync(DetailHref("contents-sort-order"), EditSortOrder);
                _detailSortOrder = EditSortOrder;
                OnPropertyChanged(nameof(DetailSortText));

                // The OPEN folder's listing re-sorts only when it is the folder that changed.
                if (_currentFolderId == documentId)
                {
                    _folderSortOrder = EditSortOrder;
                    _headerSortActive = false;
                }
            }
            catch (Exception e) { failures.Add($"contents sort order ({e.Message})"); }
        }

        if (failures.Count > 0)
        {
            Status = string.Format(Strings.Get("StErrSaveJoin"), string.Join("; ", failures));
            return; // stay in edit mode so the user can correct the rejected field(s)
        }

        IsEditing = false;
        Status = Strings.Get("StSaved");
        await ReloadDetailAsync();
        if (nameChanged)
        {
            await ReloadTreeAsync();
            await LoadFolderContentsAsync(_currentFolderId ?? documentId);
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
            IndexFields.Add(new IndexFieldViewModel { FieldName = field.FieldName, Values = string.Join(", ", field.Values) });
        }
    }

    // The Repositories/Intray preview render goes through the shared Preview surface (ADR "Desktop recycle bin
    // parity" — the Recycle bin has its own).
    private async Task LoadPreviewAsync(string versionsHref) => await Preview.RenderAsync(await _api!.Documents.GetPreviewAsync(versionsHref));
}
