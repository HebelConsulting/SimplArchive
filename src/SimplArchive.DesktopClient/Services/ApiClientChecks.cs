namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The headless hooks that drive the real api-client against a running Api — <c>--selftest</c>,
/// <c>--upload-test</c>, <c>--workflow-test</c>, <c>--multipage-test</c> and the smaller per-flow checks.
/// </summary>
/// <remarks>
/// <para>
/// Each logs its findings through <see cref="DesktopLog"/> (ADR 0906) and ends with <c>OK</c> or <c>FAILED</c>,
/// because the caller is a terminal rather than a test runner: these exercise flows that need a real token and a real server, which is
/// what puts them outside <c>SimplArchive.DesktopUiEndToEndTests</c>.
/// </para>
/// <para>
/// Extracted from <c>Program</c> along with <see cref="Views.ScreenshotRenderer"/>: driving HTTP flows is not
/// dispatching a command line, and between them the two accounted for roughly two thirds of that file's excess
/// over the 1000-line limit. They live beside <see cref="SimplArchiveApiClient"/>, which every one of them uses.
/// </para>
/// </remarks>
internal static class ApiClientChecks
{
    /// <summary>
    /// One finding: the pass line at Information with its values, the fail line at Warning. Both templates are
    /// literals at the call site; the values belong to the pass line, a failure having nothing to report.
    /// </summary>
    private static void Outcome(bool passed, string passTemplate, string failMessage, params object?[] passValues)
    {
        if (passed)
        {
            DesktopLog.Info(passTemplate, passValues);
        }
        else
        {
            DesktopLog.Warn(failMessage);
        }
    }

    internal static async Task MultipageAsync(string token, string documentName)
    {
        var api = new SimplArchiveApiClient(token);
        var document = (await api.Documents.GetRepositoriesAsync())
            .SelectMany(r => api.Documents.GetChildrenAsync(r.Href("children")).GetAwaiter().GetResult())
            .FirstOrDefault(c => c.Name == documentName)
            ?? throw new InvalidOperationException($"No document named '{documentName}' in any visible repository's top level.");
        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        DesktopLog.Info("preview-pages link present: {LinkPresent}", preview.PreviewPagesUrl is not null);
        if (preview.PreviewPagesUrl is not { } url)
        {
            DesktopLog.Warn("FAILED: no preview-pages link.");
            return;
        }

        var pages = await api.Versions.GetPreviewPagesAsync(url);
        DesktopLog.Info("page urls: {PageUrls}", pages?.Count ?? 0);
        if (pages is null)
        {
            DesktopLog.Warn("FAILED: preview-pages returned null.");
            return;
        }

        var i = 0;
        foreach (var pageUrl in pages)
        {
            var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(pageUrl);
            // PNG IHDR: width/height at bytes 16..24 (big-endian) — validates it's a real page image.
            var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            DesktopLog.Info("  page {Page}: {Width}x{Height} ({Bytes} bytes)", ++i, w, h, bytes.Length);
        }

        Outcome(i > 1, "OK: multiple pages fetched.", "FAILED: expected multiple pages.");
    }

    internal static async Task NewFolderAsync(string accessToken, string name)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        DesktopLog.Info("creating folder '{Name}' in '{Root}'…", name, root.Name);

        await api.Documents.CreateFolderAsync(root.Href("children"), name);

        var match = (await api.Documents.GetChildrenAsync(root.Href("children"))).FirstOrDefault(c => c.Name == name);
        Outcome(match is not null, "OK: '{Name}' present, isFolder={IsFolder}", "FAILED: folder not found.",
            match?.Name, match is { HasVersions: false });
    }

    internal static async Task ModifyAsync(string accessToken)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();

        var original = $"modify-test-{Guid.NewGuid():N}";
        var renamed = $"{original}-renamed";
        DesktopLog.Info("creating folder '{Name}' in '{Root}'…", original, root.Name);
        await api.Documents.CreateFolderAsync(root.Href("children"), original);
        var created = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == original);

        DesktopLog.Info("renaming to '{Name}'…", renamed);
        await api.Documents.RenameAsync(created.Href("self"), renamed);
        var afterRename = await api.Documents.GetChildrenAsync(root.Href("children"));
        Outcome(afterRename.Any(c => c.Name == renamed) && afterRename.All(c => c.Name != original),
            "OK: rename reflected.", "FAILED: rename not reflected.");

        DesktopLog.Info("deleting…");
        await api.Documents.DeleteAsync(created.Href("self"));
        var afterDelete = await api.Documents.GetChildrenAsync(root.Href("children"));
        var recycled = await api.RecycleBin.GetRecycleBinAsync(root);
        Outcome(afterDelete.All(c => c.Id != created.Id) && recycled.Any(r => r.Id == created.Id),
            "OK: gone from folder, present in recycle bin.", "FAILED: delete/recycle-bin state wrong.");

        DesktopLog.Info("restoring…");
        await api.RecycleBin.RestoreAsync(recycled.Single(r => r.Id == created.Id));
        var afterRestore = await api.Documents.GetChildrenAsync(root.Href("children"));
        var recycledAfter = await api.RecycleBin.GetRecycleBinAsync(root);
        Outcome(afterRestore.Any(c => c.Id == created.Id) && recycledAfter.All(r => r.Id != created.Id),
            "OK: restored to folder, cleared from recycle bin.", "FAILED: restore state wrong.");

        // Clean up so repeated runs don't accumulate folders.
        await api.Documents.DeleteAsync(created.Href("self"));
    }

    internal static async Task SaveAsAsync(string accessToken, string outPath)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();

        var name = $"saveas-test-{Guid.NewGuid():N}.txt";
        var content = System.Text.Encoding.UTF8.GetBytes("save-as round-trip test\n");
        DesktopLog.Info("uploading '{Name}' to '{Root}'…", name, root.Name);
        await api.Documents.UploadFileAsync(root.Href("children"), name, content);
        var document = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == name);

        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        if (preview.DownloadUrl is null)
        {
            DesktopLog.Warn("FAILED: no download URL.");
            return;
        }

        var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(preview.DownloadUrl);
        await File.WriteAllBytesAsync(outPath, bytes);
        Outcome(bytes.SequenceEqual(content),
            "OK: saved {Bytes} bytes -> {OutPath}; round-trip matches.",
            "FAILED: saved bytes don't match the uploaded content.",
            bytes.Length, outPath);

        await api.Documents.DeleteAsync(document.Href("self")); // cleanup
    }

    internal static async Task ReferenceAsync(string accessToken)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        var s = Guid.NewGuid().ToString("N")[..6];

        await api.Documents.CreateFolderAsync(root.Href("children"), $"ref-A-{s}");
        await api.Documents.CreateFolderAsync(root.Href("children"), $"ref-B-{s}");
        var a = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"ref-A-{s}");
        var b = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"ref-B-{s}");
        await api.Documents.CreateFolderAsync(a.Href("children"), $"ref-C-{s}");
        var c = (await api.Documents.GetChildrenAsync(a.Href("children"))).First(n => n.Name == $"ref-C-{s}");

        DesktopLog.Info("moving C from A to B…");
        await api.Documents.MoveAsync(c.Href("self"), b.Id);
        var cInB = (await api.Documents.GetChildrenAsync(b.Href("children"))).Any(n => n.Id == c.Id);
        var cGoneFromA = !(await api.Documents.GetChildrenAsync(a.Href("children"))).Any(n => n.Id == c.Id);
        Outcome(cInB && cGoneFromA, "OK: moved.", "FAILED: move state wrong.");

        DesktopLog.Info("referencing C into A…");
        await api.References.CreateReferenceAsync(a.Href("references"), c.Id);
        var refs = await api.References.GetReferencesAsync(a.Href("references"));
        var reference = refs.FirstOrDefault(r => r.TargetId == c.Id);
        if (reference is not null && reference.RealParentId == b.Id)
        {
            DesktopLog.Info("OK: reference present, realParentId points to B; go-to folder = '{GoTo}'.",
                (await api.GetDocumentByAddressAsync(reference.Links!.Href("go-to")!)).Name);
        }
        else
        {
            DesktopLog.Warn("FAILED: reference/realParentId wrong.");
        }

        DesktopLog.Info("removing the reference…");
        await api.References.DeleteReferenceAsync(reference!.DeleteHref!);
        Outcome((await api.References.GetReferencesAsync(a.Href("references"))).Count == 0,
            "OK: reference removed.", "FAILED: reference still present.");

        await api.Documents.DeleteAsync(a.Href("self")); // cleanup (cascades C)
        await api.Documents.DeleteAsync(b.Href("self"));
    }

    internal static async Task SearchAsync(string accessToken, string query)
    {
        var api = new SimplArchiveApiClient(accessToken);
        DesktopLog.Info("searching for '{Query}'…", query);
        var results = await api.Search.SearchAsync(query);
        DesktopLog.Info("{Count} result(s):", results.Count);
        foreach (var result in results)
        {
            DesktopLog.Info("  {Kind} {Name}   —   {Path}", result.IsFolder ? "[folder]" : "[doc]   ", result.Name, result.Path);
        }
    }

    internal static async Task ReferencingAsync(string accessToken)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        var s = Guid.NewGuid().ToString("N")[..6];

        await api.Documents.CreateFolderAsync(root.Href("children"), $"rt-A-{s}");
        await api.Documents.CreateFolderAsync(root.Href("children"), $"rt-B-{s}");
        var a = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"rt-A-{s}");
        var b = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == $"rt-B-{s}");
        await api.Documents.CreateFolderAsync(a.Href("children"), $"rt-C-{s}");
        var c = (await api.Documents.GetChildrenAsync(a.Href("children"))).First(n => n.Name == $"rt-C-{s}");

        await api.References.CreateReferenceAsync(b.Href("references"), c.Id);

        var cRow = (await api.Documents.GetChildrenAsync(a.Href("children"))).First(n => n.Id == c.Id);
        Outcome(cRow.HasReferences, "OK: hasReferences=true on the referenced item.", "FAILED: hasReferences not set.");

        var folders = await api.References.GetReferencingFoldersAsync(c.Href("referencing-folders"));
        var match = folders.FirstOrDefault(f => f.Id == b.Id);
        Outcome(match is not null, "OK: referencing folder listed with path '{Path}'.", "FAILED: referencing folder not listed.",
            match?.Path);

        await api.Documents.DeleteAsync(a.Href("self"));
        await api.Documents.DeleteAsync(b.Href("self"));
    }

    internal static async Task UploadAsync(string accessToken, string filePath)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        var name = Path.GetFileName(filePath);
        DesktopLog.Info("uploading '{Name}' into '{Root}'…", name, root.Name);

        await api.Documents.UploadFileAsync(root.Href("children"), name, await File.ReadAllBytesAsync(filePath));

        var match = (await api.Documents.GetChildrenAsync(root.Href("children"))).FirstOrDefault(c => c.Name == name);
        Outcome(match is not null, "OK: '{Name}' present, hasVersions={HasVersions}",
            "FAILED: uploaded document not found in the folder.", match?.Name, match?.HasVersions);
    }

    internal static async Task WorkflowAsync(string accessToken)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var me = await api.GetWhoAmIAsync();
        var repo = (await api.Documents.GetRepositoriesAsync()).First();
        DesktopLog.Info("repo '{Repository}', me {UserId}", repo.Name, me.UserId);

        await api.Documents.UploadFileAsync(repo.Href("children"), "wf-desktop-test.txt", System.Text.Encoding.UTF8.GetBytes("workflow desktop test"));
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == "wf-desktop-test");
        DesktopLog.Info("created doc {Name} ({DocumentId})", doc.Name, doc.Id);

        var wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        DesktopLog.Info("initial: {Status} | links: {Links}", wf?.StatusName, string.Join(",", wf?.Links.Keys ?? []));

        await api.Workflow.PostWorkflowActionAsync(wf!.Links.Href("submit")!, new { reviewerId = me.UserId });
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        DesktopLog.Info("after submit: {Status} | assignedTo: {AssignedTo} | links: {Links}",
            wf?.StatusName, wf?.AssignedToName, string.Join(",", wf?.Links.Keys ?? []));

        var tasks = await api.Workflow.GetTasksAsync();
        DesktopLog.Info("tasks: {Count} -> {Tasks}", tasks.Count, string.Join(",", tasks.Select(t => $"{t.DocumentName}/v{t.VersionNumber}")));

        await api.Workflow.PostWorkflowActionAsync(wf!.Links.Href("approve")!, null);
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        DesktopLog.Info("after approve: {Status} | links: {Links}", wf?.StatusName, string.Join(",", wf?.Links.Keys ?? []));

        await api.Workflow.PostWorkflowActionAsync(wf!.Links.Href("release")!, null);
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        DesktopLog.Info("after release: {Status}", wf?.StatusName);
        DesktopLog.Info("history:");
        foreach (var h in wf!.History)
        {
            DesktopLog.Info("  {Status} by {PerformedBy}{AssignedTo}{Rejection}", h.ToStatusName, h.PerformedByName,
                h.AssignedToName is { } a ? $" -> {a}" : string.Empty, h.RejectionReason is { } r ? $" · {r}" : string.Empty);
        }
    }

    internal static async Task SelfAsync(string accessToken)
    {
        var api = new SimplArchiveApiClient(accessToken);

        var repositories = await api.Documents.GetRepositoriesAsync();
        DesktopLog.Info("repositories: {Count}", repositories.Count);
        foreach (var repository in repositories)
        {
            DesktopLog.Info("  📁 {Name} (hasChildren={HasChildren})", repository.Name, repository.HasChildren);
        }

        var root = repositories.FirstOrDefault();
        if (root is null)
        {
            DesktopLog.Warn("no repositories visible; stopping.");
            return;
        }

        var children = await api.Documents.GetChildrenAsync(root.Href("children"));
        DesktopLog.Info("children of '{Root}': {Count}", root.Name, children.Count);

        var document = children.FirstOrDefault(c => c.HasVersions);
        if (document is null)
        {
            DesktopLog.Warn("no document with a version in the first repository; stopping.");
            return;
        }

        var mask = await api.Documents.GetMaskAsync(document.Href("mask"));
        DesktopLog.Info("mask: {Mask} v{MaskVersion}", mask.Name ?? "(none)", mask.VersionNumber);

        var indexData = await api.Documents.GetIndexDataAsync(document.Href("index-data"));
        DesktopLog.Info("index-data fields: {Count}", indexData.Count);
        foreach (var field in indexData)
        {
            DesktopLog.Info("  {Field} = {Values}", field.FieldName, string.Join(", ", field.Values));
        }

        var comments = await api.Documents.GetCommentsAsync(document.Href("chat"));
        DesktopLog.Info("comments: {Count}", comments.Count);

        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        // Whether the presigned URLs resolved, never the URLs themselves: their query strings are credentials.
        DesktopLog.Info("preview: {Preview} converted={Converted}; download: {Download}",
            preview.PreviewUrl is null ? "(none)" : "resolved", preview.PreviewConverted,
            preview.DownloadUrl is null ? "(none)" : "resolved");

        if (preview.PreviewUrl is not null)
        {
            var (bytes, contentType) = await SimplArchiveApiClient.DownloadAsync(preview.PreviewUrl);
            DesktopLog.Info("preview content-type: {ContentType} ({Bytes} bytes)", contentType, bytes.Length);
        }

        if (preview.DownloadUrl is not null)
        {
            // Reconstruct the filename with the version's extension (Document.Name is a bare stem now).
            var fileName = document.Name.EndsWith(preview.FileExtension, StringComparison.OrdinalIgnoreCase)
                ? document.Name
                : document.Name + preview.FileExtension;
            var path = await NativeFileOpener.DownloadToTempAsync(preview.DownloadUrl, fileName);
            DesktopLog.Info("downloaded '{Name}' (ext '{Extension}') -> {Path} ({Bytes} bytes)",
                document.Name, preview.FileExtension, path, new FileInfo(path).Length);
        }
    }
}
