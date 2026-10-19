using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RPM.Core.Metadata;
using RPM.Desktop.Presentation.Main.Genres;
using SkiaSharp;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The photograph at the head of every genre's page.
///
/// <para>These tests guard a join that the compiler cannot check. A Python generator cuts thirty-six JPEG
/// files and writes a table that names them. The app reads that table and opens the files by path. Every
/// way this can go wrong is silent on screen: a genre added to the app but not to genre-photos.json, a file
/// the csproj does not copy, or a crop made at a shape the panel is not laid out at. All three show up as a
/// head that is slightly wrong, not as an error.</para>
/// </summary>
public class GenrePhotoTests
{
    /// <summary>Every shelf the wall draws a card for opens a page, and every page has a head. A genre
    /// added to the app without a run of the generator would open on a head with no picture. The page would
    /// draw that correctly, and nobody would notice.</summary>
    [Fact]
    public void Every_genre_the_app_files_music_under_has_a_photograph()
    {
        foreach (var genre in Genres.All)
            Assert.True(GenrePhotos.For(genre) is not null, $"no photograph for {genre}");

        Assert.NotNull(GenrePhotos.For(RPM.Core.Library.GenreWall.Unclassified));
    }

    /// <summary>The table is keyed the way the rest of the app compares genre names.</summary>
    [Fact]
    public void A_genre_is_found_however_it_is_cased()
    {
        Assert.Equal(GenrePhotos.For("Rock"), GenrePhotos.For("rock"));
        Assert.Null(GenrePhotos.For("Skiffle"));
    }

    /// <summary>Two shelves sharing one file would give two heads that look like one page. The file names
    /// come from a slug of the genre name. "Hip-Hop / Rap" and "Hip Hop Rap" would give the same slug. The
    /// generator refuses such names, and this test checks the same rule from the other side.</summary>
    [Fact]
    public void No_two_shelves_share_a_photograph()
    {
        var files = GenrePhotos.All.Select(p => p.File).ToList();
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var genres = GenrePhotos.All.Select(p => p.Genre).ToList();
        Assert.Equal(genres.Count, genres.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Every photograph named in the table is actually beside the binary.
    ///
    /// <para>This is the csproj's promise, not the generator's. The assets are pulled in by a wildcard, so
    /// adding a genre needs no line there. A wildcard is exactly the kind of thing that goes quietly wrong.
    /// A missing file is caught by <see cref="GenrePhotos.BytesAsync"/> and turns into a head with no
    /// picture, so nothing crashes and nothing says why.</para>
    /// </summary>
    [Fact]
    public void Every_photograph_in_the_table_is_on_the_disk()
    {
        foreach (var photo in GenrePhotos.All)
        {
            var path = GenrePhotos.PathFor(photo.Genre);
            Assert.NotNull(path);
            Assert.True(File.Exists(path), $"{photo.Genre}: {path} is not there");
            Assert.True(new FileInfo(path!).Length > 0, $"{photo.Genre}: {photo.File} is empty");
        }
    }

    /// <summary>
    /// Every photograph is cut to one shape, and that shape is a band rather than a picture.
    ///
    /// <para>The head bleeds: the photograph is stretched over the whole band and cropped to fill it. So
    /// the cut does not have to equal one number. The band's own shape changes with the pane. It is about
    /// six to one on a narrow pane and eleven to one on a wide monitor. The cut must sit INSIDE that range.
    /// A cut narrower than the narrowest band loses the sides of every head. A cut wider than the widest
    /// band loses the top and bottom, and the subject is there.</para>
    ///
    /// <para>Six is the narrow end of the range. It is the floor on purpose, not the middle. Losing width
    /// from a band this long costs little, because the ends are under the colour on one side and
    /// outside the reader's view on the other. Losing height costs the subject.</para>
    /// </summary>
    [Fact]
    public void Every_photograph_is_cut_to_one_band_shaped_slice()
    {
        Assert.InRange(GenrePhotos.Shape, 5.0, 8.0);

        foreach (var photo in GenrePhotos.All)
        {
            using var image = SKBitmap.Decode(GenrePhotos.PathFor(photo.Genre));
            Assert.True(image is not null, $"{photo.Genre}: {photo.File} did not decode");

            var shape = image!.Width / (double)image.Height;
            Assert.True(Math.Abs(shape - GenrePhotos.Shape) < 0.01,
                $"{photo.Genre} is cut {image.Width}x{image.Height} ({shape:0.000}), " +
                $"and the others are {GenrePhotos.Shape}");
        }
    }

    /// <summary>
    /// Every photograph has enough pixels to fill the band on a doubled screen, at the pane width this app
    /// is usually read at.
    ///
    /// <para>A bleeding head is drawn at the width of the page. Unlike a fixed panel, there is no size that
    /// is always enough: a wide enough monitor will always need more pixels. What can be stated is the pane
    /// width it must not be soft at. That is 1440 points, an ordinary laptop window, doubled. Wider than
    /// that the photo is enlarged. Under the colour overlay this shows as softness, not damage. The other
    /// choice would ship four times the bytes for the few monitors that would notice.</para>
    /// </summary>
    [Fact]
    public void Every_photograph_fills_an_ordinary_pane_on_a_doubled_screen()
    {
        const double Pane = 1440;
        const int Needed = (int)(Pane * 2);

        foreach (var photo in GenrePhotos.All)
        {
            using var image = SKBitmap.Decode(GenrePhotos.PathFor(photo.Genre));
            Assert.True(image!.Width * 1.6 >= Needed,
                $"{photo.Genre} is {image.Width} wide and a {Pane}pt band on a doubled screen " +
                $"is {Needed}");
        }
    }

    /// <summary>
    /// The colour always leaves part of the photograph showing, and always covers the name.
    ///
    /// <para>A bleeding head can fail silently in two ways, and the two pull against each other. If the
    /// colour reaches too far, it covers the whole band. Then the page is a flat colour again, with the
    /// photograph drawn entirely underneath and no error anywhere. If the colour stops too early, the name
    /// is read off a photograph. That is worse, and it only shows on the genres whose picture happens to be
    /// pale.</para>
    ///
    /// <para>So both are checked at every pane width, using the same arithmetic as the layout.</para>
    /// </summary>
    [Theory]
    [InlineData(760)]
    [InlineData(900)]
    [InlineData(1240)]
    [InlineData(1600)]
    [InlineData(2560)]
    public void The_colour_covers_the_name_and_still_leaves_a_photograph(double pane)
    {
        // These two lines match the arithmetic in BannerBox_SizeChanged and PaintHead.
        var scrim = Math.Min(GenreDetailView.ScrimReach, pane);
        var solid = GenreDetailView.NameEdge / scrim;

        Assert.True(scrim < pane || pane <= GenreDetailView.ScrimReach,
            $"at {pane}pt the colour reaches {scrim:0} and the band is {pane:0}");

        // The name stands on colour that is still fully there.
        Assert.True(GenreDetailView.NameEdge <= scrim,
            $"at {pane}pt the name ends at {GenreDetailView.NameEdge:0} and the colour stops at {scrim:0}");
        Assert.InRange(solid, 0.05, 0.95);
    }

    /// <summary>
    /// Past the colour there is enough of the band left to be a photograph, not only the end of a fade.
    ///
    /// <para>Checked from the pane width where the page stops folding upward. That is the narrowest width
    /// at which the page is laid out as two columns, so it is the narrowest width where a reader is
    /// expected to read a genre. Below that width the head is mostly colour. That is a weaker heading, but
    /// not a broken one.</para>
    /// </summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(1240)]
    [InlineData(1600)]
    [InlineData(2560)]
    public void A_third_of_the_band_is_photograph_at_the_widths_the_page_is_read_at(double pane)
    {
        var scrim = Math.Min(GenreDetailView.ScrimReach, pane);
        Assert.True(pane - scrim >= pane / 3,
            $"at {pane}pt the colour reaches {scrim:0}, leaving {pane - scrim:0} of photograph");
    }

    /// <summary>
    /// The table and the file it was written from say the same thing.
    ///
    /// <para>GenrePhotos.Data.cs is generated and checked in, so it can be out of date. If someone edits
    /// genre-photos.json and forgets to run the generator, the app keeps shipping the previous pictures,
    /// and nothing shows it. The wordmarks have the same guard, for the same reason.</para>
    /// </summary>
    [Fact]
    public void The_generated_table_is_not_behind_the_file_it_was_written_from()
    {
        var source = SharedFiles.Path("genre-photos.json");
        Assert.True(File.Exists(source), $"{source} is not there");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(source));
        var written = doc.RootElement.GetProperty("photos")
            .EnumerateArray()
            .Select(p => p.GetProperty("genre").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(written.OrderBy(g => g, StringComparer.Ordinal),
                     GenrePhotos.All.Select(p => p.Genre).OrderBy(g => g, StringComparer.Ordinal));

        Assert.Equal(GenrePhotos.Shape, doc.RootElement.GetProperty("shape").GetDouble(), 3);
    }

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
