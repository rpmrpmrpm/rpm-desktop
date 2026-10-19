using RPM.Core.Import;

namespace RPM.Core.Tests;

public class FilenameTagsTests
{
    [Fact]
    public void Parses_track_and_title_from_a_file_name()
    {
        var got = FilenameTags.Parse("/music/incoming/03 - On My Way Home.flac", "{track} - {title}");

        Assert.NotNull(got);
        Assert.Equal("03", got!["track"]);
        Assert.Equal("On My Way Home", got["title"]);
    }

    [Fact]
    public void Matches_folders_when_the_pattern_has_them()
    {
        var got = FilenameTags.Parse(
            "/music/Enya/The Memory of Trees/11 - On My Way Home.flac",
            "{artist}/{album}/{track} - {title}");

        Assert.NotNull(got);
        Assert.Equal("Enya", got!["artist"]);
        Assert.Equal("The Memory of Trees", got["album"]);
        Assert.Equal("11", got["track"]);
        Assert.Equal("On My Way Home", got["title"]);
    }

    [Fact]
    public void Ignores_folders_the_pattern_does_not_reach()
    {
        var got = FilenameTags.Parse("/a/b/c/d/05 - Song.mp3", "{track} - {title}");

        Assert.NotNull(got);
        Assert.Equal("Song", got!["title"]);
    }

    [Fact]
    public void Returns_null_when_the_literal_text_does_not_match()
    {
        Assert.Null(FilenameTags.Parse("/music/On My Way Home.flac", "{track} - {title}"));
    }

    [Fact]
    public void Returns_null_when_the_path_is_shallower_than_the_pattern()
    {
        Assert.Null(FilenameTags.Parse("/03 - Song.flac", "{artist}/{album}/{track} - {title}"));
    }

    [Fact]
    public void Number_tokens_take_digits_only()
    {
        Assert.Null(FilenameTags.Parse("/music/One - Song.flac", "{track} - {title}"));
        Assert.Null(FilenameTags.Parse("/music/Song (Nineteen).flac", "{title} ({year})"));
    }

    [Fact]
    public void Accepts_the_zero_padded_form_of_a_number_token()
    {
        var got = FilenameTags.Parse("/music/1-07 Allegretto.flac", "{disc:00}-{track:00} {title}");

        Assert.NotNull(got);
        Assert.Equal("1", got!["disc"]);
        Assert.Equal("07", got["track"]);
        Assert.Equal("Allegretto", got["title"]);
    }

    [Fact]
    public void Every_preset_round_trips_against_the_renderer()
    {
        foreach (var pattern in FilenameTags.Presets)
        {
            var rendered = new NamingTemplate("p", pattern).Render(NamingTemplate.SampleTrack);
            var got = FilenameTags.Parse("/library/" + rendered, pattern);
            Assert.NotNull(got);
            Assert.Equal("Paranoid Android", got!["title"]);
        }
    }

    [Fact]
    public void The_best_preset_is_the_one_that_reads_the_most_files()
    {
        string[] files =
        [
            "/music/Neil Young/Harvest/01. Out On The Weekend.flac",
            "/music/Neil Young/Harvest/02. Harvest.flac",
            "/music/Neil Young/Harvest/03. A Man Needs A Maid.flac",
        ];

        Assert.Equal("{track}. {title}", FilenameTags.Presets[FilenameTags.BestPreset(files)]);
    }

    [Fact]
    public void The_best_preset_of_files_nothing_reads_is_the_first_one()
    {
        string[] files = ["/music/loose/nothing here.flac", "/music/loose/nor here.flac"];

        Assert.Equal(0, FilenameTags.BestPreset(files));
    }

    [Fact]
    public void Every_preset_reads_back_what_the_naming_template_wrote()
    {
        // The round trip above, checked for the whole list and not only for its title: each preset
        // renders a track, and reading that path back with the same pattern returns every field it
        // named. A preset that cannot do that would give the user a pattern that does not work.
        foreach (var pattern in FilenameTags.Presets)
        {
            var rendered = new NamingTemplate("p", pattern).Render(NamingTemplate.SampleTrack);
            var got = FilenameTags.Parse("/library/" + rendered, pattern);

            Assert.NotNull(got);
            foreach (var token in TagTemplate.TokensIn(pattern))
                Assert.True(got!.ContainsKey(token), $"{pattern} lost {token}");
        }
    }

    [Fact]
    public void A_greedy_field_does_not_eat_the_separator()
    {
        var got = FilenameTags.Parse("/music/Simon - Garfunkel - The Boxer.flac", "{artist} - {title}");

        Assert.NotNull(got);
        Assert.Equal("Simon", got!["artist"]);
        Assert.Equal("Garfunkel - The Boxer", got["title"]);
    }
}
