using RPM.Core.Models;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Library;
using RPM.Desktop.Presentation.Main.Library.Models;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the album list does per record as it fills in, checked against the two rules that keep a shelf
/// of ten thousand records scrolling smoothly: a line builds only its own template, and a cover is
/// read once.
/// </summary>
public class AlbumListFillTests
{
    private static byte[] Picture(SKColor colour, int edge = 64)
    {
        using var bitmap = new SKBitmap(edge, edge);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static TrackInfo Track(string title)
        => new()
        {
            FilePath = $"/music/Artist/Record/{title}.flac",
            Extension = ".flac",
            Title = title,
            Artist = "Artist",
            Album = "Record",
        };

    // ── the tint: one decode, the same answer ──

    [Fact]
    public void A_vivid_sleeve_tints_to_its_highlight()
    {
        var sleeve = Picture(new SKColor(220, 40, 30));

        Assert.Equal(CoverColor.Highlight(sleeve), CoverColor.Tint(sleeve));
    }

    [Fact]
    public void A_sleeve_with_no_colour_in_it_falls_back_to_its_dominant()
    {
        // Grey: no bucket passes the highlight's saturation floor, so the highlight returns nothing
        // and the dominant colour is used instead. Tint must still decode the picture only ONCE here.
        var sleeve = Picture(new SKColor(120, 120, 120));

        Assert.Null(CoverColor.Highlight(sleeve));
        Assert.Equal(CoverColor.Dominant(sleeve), CoverColor.Tint(sleeve));
    }

    [Fact]
    public void A_sleeve_that_will_not_decode_tints_to_nothing()
        => Assert.Null(CoverColor.Tint([1, 2, 3, 4]));

    // ── the line templates: a track builds a track, and nothing else ──

    [Fact]
    public void A_track_gets_the_track_template()
    {
        var line = AlbumTrackItem.Build([Track("One")]).Single();

        Assert.True(line.IsTrack);
        Assert.Equal(AlbumLineKind.Track, AlbumLineTemplateSelector.KindOf(line));
    }

    [Fact]
    public void A_work_header_gets_the_work_template()
    {
        // A disc divider is the other header, and it cannot be built in a test: its caption is a
        // localised string, and there is no package to read one from.
        var tracks = new List<TrackInfo>
        {
            Track("Allegretto") with { Work = "Sonata in C", Composers = "Mozart" },
            Track("Andante") with { Work = "Sonata in C", Composers = "Mozart" },
        };

        var header = AlbumTrackItem.Build(tracks, classical: true).First(l => l.IsWorkHeader);

        Assert.Equal(AlbumLineKind.WorkHeader, AlbumLineTemplateSelector.KindOf(header));
    }

    [Fact]
    public void Anything_the_selector_does_not_know_still_gets_a_line()
        => Assert.Equal(AlbumLineKind.Track, AlbumLineTemplateSelector.KindOf("not a line"));

    // ── the badge is read once, not once per rebind ──

    [Fact]
    public void A_record_starts_with_neither_its_sleeve_nor_its_mark_read()
    {
        var entry = AlbumListEntry.Album("Artist/Record", "Record", "1979", "Artist", 11);

        Assert.False(entry.Requested);
        Assert.False(entry.BadgeRead);
    }
}
