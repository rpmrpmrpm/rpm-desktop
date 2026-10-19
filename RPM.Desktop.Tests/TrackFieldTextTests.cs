using System.Globalization;
using System.Text.Json;
using RPM.Desktop.Presentation.Main.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Tests;

/// <summary>
/// How a track's facts are spelled in a grid cell.
///
/// <para>The invariant this file exists for is stated in the code it tests. There are two grids: the
/// library's table and the import queue's. A file that reads "24-bit / 96 kHz / 41.2 MB" on its way in
/// has to read the same once it is filed. One set of functions keeps that true. These are the cases where
/// a column of noughts or a blank cell would be the wrong answer.</para>
/// </summary>
public class TrackFieldTextTests
{
    private static string Number(double value, string format) =>
        value.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>Zero means "not set", and a column of noughts says the opposite of that.</summary>
    [Fact]
    public void A_tag_number_that_is_not_set_is_blank_rather_than_zero()
    {
        Assert.Equal("", TrackFieldText.Num(0));
        Assert.Equal("7", TrackFieldText.Num(7));
    }

    [Theory]
    [InlineData(".flac", "FLAC")]
    [InlineData("flac", "FLAC")]
    [InlineData(".m4a", "M4A")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_format_is_named_the_way_a_listener_names_it(string? extension, string expected)
        => Assert.Equal(expected, TrackFieldText.Kind(extension));

    [Theory]
    [InlineData(320, "320 kbps")]
    [InlineData(0, "")]
    [InlineData(null, "")]
    public void A_bitrate_nobody_read_is_blank(int? kbps, string expected)
        => Assert.Equal(expected, TrackFieldText.Bitrate(kbps));

    [Fact]
    public void A_round_sample_rate_is_said_in_whole_kilohertz()
    {
        Assert.Equal("48 kHz", TrackFieldText.SampleRate(48_000));
        Assert.Equal("96 kHz", TrackFieldText.SampleRate(96_000));
    }

    /// <summary>44.1 is the one everybody recognises, and rounding it to 44 would be wrong in the way a
    /// listener notices first.</summary>
    [Fact]
    public void A_sample_rate_that_is_not_round_keeps_its_tenth()
        => Assert.Equal($"{Number(44.1, "0.#")} kHz", TrackFieldText.SampleRate(44_100));

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void A_sample_rate_nobody_read_is_blank(int? hz)
        => Assert.Equal("", TrackFieldText.SampleRate(hz));

    /// <summary>The words come from the string table ("24-bit", "24 bits", "24 Bit"), so what is
    /// checked here is the key and the number the cell hands it.</summary>
    [Fact]
    public void A_bit_depth_reads_as_a_depth()
    {
        Assert.Equal(new Phrase("Track_BitDepth", "24"), TrackFieldText.BitDepthPhrase(24));
        Assert.Null(TrackFieldText.BitDepthPhrase(0));
        Assert.Null(TrackFieldText.BitDepthPhrase(null));
    }

    /// <summary>The counts a listener has a word for get the word; a surround layout reads the same in
    /// every language; the rest get the count.</summary>
    [Theory]
    [InlineData(1, "Track_Mono", "")]
    [InlineData(2, "Track_Stereo", "")]
    [InlineData(6, null, "5.1")]
    [InlineData(8, null, "7.1")]
    [InlineData(4, "Track_Channels", "4")]
    public void Channels_are_named_where_a_name_exists(int channels, string? key, string value)
        => Assert.Equal(new Phrase(key, value), TrackFieldText.ChannelsPhrase(channels));

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void No_channel_count_is_a_blank_cell(int? channels)
        => Assert.Null(TrackFieldText.ChannelsPhrase(channels));

    [Theory]
    [InlineData(0, "")]
    [InlineData(9, "0:09")]
    [InlineData(65, "1:05")]
    [InlineData(600, "10:00")]
    public void A_length_under_an_hour_is_minutes_and_seconds(double seconds, string expected)
        => Assert.Equal(expected, TrackFieldText.Duration(seconds));

    /// <summary>A live side or a DJ set: an hour in, the minutes have to start again rather than
    /// counting to numbers nobody reads.</summary>
    [Theory]
    [InlineData(3600, "1:00:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(7384, "2:03:04")]
    public void A_length_past_an_hour_carries_the_hours(double seconds, string expected)
        => Assert.Equal(expected, TrackFieldText.Duration(seconds));

    /// <summary>Half a second either way must not turn 59.6 seconds into "0:60".</summary>
    [Fact]
    public void A_length_that_rounds_up_to_the_minute_reads_as_a_minute()
        => Assert.Equal("1:00", TrackFieldText.Duration(59.6));

    [Fact]
    public void A_size_climbs_the_units_and_a_missing_one_is_blank()
    {
        Assert.Null(TrackFieldText.SizePhrase(0));
        Assert.Equal(new Phrase("Unit_Bytes", "512"), TrackFieldText.SizePhrase(512));
        Assert.Equal(new Phrase("Unit_KB", "2"), TrackFieldText.SizePhrase(2048));
        Assert.Equal(new Phrase("Unit_MB", Number(41.2, "0.#")), TrackFieldText.SizePhrase(43_200_512));
        Assert.Equal(new Phrase("Unit_GB", Number(1.25, "0.##")), TrackFieldText.SizePhrase(1_342_177_280));
    }

    /// <summary>
    /// The whole point of the file: the same facts spelled the same way wherever a track is listed.
    /// Both grids call these functions, so this is the property that keeps them agreeing.
    /// </summary>
    [Fact]
    public void The_same_file_reads_identically_in_both_grids()
    {
        var queueRow = (TrackFieldText.Kind(".flac"), TrackFieldText.BitDepthPhrase(24),
            TrackFieldText.SampleRate(96_000), TrackFieldText.SizePhrase(43_200_512));

        var libraryRow = (TrackFieldText.Kind("flac"), TrackFieldText.BitDepthPhrase(24),
            TrackFieldText.SampleRate(96_000), TrackFieldText.SizePhrase(43_200_512));

        Assert.Equal(queueRow, libraryRow);
        Assert.Equal(("FLAC", new Phrase("Track_BitDepth", "24"), "96 kHz", new Phrase("Unit_MB", Number(41.2, "0.#"))),
            libraryRow);
    }

    /// <summary>The keys are handed out here rather than written at a <c>Loc.Get</c> call, so the
    /// repo-wide guard over <c>strings.json</c> cannot see them. Each is checked against the file: in
    /// all five languages, with the one placeholder the cell fills where it fills one.</summary>
    [Fact]
    public void Every_key_a_cell_hands_out_is_in_the_string_table()
    {
        var phrases = new Phrase?[]
        {
            TrackFieldText.BitDepthPhrase(24),
            TrackFieldText.ChannelsPhrase(1), TrackFieldText.ChannelsPhrase(2), TrackFieldText.ChannelsPhrase(4),
            TrackFieldText.SizePhrase(512), TrackFieldText.SizePhrase(2048),
            TrackFieldText.SizePhrase(43_200_512), TrackFieldText.SizePhrase(1_342_177_280),
        };

        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));
        var problems = new List<string>();
        foreach (var phrase in phrases)
        {
            Assert.NotNull(phrase);
            if (phrase!.Value.StringKey is not { } key)
                continue;
            if (!doc.RootElement.TryGetProperty(key, out var entry))
            {
                problems.Add($"{key} is not in strings.json");
                continue;
            }

            // A word with no number ("Mono") takes no placeholder; a number in words takes {0}.
            var wantsValue = phrase.Value.Value.Length > 0;
            foreach (var locale in new[] { "en", "fr", "es", "de", "zh-Hans" })
            {
                var text = entry.TryGetProperty(locale, out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                    problems.Add($"{key}: no {locale}");
                else if (text.Contains("{0}") != wantsValue)
                    problems.Add($"{key}: {locale} \"{text}\" " + (wantsValue ? "does not show the value" : "has a placeholder nothing fills"));
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
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
