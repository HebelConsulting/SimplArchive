using SimplArchive.Cli.Commands;

namespace SimplArchive.UnitTests;

/// <summary>
/// <c>saconsole certificates revoke --on</c> names a MOMENT, because the field it writes is an instant: a CA
/// revokes at a time, and something done that same day is valid or not depending on which side of it it falls.
/// </summary>
public sealed class CertificateRevokeMomentTests
{
    [Theory]
    [InlineData("2026-10-05T14:30:00+02:00", "2026-10-05T12:30:00Z")]
    [InlineData("2026-10-05T14:30:00-04:00", "2026-10-05T18:30:00Z")]
    [InlineData("2026-10-05T14:30:00Z", "2026-10-05T14:30:00Z")]
    [InlineData("2026-10-05", "2026-10-05T00:00:00Z")]
    public void An_instant_keeps_its_offset_and_a_bare_day_is_its_start_in_UTC(string given, string expectedUtc)
    {
        var moment = CertificateRevokeSettings.RevokedAt(given);

        Assert.NotNull(moment);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), moment.Value);
    }

    [Theory]
    [InlineData("2026-10-05T14:30:00")] // no offset: whose 14:30? The archive cannot see the operator's clock.
    [InlineData("05.10.2026")]
    [InlineData("yesterday")]
    public void A_moment_the_archive_cannot_place_is_refused(string given) =>
        Assert.Null(CertificateRevokeSettings.RevokedAt(given));
}
