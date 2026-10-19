using System.Text.Json;

using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Tests;

/// <summary>
/// The sentence under a generated list that is not there yet, on the playlists pane's card.
///
/// <para>The three blockers must not share a line. Each asks the listener for something different, and the
/// wrong line sends someone to do work that changes nothing. For example, a mood list on a collection that
/// the measuring pass has only just started is not short of music.</para>
/// </summary>
public class LockedListLineTests
{
    [Fact]
    public void A_list_short_of_songs_asks_for_music()
    {
        Assert.Equal("ListLock_Songs", LockedListLine.Key(new FeatureGate(GateNeed.SongsInList, 8, 20)));
    }

    [Fact]
    public void A_list_the_pass_has_not_reached_asks_for_nothing_but_time()
    {
        // Its band would fill from music already on the disk. Telling the user to import more here is the
        // reason GateNeed exists.
        Assert.Equal("ListLock_Measuring", LockedListLine.Key(new FeatureGate(GateNeed.Measured, 8, 20)));
    }

    [Fact]
    public void A_list_read_off_the_listening_log_asks_for_listening()
    {
        Assert.Equal("ListLock_Plays", LockedListLine.Key(new FeatureGate(GateNeed.Plays, 5, 20)));
    }

    [Fact]
    public void One_song_to_go_is_its_own_line()
    {
        // "Play 1 more songs" is the reason there are two keys, not one key with a number in it.
        Assert.Equal("ListLock_PlaysOne", LockedListLine.Key(new FeatureGate(GateNeed.Plays, 19, 20)));
    }

    [Fact]
    public void One_song_short_is_its_own_line_too()
    {
        Assert.Equal("ListLock_SongsOne", LockedListLine.Key(new FeatureGate(GateNeed.SongsInList, 19, 20)));
    }

    /// <summary>The two songs lines take different numbers of arguments. The same test that picks the key
    /// also picks which line is used. A translation that drops <c>{2}</c> from the plural line prints "9 of
    /// 10 songs, more needed". A translation that adds it to the singular line prints the placeholder
    /// itself.</summary>
    [Fact]
    public void The_plural_songs_line_counts_and_the_singular_one_does_not()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));

        foreach (var locale in new[] { "en", "fr", "es", "de", "zh-Hans" })
        {
            Assert.Contains("{2}", Words(doc, "ListLock_Songs", locale));
            Assert.DoesNotContain("{2}", Words(doc, "ListLock_SongsOne", locale));
        }
    }

    private static string Words(JsonDocument doc, string key, string locale)
    {
        Assert.True(doc.RootElement.TryGetProperty(key, out var entry), $"{key} is not in strings.json");
        Assert.True(entry.TryGetProperty(locale, out var words), $"{key} has no {locale}");
        return words.GetString() ?? "";
    }

    /// <summary>The home screen's gate counts albums. No generated list is ever blocked on albums, because
    /// <c>LockedLists.Need</c> answers with the other three. If the albums case were folded into the songs
    /// line, it would print an album count after the word "songs". So this method throws instead of
    /// guessing.</summary>
    [Fact]
    public void The_home_screens_gate_is_not_a_lists_gate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LockedListLine.Key(new FeatureGate(GateNeed.Albums, 8, 25)));
    }

    /// <summary>The keys are built in code, not written at a <c>Loc.Get</c> call. So the repo-wide check
    /// over <c>strings.json</c> cannot see them. A key that is missing from the file is shown on screen as
    /// its own name.</summary>
    [Fact]
    public void Every_line_is_in_the_string_table()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));

        string[] keys =
        [
            LockedListLine.Key(new FeatureGate(GateNeed.SongsInList, 8, 20)),
            LockedListLine.Key(new FeatureGate(GateNeed.SongsInList, 19, 20)),
            LockedListLine.Key(new FeatureGate(GateNeed.Measured, 8, 20)),
            LockedListLine.Key(new FeatureGate(GateNeed.Plays, 5, 20)),
            LockedListLine.Key(new FeatureGate(GateNeed.Plays, 19, 20)),

            // And the card's own four, which the markup asks for by name.
            "ListLock_Kicker", "ListLock_Heading", "ListLock_Body", "ListLock_Hide",
        ];

        foreach (var key in keys)
        {
            Assert.True(doc.RootElement.TryGetProperty(key, out var entry), $"{key} is not in strings.json");
            Assert.True(entry.TryGetProperty("en", out var en) && en.GetString()?.Length > 0, $"{key} has no words");
        }
    }

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
