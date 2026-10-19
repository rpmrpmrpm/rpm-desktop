using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// How macOS is asked whether there is an audio CD in the drive: by looking for the table of contents
/// cddafs writes at the root of every audio CD it mounts.
///
/// <para>It is a filesystem question, so these tests use a folder built to look like
/// /Volumes. What is tested is that the test is the <c>.TOC.plist</c> and not the volume's
/// name: Finder and Music.app rename an audio CD's volume the moment they recognise the disc, so a
/// watcher keyed on "Audio CD" would stop seeing discs the moment something else identified them.</para>
/// </summary>
public class MacAudioCdMountTests : IDisposable
{
    private readonly string _volumes = Directory.CreateTempSubdirectory("rpm-volumes").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_volumes, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Volume(string name, bool audioCd)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_volumes, name)).FullName;
        if (audioCd)
            File.WriteAllText(Path.Combine(dir, ".TOC.plist"), "<plist/>");
        return dir;
    }

    [Fact]
    public void No_volumes_at_all_is_no_disc()
        => Assert.Null(MacDiscReader.FindAudioCdMount(_volumes));

    [Fact]
    public void An_ordinary_volume_is_not_a_disc()
    {
        Volume("Backup Drive", audioCd: false);
        Assert.Null(MacDiscReader.FindAudioCdMount(_volumes));
    }

    [Fact]
    public void The_volume_carrying_a_table_of_contents_is_the_disc()
    {
        Volume("Backup Drive", audioCd: false);
        var disc = Volume("Audio CD", audioCd: true);

        Assert.Equal(disc, MacDiscReader.FindAudioCdMount(_volumes));
    }

    /// <summary>The disc is found by what is on it, so the volume being named after the album it was
    /// identified as makes no difference.</summary>
    [Fact]
    public void A_renamed_disc_is_still_found()
    {
        var disc = Volume("Kind of Blue", audioCd: true);

        Assert.Equal(disc, MacDiscReader.FindAudioCdMount(_volumes));
    }

    /// <summary>A machine with no /Volumes to read answers "no disc" rather than throwing: this runs
    /// on a timer for the life of the app, where an exception every few seconds is the bug.</summary>
    [Fact]
    public void A_missing_volumes_folder_is_no_disc()
        => Assert.Null(MacDiscReader.FindAudioCdMount(Path.Combine(_volumes, "gone")));
}
