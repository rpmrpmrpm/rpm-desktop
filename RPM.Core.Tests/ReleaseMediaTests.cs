using System.Text.Json;
using RPM.Fingerprint.Catalog;
using RPM.Fingerprint.External;

namespace RPM.Core.Tests;

/// <summary>
/// The media filter: the token that the two sides of the wire agree on, the Lucene fragment it
/// becomes at MusicBrainz, and the reading back of a catalogue's format string. In all three, a
/// quiet mismatch gives a filter that silently does nothing or, worse, one that filters to the wrong
/// medium.
/// </summary>
public class ReleaseMediaTests
{
    [Theory]
    [InlineData(ReleaseMedia.Vinyl)]
    [InlineData(ReleaseMedia.Cd)]
    [InlineData(ReleaseMedia.Cassette)]
    [InlineData(ReleaseMedia.Digital)]
    [InlineData(ReleaseMedia.Sacd)]
    [InlineData(ReleaseMedia.Dvd)]
    [InlineData(ReleaseMedia.BluRay)]
    [InlineData(ReleaseMedia.ReelToReel)]
    [InlineData(ReleaseMedia.EightTrack)]
    public void Every_medium_survives_the_round_trip_through_its_token(ReleaseMedia media)
    {
        var token = media.Token();
        Assert.NotNull(token);
        Assert.Equal(media, ReleaseMediaInfo.Parse(token));
    }

    [Fact]
    public void No_filter_is_the_absence_of_a_token_rather_than_a_token_of_its_own()
    {
        Assert.Null(ReleaseMedia.Any.Token());
        Assert.Equal(ReleaseMedia.Any, ReleaseMediaInfo.Parse(null));
        Assert.Equal(ReleaseMedia.Any, ReleaseMediaInfo.Parse(""));
    }

    [Fact]
    public void A_token_nobody_recognises_filters_nothing()
    {
        // A newer client against an older API, or a hand-typed URL: the safe answer is the whole
        // catalogue, never a different medium.
        Assert.Equal(ReleaseMedia.Any, ReleaseMediaInfo.Parse("minidisc"));
    }

    [Fact]
    public void The_picker_offers_every_medium_starting_with_no_filter()
    {
        Assert.Equal(ReleaseMedia.Any, ReleaseMediaInfo.All[0]);
        Assert.Equal(Enum.GetValues<ReleaseMedia>().Length, ReleaseMediaInfo.All.Count);
        Assert.Equal(ReleaseMediaInfo.All.Distinct().Count(), ReleaseMediaInfo.All.Count);
    }

    [Theory]
    [InlineData(ReleaseMedia.Vinyl, "12\" Vinyl")]
    [InlineData(ReleaseMedia.Vinyl, "7\" Vinyl")]
    [InlineData(ReleaseMedia.Vinyl, "Shellac")]
    [InlineData(ReleaseMedia.Cd, "CD")]
    [InlineData(ReleaseMedia.Cd, "Enhanced CD")]
    [InlineData(ReleaseMedia.Cassette, "Cassette")]
    [InlineData(ReleaseMedia.Digital, "Digital Media")]
    [InlineData(ReleaseMedia.Digital, "File")]
    [InlineData(ReleaseMedia.Sacd, "Hybrid SACD")]
    [InlineData(ReleaseMedia.Dvd, "DVD-Video")]
    [InlineData(ReleaseMedia.BluRay, "Blu-ray")]
    [InlineData(ReleaseMedia.ReelToReel, "Reel-to-reel")]
    [InlineData(ReleaseMedia.EightTrack, "8-Track Cartridge")]
    public void A_format_string_reads_back_as_its_medium(ReleaseMedia media, string format)
    {
        Assert.True(media.Matches(format));
    }

    [Fact]
    public void An_sacd_is_not_a_cd_even_though_its_name_ends_in_one()
    {
        // The trap in substring matching, and the reason the narrower media are asked first.
        Assert.False(ReleaseMedia.Cd.Matches("SACD"));
        Assert.False(ReleaseMedia.Cd.Matches("Hybrid SACD"));
        Assert.True(ReleaseMedia.Sacd.Matches("SACD"));
    }

    [Fact]
    public void No_filter_matches_everything_including_a_release_that_names_no_format()
    {
        Assert.True(ReleaseMedia.Any.Matches("CD"));
        Assert.True(ReleaseMedia.Any.Matches(null));
        // A narrowed filter is the opposite: a release that will not say what it is on cannot claim
        // to be the medium being looked for.
        Assert.False(ReleaseMedia.Vinyl.Matches(null));
    }

    [Fact]
    public void Every_medium_narrows_the_musicbrainz_query_and_none_shares_a_fragment()
    {
        var fragments = ReleaseMediaInfo.All
            .Where(m => m != ReleaseMedia.Any)
            .Select(MusicBrainzClient.FormatFilter)
            .ToList();

        Assert.All(fragments, f => Assert.False(string.IsNullOrWhiteSpace(f)));
        Assert.Equal(fragments.Count, fragments.Distinct().Count());
        Assert.Null(MusicBrainzClient.FormatFilter(ReleaseMedia.Any));
    }

    [Fact]
    public void The_format_names_are_quoted_phrases_because_the_field_is_not_tokenised()
    {
        // format:CD matches nothing; format:"CD" matches everything. The quotes are the only
        // difference, and losing them gives a filter that returns an empty search.
        foreach (var media in ReleaseMediaInfo.All.Where(m => m != ReleaseMedia.Any))
        {
            var fragment = MusicBrainzClient.FormatFilter(media)!;
            Assert.DoesNotContain("format:\"\"", fragment);
            Assert.Matches("format:\"", fragment);
        }
    }

    [Fact]
    public void The_medium_filter_reaches_the_query_anded_onto_the_album()
    {
        var q = MusicBrainzClient.BuildReleaseSearchQuery(
            "rumours", formatFilter: MusicBrainzClient.FormatFilter(ReleaseMedia.Cassette));

        Assert.Contains("AND (format:\"Cassette\")", q);
    }

    [Fact]
    public void Every_medium_has_a_name_to_show_in_the_picker()
    {
        // The picker's labels are looked up as Media_{value}. The UI-wide key guard cannot see this,
        // because the key is composed and not written out. A medium with no string would show the
        // user a row reading "Media_Sacd".
        var strings = SharedFiles.Path("strings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(strings));
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var missing = ReleaseMediaInfo.All.Select(m => $"Media_{m}").Where(k => !keys.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Media labels absent from strings.json: " + string.Join(", ", missing));
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
