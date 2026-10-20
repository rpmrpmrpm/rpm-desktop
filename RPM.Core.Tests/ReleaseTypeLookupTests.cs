using System.Text.Json;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;
using RPM.Fingerprint.External;

namespace RPM.Core.Tests;

/// <summary>
/// What kind of release a catalogue says a record is, from the MusicBrainz release group to the release type the
/// import and the album editor are filled with.
/// </summary>
public class ReleaseTypeLookupTests
{
    private static JsonElement Group(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_release_groups_primary_and_secondary_types_are_read()
    {
        var group = Group("""{"id":"g","primary-type":"Album","secondary-types":["Live","Compilation"]}""");

        Assert.Equal("Album", MusicBrainzClient.PrimaryTypeOf(group));
        Assert.Equal(["Live", "Compilation"], MusicBrainzClient.SecondaryTypesOf(group));
    }

    [Fact]
    public void A_group_that_says_nothing_gives_nothing()
    {
        var group = Group("""{"id":"g","primary-type":null}""");

        Assert.Null(MusicBrainzClient.PrimaryTypeOf(group));
        Assert.Empty(MusicBrainzClient.SecondaryTypesOf(group));
        Assert.Null(MusicBrainzClient.PrimaryTypeOf(default));
        Assert.Empty(MusicBrainzClient.SecondaryTypesOf(default));
    }

    [Theory]
    [InlineData("Album", new string[0], ReleaseType.Album)]
    [InlineData("EP", new string[0], ReleaseType.Ep)]
    [InlineData("Single", new[] { "Live" }, ReleaseType.Single)]
    [InlineData("Album", new[] { "Live" }, ReleaseType.Live)]
    [InlineData("Album", new[] { "Mixtape/Street" }, ReleaseType.Mixtape)]
    [InlineData("Album", new[] { "Soundtrack" }, ReleaseType.Soundtrack)]
    public void A_listing_fills_the_release_type_from_the_catalogues_types(
        string primary, string[] secondary, ReleaseType expected)
    {
        var listing = new CatalogReleaseDetail("r", "Record", "Act", 1999, [], [],
            PrimaryType: primary, SecondaryTypes: secondary);

        Assert.Equal(expected, RpmMetadataProvider.Detail(listing).ReleaseType);
    }

    [Fact]
    public void A_listing_without_types_leaves_the_release_type_unset()
        => Assert.Null(RpmMetadataProvider.Detail(new CatalogReleaseDetail("r", "Record", "Act", 1999, [], [])).ReleaseType);
}
