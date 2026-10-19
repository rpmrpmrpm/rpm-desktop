using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// The made sleeves, kept on disk.
///
/// <para>A figure is six gradients, a shaft of light and a blur. The wall asks for one as soon as a tile
/// with no artwork is realized, which happens while the wall is scrolled. So a record is painted only once.
/// Every later request, in this session or a later one, reads the saved file. These tests check
/// this.</para>
///
/// <para>This class is in the palette collection. Drawing needs the app's ten colours, and an empty palette
/// gives no figure to cache. <see cref="RpmPalette.Override"/> is global to the process, so two classes
/// must not use it at the same time. See <see cref="PaletteStandInCollection"/>.
/// <see cref="FigureCache.RootOverride"/> is used by this class only.</para>
/// </summary>
[Collection(PaletteStandInCollection.Name)]
public class FigureCacheTests : IDisposable
{
    private readonly PaletteStandIn _palette = new();

    /// <summary>Each test uses its own folder, never the real cache folder. A test that deleted the figures
    /// of the person running it would be a worse bug than the one it looks for.</summary>
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rpm-figures-" + Guid.NewGuid().ToString("N"));

    public FigureCacheTests()
    {
        Directory.CreateDirectory(_root);
        FigureCache.RootOverride = _root;
    }

    public void Dispose()
    {
        FigureCache.RootOverride = null;
        _palette.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string[] Entries() => Directory.GetFiles(_root, "*.fig");

    [Fact]
    public void A_record_with_no_artwork_is_painted_and_left_on_the_disk()
    {
        Assert.NotEmpty(FigureCache.Sleeve("Talking Heads/Remain in Light")!);
        Assert.Single(Entries());
    }

    /// <summary>The painter must run only once. The test writes bytes that the painter could never produce
    /// into the file, and asks again. The bytes that come back show that the file was read.</summary>
    [Fact]
    public void The_second_ask_reads_the_file_rather_than_painting_again()
    {
        FigureCache.Sleeve("Talking Heads/Remain in Light");

        var sentinel = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(Entries().Single(), sentinel);

        Assert.Equal(sentinel, FigureCache.Sleeve("Talking Heads/Remain in Light"));
    }

    /// <summary>Two records are two pictures, so they need two entries. A cache that answered one record
    /// with another record's figure would show a shelf of identical sleeves.</summary>
    [Fact]
    public void Two_records_are_two_entries()
    {
        var first = FigureCache.Sleeve("Talking Heads/Remain in Light");
        var second = FigureCache.Sleeve("Alan Parsons/On Air");

        Assert.Equal(2, Entries().Length);
        Assert.NotEqual(first, second);
    }

    /// <summary>The name is folded for case (see SoftFigure.Seed). Two spellings of one folder are one
    /// picture, so they must share one file. Two files would mean one of them is painted again on every
    /// request.</summary>
    [Fact]
    public void A_records_spelling_does_not_give_it_a_second_entry()
    {
        FigureCache.Sleeve("Talking Heads/Remain in Light");
        FigureCache.Sleeve("talking heads/remain in light");

        Assert.Single(Entries());
    }

    /// <summary>A record with no name to be drawn from is not a picture, and not an entry either.</summary>
    [Fact]
    public void Nothing_is_drawn_for_nothing()
    {
        Assert.Null(FigureCache.Sleeve(""));
        Assert.Empty(Entries());
    }
}
