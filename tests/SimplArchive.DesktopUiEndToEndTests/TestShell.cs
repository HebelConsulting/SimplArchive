using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

/// <summary>
/// The window a tab view-model is given when a test constructs it without one (#517, ADR 0729).
/// </summary>
/// <remarks>
/// It CAPTURES what the tab reports rather than discarding it: before the seam became a constructor argument a
/// test simply left <c>StatusReporter</c> null, so everything a tab said went nowhere and no test could assert
/// on it. Recording it costs nothing and makes the messages available to any test that wants them.
/// </remarks>
internal sealed class TestShell : IShellContext
{
    public List<string> Reports { get; } = [];

    public int CheckoutsChangedCount { get; private set; }

    public void Report(string status) => Reports.Add(status);

    public void SaveLayout()
    {
    }

    public void ActivateIntray()
    {
    }

    public Task DocumentChangedOnServerAsync(Guid documentId) => Task.CompletedTask;

    public Task CheckoutsChangedAsync()
    {
        CheckoutsChangedCount++;
        return Task.CompletedTask;
    }

    public DropFiling? DropFiling => null;

    public OcrLanguageCatalog? OcrLanguages => null;

    public Guid? CurrentUserId => null;
}
