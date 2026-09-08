using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of #465 — showing and editing a user's e-mail, which is their login identifier. The desktop
// is the canonical client (ADR 0511), so this is where the behaviour is defined and the web follows.
//
// Two halves, deliberately separate. The api-client half drives the real endpoint through the real client, and
// asserts the address is taken from the ROW's `email` rel rather than composed. The view-model half pins the
// two rules that have gone wrong before elsewhere: the pencil appears only where the server offered the rel
// (ADR 0543 — absence means "not available to you, here, now"), and a new selection does not inherit the
// previous subject's entry (ADR 0559).
[Collection(UiCollection.Name)]
public class DesktopUserEmailTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopUserEmailTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_address_comes_from_the_row_and_the_change_reads_back()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var created = await client.Admin.CreateUserAsync($"dt-mail-{suffix}@example.test", "dt-mail-" + suffix);
        var row = (await client.Admin.GetUsersAsync()).Single(u => u.Id == created.Id);

        // The listing carries the address itself and the rel that changes it — the two things the pane needs.
        Assert.Equal($"dt-mail-{suffix}@example.test", row.Email);
        var href = row.Href("email");
        Assert.NotNull(href);

        var moved = await client.Admin.SetUserEmailAsync(href!, $"dt-moved-{suffix}@example.test");
        Assert.Equal($"dt-moved-{suffix}@example.test", moved.Email);

        // Read back from the server rather than from what we just sent.
        Assert.Equal($"dt-moved-{suffix}@example.test",
            (await client.Admin.GetUsersAsync()).Single(u => u.Id == created.Id).Email);

        // A second user cannot take it — the tenant-scoped unique index, surfaced as a message rather than a
        // raw 409 (the localized text is the same key the create path already used).
        var other = await client.Admin.CreateUserAsync($"dt-other-{suffix}@example.test", "dt-other-" + suffix);
        var otherHref = (await client.Admin.GetUsersAsync()).Single(u => u.Id == other.Id).Href("email")!;

        var clash = await Assert.ThrowsAsync<ApiActionException>(
            () => client.Admin.SetUserEmailAsync(otherHref, $"dt-moved-{suffix}@example.test"));
        Assert.False(string.IsNullOrWhiteSpace(clash.Message));

        var refused = await Assert.ThrowsAsync<ApiActionException>(
            () => client.Admin.SetUserEmailAsync(otherHref, "not-an-address"));
        Assert.False(string.IsNullOrWhiteSpace(refused.Message));
    }

    [Fact]
    public void The_pencil_follows_the_rel_and_the_entry_does_not_survive_a_new_selection()
    {
        var vm = new MainWindowViewModel();

        var editable = Row("editable@example.test", withEmailRel: true);
        var frozen = Row("frozen@example.test", withEmailRel: false);

        vm.SelectedPrincipal = editable;
        Assert.Equal("editable@example.test", vm.SelectedPrincipalEmail);
        Assert.True(vm.CanEditPrincipalEmail);

        vm.BeginPrincipalEmailEditCommand.Execute(null);
        Assert.True(vm.IsEditingPrincipalEmail);
        vm.PrincipalEmailEntry = "half-typed@example.test";

        // A row the server did NOT offer the rel for: the address still shows, the pencil does not — and the
        // half-typed entry from the previous subject is gone rather than aimed at this one.
        vm.SelectedPrincipal = frozen;
        Assert.Equal("frozen@example.test", vm.SelectedPrincipalEmail);
        Assert.False(vm.CanEditPrincipalEmail);
        Assert.False(vm.IsEditingPrincipalEmail);
        Assert.NotEqual("half-typed@example.test", vm.PrincipalEmailEntry);

        // A group has no login address at all, so the row never offers one.
        var groupSource = new AdminClient.PrincipalInfo(true, Guid.NewGuid(), "A group", true, NoRights);
        vm.SelectedPrincipal = new PrincipalRowViewModel(true, groupSource.Id, groupSource.Name, true, NoRights, source: groupSource);
        Assert.False(vm.CanEditPrincipalEmail);
    }

    private static PrincipalRowViewModel Row(string email, bool withEmailRel)
    {
        var id = Guid.NewGuid();
        var source = new AdminClient.PrincipalInfo(
            IsGroup: false,
            Id: id,
            Name: email,
            IsActive: true,
            Rights: NoRights,
            Links: withEmailRel
                ? new Dictionary<string, string> { ["email"] = "https://example.test/some/advertised/address" }
                : new Dictionary<string, string>(),
            Email: email);

        return new PrincipalRowViewModel(false, id, email, true, NoRights, source: source);
    }

    private static AdminClient.SystemRightsData NoRights =>
        new(false, false, false, false, false, false, false, false, false, false, false, false, false);
}
