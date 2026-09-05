using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Check-out (ADR "Document check-out / check-in"): taking the lock and downloading the current version into
// the local working folder, overriding somebody else's lock, and the tab's count badge.
//
// It came out of a heading reading "Intray", which contained NO intray at all -- six subjects across 249
// lines: the user-context setup, the caller's system-rights flags, impersonation, check-out, the crash-guard
// reconnect, and the TIFF backfill. That is the second heading found in this burn-down that names something it
// contains none of (#941); the first was the web's "Rename / delete / recycle bin".
public sealed partial class MainWindowViewModel
{
    // Check-out tab count badge.
    public int CheckoutCount => Checkout.Count;

    public bool HasCheckouts => Checkout.Count > 0;

    // Check out the selected document: take the lock + download the current version into the local checkout
    // folder for editing. Enabled for a document row that isn't already checked out.
    public bool CanCheckOut => SelectedItem is { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false, CheckedOut: false };

    // Override a document checked out by someone else (a CanOverrideCheckout holder force-releases the lock).
    public bool CanOverrideSelected => CanOverrideCheckout && SelectedItem is { CheckedOut: true, CheckedOutByMe: false };

    [RelayCommand]
    private async Task CheckOutSelectedAsync()
    {
        if (_api is null || SelectedItem is not { } item || !CanCheckOut)
        {
            return;
        }

        Status = string.Format(Strings.Get("StCheckingOut"), item.Name);
        try
        {
            await _api.Checkout.CheckOutViaDocumentAsync(item.Href("self"));
            // The lock is acquired server-side; editing happens via the WebDAV mount (ADR 0513) — no local copy.
            Status = string.Format(Strings.Get("StCheckedOut"), item.Name);
            await RefreshAfterCheckoutChangeAsync();
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrCheckout2"), item.Name, e.Message));
        }
    }

    [RelayCommand]
    private async Task OverrideCheckoutSelectedAsync()
    {
        if (_api is null || SelectedItem is not { } item || !CanOverrideSelected)
        {
            return;
        }

        try
        {
            await _api.Checkout.CheckInViaDocumentAsync(item.Href("self")); // force-release (override)
            Status = string.Format(Strings.Get("StReleasedCheckout"), item.Name);
            await RefreshAfterCheckoutChangeAsync();
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrOverride2"), item.Name, e.Message));
        }
    }

    // The current version's file extension for a document (so the working copy keeps the right type) —
    // read from the versions address the caller's row advertised (ADR 0555).
    private async Task<string> ResolveFileExtensionAsync(string versionsHref)
    {
        if (_api is null)
        {
            return "";
        }

        var fields = await _api.Documents.GetSystemFieldsAsync(versionsHref);
        return fields?.FileExtension ?? "";
    }
}
