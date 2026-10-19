using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using SkiaSharp;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The photograph behind a generated playlist's frame.
///
/// <para>What is guarded here is a seam nothing in the compiler looks at. A Python generator cuts
/// seventy-nine JPEGs and writes a table naming them; the app reads that table and opens the files by
/// path. Every way that can come apart is silent on screen, because a list with no photograph draws
/// its figure instead and that is a finished cover: a file the csproj forgot to copy, a table written
/// from a JSON that was edited afterwards, a picture cut at the wrong shape, a query rewritten in
/// <see cref="CoverLooks"/> after the picture that answered it was chosen. None of those throw.</para>
///
/// <para>The query is kept in the JSON for this reason. A rewritten query must not leave a list quietly
/// wearing a picture of something else. So the check is made here, where a rewritten line fails a
/// test.</para>
/// </summary>
public class PlaylistPhotoTests
{
    /// <summary>The repo, found by walking up to the file that only its root has.</summary>
    private static string Root
    {
        get
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at is not null && !File.Exists(Path.Combine(at.FullName, "Directory.Packages.props")))
                at = at.Parent;

            Assert.NotNull(at);
            return at!.FullName;
        }
    }

    private static JsonDocument Book()
        => JsonDocument.Parse(
            File.ReadAllText(SharedFiles.Path("playlist-photos.json")));

    /// <summary>The square a cover is composed at, as the JSON states it. Read rather than restated:
    /// the generator cuts to this number and <c>PlaylistArt.Edge</c> draws at it, and a test that
    /// carried its own copy would agree with neither.</summary>
    private static int Edge
    {
        get
        {
            using var book = Book();
            return book.RootElement.GetProperty("edge").GetInt32();
        }
    }

    /// <summary>
    /// Every list in the table still asks for the words its picture was found with.
    ///
    /// <para>This is the one check with teeth. A line in <see cref="CoverLooks"/> is the record of what
    /// a list is FOR, and rewriting one is how somebody says a row should stop wearing a picture of a
    /// rave warehouse. Nothing else would notice: the picture is filed by what the list IS, so it
    /// survives every change to what the list LOOKS FOR.</para>
    ///
    /// <para>Failing here means one of two things, and the fix is not the same: either the words moved
    /// and the picture should be re-picked (a new id in the JSON), or the words moved and the picture
    /// still suits them (the query line in the JSON is updated to match). Both are decisions; neither
    /// is something a runtime could have made.</para>
    /// </summary>
    [Fact]
    public void Every_picture_still_answers_what_its_list_asks_for()
    {
        using var book = Book();
        foreach (var entry in book.RootElement.GetProperty("photos").EnumerateArray())
        {
            var id = IdOf(entry);
            var look = CoverLooks.Look(id);

            Assert.True(look is not null, $"{entry.GetProperty("file").GetString()}: no look at all");
            Assert.Equal(look!.Value.Query, entry.GetProperty("query").GetString());
        }
    }

    /// <summary>The generated table says what the JSON says. The two come apart the moment somebody
    /// edits the JSON and does not run the generator, and then the app ships the older set.</summary>
    [Fact]
    public void The_table_and_the_json_name_the_same_pictures()
    {
        using var book = Book();
        var written = book.RootElement.GetProperty("photos").EnumerateArray()
            .Select(e => (Id: IdOf(e), File: e.GetProperty("file").GetString()!))
            .ToList();

        Assert.Equal(written.Count, PlaylistPhotos.All.Count);

        foreach (var (id, file) in written)
        {
            var found = PlaylistPhotos.For(id);
            Assert.True(found is not null, $"{file}: the table has no entry for {id}");
            Assert.Equal(file, found!.Value.File);
        }
    }

    /// <summary>Two lists sharing one file would be two covers that look like one list. The names come
    /// out of the kind and the narrowing, so this is really a guard on two entries narrowing the
    /// same.</summary>
    [Fact]
    public void No_two_lists_share_a_photograph()
    {
        var files = PlaylistPhotos.All.Select(p => p.File).ToList();
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var ids = PlaylistPhotos.All.Select(p => p.List).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>
    /// Every photograph named in the table is actually beside the binary.
    ///
    /// <para>This is the csproj's promise and not the generator's: the assets are pulled in by a
    /// wildcard so that adding a list needs no line there, and a wildcard is exactly the kind of thing
    /// that goes quietly wrong. A missing file turns into a drawn figure, so nothing crashes and
    /// nothing says why.</para>
    /// </summary>
    [Fact]
    public void Every_photograph_in_the_table_is_on_the_disk()
    {
        foreach (var photo in PlaylistPhotos.All)
        {
            var path = PlaylistPhotos.PathFor(photo.List);
            Assert.NotNull(path);
            Assert.True(File.Exists(path), $"{photo.File}: {path} is not there");
            Assert.True(new FileInfo(path!).Length > 0, $"{photo.File} is empty");
        }
    }

    /// <summary>
    /// Every photograph is the square the cover is composed at.
    ///
    /// <para>A cover scales what it reads until it fills the square, so a picture of another shape is
    /// drawn correctly and loses its edges. That is the behaviour a hand-added entry leans on and it is
    /// not what the generator should produce: a landscape shipped whole is three times the bytes for
    /// pixels every machine throws away, and it moves the crop from a place somebody looked at it to a
    /// place nobody does.</para>
    /// </summary>
    [Fact]
    public void Every_photograph_is_cut_to_the_square_a_cover_is_drawn_at()
    {
        var edge = Edge;
        foreach (var photo in PlaylistPhotos.All)
        {
            using var bitmap = SKBitmap.Decode(PlaylistPhotos.PathFor(photo.List));
            Assert.True(bitmap is not null, $"{photo.File} does not decode");
            Assert.Equal(edge, bitmap!.Width);
            Assert.Equal(edge, bitmap.Height);
        }
    }

    /// <summary>The credit is recorded for every one of them. The licence asks for none, and an empty
    /// field is a row somebody added without reading where the picture came from.</summary>
    [Fact]
    public void Every_photograph_records_who_took_it()
    {
        foreach (var photo in PlaylistPhotos.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(photo.By), $"{photo.File}: no photographer");
            Assert.False(string.IsNullOrWhiteSpace(photo.Alt), $"{photo.File}: no description");
        }
    }

    /// <summary>A list with no entry is the ordinary case rather than a hole: most generated lists have
    /// no photograph and draw their figure. Pinned so that a reader of <see cref="PlaylistPhotos"/>
    /// does not have to take the sentence on trust.</summary>
    [Fact]
    public void A_list_with_no_photograph_answers_null_rather_than_throwing()
    {
        Assert.Null(PlaylistPhotos.For(SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed)));
        Assert.Null(PlaylistPhotos.PathFor(SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed)));
    }

    private static SmartPlaylistId IdOf(JsonElement entry)
    {
        var kind = Enum.Parse<SmartPlaylistKind>(entry.GetProperty("kind").GetString()!);
        var genre = entry.GetProperty("genre").ValueKind == JsonValueKind.Null
            ? null
            : entry.GetProperty("genre").GetString();
        return new SmartPlaylistId(kind, genre, entry.GetProperty("decade").GetInt32());
    }
}
