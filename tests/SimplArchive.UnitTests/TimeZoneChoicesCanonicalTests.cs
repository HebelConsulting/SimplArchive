using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The local zone is stamped on new appointments and must be one the picker offers (#1126). An alias the host reports
// for itself (macOS reported Berlin as "Atlantic/Jan_Mayen" in October 2026) maps to its canonical id.
public sealed class TimeZoneChoicesCanonicalTests
{
    [Fact]
    public void An_alias_the_picker_does_not_offer_becomes_its_canonical_zone()
    {
        if (TimeZoneChoices.All().Count == 0)
        {
            return;   // this host has no zone database; Contains() declines to judge there too
        }

        Assert.DoesNotContain("Atlantic/Jan_Mayen", TimeZoneChoices.All());

        Assert.Equal("Europe/Berlin", TimeZoneChoices.Canonical("Atlantic/Jan_Mayen"));
    }

    [Fact]
    public void An_offered_zone_and_an_unknown_id_are_left_alone()
    {
        if (TimeZoneChoices.All().Count == 0)
        {
            return;   // this host has no zone database; Contains() declines to judge there too
        }


        Assert.Equal("Europe/Zurich", TimeZoneChoices.Canonical("Europe/Zurich"));
        Assert.Equal("Not/AZone", TimeZoneChoices.Canonical("Not/AZone"));
    }

    [Fact]
    public void The_local_zone_is_always_one_the_picker_offers()
    {
        if (TimeZoneChoices.All().Count == 0)
        {
            return;   // this host has no zone database; Contains() declines to judge there too
        }


        Assert.Contains(TimeZoneChoices.Local(), TimeZoneChoices.All());
    }
}
