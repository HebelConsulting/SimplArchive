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
// WHY A PER-FILE BUDGET rather than a flat ban, in the shape ClientHypermediaTests established: there are 181
// sites across 60 files, so a ban would fail the build today and be suppressed tomorrow. A file's number may
// only go DOWN; converting a site lowers it in the same commit, and a file that reaches zero loses its entry.
// A number that goes UP is a NEW site written the old way, which is what this exists to stop.
//
// WHAT IT CANNOT SEE, stated because a guard that overclaims gets ignored: it matches the first argument of a
// mutating send being a rel-derived expression. A site that assigns the href to a plain local first, or that
// carries it through a record field, is invisible to it. The desktop has many of those — it parses resources
// into records EARLY and drops the method at the parser — so its real debt is larger than its budget says.
// Measuring the shape it can see honestly beats measuring the whole thing badly.
public partial class ClientLinkMethodTests
{
    // The first argument of a mutating send.
    [GeneratedRegex(@"\b(Post|Put|Delete)(?:AsJson)?Async\(\s*([A-Za-z_][\w.]*\([^()]*\)|[A-Za-z_]\w*)")]
    private static partial Regex MutatingSend();

    // ... that is an address the SERVER advertised, as opposed to one the caller was handed for other reasons.
    [GeneratedRegex(@"(Href|RequireHref|RelHref|RequireRel|Rel)\(|[Hh]ref$|Href\b")]
    private static partial Regex RelDerived();

    // Sites that still name their own verb, per file. THIS MAY ONLY GO DOWN.
    private static readonly Dictionary<string, int> Budget = new(StringComparer.Ordinal)
    {
        ["src/SimplArchive.Client/Components/Tabs/AuditTab.razor"] = 2,
        ["src/SimplArchive.Client/Components/Tabs/CheckoutTab.razor"] = 7,
        ["src/SimplArchive.Client/Components/Tabs/IntrayPageOperations.razor"] = 6,
        ["src/SimplArchive.Client/Components/Tabs/LegalHoldsTab.razor"] = 2,
        ["src/SimplArchive.Client/Components/Tabs/RecycleBinTab.razor"] = 5,
        ["src/SimplArchive.Client/Components/Tabs/RetentionTab.razor"] = 2,
        ["src/SimplArchive.Client/Components/Tabs/SearchTab.razor"] = 2,
        ["src/SimplArchive.Client/Components/Tabs/TagsTab.razor"] = 4,
        ["src/SimplArchive.Client/Components/Tabs/TenantTab.razor"] = 3,
        ["src/SimplArchive.Client/Components/Tabs/UsersGroupsTab.razor"] = 7,
        ["src/SimplArchive.Client/Components/UserEmailField.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ActivateModuleDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/BookingsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ImapDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/MailDomainsDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/ManageAccessDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/ModuleSettingsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/PasskeysDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ReminderDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/SensitivityLabelsDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/ServiceAccountsDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/WorkflowDialog.razor"] = 1,
        ["src/SimplArchive.Client/Pages/Home.Filing.razor.cs"] = 6,
        ["src/SimplArchive.Client/Pages/Home.RowActions.razor.cs"] = 1,
        ["src/SimplArchive.Client/Pages/Home.razor"] = 1,
        ["src/SimplArchive.Client/Services/AnnotationEditor.cs"] = 1,
        ["src/SimplArchive.Client/Services/DocumentActions.cs"] = 7,
        ["src/SimplArchive.Client/Services/IntrayUploads.cs"] = 1,
        ["src/SimplArchive.Client/Services/ProfilePhotoUpload.cs"] = 1,
        ["src/SimplArchive.Client/Services/StructuredEditors.cs"] = 1,
        ["src/SimplArchive.Client/Services/UploadConflictResolver.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Services/AdminClient.cs"] = 19,
        ["src/SimplArchive.DesktopClient/Services/ApiClientChecks.cs"] = 7,
        ["src/SimplArchive.DesktopClient/Services/BookingsClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/CheckoutClient.cs"] = 5,
        ["src/SimplArchive.DesktopClient/Services/DavCollectionsClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.Acl.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.Export.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.Folders.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.GenericActions.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.ModuleActions.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.SystemFields.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.Tags.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.cs"] = 9,
        ["src/SimplArchive.DesktopClient/Services/ExternalLinksClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/IndexDataWrites.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/IntrayApi.cs"] = 10,
        ["src/SimplArchive.DesktopClient/Services/LegalHoldsClient.cs"] = 4,
        ["src/SimplArchive.DesktopClient/Services/MasksClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/NotificationsClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/ProfileClient.cs"] = 4,
        ["src/SimplArchive.DesktopClient/Services/RecycleBinClient.cs"] = 5,
        ["src/SimplArchive.DesktopClient/Services/ReferencesClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/RemindersClient.cs"] = 4,
        ["src/SimplArchive.DesktopClient/Services/SearchClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/StructuredEditorClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/TagsClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/VersionsClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/ViewModels/MainWindowViewModel.ItemActions.cs"] = 1,
        ["src/SimplArchive.DesktopClient/ViewModels/MainWindowViewModel.Screenshots.cs"] = 3,
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

                    if (MutatingSend().Match(line) is { Success: true } m && RelDerived().IsMatch(m.Groups[2].Value))
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
