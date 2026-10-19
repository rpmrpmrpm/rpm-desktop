using RPM.Core.Library;

namespace RPM.Core.Tests;

public class MoodPickTests
{
    private static GenreShare Plays(string genre, double share) =>
        new(genre, ListeningTally.None, share);

    private static HashSet<SmartPlaylistId> Offered(params SmartPlaylistId[] ids) => [.. ids];

    /// <summary>A day that is not the one day in seven, so the usual answer applies.</summary>
    private const int AnOrdinaryDay = 3;

    [Fact]
    public void The_cut_they_play_most_at_this_hour_is_what_is_held_up()
    {
        // The hour asks for Calm; jazz is what they play then; the shelf offers both cuts.
        var pick = MoodPick.For(
            [SmartPlaylistKind.Calm],
            [Plays("Jazz", 0.6), Plays("Rock", 0.4)],
            Offered(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Jazz"),
                    SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock"),
                    SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm)),
            AnOrdinaryDay);

        Assert.Equal(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Jazz"), pick);
    }

    [Fact]
    public void A_cut_the_shelf_does_not_offer_is_not_a_list()
    {
        // They play jazz most, but there is not enough quiet jazz on this shelf for a row. The
        // next genre they play wins, instead of a list that does not exist.
        var pick = MoodPick.For(
            [SmartPlaylistKind.Calm],
            [Plays("Jazz", 0.6), Plays("Rock", 0.4)],
            Offered(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock"),
                    SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm)),
            AnOrdinaryDay);

        Assert.Equal(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock"), pick);
    }

    [Fact]
    public void With_nothing_listened_to_the_whole_shelf_stands()
    {
        var pick = MoodPick.For(
            [SmartPlaylistKind.Calm],
            [],
            Offered(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Jazz"),
                    SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm)),
            AnOrdinaryDay);

        Assert.Equal(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), pick);
    }

    [Fact]
    public void A_mood_this_shelf_cannot_fill_is_stepped_past()
    {
        // The hour asks for Lights out and this collection has no such list. The next mood in the
        // hour's own order is used instead.
        var pick = MoodPick.For(
            [SmartPlaylistKind.Sleep, SmartPlaylistKind.Calm],
            [Plays("Jazz", 1)],
            Offered(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Jazz")),
            AnOrdinaryDay);

        Assert.Equal(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Jazz"), pick);
    }

    [Fact]
    public void A_shelf_offering_none_of_the_moods_is_no_pick()
    {
        Assert.Null(MoodPick.For(
            [SmartPlaylistKind.Calm, SmartPlaylistKind.Focus],
            [Plays("Jazz", 1)],
            Offered(),
            AnOrdinaryDay));
    }

    [Fact]
    public void One_day_in_seven_holds_up_another_of_the_group()
    {
        var genres = new[] { Plays("Jazz", 0.5), Plays("Rock", 0.3) };
        var offered = Offered(
            SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Jazz"),
            SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock"),
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm));

        var usual = 0;
        var other = 0;
        for (var day = 0; day < 70; day++)
        {
            var pick = MoodPick.For([SmartPlaylistKind.Calm], genres, offered, day);
            if (pick == SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Jazz"))
                usual++;
            else
                other++;
        }

        Assert.Equal(60, usual);
        Assert.Equal(10, other);
    }

    [Fact]
    public void The_days_that_differ_walk_the_group_rather_than_repeating_one_alternative()
    {
        // Three in the group, so the two that are not the usual answer take turns.
        var group = 3;

        Assert.Equal(0, MoodPick.Which(group, 1));    // an ordinary day
        Assert.Equal(1, MoodPick.Which(group, 0));    // the first seventh day
        Assert.Equal(2, MoodPick.Which(group, 7));    // the next one moves one further
        Assert.Equal(1, MoodPick.Which(group, 14));   // and it wraps
        Assert.Equal(2, MoodPick.Which(group, 21));
    }

    [Fact]
    public void A_group_of_one_has_nowhere_to_step()
    {
        for (var day = 0; day < 21; day++)
            Assert.Equal(0, MoodPick.Which(1, day));
    }

    [Fact]
    public void A_day_number_below_nought_still_steps_forwards()
    {
        // The caller passes DateTime.DayOfYear, and may pass the result of arithmetic on it.
        Assert.InRange(MoodPick.Which(3, -7), 0, 2);
        Assert.InRange(MoodPick.Which(3, -14), 0, 2);
    }
}
