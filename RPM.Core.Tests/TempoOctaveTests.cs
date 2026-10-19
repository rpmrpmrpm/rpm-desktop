using RPM.Core.Analysis;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// The hint that says a record could be counted another way. It never changes a tempo. These tests
/// guard against the two failures that would make it useless: firing on records that were read
/// correctly, and staying quiet on the ones that were not.
/// </summary>
public class TempoOctaveTests
{
    [Theory]
    [InlineData(128, "Electronic / Dance")]
    [InlineData(174, "Electronic / Dance")]
    [InlineData(90, "Hip-Hop / Rap")]
    [InlineData(120, "Rock")]
    [InlineData(45, "Classical")]
    [InlineData(124, null)]
    public void A_tempo_where_the_music_is_counted_raises_nothing(double bpm, string? genre)
        => Assert.Null(TempoOctave.OtherCount(bpm, genre));

    [Theory]
    // The error this exists for: a house record the model counted at half speed.
    [InlineData(64, "Electronic / Dance", 128)]
    [InlineData(62.5, "Deep House", 125)]
    // And the one hip-hop suffers from, the other way round: trap written at 140, counted at 70.
    [InlineData(140, "Hip-Hop / Rap", 70)]
    [InlineData(170, "R&B", 85)]
    [InlineData(190, "Reggae", 95)]
    // With nothing to go on, the fold window is the band, so the hint fires on exactly the readings
    // the fold would rewrite without asking.
    [InlineData(64, null, 128)]
    [InlineData(55, "Spoken Word", 110)]
    public void A_tempo_outside_it_offers_the_count_that_is_inside(double bpm, string? genre, double expected)
        => Assert.Equal(expected, TempoOctave.OtherCount(bpm, genre)!.Value, 6);

    [Fact]
    public void A_reading_neither_count_explains_is_left_alone()
    {
        // 30 BPM against hip-hop's 70..115: the double is 60, still below the band. Something is
        // wrong with this reading and it is not the octave, so there is nothing to offer.
        Assert.Null(TempoOctave.OtherCount(30, "Hip-Hop / Rap"));

        // The same from above. 480 halves to 240, which is still not hip-hop.
        Assert.Null(TempoOctave.OtherCount(480, "Hip-Hop / Rap"));
    }

    [Fact]
    public void A_slow_record_of_a_slow_kind_is_not_asked_about()
    {
        // The fold would double this on sight and call a largo a scherzo. Classical is counted from 40,
        // so 48 is where it belongs and there is no question to raise.
        Assert.Null(TempoOctave.OtherCount(48, "Classical"));

        // Where the genre says otherwise, the same tempo does raise one.
        Assert.Equal(96, TempoOctave.OtherCount(48, "Pop")!.Value, 6);
    }

    [Fact]
    public void Downtempo_electronic_music_is_not_read_as_miscounted_house()
    {
        // 90 BPM electronic records exist in quantity (dub, trip-hop, downtempo). Telling somebody their
        // 90 might be a 180 on every one of them would make people stop reading the hint.
        Assert.Null(TempoOctave.OtherCount(90, "Electronic / Dance"));
        Assert.Null(TempoOctave.OtherCount(95, "Trip Hop"));
    }

    [Fact]
    public void The_genre_is_read_the_way_the_rest_of_the_app_reads_one()
    {
        // Whatever a file or a catalogue says, resolved through the same closed list every other genre
        // decision goes through.
        Assert.Equal(TempoOctave.BandFor("Electronic / Dance"), TempoOctave.BandFor("edm"));
        Assert.Equal(TempoOctave.BandFor("House"), TempoOctave.BandFor("Deep House"));
        Assert.Equal(TempoOctave.BandFor("Hip-Hop / Rap"), TempoOctave.BandFor("hip hop"));

        // Techno has a shelf of its own and is counted on its own: a dance floor keeps a steady pace, and
        // the wider Electronic / Dance window is there for the downtempo records that shelf still holds.
        // What must hold is that a style reaches the same band as the shelf it hangs off (the line
        // above), and that no shelf fell back to the default (the lines below).
        Assert.NotEqual(TempoOctave.Default, TempoOctave.BandFor("Techno"));
        Assert.Equal(TempoOctave.BandFor("Hip-Hop / Rap"), TempoOctave.BandFor("Rap"));
        Assert.Equal(TempoOctave.BandFor("Hip-Hop / Rap"), TempoOctave.BandFor("Trap"));
        Assert.Equal(TempoOctave.BandFor("Classical"), TempoOctave.BandFor("Neo-Classical"));

        // And none of them fell through to the default, which is what "no band at all" looks like.
        foreach (var promoted in new[] { "Techno", "House", "Rap", "Trap", "Funk", "Soul", "Indie" })
            Assert.NotEqual(TempoOctave.Default, TempoOctave.BandFor(promoted));
    }

    [Fact]
    public void A_genre_with_nothing_to_say_falls_back_to_the_old_window()
    {
        Assert.Equal(TempoOctave.Default, TempoOctave.BandFor(null));
        Assert.Equal(TempoOctave.Default, TempoOctave.BandFor("Religious"));
        Assert.Equal(TempoOctave.Default, TempoOctave.BandFor("Norwegian Sea Shanties"));
        Assert.Equal((TempoEstimator.MinBpm, TempoEstimator.MaxBpm), TempoOctave.Default);
    }

    [Fact]
    public void Every_genre_on_the_shelf_has_a_band_that_holds_a_dance_floor()
    {
        // Not a table of exact numbers, because those are a matter of judgement and may be tuned. What
        // must hold is that no band is so narrow that it fires on ordinary music, and that every genre
        // the app files records under has an answer at all.
        foreach (var genre in Genres.All)
        {
            var (low, high) = TempoOctave.BandFor(genre);
            Assert.True(high > low * 1.4, $"{genre} is counted in {low}..{high}, too narrow to be quiet");
            Assert.True(high < low * 5.1, $"{genre} is counted in {low}..{high}, too wide to catch anything");
            Assert.True(low >= 40 && high <= 220, $"{genre} is counted in {low}..{high}");
        }
    }

    [Fact]
    public void At_most_one_of_the_two_counts_can_ever_be_the_answer()
    {
        // The offer is never a choice between two, so the caller needs only one line and one button. The
        // reason: a band wide enough to hold both a tempo's double and its half (Classical's 40..200 is
        // one) holds the tempo itself as well, and there is nothing to ask about a reading that is
        // already where it belongs.
        foreach (var genre in Genres.All)
        {
            var (low, high) = TempoOctave.BandFor(genre);
            for (double bpm = 20; bpm < 400; bpm += 0.5)
            {
                bool doubleFits = bpm * 2 >= low && bpm * 2 <= high;
                bool halfFits = bpm / 2 >= low && bpm / 2 <= high;
                bool itselfFits = bpm >= low && bpm <= high;

                if (doubleFits && halfFits)
                    Assert.True(itselfFits, $"{bpm} of {genre} fits {low}..{high} both ways but not itself");

                double? offered = TempoOctave.OtherCount(bpm, genre);
                if (itselfFits)
                    Assert.Null(offered);
                else if (doubleFits)
                    Assert.Equal(bpm * 2, offered!.Value, 6);
                else if (halfFits)
                    Assert.Equal(bpm / 2, offered!.Value, 6);
                else
                    Assert.Null(offered);
            }
        }
    }

    [Fact]
    public void Nothing_is_offered_for_a_record_with_no_tempo()
    {
        Assert.Null(TempoOctave.OtherCount(0, "Pop"));
        Assert.Null(TempoOctave.OtherCount(-1, "Pop"));
    }
}
