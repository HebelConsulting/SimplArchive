using SimplArchive.Api.Documents;

namespace SimplArchive.Api.Hypermedia;

/// <summary>
/// The per-row answers to "may this caller delete / rename / move THIS item?", carried by every listing a
/// client builds a node from.
/// </summary>
/// <remarks>
/// <para>
/// Flags rather than rels, and that is ADR 0719 deciding rather than taste: <c>DELETE</c> and <c>PUT</c> live
/// at the item's OWN address, so a <c>delete</c> rel beside <c>self</c> would be the same URL under a second
/// name with the method already saying which action it is. <c>CanMove</c> is the odd one — <c>move</c> IS a
/// rel on the single-document resource, because that endpoint has an address of its own — but a listing row
/// does not carry the document's full link set, and inventing a rel here purely to answer a yes/no question
/// would make the row's link list mean something different from the resource's.
/// </para>
/// <para>
/// Inline on the row, not behind a rel or a second request, for the reason ADR 0557 gives and #673 already
/// applied to <c>Admits</c>: a context menu opens on a right-click, and a round trip there is a visible pause
/// on the one interaction that must feel instant. The value already travels with the listing, which is exactly
/// where 0557 says to take it from.
/// </para>
/// <para>
/// <b>What "false" promises.</b> Absence of the capability means the same thing a missing rel means (ADR
/// 0543): not available to you, here, now — so the client disables the affordance rather than offering it and
/// handling a 403. <c>CanMove</c> carries the narrower promise: it says this ITEM may be moved, never that a
/// given move will succeed, because a move also needs <c>CanCreateSubItems</c> on the TARGET and no row can
/// answer that before a target is chosen (the picker owns that half, ADR 0689).
/// </para>
/// </remarks>
public interface ICarriesRowCapabilities
{
    bool CanDelete { get; set; }

    bool CanEditIndexData { get; set; }

    bool CanMove { get; set; }

    /// <summary>May the caller open Manage access on this row?</summary>
    /// <remarks>
    /// A flag and NOT the `acl-entries` rel, which was the first attempt and hid the action on every row: the
    /// listings deliberately omit rights-dependent rels (their own comments say so), so that gate was always
    /// false. It is the same right the detail pane already gates on, which is what closes the split where one
    /// action had two answers.
    /// </remarks>
    bool CanManagePermissions { get; set; }

    /// <summary>May the caller create a plain child — a folder, or an uploaded document — inside this row?</summary>
    /// <remarks>
    /// The successor to the `create-child` rel (#854, ADR 0719). That rel and `children` addressed the SAME
    /// URL and differed only by method, so the pair was one address under two names; the method already says
    /// which action it is, and what the second name actually carried was a capability.
    ///
    /// It answers BOTH halves — the mask policy (<c>ChildCreationPolicy.AdmitsPlainChild</c>) AND
    /// <c>CanCreateSubItems</c> on this row. The rel it replaces answered only the first on three of its four
    /// emission sites, because a per-row rights resolution used to cost a query per row; its own comment said
    /// so. <c>GetCallerRightsForManyAsync</c> removed that cost, so the flag can mean what a client needs it to
    /// mean — "would a create here actually succeed?" — instead of "would the mask permit one, rights
    /// notwithstanding".
    /// </remarks>
    bool CanCreateChildren { get; set; }
}

/// <summary>Stamps the capabilities onto a page of rows, in one batch rather than one lookup per row.</summary>
/// <remarks>
/// <para>
/// ONE implementation over a type parameter, deliberately: four different row types need this same answer
/// (the children listing, the repositories listing, search hits and the references listing), and #638's own
/// motivating defect was a rel present on three of six surfaces — "looks correct in testing and hides the
/// action everywhere else". Four copies of this loop would be four chances to diverge, and nothing would point
/// out the one that had.
/// </para>
/// <para>
/// The per-row rights come from <see cref="DocumentAccessService.GetCallerRightsForManyAsync"/>, which resolves
/// the whole page in about the queries one document costs — without it, stamping a 50-row page would have meant
/// several hundred round trips on the hottest read in the app, which is the cost that makes a rule like this
/// go unimplemented.
/// </para>
/// <para>
/// A row whose id the rights lookup did not answer gets all-false rather than being skipped: silently leaving
/// the defaults would also be all-false, but only by accident, and an accident that agrees with the safe answer
/// is the kind that stops agreeing later.
/// </para>
/// </remarks>
public static class RowCapabilities
{
    /// <param name="admitsPlainChild">
    /// The mask half of <see cref="ICarriesRowCapabilities.CanCreateChildren"/>, which the rights lookup cannot
    /// answer: whether this row's own mask admits a plain child at all. A lambda rather than a second interface
    /// member, because it is the one thing that genuinely differs per row type and the call site is where a
    /// reader wants to see it.
    /// </param>
    public static async Task StampAsync<TRow>(
        IReadOnlyCollection<TRow> rows,
        Func<TRow, Guid> documentIdOf,
        Func<TRow, bool> admitsPlainChild,
        DocumentAccessService access,
        CancellationToken cancellationToken)
        where TRow : ICarriesRowCapabilities
    {
        if (rows.Count == 0)
        {
            return;
        }

        var rights = await access.GetCallerRightsForManyAsync([.. rows.Select(documentIdOf)], cancellationToken);

        foreach (var row in rows)
        {
            if (!rights.TryGetValue(documentIdOf(row), out var r))
            {
                continue;
            }

            row.CanDelete = r.CanDelete;
            row.CanEditIndexData = r.CanEditIndexData;
            row.CanMove = r.CanMove;
            row.CanManagePermissions = r.CanManagePermissions;
            row.CanCreateChildren = admitsPlainChild(row) && r.CanCreateSubItems;
        }
    }
}
