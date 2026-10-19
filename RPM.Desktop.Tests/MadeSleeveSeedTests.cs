using RPM.Core.Metadata;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What a record with no artwork is drawn from.
///
/// <para>A made sleeve is a generated picture, built from a hash of one name. So the name is the whole
/// contract. The same record can appear on the wall, in the list, in the drawer, and in the suggestions row
/// of another album. Each time it must give the same name. Otherwise the reader sees four different sleeves
/// for one record and takes them to be four records.</para>
/// </summary>
public class MadeSleeveSeedTests
{
    /// <summary>A card whose cover read has returned nothing. This is the only state in which a made sleeve
    /// is drawn. See <see cref="MadeSleeveWhenTests"/> for the other half of this rule.</summary>
    private static SimilarRow Row(string title, string artist, string folder, SimilarBecause why = SimilarBecause.SameGenre)
        => new(new SimilarAlbum(title, artist, folder, why), new SimilarTileMetrics()) { CoverMissing = true };

    /// <summary>The folder is the seed. The wall's tile and the album list are drawn from it. Every
    /// suggestion is now a record on this shelf (see SimilarAlbums), so the folder is the only seed there
    /// is. No suggestion card opens a web page.</summary>
    [Fact]
    public void A_record_is_drawn_from_its_folder()
        => Assert.Equal("Alan Parsons/On Air", Row("On Air", "Alan Parsons", "Alan Parsons/On Air").FigureSeed);

    [Fact]
    public void Two_records_are_two_different_pictures()
        => Assert.NotEqual(Row("On Air", "Alan Parsons", "Alan Parsons/On Air").FigureSeed,
                           Row("Try Anything Once", "Alan Parsons", "Alan Parsons/Try Anything Once").FigureSeed);

    /// <summary>The reason a record is suggested is only a label on the card. The same record can be
    /// suggested for two different reasons, and it is still the same record, so it is the same
    /// picture.</summary>
    [Fact]
    public void Why_it_is_suggested_does_not_change_its_picture()
        => Assert.Equal(Row("On Air", "Alan Parsons", "Alan Parsons/On Air", SimilarBecause.LikeTheArtist).FigureSeed,
                        Row("On Air", "Alan Parsons", "Alan Parsons/On Air", SimilarBecause.SameEra).FigureSeed);

    /// <summary>The seed is asked for each time the row is realized again while it scrolls past. The answer
    /// must not depend on when it is asked.</summary>
    [Fact]
    public void The_same_row_gives_the_same_seed_every_time()
    {
        var row = Row("On Air", "Alan Parsons", "Alan Parsons/On Air");
        Assert.Equal(row.FigureSeed, row.FigureSeed);
    }
}

/// <summary>
/// When a record is drawn as a made sleeve. This is a separate question from what it is drawn from.
///
/// <para>The figure is tied to the state of the record, not to the folder. Every surface has the folder as
/// soon as it is realized. If the figure were tied to the folder, every record on a wall of two thousand
/// would be painted and then hidden under its own artwork. The seed answers the real question: has this
/// record been looked for everywhere and found to have no cover?</para>
/// </summary>
public class MadeSleeveWhenTests
{
    private static AlbumListEntry Entry()
        => AlbumListEntry.Album("Talking Heads/Remain in Light", "Remain in Light", "1980", "Talking Heads", 8);

    /// <summary>A record that is realized but not yet read is not a record without artwork. It is a record
    /// whose sleeve nobody has asked for yet.</summary>
    [Fact]
    public void A_record_nobody_has_read_yet_is_drawn_nothing()
        => Assert.Null(Entry().FigureSeed);

    [Fact]
    public void A_record_the_read_came_back_empty_for_is_drawn_from_its_folder()
    {
        var entry = Entry();
        entry.CoverMissing = true;
        Assert.Equal("Talking Heads/Remain in Light", entry.FigureSeed);
    }

    /// <summary>If artwork is added to a record while the shelf is open, the made sleeve is removed. The
    /// figure is only a placeholder. A figure left under a real cover would hold memory for a picture
    /// nobody can see.</summary>
    [Fact]
    public void Artwork_arriving_takes_the_made_sleeve_away_again()
    {
        var entry = Entry();
        entry.CoverMissing = true;
        entry.CoverMissing = false;
        Assert.Null(entry.FigureSeed);
    }

    /// <summary>The surface draws from the seed, so the change must be announced. A figure that appeared
    /// only on the next rebind would leave an empty square on a shelf that has finished reading.</summary>
    [Fact]
    public void The_verdict_tells_the_surface_the_seed_has_changed()
    {
        var entry = Entry();
        var said = new List<string?>();
        entry.PropertyChanged += (_, e) => said.Add(e.PropertyName);

        entry.CoverMissing = true;

        Assert.Contains(nameof(AlbumListEntry.FigureSeed), said);
    }

    /// <summary>The change is announced once. The list re-reads a record on every cloud change and every
    /// forced reload. If the verdict were announced each time, the brush would be rebound and the picture
    /// decoded again, even though the record's state has not changed.</summary>
    [Fact]
    public void The_same_verdict_twice_is_said_once()
    {
        var entry = Entry();
        entry.CoverMissing = true;

        var said = 0;
        entry.PropertyChanged += (_, _) => said++;
        entry.CoverMissing = true;

        Assert.Equal(0, said);
    }
}
