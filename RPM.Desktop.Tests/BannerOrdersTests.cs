using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// The banner's own order of an artist's photographs, saved on disk so the ranking is done once and
/// not on every launch.
/// </summary>
public class BannerOrdersTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-banner-orders-" + Guid.NewGuid().ToString("N"));

    public BannerOrdersTests() => BannerOrders.Dir = _dir;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be deleted does not fail the test.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void An_artist_nobody_has_featured_is_not_kept()
        => Assert.Null(BannerOrders.For("Nobody At All"));

    [Fact]
    public void An_order_survives_being_written_and_read()
    {
        BannerOrders.Keep("Enya", ["one", "two", "three"], ranked: true);

        var kept = BannerOrders.For("Enya");

        Assert.NotNull(kept);
        Assert.Equal(["one", "two", "three"], kept!.Urls);
        Assert.True(kept.Ranked);
        Assert.Null(kept.Chosen);
    }

    /// <summary>Case and surrounding spaces do not make two acts, the same as everywhere else.</summary>
    [Fact]
    public void An_artist_is_matched_however_their_name_is_written()
    {
        BannerOrders.Keep("Sister Hazel", ["one"], ranked: false);

        Assert.NotNull(BannerOrders.For("  sister hazel "));
    }

    /// <summary>The photograph that actually downloaded is a fact about this machine, not a judgement
    /// about shape, so it is kept beside the order instead of replacing it.</summary>
    [Fact]
    public void The_picture_that_arrived_is_kept_beside_the_order()
    {
        BannerOrders.Keep("Enya", ["one", "two"], ranked: true);
        BannerOrders.Chose("Enya", "two");

        var kept = BannerOrders.For("Enya");

        Assert.Equal("two", kept!.Chosen);
        Assert.Equal(["one", "two"], kept.Urls);
        Assert.True(kept.Ranked);
    }

    /// <summary>A new ranking replaces the order and keeps the downloaded photograph.</summary>
    [Fact]
    public void A_new_order_does_not_forget_what_arrived()
    {
        BannerOrders.Keep("Enya", ["one", "two"], ranked: false);
        BannerOrders.Chose("Enya", "two");
        BannerOrders.Keep("Enya", ["three", "two"], ranked: true);

        var kept = BannerOrders.For("Enya");

        Assert.Equal(["three", "two"], kept!.Urls);
        Assert.Equal("two", kept.Chosen);
        Assert.True(kept.Ranked);
    }

    /// <summary>Whether the ranker actually ran is saved too: otherwise an artist whose photographs
    /// cannot be measured from this machine would be ranked again every time the screen opens.</summary>
    [Fact]
    public void An_order_that_could_not_be_ranked_says_so()
    {
        BannerOrders.Keep("Various", ["one", "two"], ranked: false);

        Assert.False(BannerOrders.For("Various")!.Ranked);
    }

    [Fact]
    public void Two_artists_do_not_share_an_order()
    {
        BannerOrders.Keep("Enya", ["one"], ranked: true);
        BannerOrders.Keep("Sister Hazel", ["two"], ranked: true);

        Assert.Equal(["one"], BannerOrders.For("Enya")!.Urls);
        Assert.Equal(["two"], BannerOrders.For("Sister Hazel")!.Urls);
    }

    [Fact]
    public void An_empty_order_is_not_written_down()
    {
        BannerOrders.Keep("Enya", [], ranked: true);

        Assert.Null(BannerOrders.For("Enya"));
    }
}
