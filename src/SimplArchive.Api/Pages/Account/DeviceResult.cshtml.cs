using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SimplArchive.Api.Pages.Account;

/// <summary>
/// Where the device-grant approval ends (ADR 0823) — the page OpenIddict redirects to once it has recorded
/// the answer. Its whole job is to tell the administrator that the browser is finished and the terminal is
/// where the work continues; it deliberately offers no onward navigation, because the browser was a detour.
/// </summary>
/// <remarks>
/// Anonymous and stateless on purpose: the approval already happened at <c>/connect/verify</c>, so this page
/// grants nothing and can only be read. That the outcome arrives as a query parameter therefore costs
/// nothing — somebody typing <c>?outcome=approved</c> by hand changes the words on a dead end and no token.
/// It is a SEPARATE page rather than a state of the device page because <c>/connect/verify</c> IS OpenIddict's
/// end-user verification endpoint: landing back on it would re-enter the endpoint and re-resolve a code that
/// has just been consumed.
/// </remarks>
[AllowAnonymous]
public class DeviceResultModel : PageModel
{
    public const string Approved = "approved";
    public const string Refused = "refused";

    /// <summary>True when the grant was approved; false for a refusal and for anything unrecognised.</summary>
    public bool WasApproved { get; private set; }

    public void OnGet(string? outcome) =>
        WasApproved = string.Equals(outcome, Approved, StringComparison.Ordinal);
}
