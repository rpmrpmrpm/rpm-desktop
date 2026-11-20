using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What a generated list is called and where it sits, as every head that names one asks it: the desktop with its
/// resource table, the store with the table of the asking app's language. Interface text comes from the table
/// that is passed in; the titles of a mood over one genre are names and the same in every language.
/// </summary>
public class ListNamesTests
{
    /// <summary>A table that answers each key with itself in brackets, so a test can see which key was asked.</summary>
    private static string Keys(string key) => $"[{key}]";

    [Fact]
    public void A_whole_shelf_is_named_from_the_callers_table()
    {
        Assert.Equal("[Playlists_Calm]", ListNames.Name(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), Keys));
        Assert.Equal("[Playlists_MostPlayed]", ListNames.Name("MostPlayed", null, 0, Keys));
    }

    [Fact]
    public void A_mood_over_a_genre_has_its_own_title_in_every_language()
    {
        Assert.Equal("Slow Burn", ListNames.Name(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock"), Keys));
        Assert.Equal("Slow Burn", ListNames.Name("Calm", "Rock", 0, _ => "anything"));
    }

    [Fact]
    public void A_decade_is_its_number_and_an_unknown_kind_its_own_word()
    {
        Assert.Equal("1970s", ListNames.Name("Calm", null, 1970, Keys));
        Assert.Equal("Windswept", ListNames.Name("Windswept", null, 0, Keys));
    }

    [Fact]
    public void Each_list_sits_in_its_folder()
    {
        Assert.Equal(["[Playlists_DecadesFolder]"], ListNames.FolderChain("Calm", null, 1970, Keys));
        Assert.Equal(["[Playlists_MoodsFolder]"], ListNames.FolderChain("Calm", null, 0, Keys));
        Assert.Equal(["[Playlists_MoodsFolder]", "[Playlists_Calm]"], ListNames.FolderChain("Calm", "Rock", 0, Keys));
        Assert.Equal(["[Playlists_SmartFolder]"], ListNames.FolderChain("MostPlayed", null, 0, Keys));
    }

    /// <summary>The store dresses its cut with the same function as the desktop, so a list has one name, one
    /// place and one id whoever cut it.</summary>
    [Fact]
    public void A_cut_is_dressed_with_name_place_and_id()
    {
        var cut = new MoodListsPayload(MoodListPush.Recipe, [new MoodListPayload("Calm", "Rock", 0, ["a", "b"]) { Shows = 1 }]);

        var list = Assert.Single(MoodListPush.Dressed(cut, Keys).Lists);

        Assert.Equal("Slow Burn", list.Title);
        Assert.Equal(["[Playlists_MoodsFolder]", "[Playlists_Calm]"], list.Folder);
        Assert.Equal(MoodListPush.IdOf("Calm", "Rock", 0).ToString("D"), list.Id);
        Assert.Equal(["a", "b"], list.TrackIds);
        Assert.Equal(1, list.Shows);
    }
}
