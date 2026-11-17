using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Tests;

/// <summary>
/// The header's photograph of the artist, switched off in Settings > Display.
///
/// <para>Off means the app does not go looking. That is the half worth testing: a header that fetched,
/// ranked and downloaded an artist's photographs and then did not draw them would look the same and cost
/// the same. So the test checks that no picture is asked for, and that the record's own cover and disc are
/// not changed by the switch.</para>
///
/// <para>The switch is checked on every show, not given once as a value. This lets the user change it
/// during a track. For that reason it is passed as a delegate. A test can then say "off" without a settings
/// store.</para>
/// </summary>
public class HeaderArtistPhotoTests
{
    [Fact]
    public async Task No_photograph_is_asked_for_when_the_header_wears_none()
    {
        var (stage, file) = await RunOneTrackAsync(headerArtistPhoto: false);
        try
        {
            Assert.DoesNotContain("LoadBackdropAsync", stage.Calls);
            Assert.Contains("ClearBackdrop", stage.Calls);

            // The sleeve and the disc are not what this switch is about, and they still go up in order.
            Assert.Contains("ShowCover", stage.Calls);
            Assert.True(stage.Calls.IndexOf("ShowCover") < stage.Calls.IndexOf("ShowDisc"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ── a lookup or a download that failed ──

    /// <summary>The previous artist's photograph must not stay up while another artist plays.</summary>
    [Fact]
    public void A_photograph_of_another_artist_does_not_stay_after_a_failed_lookup()
    {
        Assert.False(ArtworkManager.PhotoBelongsTo("Eros Ramazzotti", "The Commodores"));
        Assert.False(ArtworkManager.PhotoBelongsTo(null, "The Commodores"));
    }

    [Fact]
    public void A_photograph_of_the_same_artist_stays_after_a_failed_lookup()
    {
        Assert.True(ArtworkManager.PhotoBelongsTo("The Commodores", "The Commodores"));
        Assert.True(ArtworkManager.PhotoBelongsTo("the commodores", "The Commodores"));
    }

    /// <summary>A credit is shown with its lead name's photograph, so that photograph may stay.</summary>
    [Fact]
    public void The_lead_names_photograph_stays_for_a_credit_that_starts_with_it()
    {
        Assert.True(ArtworkManager.PhotoBelongsTo(
            "Aretha Franklin", "Aretha Franklin, The Royal Philharmonic Orchestra"));
    }

    /// <summary>Asked again only after the service's own hold on the failure, and not forever.</summary>
    [Fact]
    public void A_failed_lookup_is_asked_again_after_the_hold_a_few_times()
    {
        for (var i = 0; i < ArtworkManager.MaxReasks; i++)
            Assert.True(ArtworkManager.ReaskAfter(i) > RPM.Desktop.Services.Art.ArtworkService.HoldBriefly);

        Assert.Null(ArtworkManager.ReaskAfter(ArtworkManager.MaxReasks));
    }

    /// <summary>One track through a manager that answers the switch the given way.</summary>
    private static async Task<(RecordingArtStage Stage, string File)> RunOneTrackAsync(bool headerArtistPhoto)
    {
        // A real file is needed. With none, the pipeline takes the cloud-sleeve branch and reaches for
        // services that no test host has.
        var file = Path.Combine(Path.GetTempPath(), $"rpm-header-photo-{Guid.NewGuid():N}.flac");
        await File.WriteAllBytesAsync(file, [0]);

        var stage = new RecordingArtStage();
        var manager = new ArtworkManager(stage, artwork: null, library: null, preselector: null,
                                         headerArtistPhoto: () => headerArtistPhoto);
        manager.SetPlaying(new TrackRef("Song", "Artist", "Album", file));

        var waited = 0;
        while (!stage.Calls.Contains("ShowDisc") && waited < 5000)
        {
            await Task.Delay(20);
            waited += 20;
        }

        return (stage, file);
    }
}
