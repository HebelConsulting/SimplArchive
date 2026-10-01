using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.UnitTests;

/// <summary>
/// The vocabulary <c>saconsole acl</c> speaks (#1504).
/// </summary>
/// <remarks>
/// Pinned here rather than end to end because it is pure: an end-to-end run would exercise one combination
/// and call the parser covered, while the mistakes this can make — a preset granting more than it says, a
/// subtraction that does not subtract — are all in the expansion.
/// </remarks>
public class AclRightsTests
{
    [Fact]
    public void There_are_nine_rights_and_the_record_covers_all_of_them()
    {
        // The guard against the parser and the request body drifting apart: every name must be readable and
        // writable, or a right exists on the wire that the vocabulary cannot express.
        Assert.Equal(9, AclRights.All.Count);
        Assert.Equal(AclRights.All.Count, AclRights.All.Distinct(StringComparer.Ordinal).Count());

        foreach (var right in AclRights.All)
        {
            Assert.True(AclRights.None.With(right, granted: true).Has(right));
            Assert.False(AclRights.None.With(right, granted: true).With(right, granted: false).Has(right));
        }
    }

    [Theory]
    [InlineData("read", "see,read-content")]
    [InlineData("full", "see,read-content,edit-content,edit-index-data,delete,create-sub-items,manage-permissions,move,annotate")]
    [InlineData("see", "see")]
    [InlineData("see,annotate", "see,annotate")]
    [InlineData("annotate,see", "see,annotate")]       // output is canonical order, not input order
    [InlineData("SEE,Annotate", "see,annotate")]       // names are matched case-insensitively
    [InlineData("see,see", "see")]                     // repeats collapse
    public void An_expression_expands_to_the_rights_it_names(string expression, string expected) =>
        Assert.Equal(expected.Split(','), AclRights.Expand(expression));

    [Fact]
    public void Manage_adds_the_permission_right_to_a_READER_and_is_not_everything()
    {
        // The preset most likely to be reached for carelessly, so its content is pinned. Bundling the ability
        // to hand out rights with the ability to change content would make `manage` grant more than the word
        // says — and the escalation cap would not catch it, because a caller holding both may grant both.
        var manage = AclRights.Parse("manage");

        Assert.True(manage.CanSee);
        Assert.True(manage.CanReadContent);
        Assert.True(manage.CanManagePermissions);
        Assert.False(manage.CanEditContent);
        Assert.False(manage.CanDelete);
        Assert.NotEqual(AclRights.Parse("full"), manage);
    }

    [Fact]
    public void A_preset_can_be_taken_back_down_with_a_minus()
    {
        var rights = AclRights.Parse("write,-edit-content");

        Assert.True(rights.CanEditIndexData);
        Assert.False(rights.CanEditContent);
        Assert.DoesNotContain("edit-content", rights.Granted);
    }

    [Fact]
    public void Subtraction_is_LEFT_TO_RIGHT_so_position_means_what_it_reads_as()
    {
        // `write,-edit-content` is "the write preset, without that one".
        Assert.DoesNotContain("edit-content", AclRights.Expand("write,-edit-content"));

        // `-edit-content,write` subtracts from nothing and then adds the preset, so it is just the preset.
        // Stated as a test because the alternative — subtractions applied last wherever they appear — is
        // identical in the common case and differs exactly when somebody wrote something unusual on purpose.
        Assert.Contains("edit-content", AclRights.Expand("-edit-content,write"));
    }

    [Fact]
    public void An_unknown_name_is_refused_and_the_refusal_LISTS_the_vocabulary()
    {
        var refusal = Assert.Throws<CliException>(() => AclRights.Expand("see,reed-content"));

        Assert.Contains("'reed-content' is not a right", refusal.Message, StringComparison.Ordinal);

        // Every right and every preset, because a right is not guessable from the domain — `edit-index-data`
        // and `edit-content` are different rights over the same document.
        foreach (var right in AclRights.All)
        {
            Assert.Contains(right, refusal.Message, StringComparison.Ordinal);
        }

        foreach (var preset in AclRights.Presets.Keys)
        {
            Assert.Contains(preset, refusal.Message, StringComparison.Ordinal);
        }

        // And it says how to subtract, since that is the one piece of syntax nobody would guess.
        Assert.Contains("-edit-content", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_withholds_everything_the_expression_does_not_name()
    {
        // What makes `set` a replace: the parsed value is the COMPLETE intended set, so an unnamed right is
        // false rather than absent. The endpoint is a PUT and takes all nine.
        var rights = AclRights.Parse("see");

        Assert.True(rights.CanSee);
        Assert.Equal(["see"], rights.Granted);
        Assert.Equal(8, AclRights.All.Count(r => !rights.Has(r)));
    }

    [Fact]
    public void An_empty_expression_grants_nothing_rather_than_everything()
    {
        // The direction that matters: a `--rights` the shell expanded to nothing must not be read as "full".
        Assert.Empty(AclRights.Expand(string.Empty));
        Assert.Equal(AclRights.None, AclRights.Parse(string.Empty));
        Assert.Equal(AclRights.None, AclRights.Parse("   "));
    }

    [Fact]
    public void Plus_and_Minus_leave_the_rest_alone_which_is_what_grant_and_revoke_rest_on()
    {
        var current = AclRights.Parse("read");

        var granted = current.Plus(AclRights.Expand("annotate"));
        Assert.Equal(["see", "read-content", "annotate"], granted.Granted);

        var revoked = granted.Minus(AclRights.Expand("read-content"));
        Assert.Equal(["see", "annotate"], revoked.Granted);

        // Revoking something not held is not an error and changes nothing — so `revoke` is safe to re-run.
        Assert.Equal(revoked, revoked.Minus(AclRights.Expand("delete")));
    }

    [Fact]
    public void Revoking_everything_leaves_an_entry_granting_NOTHING_rather_than_no_entry()
    {
        // Worth pinning because the two are different acts with different outcomes: an explicit empty grant
        // still exists, while deleting the entry returns the principal to whatever inheritance gives them.
        var emptied = AclRights.Parse("full").Minus(AclRights.All);

        Assert.Equal(AclRights.None, emptied);
        Assert.Empty(emptied.Granted);
    }
}
