using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>The details of a track that rpm already has, merged with what the other app says.</summary>
public class MigrationMergeTests
{
    private static readonly DateTimeOffset Y2015 = new(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Y2020 = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Y2024 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SourceTrack Source(int rating = 0, bool favorite = false, DateTimeOffset? added = null, DateTimeOffset? last = null) =>
        new("s1", new LocalFile("/m/a.mp3"), "T", "A", "Al", "A", 1, 1, 200, 1000, rating, favorite, 3, last, added);

    private static RpmSide Rpm(int rating = 0, bool favorite = false, long favoritedAt = 0, long added = 0, long last = 0) =>
        new(rating, favorite, favoritedAt, added, last);

    [Fact]
    public void Merge_keeps_existing_rating() =>
        Assert.Equal(6, MigrationMerge.Merge(Rpm(rating: 6), Source(rating: 9)).Rating);

    [Fact]
    public void Merge_takes_the_source_rating_when_rpm_has_none() =>
        Assert.Equal(9, MigrationMerge.Merge(Rpm(), Source(rating: 9)).Rating);

    [Fact]
    public void Merge_favourite_is_either_with_earlier_date()
    {
        var fromSource = MigrationMerge.Merge(Rpm(), Source(favorite: true, added: Y2015));
        Assert.True(fromSource.Favorite);

        var both = MigrationMerge.Merge(Rpm(favorite: true, favoritedAt: Y2024.UtcTicks), Source(favorite: true, added: Y2015));
        Assert.True(both.Favorite);
        Assert.Equal(Y2024.UtcTicks, both.FavoritedUtcTicks);

        Assert.True(MigrationMerge.Merge(Rpm(favorite: true, favoritedAt: 7), Source()).Favorite);
        Assert.False(MigrationMerge.Merge(Rpm(), Source()).Favorite);
    }

    [Fact]
    public void Merge_favourite_from_the_source_is_dated_when_it_was_added()
    {
        // The other apps do not say when a song was loved. The date it joined that library is the
        // earliest the mark can have been made, and it orders the favourites list sensibly.
        var merged = MigrationMerge.Merge(Rpm(), Source(favorite: true, added: Y2015));
        Assert.Equal(Y2015.UtcTicks, merged.FavoritedUtcTicks);
    }

    [Fact]
    public void Merge_added_is_earlier_and_ignores_missing()
    {
        Assert.Equal(Y2015.UtcTicks, MigrationMerge.Merge(Rpm(added: Y2020.UtcTicks), Source(added: Y2015)).AddedUtcTicks);
        Assert.Equal(Y2015.UtcTicks, MigrationMerge.Merge(Rpm(added: Y2015.UtcTicks), Source(added: Y2020)).AddedUtcTicks);
        Assert.Equal(Y2020.UtcTicks, MigrationMerge.Merge(Rpm(added: Y2020.UtcTicks), Source(added: null)).AddedUtcTicks);
        Assert.Equal(Y2020.UtcTicks, MigrationMerge.Merge(Rpm(added: 0), Source(added: Y2020)).AddedUtcTicks);
    }

    [Fact]
    public void Merge_last_played_is_later()
    {
        Assert.Equal(Y2024.UtcTicks, MigrationMerge.Merge(Rpm(last: Y2020.UtcTicks), Source(last: Y2024)).LastPlayedUtcTicks);
        Assert.Equal(Y2024.UtcTicks, MigrationMerge.Merge(Rpm(last: Y2024.UtcTicks), Source(last: Y2020)).LastPlayedUtcTicks);
        Assert.Equal(Y2020.UtcTicks, MigrationMerge.Merge(Rpm(last: Y2020.UtcTicks), Source(last: null)).LastPlayedUtcTicks);
    }
}
