using RPM.Core.Analysis;
using RPM.Core.Library;

namespace RPM.Core.Tests;

public class MoodRecipeTests
{
    private static MoodList.Song Song(string path, string? genre, string folder, string artist, int year) =>
        new(path, genre, folder, artist, year, Liked: false, Styles: [], MoodList.Sound.Nothing, WithinGenreBy: 0);

    private static readonly MoodList Calm = new(
    [
        Song("a1", "Jazz", "A", "Ann", 1975),
        Song("a2", "Jazz", "A", "Ann", 1975),
        Song("a3", "Jazz", "A2", "Ann", 1978),
        Song("b1", "Rock", "B", "Bob feat. Ann", 1991),
        Song("c1", "Rock", "C", "Cat", 0),
        Song("d1", null, "D", "Dee", 1985),
    ], new HashSet<string>());

    private static readonly Func<IReadOnlyList<string>, IReadOnlyDictionary<string, MusicalKey>> NoKeys =
        _ => new Dictionary<string, MusicalKey>();

    private static MoodRecipe Recipe(IReadOnlyList<string>? genres = null, IReadOnlyList<int>? decades = null, IReadOnlyList<string>? artists = null) =>
        new(MoodRecipeMood.Calm, genres ?? MoodRecipe.Everything, decades ?? [], artists ?? MoodRecipe.Everything, MoodCover.OfMood);

    [Fact]
    public void A_recipe_from_before_covers_wears_the_moods_picture()
    {
        var read = MoodRecipe.FromJson("""{"mood":"Calm","genres":[],"decades":[],"artists":[]}""");
        Assert.NotNull(read);
        Assert.Equal(MoodCoverSource.Mood, read.Cover.Source);
    }

    [Fact]
    public void Nothing_named_is_the_whole_list()
    {
        Assert.Equal(6, Calm.Matching(Recipe()).Count);
    }

    [Fact]
    public void Genres_and_decades_narrow_and_an_undated_song_is_in_no_decade()
    {
        Assert.Equal(["a1", "a2", "a3"], Calm.Matching(Recipe(genres: ["Jazz"])).Songs);
        Assert.Equal(["a1", "a2", "a3", "b1"], Calm.Matching(Recipe(decades: [1970, 1990])).Songs);
        Assert.Equal(["b1"], Calm.Matching(Recipe(genres: ["Rock"], decades: [1990])).Songs);
    }

    [Fact]
    public void A_named_act_matches_a_shared_credit_too()
    {
        Assert.Equal(["a1", "a2", "a3", "b1"], Calm.Matching(Recipe(artists: ["ann"])).Songs);
    }

    [Fact]
    public void Without_named_acts_a_record_gives_one_song()
    {
        var songs = Calm.Matching(Recipe(genres: ["Jazz"])).ForRecipe(100, actsNamed: false, NoKeys);
        Assert.Equal(["a1", "a3"], songs.Order());
    }

    [Fact]
    public void With_named_acts_a_record_gives_more()
    {
        var songs = Calm.Matching(Recipe(artists: ["Ann"])).ForRecipe(100, actsNamed: true, NoKeys);
        Assert.Equal(["a1", "a2", "a3", "b1"], songs.Order());
    }

    [Fact]
    public void A_recipe_reads_back_as_written_and_anything_else_is_none()
    {
        var recipe = new MoodRecipe(MoodRecipeMood.Dancefloor, ["House"], [1990, 2000], ["Daft Punk"], MoodCover.OfFigure(3, 42));
        var read = MoodRecipe.FromJson(recipe.ToJson());
        Assert.NotNull(read);
        Assert.Equal(MoodRecipeMood.Dancefloor, read.Mood);
        Assert.Equal(["House"], read.Genres);
        Assert.Equal([1990, 2000], read.Decades);
        Assert.Equal(["Daft Punk"], read.Artists);
        Assert.Equal(SmartPlaylistKind.Danceable, read.Kind);
        Assert.Equal(MoodCoverSource.Figure, read.Cover.Source);
        Assert.Equal((3, 42u), (read.Cover.Style, read.Cover.Seed));
        Assert.Null(MoodRecipe.FromJson("not json"));
        Assert.Null(MoodRecipe.FromJson(null));
    }
}
