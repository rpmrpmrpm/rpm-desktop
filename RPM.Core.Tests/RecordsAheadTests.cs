using RPM.Core.Audio;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// How much of the running order is put on the phone before it is needed.
///
/// <para>The answer is deliberately not a count. Three records ahead is twelve megabytes at 128 and
/// a hundred at the original quality, and the second number is one that a phone user notices.</para>
/// </summary>
public class RecordsAheadTests
{
    private static IEnumerable<double> Album(int count, double seconds = 240) =>
        Enumerable.Repeat(seconds, count);

    [Fact]
    public void Three_renditions_fit_wifi_easily()
    {
        Assert.Equal(3, RecordsAhead.Count(Album(10), 128, cellular: false));
    }

    [Fact]
    public void Never_more_than_the_ceiling_however_small_the_records()
    {
        // Thirty-second pieces on a fast connection would allow hundreds. Beyond a few, the fetches
        // compete with the record playing for a connection that someone is listening to.
        Assert.Equal(RecordsAhead.Most, RecordsAhead.Count(Album(50, seconds: 30), 96, cellular: false));
    }

    [Fact]
    public void The_original_file_fits_fewer()
    {
        // Twenty-seven megabytes a record against under four. The budget decides here, not the
        // ceiling. This is why the answer is not a count.
        Assert.Equal(2, RecordsAhead.Count(Album(10), AudioLadder.Original, cellular: false));
        Assert.Equal(3, RecordsAhead.Count(Album(10), 128, cellular: false));
    }

    [Fact]
    public void A_data_plan_gets_one_record_of_cover_and_not_an_afternoon()
    {
        Assert.Equal(1, RecordsAhead.Count(Album(10), AudioLadder.Original, cellular: true));
    }

    [Fact]
    public void The_first_record_is_always_allowed_however_heavy()
    {
        // An hour-long set at the original quality is far past any budget here. Refusing it would
        // give no look-ahead at all to the setting that needs it most.
        Assert.Equal(1, RecordsAhead.Count([3600], AudioLadder.Original, cellular: true));
    }

    [Fact]
    public void A_length_nobody_knows_is_charged_for_rather_than_waved_through()
    {
        // Zero means the catalogue has no duration. Treating it as free would fetch three of them.
        var unknown = RecordsAhead.Count(Album(10, seconds: 0), AudioLadder.Original, cellular: true);

        Assert.Equal(1, unknown);
    }

    [Fact]
    public void Nothing_coming_means_nothing_to_fetch()
    {
        Assert.Equal(0, RecordsAhead.Count([], 128, cellular: false));
    }

    [Fact]
    public void A_record_weighs_its_length_times_its_rate()
    {
        // Four minutes at 128 kbps: 240 * 128000 / 8.
        Assert.Equal(3_840_000, RecordsAhead.Weighs(240, 128));

        // And the file as uploaded is charged at what FLAC actually costs, not at nothing.
        Assert.True(RecordsAhead.Weighs(240, AudioLadder.Original) > RecordsAhead.Weighs(240, 320));
    }
}
