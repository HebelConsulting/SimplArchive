using SimplArchive.Application.Security;

namespace SimplArchive.UnitTests;

// The chosen-password rule (#849, ADR 0065's other half): a minimum length and a breach check, and
// deliberately nothing else — NIST argues against composition rules, expiry and history.
public class PasswordPolicyTests
{
    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]          // 11 — the boundary, one under
    public void A_password_under_twelve_characters_is_refused(string password) =>
        Assert.Equal(PasswordRefusal.TooShort, PasswordPolicy.Refusal(password));

    [Fact]
    public void Twelve_characters_is_accepted_at_the_boundary() =>
        // Exactly 12 and not reducible to anything on the list: the boundary must be inclusive, or the stated
        // minimum and the enforced one differ by one, which is the classic off-by-one nobody reads the code for.
        Assert.Null(PasswordPolicy.Refusal("vrkplmzqdwtf"));

    [Fact]
    public void An_absurdly_long_password_is_refused_rather_than_hashed() =>
        Assert.Equal(PasswordRefusal.TooLong, PasswordPolicy.Refusal(new string('x', 257)));

    // THE POINT OF THE WHOLE DESIGN. Only 10 of the list's 10,001 entries are >= 12 characters, so a verbatim
    // lookup would refuse almost nothing that the length rule does not already refuse. These are what people
    // actually choose when told to pick twelve characters with a number in it — and every one of them reduces
    // to a list entry.
    [Theory]
    [InlineData("Password2026")]
    [InlineData("password123!")]
    [InlineData("P@ssw0rd2026")]
    [InlineData("Basketball26")]
    [InlineData("Sunshine2026!")]
    [InlineData("!!!football!!!")]
    public void A_decorated_common_password_is_still_common(string password) =>
        Assert.Equal(PasswordRefusal.Common, PasswordPolicy.Refusal(password));

    [Fact]
    public void A_password_that_is_only_decoration_is_refused() =>
        // Normalises to nothing at all. Treating an empty reduction as a miss would ACCEPT "123456789012",
        // which is the single most predictable twelve-character string there is.
        Assert.Equal(PasswordRefusal.Common, PasswordPolicy.Refusal("123456789012"));

    // The other half of a normalisation rule, and the half that is usually missing: it must not refuse strong
    // passwords. Over-normalising is how a well-meant check starts rejecting a password manager's output, and
    // the person then picks something they can type instead.
    [Theory]
    [InlineData("correct-horse-battery-staple")]  // a passphrase: interior characters must survive
    [InlineData("Tc9#mWq2vLx8ZrN4")]              // a password manager's output
    [InlineData("quiet-lantern-prawn-9182")]      // decoration at the end, but the word is not common
    public void A_strong_password_is_accepted(string password) =>
        Assert.Null(PasswordPolicy.Refusal(password));

    [Fact]
    public void Normalisation_strips_only_the_ends() =>
        // Pinned explicitly because it is the property that keeps a passphrase intact: interior punctuation is
        // part of the password, not decoration, and removing it would collapse unrelated passphrases together.
        Assert.Equal("horse-battery", PasswordPolicy.Normalise("!!Horse-Battery99!!"));

    [Fact]
    public void The_list_was_actually_loaded() =>
        // ANTI-VACUOUS. Every Common assertion above passes trivially against an empty set — a resource that
        // failed to embed, a renamed file — so one assertion has to prove the list is really there.
        //
        // "unbelievable" is one of the TEN entries that are twelve characters or longer, so it is refused by
        // the verbatim lookup with no normalisation involved — which is what makes it a test of the LIST
        // rather than of the reduction. The first version of this assertion used a qwerty run I had assumed
        // was on the list and never checked; it is not, so the check proved nothing and failed for the one
        // reason an anti-vacuous test must not fail.
        Assert.Equal(PasswordRefusal.Common, PasswordPolicy.Refusal("unbelievable"));
}
