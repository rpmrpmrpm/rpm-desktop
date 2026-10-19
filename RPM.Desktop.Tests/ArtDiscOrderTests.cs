using RPM.Core.Art;
using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where the album's photographed disc is read, and where it is handed over.
///
/// <para>The header changes a record the way a person does: it lifts the disc off, changes its face and
/// puts it back. The face is three things (the pressing colour, the label, and the photographed disc
/// over both), and all three must be ready by the time the disc is off the deck, because that is the
/// only moment they can change out of view. A disc read after the artist lookup would arrive after the
/// record is back down, and the change from a CD cover to a vinyl label would then be visible on
/// screen for about two seconds.</para>
///
/// <para>So the disc is read before anything is shown, and the sleeve is handed over BEFORE the disc:
/// the sleeve tells the header the record has changed, and the disc (already read) must be ready when
/// it does. Both rules are tested here because both are required and neither is visible from the code
/// that depends on them.</para>
/// </summary>
public class ArtDiscOrderTests
{
    [Fact]
    public async Task Disc_is_read_before_the_sleeve_goes_up()
    {
        var (stage, file) = await RunOneTrackAsync();
        try
        {
            Assert.Contains("LoadLocalDiscAsync", stage.Calls);
            Assert.True(stage.Calls.IndexOf("LoadLocalDiscAsync") < stage.Calls.IndexOf("ShowCover"),
                        $"disc read after the sleeve went up: {string.Join(", ", stage.Calls)}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Sleeve_is_shown_before_the_disc()
    {
        var (stage, file) = await RunOneTrackAsync();
        try
        {
            Assert.Equal(1, stage.Calls.Count(c => c == "ShowDisc"));
            Assert.True(stage.Calls.IndexOf("ShowCover") < stage.Calls.IndexOf("ShowDisc"),
                        $"disc handed over before the sleeve: {string.Join(", ", stage.Calls)}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>One track through the manager, with no artwork service. This is the shortest path that
    /// still shows a sleeve and a disc, and the one every track takes before the lookup happens.</summary>
    private static async Task<(RecordingArtStage Stage, string File)> RunOneTrackAsync()
    {
        // A real file: without one, the pipeline takes the cloud-sleeve branch and needs services that
        // no test host has.
        var file = Path.Combine(Path.GetTempPath(), $"rpm-disc-order-{Guid.NewGuid():N}.flac");
        await File.WriteAllBytesAsync(file, [0]);

        var stage = new RecordingArtStage();
        // The header shows a photograph. Every track takes this path, and this test checks its order.
        var manager = new ArtworkManager(stage, artwork: null, library: null, preselector: null,
                                         headerArtistPhoto: () => true);
        manager.SetPlaying(new TrackRef("Song", "Artist", "Album", file));

        // The pump cannot be awaited from outside, so wait for its effect, not for the task.
        var waited = 0;
        while (!stage.Calls.Contains("ShowDisc") && waited < 5000)
        {
            await Task.Delay(20);
            waited += 20;
        }

        return (stage, file);
    }
}
