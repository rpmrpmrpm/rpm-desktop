using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class StationIconsTests
{
    private static string[] Urls(IReadOnlyList<StationIcon> icons) => icons.Select(i => i.Url).ToArray();

    /// <summary>NPO 3FM's front page, near enough as it is served. Nothing in the directory mentions
    /// this picture; the station's own page declares it.</summary>
    private const string ThreeFm = """
        <html><head>
        <meta content="https://www.npo3fm.nl/images/og_image.jpg" property="og:image" data-next-head=""/>
        <link href="/apple-touch-icon.png" rel="apple-touch-icon" sizes="180x180" data-next-head=""/>
        <link rel="icon" href="/favicon-32x32.png" sizes="32x32"/>
        </head><body></body></html>
        """;

    [Fact]
    public void The_page_gives_up_the_icons_it_declares()
    {
        var icons = StationIcons.From(ThreeFm, "https://www.npo3fm.nl/");

        Assert.Equal(
            ["https://www.npo3fm.nl/apple-touch-icon.png",
             "https://www.npo3fm.nl/favicon-32x32.png",
             "https://www.npo3fm.nl/images/og_image.jpg"],
            Urls(icons));
    }

    /// <summary>An apple-touch icon is declared for a phone's home screen, so it is square, it is the
    /// mark and not a photograph, and it is big enough to be worth having.</summary>
    [Fact]
    public void The_home_screen_icon_is_tried_before_the_others()
    {
        var icons = StationIcons.From(ThreeFm, "https://www.npo3fm.nl/");

        Assert.Equal(StationIconKind.AppleTouch, icons[0].Kind);
        Assert.Equal(StationIconKind.Icon, icons[1].Kind);
        Assert.Equal(StationIconKind.Social, icons[2].Kind);
    }

    [Fact]
    public void The_bigger_of_two_declared_sizes_goes_first()
    {
        var icons = StationIcons.From(
            """
            <link rel="icon" href="/small.png" sizes="16x16">
            <link rel="icon" href="/big.png" sizes="192x192">
            <link rel="icon" href="/mid.png" sizes="32x32">
            """,
            "https://a.test/");

        Assert.Equal(["https://a.test/big.png", "https://a.test/mid.png", "https://a.test/small.png"],
                     Urls(icons));
    }

    /// <summary>"any" is what a scalable icon declares, and it is bigger than any number.</summary>
    [Fact]
    public void A_scalable_icon_outsizes_a_measured_one()
    {
        var icons = StationIcons.From(
            """
            <link rel="icon" href="/big.png" sizes="192x192">
            <link rel="icon" href="/mark.svg" sizes="any">
            """,
            "https://a.test/");

        Assert.Equal("https://a.test/mark.svg", icons[0].Url);
    }

    [Theory]
    [InlineData("/logo.png", "https://a.test/logo.png")]
    [InlineData("logo.png", "https://a.test/deep/logo.png")]
    [InlineData("../logo.png", "https://a.test/logo.png")]
    [InlineData("//cdn.test/logo.png", "https://cdn.test/logo.png")]
    [InlineData("https://cdn.test/logo.png", "https://cdn.test/logo.png")]
    public void An_href_is_resolved_against_the_page_it_was_found_on(string href, string absolute)
    {
        var icons = StationIcons.From($"<link rel=\"icon\" href=\"{href}\">", "https://a.test/deep/page");

        Assert.Equal(absolute, Assert.Single(icons).Url);
    }

    /// <summary>A picture written into the page needs no fetching, and nothing here is set up to
    /// supply one.</summary>
    [Fact]
    public void An_inline_icon_is_not_an_address()
    {
        Assert.Empty(StationIcons.From(
            """<link rel="icon" href="data:image/png;base64,iVBORw0KGgo=">""", "https://a.test/"));
    }

    [Fact]
    public void Attributes_are_read_however_they_are_written()
    {
        var icons = StationIcons.From(
            "<LINK HREF='/one.png' REL='apple-touch-icon'><link href=/two.png rel=icon>",
            "https://a.test/");

        Assert.Equal(["https://a.test/one.png", "https://a.test/two.png"], Urls(icons));
    }

    [Fact]
    public void The_same_icon_declared_twice_is_listed_once()
    {
        var icons = StationIcons.From(
            """
            <link rel="apple-touch-icon" href="/logo.png">
            <link rel="apple-touch-icon-precomposed" href="/logo.png">
            """,
            "https://a.test/");

        Assert.Single(icons);
    }

    [Fact]
    public void A_page_that_declares_nothing_gives_nothing()
    {
        Assert.Empty(StationIcons.From("<html><head><title>Radio</title></head></html>", "https://a.test/"));
        Assert.Empty(StationIcons.From("", "https://a.test/"));
        Assert.Empty(StationIcons.From(null, "https://a.test/"));
    }

    /// <summary>A relative href with no page to resolve it against is not an address.</summary>
    [Fact]
    public void Without_the_page_there_is_nothing_to_resolve_against()
    {
        Assert.Empty(StationIcons.From("""<link rel="icon" href="/logo.png">""", null));
        Assert.Empty(StationIcons.From("""<link rel="icon" href="/logo.png">""", "not a url"));
    }

    // ── the guess, for a page that declares nothing ──

    [Fact]
    public void Every_site_is_asked_for_the_usual_address()
    {
        Assert.Equal("https://www.538.nl/apple-touch-icon.png", StationIcons.WellKnown("https://www.538.nl/"));
        Assert.Equal("https://a.test/apple-touch-icon.png", StationIcons.WellKnown("https://a.test/deep/page"));
        Assert.Null(StationIcons.WellKnown(""));
        Assert.Null(StationIcons.WellKnown("ftp://a.test/"));
    }

    // ── and the check that keeps a photograph out of a logo well ──

    /// <summary>Radio 538's social image is a 1200×630 photograph of its presenters. It has the shape
    /// of a share card, and the shape is the only thing that says so before it is on screen.</summary>
    [Fact]
    public void A_share_card_is_not_a_mark()
    {
        Assert.False(StationIcons.LooksLikeAMark(1200, 630));
        Assert.False(StationIcons.LooksLikeAMark(1920, 1080));
    }

    // ── the web app manifest, which is where the big transparent copy is ──

    /// <summary>NPO Radio 1's manifest, as it is served. Its apple-touch icon is 180 pixels with no
    /// alpha channel; these two are the same mark with the alpha intact, and the larger is half as
    /// big again as anything the page itself declares.</summary>
    private const string RadioOneManifest = """
        {"name":"NPO Radio 1","icons":[
          {"src":"/android-chrome-192x192.png","sizes":"192x192","type":"image/png"},
          {"src":"/android-chrome-256x256.png","sizes":"256x256","type":"image/png"}],
         "theme_color":"#ffffff"}
        """;

    [Fact]
    public void The_manifest_is_found_where_the_page_declares_one()
    {
        const string page = """
            <html><head>
            <link rel="manifest" href="/site.webmanifest"/>
            </head></html>
            """;

        Assert.Equal(
            "https://www.nporadio1.nl/site.webmanifest",
            StationIcons.ManifestAt(page, "https://www.nporadio1.nl/"));
    }

    [Fact]
    public void A_page_that_declares_no_manifest_has_none()
    {
        Assert.Null(StationIcons.ManifestAt(ThreeFm, "https://www.npo3fm.nl/"));
    }

    [Fact]
    public void The_manifest_gives_up_its_icons_biggest_first()
    {
        var icons = StationIcons.FromManifest(
            RadioOneManifest, "https://www.nporadio1.nl/site.webmanifest");

        Assert.Equal(
            ["https://www.nporadio1.nl/android-chrome-256x256.png",
             "https://www.nporadio1.nl/android-chrome-192x192.png"],
            Urls(icons));
        Assert.All(icons, i => Assert.Equal(StationIconKind.Manifest, i.Kind));
    }

    /// <summary>The spec resolves an icon's src against the MANIFEST, not against the page. A site
    /// that keeps its manifest in a subdirectory writes paths that mean nothing from the page's
    /// address.</summary>
    [Fact]
    public void An_icons_address_is_resolved_against_the_manifest()
    {
        var icons = StationIcons.FromManifest(
            """{"icons":[{"src":"icons/logo-512.png","sizes":"512x512"}]}""",
            "https://station.test/pwa/manifest.json");

        Assert.Equal("https://station.test/pwa/icons/logo-512.png", Assert.Single(icons).Url);
    }

    /// <summary>What the manifest is for: a station's page tops out at the apple-touch icon, and the
    /// manifest's copy of the same mark is bigger and is the one that kept its alpha. Folded together,
    /// the manifest's goes first.</summary>
    [Fact]
    public void The_manifests_mark_is_tried_before_the_pages_own_icons()
    {
        var page = StationIcons.From(ThreeFm, "https://www.npo3fm.nl/");
        var manifest = StationIcons.FromManifest(
            RadioOneManifest, "https://www.npo3fm.nl/site.webmanifest");

        var icons = StationIcons.Best([.. page, .. manifest]);

        Assert.Equal(StationIconKind.Manifest, icons[0].Kind);
        Assert.Equal("https://www.npo3fm.nl/android-chrome-256x256.png", icons[0].Url);
        Assert.Equal(StationIconKind.AppleTouch, icons[2].Kind);
    }

    /// <summary>A monochrome icon is a silhouette for the platform to tint: the shape of the mark and
    /// none of its colour. Used as a logo it is a flat blob, which looks like a picture and is worse
    /// than the antenna.</summary>
    [Fact]
    public void A_silhouette_is_not_a_logo()
    {
        var icons = StationIcons.FromManifest("""
            {"icons":[
              {"src":"/mono.png","sizes":"512x512","purpose":"monochrome"},
              {"src":"/mark.png","sizes":"192x192"}]}
            """, "https://station.test/manifest.json");

        Assert.Equal("https://station.test/mark.png", Assert.Single(icons).Url);
    }

    /// <summary>A maskable icon is the mark inside a safe zone, so about a fifth of each edge is
    /// deliberate empty space. It is still worth having, and it is worth its declared size minus that
    /// padding: a maskable 512 beats a plain 192 and loses to a plain 480.</summary>
    [Fact]
    public void A_maskable_icon_is_ranked_by_the_mark_rather_than_the_square()
    {
        var icons = StationIcons.FromManifest("""
            {"icons":[
              {"src":"/maskable.png","sizes":"512x512","purpose":"maskable"},
              {"src":"/plain.png","sizes":"480x480"},
              {"src":"/small.png","sizes":"192x192"}]}
            """, "https://station.test/manifest.json");

        Assert.Equal(
            ["https://station.test/plain.png",      // 480
             "https://station.test/maskable.png",   // 512 declared, 409 of it mark
             "https://station.test/small.png"],     // 192
            Urls(icons));
    }

    /// <summary>A drawing declares "any" rather than a number, and it is worth more than any number:
    /// it is the mark at whatever size is asked for.</summary>
    [Fact]
    public void A_drawing_in_the_manifest_outranks_every_size()
    {
        var icons = StationIcons.FromManifest("""
            {"icons":[
              {"src":"/logo-1024.png","sizes":"1024x1024","type":"image/png"},
              {"src":"/logo.svg","sizes":"any","type":"image/svg+xml"}]}
            """, "https://station.test/manifest.json");

        Assert.Equal("https://station.test/logo.svg", icons[0].Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"name":"no icons here"}""")]
    [InlineData("""{"icons":"not an array"}""")]
    [InlineData("[]")]
    public void A_manifest_with_nothing_in_it_yields_nothing(string json)
    {
        Assert.Empty(StationIcons.FromManifest(json, "https://station.test/manifest.json"));
    }

    // ── the logo the page names as such ──

    /// <summary>France Inter's shape: an @graph with the organization several levels in. A regex for
    /// "logo" cannot tell that nesting from the escaping, which is why this one is parsed.</summary>
    [Fact]
    public void A_logo_named_in_linked_data_is_found()
    {
        const string page = """
            <html><head>
            <script type="application/ld+json">
            {"@context":"https://schema.org","@graph":[
              {"@type":"WebSite","url":"https://station.test/"},
              {"@type":"RadioStation","name":"Station",
               "logo":{"@type":"ImageObject","url":"https://station.test/logo.png"}}]}
            </script>
            </head></html>
            """;

        var icons = StationIcons.From(page, "https://station.test/");

        Assert.Equal("https://station.test/logo.png", Assert.Single(icons).Url);
        Assert.Equal(StationIconKind.Logo, icons[0].Kind);
    }

    [Fact]
    public void A_logo_written_as_a_plain_address_is_found_too()
    {
        const string page = """
            <html><head>
            <script type="application/ld+json">
            {"@type":"Organization","logo":"/img/mark.svg"}
            </script>
            </head></html>
            """;

        Assert.Equal(
            "https://station.test/img/mark.svg",
            Assert.Single(StationIcons.From(page, "https://station.test/")).Url);
    }

    /// <summary>One block of several that does not parse is skipped, and everything else on the page
    /// is still read. Broken JSON-LD is common and it is not the page's only source.</summary>
    [Fact]
    public void A_broken_block_does_not_lose_the_rest_of_the_page()
    {
        const string page = """
            <html><head>
            <script type="application/ld+json">{ this is not json</script>
            <link rel="apple-touch-icon" href="/apple-touch-icon.png" sizes="180x180"/>
            <script type="application/ld+json">{"@type":"Organization","logo":"/mark.png"}</script>
            </head></html>
            """;

        Assert.Equal(
            ["https://station.test/apple-touch-icon.png", "https://station.test/mark.png"],
            Urls(StationIcons.From(page, "https://station.test/")));
    }

    /// <summary>An organization's "image" is a photograph of a building or a studio as often as it is
    /// a mark, so only "logo" is taken. The share card is already handled, and shape-checked.</summary>
    [Fact]
    public void A_photograph_named_image_is_not_taken_for_a_logo()
    {
        const string page = """
            <html><head>
            <script type="application/ld+json">
            {"@type":"Organization","image":"https://station.test/studio.jpg"}
            </script>
            </head></html>
            """;

        Assert.Empty(StationIcons.From(page, "https://station.test/"));
    }

    // ── the tile picture ──

    [Fact]
    public void The_tile_picture_is_read_and_ranked_under_the_icons()
    {
        const string page = """
            <html><head>
            <meta name="msapplication-TileImage" content="/tile-144.png"/>
            <link rel="icon" href="/favicon-32x32.png" sizes="32x32"/>
            <link rel="apple-touch-icon" href="/apple-touch-icon.png" sizes="180x180"/>
            </head></html>
            """;

        var icons = StationIcons.From(page, "https://station.test/");

        Assert.Equal(
            ["https://station.test/apple-touch-icon.png",
             "https://station.test/tile-144.png",
             "https://station.test/favicon-32x32.png"],
            Urls(icons));
        Assert.Equal(StationIconKind.Tile, icons[1].Kind);
    }

    /// <summary>Kind before size, and this is why. A 1200x630 share card is a photograph of the
    /// breakfast presenters; a 180-pixel apple-touch icon is the station's mark. Sorting on the number
    /// alone puts the photograph first.</summary>
    [Fact]
    public void A_bigger_share_card_still_loses_to_a_smaller_mark()
    {
        var icons = StationIcons.Best([
            new StationIcon("https://station.test/og.jpg", StationIconKind.Social, 1200),
            new StationIcon("https://station.test/apple-touch-icon.png", StationIconKind.AppleTouch, 180),
        ]);

        Assert.Equal("https://station.test/apple-touch-icon.png", icons[0].Url);
    }

    [Theory]
    [InlineData(512, 512)]
    [InlineData(180, 180)]
    [InlineData(400, 300)]
    [InlineData(300, 400)]
    public void A_mark_is_roughly_square(int width, int height)
    {
        Assert.True(StationIcons.LooksLikeAMark(width, height));
    }

    [Fact]
    public void A_picture_of_no_size_is_no_mark()
    {
        Assert.False(StationIcons.LooksLikeAMark(0, 0));
        Assert.False(StationIcons.LooksLikeAMark(512, 0));
    }
}
