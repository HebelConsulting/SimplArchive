using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// A client must send AT a rel, never at an href it pairs with a verb of its own choosing (#1192).
//
// ADR 0543 says rel names are the compatibility surface, and #1173 cashed that at scale: fourteen routes moved
// and NO client learned a new address. The address half is true. The METHOD half was never true — a Link
// carries a `Method`, no client read it, and every call site named the verb itself. So a route that kept its
// rel and changed only its verb still broke every client, and nine call sites had to be corrected by hand.
//
// The fix is a helper that sends at a rel (Links.SendAsync on the web, ApiCore.SendRelAsync on the desktop),
// where the address and the method come from the same link and cannot disagree. This guard is the burn-down.
//
// WHY A PER-FILE BUDGET rather than a flat ban, in the shape ClientHypermediaTests established: there are 247
// sites across 77 files, so a ban would fail the build today and be suppressed tomorrow. A file's number may
// only go DOWN; converting a site lowers it in the same commit, and a file that reaches zero loses its entry.
// A number that goes UP is a NEW site written the old way, which is what this exists to stop.
//
// IT SHIPPED AT 181, AND THAT NUMBER WAS THE PROBLEM. The first detector required the address to be a
// RECOGNISABLE rel-derived expression at the call site, so it saw `Http.PostAsJsonAsync(Href(links, "x"), …)`
// and missed every site that passes the address through a PARAMETER (`MoveIntrayItemAsync(moveUrl, …)`), a
// HELPER (`PostBulkAsync(url, …)`, one method hardcoding POST for five operations), a CHAINED expression
// (`rel.GetProperty("href").GetString()`), or a rel resolved elsewhere (`await ApiRoot.RequireAsync("intray")`).
//
// That was not a harmless undercount. It was blind in exactly the shape that broke: when ADR 0797 changed six
// routes' METHODS, five call sites kept the old verb and answered 405 — the intray send and three of the five
// bulk operations, in BOTH clients, in shipped features — and none of the five was in this ledger. A guard that
// reads as complete while omitting the dangerous cases is worse than one that admits it is partial.
//
// So the detector now keys on the RECEIVER being an http client and counts every address that is not a literal.
// 251 sends, 4 of them literal URLs — which are ADR 0543's violation and ClientHypermediaTests' business, not
// this one's — leaving 247.
public partial class ClientLinkMethodTests
{
    // A mutating send ON AN HTTP CLIENT, capturing the address it is given.
    //
    // The receiver matters, and its absence is what made the first version of this ledger wrong in BOTH
    // directions. Without it the pattern also matched method DECLARATIONS — `Task PutAsync(HttpClient http, …)`,
    // `Task PostAsync(int pageIndex, …)` — which inflated a trial count to 284; with it, 251.
    [GeneratedRegex(@"\b(?:_?[Hh]ttp|[Cc]lient|api|core\.Http|_core\.Http|Http)\s*\.\s*(Post|Put|Delete)(?:AsJson)?Async\(\s*([^,)]+)")]
    private static partial Regex MutatingSend();

    // A LITERAL address is not this guard's business — a composed URL is ADR 0543's violation and
    // ClientHypermediaTests owns it. Everything else came from somewhere: a rel, a row, a parameter, a field.
    [GeneratedRegex(@"^\s*(\$?""|@\$?"")")]
    private static partial Regex LiteralAddress();

    // Sites that still name their own verb, per file. THIS MAY ONLY GO DOWN.
    private static readonly Dictionary<string, int> Budget = new(StringComparer.Ordinal)
    {
        ["src/SimplArchive.Client/Components/Tabs/AuditTab.razor"] = 2,
        ["src/SimplArchive.Client/Components/Tabs/CheckoutTab.razor"] = 9,
        ["src/SimplArchive.Client/Components/Tabs/IntrayPageOperations.razor"] = 6,
        ["src/SimplArchive.Client/Components/Tabs/IntrayTab.ItemActions.razor.cs"] = 3,
        ["src/SimplArchive.Client/Components/Tabs/IntrayTab.razor"] = 3,
        ["src/SimplArchive.Client/Components/Tabs/LegalHoldsTab.razor"] = 3,
        ["src/SimplArchive.Client/Components/Tabs/RecycleBinTab.razor"] = 5,
        ["src/SimplArchive.Client/Components/Tabs/RetentionTab.razor"] = 2,
        ["src/SimplArchive.Client/Components/Tabs/SearchTab.razor"] = 3,
        ["src/SimplArchive.Client/Components/Tabs/TagsTab.razor"] = 5,
        ["src/SimplArchive.Client/Components/Tabs/TenantTab.razor"] = 3,
        ["src/SimplArchive.Client/Components/Tabs/UsersGroupsTab.razor"] = 10,
        ["src/SimplArchive.Client/Components/UserEmailField.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ActivateModuleDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/BookingsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ChangePasswordDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ExternalLinksDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ImapDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/MailDomainsDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/ManageAccessDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/MfaSetupDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/ModuleSettingsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/NotificationPreferencesDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/PasskeysDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/ReminderDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/SensitivityLabelsDialog.razor"] = 4,
        ["src/SimplArchive.Client/Dialogs/ServiceAccountsDialog.razor"] = 4,
        ["src/SimplArchive.Client/Dialogs/VersionsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/WebDavDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/WorkflowDialog.razor"] = 1,
        ["src/SimplArchive.Client/Layout/MainLayout.razor"] = 1,
        ["src/SimplArchive.Client/Pages/Home.Chat.razor.cs"] = 2,
        ["src/SimplArchive.Client/Pages/Home.Filing.razor.cs"] = 8,
        ["src/SimplArchive.Client/Pages/Home.Navigation.razor.cs"] = 1,
        ["src/SimplArchive.Client/Pages/Home.RowActions.razor.cs"] = 2,
        ["src/SimplArchive.Client/Pages/Home.TiffBackfill.razor.cs"] = 1,
        ["src/SimplArchive.Client/Pages/Home.razor"] = 1,
        ["src/SimplArchive.Client/Services/AnnotationEditor.cs"] = 1,
        ["src/SimplArchive.Client/Services/BrowseService.cs"] = 1,
        ["src/SimplArchive.Client/Services/DocumentActions.cs"] = 11,
        ["src/SimplArchive.Client/Services/IntrayUploads.cs"] = 2,
        ["src/SimplArchive.Client/Services/ProfilePhotoUpload.cs"] = 1,
        ["src/SimplArchive.Client/Services/StructuredEditors.cs"] = 1,
        ["src/SimplArchive.Client/Services/UploadConflictResolver.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Services/AdminClient.cs"] = 27,
        ["src/SimplArchive.DesktopClient/Services/AnnotationsClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/ApiCore.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/AuditClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/BookingsClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/CheckoutClient.cs"] = 5,
        ["src/SimplArchive.DesktopClient/Services/DavCollectionsClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.Acl.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.Export.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.Folders.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.GenericActions.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.ModuleActions.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.SystemFields.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.Tags.cs"] = 4,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.cs"] = 10,
        ["src/SimplArchive.DesktopClient/Services/ExternalLinksClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/IndexDataWrites.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/IntrayApi.cs"] = 14,
        ["src/SimplArchive.DesktopClient/Services/LegalHoldsClient.cs"] = 5,
        ["src/SimplArchive.DesktopClient/Services/MasksClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/NotificationsClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/OidcLoopbackAuthenticator.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/ProfileClient.cs"] = 12,
        ["src/SimplArchive.DesktopClient/Services/RecycleBinClient.cs"] = 5,
        ["src/SimplArchive.DesktopClient/Services/ReferencesClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/RemindersClient.cs"] = 4,
        ["src/SimplArchive.DesktopClient/Services/RepositoryArchiveClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/SearchClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/SimplArchiveApiClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/StructuredEditorClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/TagsClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/VersionsClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/WorkflowClient.cs"] = 2,
    };

    [Fact]
    public void No_client_pairs_an_advertised_address_with_a_verb_of_its_own()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var client in new[] { "SimplArchive.Client", "SimplArchive.DesktopClient" })
        {
            var dir = Path.Combine(root, "src", client);
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".razor", StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                var n = 0;
                foreach (var line in File.ReadAllLines(file))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("@*", StringComparison.Ordinal)
                        || trimmed.StartsWith("*", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (MutatingSend().Match(line) is { Success: true } m && !LiteralAddress().IsMatch(m.Groups[2].Value))
                    {
                        n++;
                    }
                }

                if (n > 0)
                {
                    counted[relative] = n;
                }
            }
        }

        Assert.True(counted.Count > 10,
            $"Only {counted.Count} files matched — the detector stopped seeing the clients, which would make this pass vacuously.");

        var grew = counted
            .Where(c => c.Value > Budget.GetValueOrDefault(c.Key))
            .Select(c => $"  {c.Key}: {Budget.GetValueOrDefault(c.Key)} -> {c.Value}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.True(grew.Count == 0,
            "These files gained a call site that names its own verb on an advertised address (#1192):\n"
            + string.Join("\n", grew)
            + "\n\nSend AT the rel instead — Links.SendAsync (web) or ApiCore.SendRelAsync (desktop) — so the"
            + "\nmethod comes from the same link as the address and the two cannot disagree.");

        // The other direction, which is what makes it a burn-down rather than a list that rots.
        var paid = Budget
            .Where(b => counted.GetValueOrDefault(b.Key) < b.Value)
            .Select(b => $"  {b.Key}: {b.Value} -> {counted.GetValueOrDefault(b.Key)}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.True(paid.Count == 0,
            "These files have FEWER such sites than their budget. Lower the budget in the same commit that\n"
            + "converted them (delete the entry at zero), or the ledger stops describing anything:\n"
            + string.Join("\n", paid));
    }
}
