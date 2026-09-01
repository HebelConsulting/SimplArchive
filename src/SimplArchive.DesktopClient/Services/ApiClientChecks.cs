namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The headless hooks that drive the real api-client against a running Api — <c>--selftest</c>,
/// <c>--upload-test</c>, <c>--workflow-test</c>, <c>--multipage-test</c> and the smaller per-flow checks.
/// </summary>
/// <remarks>
/// <para>
/// Each writes its findings to the console and ends with <c>OK</c> or <c>FAILED</c>, because the caller is a
/// terminal rather than a test runner: these exercise flows that need a real token and a real server, which is
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
    internal static async Task MultipageAsync(string token, string documentName)
    {
        var api = new SimplArchiveApiClient(token);
        var document = (await api.Documents.GetRepositoriesAsync())
            .SelectMany(r => api.Documents.GetChildrenAsync(r.Href("children")).GetAwaiter().GetResult())
            .FirstOrDefault(c => c.Name == documentName)
            ?? throw new InvalidOperationException($"No document named '{documentName}' in any visible repository's top level.");
        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        Console.WriteLine($"preview-pages link present: {preview.PreviewPagesUrl is not null}");
        if (preview.PreviewPagesUrl is not { } url)
        {
            Console.WriteLine("FAILED: no preview-pages link.");
            return;
        }

        var pages = await api.Versions.GetPreviewPagesAsync(url);
        Console.WriteLine($"page urls: {pages?.Count ?? 0}");
        if (pages is null)
        {
            Console.WriteLine("FAILED: preview-pages returned null.");
            return;
        }

        var i = 0;
        foreach (var pageUrl in pages)
        {
            var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(pageUrl);
            // PNG IHDR: width/height at bytes 16..24 (big-endian) — validates it's a real page image.
            var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            Console.WriteLine($"  page {++i}: {w}x{h} ({bytes.Length} bytes)");
        }

        Console.WriteLine(i > 1 ? "OK: multiple pages fetched." : "FAILED: expected multiple pages.");
    }

    internal static async Task NewFolderAsync(string accessToken, string name)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        Console.WriteLine($"creating folder '{name}' in '{root.Name}'…");

        await api.Documents.CreateFolderAsync(root.Href("children"), name);

        var match = (await api.Documents.GetChildrenAsync(root.Href("children"))).FirstOrDefault(c => c.Name == name);
        Console.WriteLine(match is null
            ? "FAILED: folder not found."
            : $"OK: '{match.Name}' present, isFolder={!match.HasVersions}");
    }

    internal static async Task ModifyAsync(string accessToken)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();

        var original = $"modify-test-{Guid.NewGuid():N}";
        var renamed = $"{original}-renamed";
        Console.WriteLine($"creating folder '{original}' in '{root.Name}'…");
        await api.Documents.CreateFolderAsync(root.Href("children"), original);
        var created = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == original);

        Console.WriteLine($"renaming to '{renamed}'…");
        await api.Documents.RenameAsync(created.Href("self"), renamed);
        var afterRename = await api.Documents.GetChildrenAsync(root.Href("children"));
        Console.WriteLine(afterRename.Any(c => c.Name == renamed) && afterRename.All(c => c.Name != original)
            ? "OK: rename reflected."
            : "FAILED: rename not reflected.");

        Console.WriteLine("deleting…");
        await api.Documents.DeleteAsync(created.Href("self"));
        var afterDelete = await api.Documents.GetChildrenAsync(root.Href("children"));
        var recycled = await api.Documents.GetRecycleBinAsync(root);
        Console.WriteLine(afterDelete.All(c => c.Id != created.Id) && recycled.Any(r => r.Id == created.Id)
            ? "OK: gone from folder, present in recycle bin."
            : "FAILED: delete/recycle-bin state wrong.");

        Console.WriteLine("restoring…");
        await api.Documents.RestoreAsync(recycled.Single(r => r.Id == created.Id));
        var afterRestore = await api.Documents.GetChildrenAsync(root.Href("children"));
        var recycledAfter = await api.Documents.GetRecycleBinAsync(root);
        Console.WriteLine(afterRestore.Any(c => c.Id == created.Id) && recycledAfter.All(r => r.Id != created.Id)
            ? "OK: restored to folder, cleared from recycle bin."
            : "FAILED: restore state wrong.");

        // Clean up so repeated runs don't accumulate folders.
        await api.Documents.DeleteAsync(created.Href("self"));
    }

    internal static async Task SaveAsAsync(string accessToken, string outPath)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();

        var name = $"saveas-test-{Guid.NewGuid():N}.txt";
        var content = System.Text.Encoding.UTF8.GetBytes("save-as round-trip test\n");
        Console.WriteLine($"uploading '{name}' to '{root.Name}'…");
        await api.Documents.UploadFileAsync(root.Href("children"), name, content);
        var document = (await api.Documents.GetChildrenAsync(root.Href("children"))).First(c => c.Name == name);

        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        if (preview.DownloadUrl is null)
        {
            Console.WriteLine("FAILED: no download URL.");
            return;
        }

        var (bytes, _) = await SimplArchiveApiClient.DownloadAsync(preview.DownloadUrl);
        await File.WriteAllBytesAsync(outPath, bytes);
        Console.WriteLine(bytes.SequenceEqual(content)
            ? $"OK: saved {bytes.Length} bytes -> {outPath}; round-trip matches."
            : "FAILED: saved bytes don't match the uploaded content.");

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

        Console.WriteLine("moving C from A to B…");
        await api.Documents.MoveAsync(c.Href("self"), b.Id);
        var cInB = (await api.Documents.GetChildrenAsync(b.Href("children"))).Any(n => n.Id == c.Id);
        var cGoneFromA = !(await api.Documents.GetChildrenAsync(a.Href("children"))).Any(n => n.Id == c.Id);
        Console.WriteLine(cInB && cGoneFromA ? "OK: moved." : "FAILED: move state wrong.");

        Console.WriteLine("referencing C into A…");
        await api.Documents.CreateReferenceAsync(a.Href("references"), c.Id);
        var refs = await api.Documents.GetReferencesAsync(a.Href("references"));
        var reference = refs.FirstOrDefault(r => r.TargetId == c.Id);
        Console.WriteLine(reference is not null && reference.RealParentId == b.Id
            ? $"OK: reference present, realParentId points to B; go-to folder = '{(await api.GetDocumentByAddressAsync(reference.Links!["go-to"])).Name}'."
            : "FAILED: reference/realParentId wrong.");

        Console.WriteLine("removing the reference…");
        await api.Documents.DeleteReferenceAsync(reference!.DeleteHref!);
        Console.WriteLine((await api.Documents.GetReferencesAsync(a.Href("references"))).Count == 0 ? "OK: reference removed." : "FAILED: reference still present.");

        await api.Documents.DeleteAsync(a.Href("self")); // cleanup (cascades C)
        await api.Documents.DeleteAsync(b.Href("self"));
    }

    internal static async Task SearchAsync(string accessToken, string query)
    {
        var api = new SimplArchiveApiClient(accessToken);
        Console.WriteLine($"searching for '{query}'…");
        var results = await api.Search.SearchAsync(query);
        Console.WriteLine($"{results.Count} result(s):");
        foreach (var result in results)
        {
            Console.WriteLine($"  {(result.IsFolder ? "[folder]" : "[doc]   ")} {result.Name}   —   {result.Path}");
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

        await api.Documents.CreateReferenceAsync(b.Href("references"), c.Id);

        var cRow = (await api.Documents.GetChildrenAsync(a.Href("children"))).First(n => n.Id == c.Id);
        Console.WriteLine(cRow.HasReferences ? "OK: hasReferences=true on the referenced item." : "FAILED: hasReferences not set.");

        var folders = await api.Documents.GetReferencingFoldersAsync(c.Href("referencing-folders"));
        var match = folders.FirstOrDefault(f => f.Id == b.Id);
        Console.WriteLine(match is not null
            ? $"OK: referencing folder listed with path '{match.Path}'."
            : "FAILED: referencing folder not listed.");

        await api.Documents.DeleteAsync(a.Href("self"));
        await api.Documents.DeleteAsync(b.Href("self"));
    }

    internal static async Task UploadAsync(string accessToken, string filePath)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var root = (await api.Documents.GetRepositoriesAsync()).First();
        var name = Path.GetFileName(filePath);
        Console.WriteLine($"uploading '{name}' into '{root.Name}'…");

        await api.Documents.UploadFileAsync(root.Href("children"), name, await File.ReadAllBytesAsync(filePath));

        var match = (await api.Documents.GetChildrenAsync(root.Href("children"))).FirstOrDefault(c => c.Name == name);
        Console.WriteLine(match is null
            ? "FAILED: uploaded document not found in the folder."
            : $"OK: '{match.Name}' present, hasVersions={match.HasVersions}");
    }

    internal static async Task WorkflowAsync(string accessToken)
    {
        var api = new SimplArchiveApiClient(accessToken);
        var me = await api.GetWhoAmIAsync();
        var repo = (await api.Documents.GetRepositoriesAsync()).First();
        Console.WriteLine($"repo '{repo.Name}', me {me.UserId}");

        await api.Documents.UploadFileAsync(repo.Href("children"), "wf-desktop-test.txt", System.Text.Encoding.UTF8.GetBytes("workflow desktop test"));
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == "wf-desktop-test");
        Console.WriteLine($"created doc {doc.Name} ({doc.Id})");

        var wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        Console.WriteLine($"initial: {wf?.StatusName} | links: {string.Join(",", wf?.Links.Keys ?? [])}");

        await api.Workflow.PostWorkflowActionAsync(wf!.Links["submit"], new { reviewerId = me.UserId });
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        Console.WriteLine($"after submit: {wf?.StatusName} | assignedTo: {wf?.AssignedToName} | links: {string.Join(",", wf?.Links.Keys ?? [])}");

        var tasks = await api.Workflow.GetTasksAsync();
        Console.WriteLine($"tasks: {tasks.Count} -> {string.Join(",", tasks.Select(t => $"{t.DocumentName}/v{t.VersionNumber}"))}");

        await api.Workflow.PostWorkflowActionAsync(wf!.Links["approve"], null);
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        Console.WriteLine($"after approve: {wf?.StatusName} | links: {string.Join(",", wf?.Links.Keys ?? [])}");

        await api.Workflow.PostWorkflowActionAsync(wf!.Links["release"], null);
        wf = await api.Documents.GetWorkflowAsync(doc.Href("versions"));
        Console.WriteLine($"after release: {wf?.StatusName}");
        Console.WriteLine("history:");
        foreach (var h in wf!.History)
        {
            Console.WriteLine($"  {h.ToStatusName} by {h.PerformedByName}{(h.AssignedToName is { } a ? $" -> {a}" : "")}{(h.RejectionReason is { } r ? $" · {r}" : "")}");
        }
    }

    internal static async Task SelfAsync(string accessToken)
    {
        var api = new SimplArchiveApiClient(accessToken);

        var repositories = await api.Documents.GetRepositoriesAsync();
        Console.WriteLine($"repositories: {repositories.Count}");
        foreach (var repository in repositories)
        {
            Console.WriteLine($"  📁 {repository.Name} (hasChildren={repository.HasChildren})");
        }

        var root = repositories.FirstOrDefault();
        if (root is null)
        {
            Console.WriteLine("no repositories visible; stopping.");
            return;
        }

        var children = await api.Documents.GetChildrenAsync(root.Href("children"));
        Console.WriteLine($"children of '{root.Name}': {children.Count}");

        var document = children.FirstOrDefault(c => c.HasVersions);
        if (document is null)
        {
            Console.WriteLine("no document with a version in the first repository; stopping.");
            return;
        }

        var mask = await api.Documents.GetMaskAsync(document.Href("mask"));
        Console.WriteLine($"mask: {mask.Name ?? "(none)"} v{mask.VersionNumber}");

        var indexData = await api.Documents.GetIndexDataAsync(document.Href("index-data"));
        Console.WriteLine($"index-data fields: {indexData.Count}");
        foreach (var field in indexData)
        {
            Console.WriteLine($"  {field.FieldName} = {string.Join(", ", field.Values)}");
        }

        var comments = await api.Documents.GetCommentsAsync(document.Href("chat"));
        Console.WriteLine($"comments: {comments.Count}");

        var preview = await api.Documents.GetPreviewAsync(document.Href("versions"));
        Console.WriteLine($"preview: {(preview.PreviewUrl is null ? "(none)" : "resolved")} converted={preview.PreviewConverted}; download: {(preview.DownloadUrl is null ? "(none)" : "resolved")}");

        if (preview.PreviewUrl is not null)
        {
            var (bytes, contentType) = await SimplArchiveApiClient.DownloadAsync(preview.PreviewUrl);
            Console.WriteLine($"preview content-type: {contentType} ({bytes.Length} bytes)");
        }

        if (preview.DownloadUrl is not null)
        {
            // Reconstruct the filename with the version's extension (Document.Name is a bare stem now).
            var fileName = document.Name.EndsWith(preview.FileExtension, StringComparison.OrdinalIgnoreCase)
                ? document.Name
                : document.Name + preview.FileExtension;
            var path = await NativeFileOpener.DownloadToTempAsync(preview.DownloadUrl, fileName);
            Console.WriteLine($"downloaded '{document.Name}' (ext '{preview.FileExtension}') -> {path} ({new FileInfo(path).Length} bytes)");
        }
    }
}
