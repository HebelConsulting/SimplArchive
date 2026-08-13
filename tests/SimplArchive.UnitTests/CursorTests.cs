using SimplArchive.Api.Pagination;

namespace SimplArchive.UnitTests;

// The keyset cursor (ADR 0207) in both its forms. The (timestamp, sequence) form exists for the audit log,
// whose tiebreaker is the hash chain's monotonic Sequence rather than a random row id (issue #478) — so the
// two forms must not be confusable, or a cursor minted for one list would be silently misread by the other
// and page through the wrong rows.
public class CursorTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_id_cursor_round_trips()
    {
        var id = Guid.NewGuid();

        Assert.True(Cursor.TryDecode(Cursor.Encode(At, id), out var at, out var decoded));
        Assert.Equal(At, at);
        Assert.Equal(id, decoded);
    }

    [Fact]
    public void A_sequence_cursor_round_trips()
    {
        Assert.True(Cursor.TryDecodeSequence(Cursor.Encode(At, 42L), out var at, out var sequence));
        Assert.Equal(At, at);
        Assert.Equal(42L, sequence);
    }

    // The point of the '#' marker. Without it a sequence cursor's "42" would parse as neither a Guid nor an
    // error in some readings, and an id cursor's Guid would fail long.TryParse only by luck of formatting —
    // "must not be confusable" is worth asserting rather than assuming.
    [Fact]
    public void Neither_form_decodes_as_the_other()
    {
        Assert.False(Cursor.TryDecodeSequence(Cursor.Encode(At, Guid.NewGuid()), out _, out _));
        Assert.False(Cursor.TryDecode(Cursor.Encode(At, 42L), out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("bm90LWEtY3Vyc29y")] // base64 of "not-a-cursor"
    public void Junk_is_refused_rather_than_throwing(string? cursor)
    {
        Assert.False(Cursor.TryDecode(cursor, out _, out _));
        Assert.False(Cursor.TryDecodeSequence(cursor, out _, out _));
    }
}
