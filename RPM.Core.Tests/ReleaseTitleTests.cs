using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// Composing the name of a movement.
///
/// <para>The work and the movement are separate tags and separate facts. That lets a player print a
/// sonata's name once and its movements under it. The title is still the title, though, and
/// everything that knows nothing about works reads it: a filename template, a car stereo, another
/// tagger, the phone's lock screen. Both catalogues and the rip compose it through here, so a ripped
/// file and the listing it was named from cannot disagree.</para>
/// </summary>
public class ReleaseTitleTests
{
    [Fact]
    public void A_movement_is_named_after_its_work()
        => Assert.Equal("Sonata In C Major, KV. 330: Allegretto",
                        ReleaseTrackInfo.TitleOf("Sonata In C Major, KV. 330", "Allegretto"));

    [Fact]
    public void A_track_belonging_to_no_work_is_left_alone()
        => Assert.Equal("Fantasy In D Minor, KV. 397",
                        ReleaseTrackInfo.TitleOf(null, "Fantasy In D Minor, KV. 397"));

    /// <summary>Some submitters write the whole name into every row. Saying it twice does not
    /// help.</summary>
    [Fact]
    public void A_movement_already_naming_its_work_is_not_told_twice()
        => Assert.Equal("Symphony No. 5: I. Allegro",
                        ReleaseTrackInfo.TitleOf("Symphony No. 5", "Symphony No. 5: I. Allegro"));

    [Fact]
    public void A_work_with_no_movement_named_is_the_work()
        => Assert.Equal("Boléro", ReleaseTrackInfo.TitleOf("Boléro", "   "));

    [Fact]
    public void A_blank_work_names_nothing()
        => Assert.Equal("Dreams", ReleaseTrackInfo.TitleOf("  ", "Dreams"));

    /// <summary>Whitespace around either half is a submitter's typing, not part of the name.</summary>
    [Fact]
    public void Both_halves_are_trimmed()
        => Assert.Equal("Sonata: Allegro", ReleaseTrackInfo.TitleOf("  Sonata ", " Allegro  "));
}
