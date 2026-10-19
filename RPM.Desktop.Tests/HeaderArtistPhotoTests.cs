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
