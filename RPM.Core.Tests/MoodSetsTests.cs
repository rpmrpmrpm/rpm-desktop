using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The dropped squares of the mood grid (<see cref="MoodSets"/>): Sunrise Set, End Credits and
/// Fortissimo, taken out on 2026-10-26. A dropped square is not offered, and nothing else may quietly
/// bring its songs back as a list of its own.
/// </summary>
public class MoodSetsTests
{
    [Theory]
    [InlineData(SmartPlaylistKind.Calm, "House")]
    [InlineData(SmartPlaylistKind.Calm, "Soundtrack")]
    [InlineData(SmartPlaylistKind.Energetic, "Classical")]
    public void The_dropped_squares_are_not_offered(SmartPlaylistKind kind, string genre)
    {
        Assert.True(MoodSets.IsDropped(kind, genre));
        Assert.False(MoodSets.IsOffered(kind, genre));
    }

    /// <summary>Dropping is not absorbing: no list took these over, so none may lead on one or draw
    /// from one. A set that drew from a dropped square would be the dropped list back under another
    /// name.</summary>
    [Fact]
    public void A_dropped_square_is_neither_absorbed_nor_drawn_by_any_list()
    {
        foreach (var cell in MoodSets.EveryDropped)
        {
            Assert.False(MoodSets.IsAbsorbed(cell.Kind, cell.Genre));
            foreach (var (_, cells) in MoodSets.EverySet)
                Assert.DoesNotContain(cell, cells);
        }
    }

    /// <summary>Their neighbours are untouched: the loud house and the quiet classical are still
    /// lists.</summary>
    [Fact]
    public void The_other_squares_of_the_same_genres_are_still_offered()
    {
        Assert.True(MoodSets.IsOffered(SmartPlaylistKind.Energetic, "House"));
        Assert.True(MoodSets.IsOffered(SmartPlaylistKind.Calm, "Classical"));
        Assert.True(MoodSets.IsOffered(SmartPlaylistKind.Energetic, "Soundtrack"));
    }
}
