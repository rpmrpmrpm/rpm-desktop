using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// Asking a station's logo host for the size the screen actually draws.
///
/// <para>A logo in a directory entry is not the address of a picture. It is the address of a picture
/// AT A SIZE, and the size is whatever the person who filed the entry pasted. The same iHeart asset
/// can be filed under one of a station's streams as <c>contain(32,32)</c> and under another as
/// <c>contain(360,360)</c>. Whichever one the fold keeps is the one drawn, and thirty-two pixels
/// stretched into a hundred-point well is a blocky logo, although the picture itself is fine.</para>
///
/// <para>The addresses below are real ones from the directory's American answers, and every rewrite
/// they assert was checked against the host itself.</para>
/// </summary>
public class RadioLogoSizeTests
{
    /// <summary>102.7 KIIS FM, as the directory files it.</summary>
    private const string IHeart =
        "https://i.iheart.com/v3/re/assets.brands/61d4a04fc786e58c38995162"
        + "?ops=gravity(%22center%22),contain(32,32),quality(65)";

    /// <summary>A station whose mark is on Wix, cropped out of a larger upload and then fitted. The
    /// crop's own w_ and h_ are source pixels and are not a size to ask for.</summary>
    private const string Wix =
        "https://static.wixstatic.com/media/00a61a_a29da417.png"
        + "/v1/crop/x_24,y_30,w_555,h_544/fill/w_136,h_133,al_c,q_85,enc_auto/logo.png";

    private const string TuneIn = "https://cdn-profiles.tunein.com/s27943/images/logod.png";

    // ── the size in the address is raised to the size on the screen ──

    [Fact]
    public void A_logo_filed_at_a_browser_tab_size_is_asked_for_at_the_size_it_is_drawn()
        => Assert.Contains("contain(102,102)", RadioLogo.Sized(IHeart, 102));

    /// <summary>Up only. One address is fetched by a row that wants a hundred pixels and by the
    /// playing square that wants several hundred. Trimming a generous address to the row's need would
    /// give a small logo again on the larger surface.</summary>
    [Fact]
    public void An_address_already_asking_for_enough_is_left_alone()
    {
        var generous = IHeart.Replace("contain(32,32)", "contain(360,360)");

        Assert.Equal(generous, RadioLogo.Sized(generous, 102));
    }

    /// <summary>Both axes by the one factor. The box these hosts take is a crop as well as a size, so
    /// squaring a wordmark's box would cut the ends off the name.</summary>
    [Fact]
    public void A_box_that_is_not_square_keeps_its_shape()
    {
        var sized = RadioLogo.Sized(Wix, 266)!;

        Assert.Contains("fill/w_272,h_266", sized);
    }

    /// <summary>…and the crop in front of it is not a size to ask for: those are source pixels, and
    /// rewriting them moves the crop rather than resizing the picture.</summary>
    [Fact]
    public void The_crop_before_the_box_is_not_touched()
        => Assert.Contains("crop/x_24,y_30,w_555,h_544", RadioLogo.Sized(Wix, 512));

    /// <summary>TuneIn takes one of four letters and not a size. The smallest letter that covers what
    /// was asked for.</summary>
    [Fact]
    public void A_host_with_a_ladder_rather_than_a_size_is_walked_up_it()
    {
        Assert.EndsWith("/images/logog.png", RadioLogo.Sized(TuneIn, 512));
        Assert.EndsWith("/images/logod.png", RadioLogo.Sized(TuneIn, 200));
    }

    /// <summary>…and never back down it. A rung that already covers the surface is left where it
    /// is.</summary>
    [Fact]
    public void A_rung_that_already_covers_the_surface_is_not_stepped_down()
        => Assert.Equal(TuneIn, RadioLogo.Sized(TuneIn, 102));

    /// <summary>Nothing is asked for past the ceiling: the biggest surface a station's mark is drawn
    /// on is still only a badge.</summary>
    [Fact]
    public void Nothing_is_asked_for_past_the_ceiling()
        => Assert.Contains("contain(1024,1024)", RadioLogo.Sized(IHeart, 4096));

    // ── and everything else is handed back as it came ──

    [Theory]
    [InlineData("https://www.181.fm/favicon.ico")]
    [InlineData("https://npr.brightspotcdn.com/logo.png")]
    [InlineData("not an address at all")]
    [InlineData("")]
    public void An_ordinary_address_is_untouched(string url)
        => Assert.Equal(url, RadioLogo.Sized(url, 512));

    [Fact]
    public void Nothing_is_asked_of_a_host_when_no_size_is_wanted()
        => Assert.Equal(IHeart, RadioLogo.Sized(IHeart, 0));

    // ── two spellings of one picture ──

    /// <summary>The same asset filed at two sizes is ONE picture. The fold counts agreement with this,
    /// so a station whose entries name one asset three times has not disagreed three ways.</summary>
    [Fact]
    public void One_asset_filed_at_two_sizes_is_one_picture()
        => Assert.Equal(
            RadioLogo.SamePicture(IHeart),
            RadioLogo.SamePicture(IHeart.Replace("contain(32,32)", "contain(360,360)")));

    /// <summary>…and two assets on one host are two pictures, whatever size either is filed at.</summary>
    [Fact]
    public void Two_assets_on_one_host_are_two_pictures()
        => Assert.NotEqual(
            RadioLogo.SamePicture(IHeart),
            RadioLogo.SamePicture(IHeart.Replace("61d4a04fc786e58c38995162", "2ebe75353930572878")));

    // ── how much picture is really there ──

    /// <summary>
    /// The address that answers honestly. A host that cuts to order returns exactly the size asked
    /// for, whatever is behind it. So the only way to learn that the first logo of 102.7 KIIS FM is a
    /// sixteen-pixel browser-tab icon is to ask for the asset bare.
    /// </summary>
    [Fact]
    public void A_resizing_host_can_be_asked_what_it_actually_has()
        => Assert.Equal(
            "https://i.iheart.com/v3/re/assets.brands/61d4a04fc786e58c38995162",
            RadioLogo.Native(IHeart));

    /// <summary>An address that already asks for no particular size is already the answer, and a
    /// second request would only repeat the first.</summary>
    [Fact]
    public void An_address_that_is_already_bare_has_nothing_to_ask()
        => Assert.Null(RadioLogo.Native("https://i.iheart.com/v3/re/assets.brands/61d4a04f"));

    /// <summary>Only the host that invents pixels. Wix cuts from a real upload and TuneIn's letters
    /// are a real ladder, so what those two return is what they really have. Stripping their addresses
    /// would fetch the original upload, which can be a photograph.</summary>
    [Theory]
    [InlineData(Wix)]
    [InlineData(TuneIn)]
    [InlineData("https://www.181.fm/favicon.ico")]
    public void Every_other_host_is_taken_at_its_word(string url)
        => Assert.Null(RadioLogo.Native(url));

    // ── wikimedia, which renders at three widths and refuses every other ──

    /// <summary>The width in the directory is whatever a browser asked for at some earlier time, and
    /// Wikimedia answers 400 to any width outside its own ladder. Checked against three files: 120,
    /// 250 and 500 are served; 80, 160, 180, 220, 320, 400, 512, 640, 800, 1024 and 1200 are not.</summary>
    [Theory]
    [InlineData(640, 512, 500)]
    [InlineData(1200, 512, 500)]
    [InlineData(220, 512, 500)]
    [InlineData(120, 512, 500)]
    public void A_wikimedia_thumbnail_is_asked_for_at_a_width_they_render(int filed, int want, int served)
    {
        var url = $"https://upload.wikimedia.org/wikipedia/commons/thumb/d/da/"
                + $"NPO_Radio_1_logo_2014.svg/{filed}px-NPO_Radio_1_logo_2014.svg.png";

        Assert.Contains($"/{served}px-", RadioLogo.Sized(url, want));
    }

    /// <summary>Never bigger than what the surface asked for: the row wants a hundred pixels and the
    /// smallest rung is 120, so it gets 120 and not the 500 it did not ask for.</summary>
    [Fact]
    public void A_small_want_gets_the_smallest_rung_rather_than_the_biggest()
    {
        var url = "https://upload.wikimedia.org/wikipedia/commons/thumb/d/da/"
                + "NPO_Radio_1_logo_2014.svg/640px-NPO_Radio_1_logo_2014.svg.png";

        Assert.Contains("/120px-", RadioLogo.Sized(url, 100));
    }

    /// <summary>Their own API hands out a language-prefixed render. The width still moves and the
    /// prefix is left alone.</summary>
    [Fact]
    public void A_language_prefixed_render_keeps_its_prefix()
    {
        var url = "https://thumb.wikimedia.org/wikipedia/commons/thumb/a/a0/"
                + "France_Inter_logo_2021.svg/langfr-640px-France_Inter_logo_2021.svg.png";

        var asked = RadioLogo.Sized(url, 512);

        Assert.Contains("langfr-500px-", asked);
    }

    /// <summary>A direct file, not a thumbnail, has no width and is served exactly as it is. These
    /// are common: 15 of the 29 Wikimedia logos in the directory's top 1,500 stations are direct
    /// files, and they load without any change.</summary>
    [Fact]
    public void A_direct_wikimedia_file_is_left_alone()
    {
        const string url = "https://upload.wikimedia.org/wikipedia/commons/7/70/Qmusic_logo.svg";

        Assert.Equal(url, RadioLogo.Sized(url, 512));
    }

    /// <summary>Wikimedia does not refuse the app, so its logos get no ranking penalty. A station's own
    /// mark on Wikimedia is ranked as the picture it is.</summary>
    [Fact]
    public void A_wikimedia_logo_is_no_longer_ranked_below_a_dead_link()
    {
        Assert.True(
            RadioLogo.Rank("https://upload.wikimedia.org/wikipedia/commons/7/70/Qmusic_logo.svg", null)
            > RadioLogo.Rank("https://somewhere.test/favicon.ico", null));
    }

    // ── which one to reach for first ──

    /// <summary>A host that cuts to order gives the surface what it needs. One that does not gives
    /// whatever somebody uploaded. When everything else is equal, the first is the better first
    /// try.</summary>
    [Fact]
    public void A_host_that_cuts_to_order_is_tried_before_one_that_does_not()
        => Assert.True(
            RadioLogo.Rank(IHeart, homepage: "https://kiisfm.iheart.com/")
            > RadioLogo.Rank("https://cdn.example.com/kiis.png", homepage: "https://kiisfm.iheart.com/"));

    /// <summary>…but not before the broadcaster's own copy of their own mark, which is the one signal
    /// worth more than convenience.</summary>
    [Fact]
    public void The_stations_own_site_still_wins()
        => Assert.True(
            RadioLogo.Rank("https://kiisfm.iheart.com/logo.png", homepage: "https://kiisfm.iheart.com/")
            > RadioLogo.Rank(IHeart, homepage: "https://kiisfm.iheart.com/"));
}
