using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// The genres' photographs, kept on disk.
///
/// <para>Resolving one takes a walk of up to six artists, a lookup for each, and two models run over
/// whichever of them has a face. <c>GenreFaces</c> keeps every answer for the life of the process, so
/// scrolling back over a wall costs nothing. That table is empty at launch, so without this cache the cards
/// would come up slowly, one or two a second. These tests check that a shelf is resolved only once. Every
/// later ask, in this session or a later one, is a read of a file.</para>
///
/// <para>This class is in the <c>GenreFaces</c> collection. <see cref="GenreFaceCache.RootOverride"/> is
/// global to the process, and <see cref="GenreFacesTests"/> uses it too. Two classes using it at once would
/// each delete the other's folder.</para>
/// </summary>
[Collection("GenreFaces")]
public class GenreFaceCacheTests : IDisposable
{
    /// <summary>Each test uses its own folder, never the real one. A test that deleted the faces of the
    /// person running it would be worse than the bug it looks for.</summary>
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rpm-genre-faces-" + Guid.NewGuid().ToString("N"));

    public GenreFaceCacheTests()
    {
        Directory.CreateDirectory(_root);
        GenreFaceCache.RootOverride = _root;
    }

    public void Dispose()
    {
        GenreFaceCache.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>The key used by GenreFaces: the shelf, the shape and the acts considered.</summary>
    private static string Key(string genre, params string[] acts)
        => $"{genre}␟1.330␟{string.Join('|', acts)}";

    [Fact]
    public void A_shelf_nobody_has_asked_about_is_not_known()
        => Assert.Null(GenreFaceCache.Known(Key("Rock", "Sting")));

    [Fact]
    public void A_face_comes_back_as_it_went_in()
    {
        var key = Key("Rock", "Sting");
        var face = new byte[] { 1, 2, 3, 4, 5 };

        GenreFaceCache.Keep(key, face);

        Assert.Equal(face, GenreFaceCache.Known(key));
    }

    /// <summary>
    /// "Nobody on this shelf has a photograph" is an answer and is kept.
    /// </summary>
    /// <remarks>
    /// <b>The most important case, and the easiest to forget.</b> It is the most expensive answer: every
    /// act on the shelf was checked, and none had a face. On a borrowed library it is the answer for most
    /// shelves. If it is not kept, every launch repeats the whole walk for each shelf that will never have
    /// a picture.
    ///
    /// <para>So two cases must be told apart: null means "not asked before", and an empty entry means
    /// "asked, and the answer was nothing".</para>
    /// </remarks>
    [Fact]
    public void Nobody_having_a_photograph_is_an_answer_and_is_kept()
    {
        var key = Key("Other", "Nobody", "Also Nobody");

        GenreFaceCache.Keep(key, null);

        var known = GenreFaceCache.Known(key);
        Assert.NotNull(known);
        Assert.Empty(known);
    }

    /// <summary>A shelf whose acts changed is a different question, so it does not get the old picture.
    /// GenreFaces applies the same rule in memory.</summary>
    [Fact]
    public void A_shelf_whose_acts_changed_is_asked_afresh()
    {
        GenreFaceCache.Keep(Key("Rock", "Sting"), [9, 9, 9]);

        Assert.Null(GenreFaceCache.Known(Key("Rock", "Sting", "The Police")));
    }

    /// <summary>The order of the acts is part of the question. The caller rotates them so that the same
    /// collection asks the same question on every machine. That only works while the order is
    /// kept.</summary>
    [Fact]
    public void The_order_of_the_acts_is_part_of_the_question()
    {
        GenreFaceCache.Keep(Key("Rock", "Sting", "The Police"), [7]);

        Assert.Null(GenreFaceCache.Known(Key("Rock", "The Police", "Sting")));
    }

    /// <summary>Two shelves never share an entry. That is the purpose of the key.</summary>
    [Fact]
    public void Two_shelves_keep_their_own_faces()
    {
        GenreFaceCache.Keep(Key("Rock", "Sting"), [1]);
        GenreFaceCache.Keep(Key("Jazz", "Miles Davis"), [2]);

        Assert.Equal([1], GenreFaceCache.Known(Key("Rock", "Sting")));
        Assert.Equal([2], GenreFaceCache.Known(Key("Jazz", "Miles Davis")));
    }

    /// <summary>A new answer replaces the old one, so a shelf has one entry however many times it is
    /// resolved.</summary>
    [Fact]
    public void A_shelf_keeps_one_entry()
    {
        var key = Key("Rock", "Sting");

        GenreFaceCache.Keep(key, [1]);
        GenreFaceCache.Keep(key, [2, 2]);

        Assert.Equal([2, 2], GenreFaceCache.Known(key));
        Assert.Single(Directory.GetFiles(_root, "*.face"));
    }

    /// <summary>A half-written file must not be read as a picture by the next launch. So the write goes to
    /// a .tmp file, which is then renamed.</summary>
    [Fact]
    public void Nothing_half_written_is_left_on_the_disk()
    {
        GenreFaceCache.Keep(Key("Rock", "Sting"), [1, 2, 3]);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
