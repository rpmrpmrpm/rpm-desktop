using System.Text.Json;
using RPM.Fingerprint.Catalog;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// Checks that the generated reader for the rpm API's shapes reads what the API actually writes.
///
/// <para>The server is an ASP.NET Core controller API with default options, which is
/// <see cref="JsonSerializerDefaults.Web"/>: camelCase names. The contracts use PascalCase names and
/// have no JsonPropertyName attributes, so the naming policy on <see cref="CatalogWireJson"/> is the
/// only thing that joins them. A reader without that policy parses the body without error and leaves
/// every property null. A result list can then be null, and the code that enumerates it fails.</para>
///
/// <para>So the body under test is not typed out here. It is produced with the server's own options,
/// so a contract that gains a property is covered as soon as it gains it.</para>
/// </summary>
public class CatalogWireJsonTests
{
    /// <summary>What the API writes, given a value: the server's defaults, reflected.</summary>
    private static string AsServerWrites<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void A_cover_search_answer_is_read_back_whole()
    {
        var sent = new CoverSearchResponse(
            [
                new CoverCandidate("Discogs", "https://img/thumb.jpg", "https://img/full.jpg")
                {
                    Title = "Rumours",
                    Width = 1425,
                    Height = 1400,
                    PageUrl = "https://www.discogs.com/release/1",
                    Note = "1977, GB, Vinyl",
                },
            ],
            ["CoverArtArchive", "Discogs"],
            true,
            ["8a4d1e35-0000-0000-0000-000000000000"]);

        var read = JsonSerializer.Deserialize(AsServerWrites(sent), CatalogWireJson.Default.CoverSearchResponse);

        Assert.NotNull(read);
        Assert.Equal(sent.Sources, read.Sources);
        Assert.Equal(sent.ReleaseGroupIds, read.ReleaseGroupIds);
        Assert.True(read.Cached);

        var candidate = Assert.Single(read.Results);
        Assert.Equal("Discogs", candidate.Source);
        Assert.Equal("https://img/thumb.jpg", candidate.ThumbUrl);
        Assert.Equal("https://img/full.jpg", candidate.FullUrl);
        Assert.Equal("Rumours", candidate.Title);
        Assert.Equal(1425, candidate.Width);
        Assert.Equal(1400, candidate.Height);
        Assert.Equal("https://www.discogs.com/release/1", candidate.PageUrl);
        Assert.Equal("1977, GB, Vinyl", candidate.Note);
    }

    [Fact]
    public void A_release_search_answer_is_read_back_whole()
    {
        var sent = new List<CatalogRelease>
        {
            new("f1", "Rumours", "Fleetwood Mac", 1977, "1977-02-04", "GB", "Vinyl",
                MediumPosition: 1, TrackCount: 11, Label: "Warner Bros.", CatalogNumber: "K 56344",
                Note: null, Score: 98),
        };

        var read = JsonSerializer.Deserialize(AsServerWrites(sent), CatalogWireJson.Default.ListCatalogRelease);

        Assert.NotNull(read);
        var release = Assert.Single(read);
        Assert.Equal("f1", release.ReleaseId);
        Assert.Equal("Rumours", release.Title);
        Assert.Equal("Fleetwood Mac", release.Artist);
        Assert.Equal(1977u, release.Year);
    }

    /// <summary>
    /// A field the server did not send arrives as null however non-nullable the contract declares it,
    /// which is why the client fills these lists in at the seam rather than handing the answer
    /// straight to a window that enumerates them.
    /// </summary>
    [Fact]
    public void A_missing_list_is_null_rather_than_empty()
    {
        var read = JsonSerializer.Deserialize("{}", CatalogWireJson.Default.CoverSearchResponse);

        Assert.NotNull(read);
        Assert.Null(read.Results);
        Assert.Null(read.Sources);
    }
}
