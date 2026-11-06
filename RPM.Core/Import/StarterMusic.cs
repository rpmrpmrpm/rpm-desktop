using RPM.Core.Diagnostics;

namespace RPM.Core.Import;

/// <summary>
/// The tracks that ship with the app, for someone who wants to try it before pointing it at their own
/// music. The setup wizard offers them beside its folder tile.
///
/// <para><b>They are never imported from where they ship.</b> An import can move its source, and it
/// writes tags, and neither may happen to the app's own files. So they are copied to a folder of their
/// own first, and that folder goes through the ordinary import like any other a listener picks.</para>
///
/// <para>They sit directly in Assets/Sounds, not in a folder of their own, because that folder ships
/// already. A delta update cannot write a file into a folder the previous package does not have.</para>
/// </summary>
public static class StarterMusic
{
    /// <summary>The file names, each track with its tags and cover inside it. Their names are not what
    /// anybody sees, since the import files them by those tags. A disc is a picture of the record that
    /// lands beside the track as disc.png, which the import carries into the album folder.</summary>
    private static readonly (string Track, string? Disc)[] Records =
    [
        ("starter-connect.mp3", null),
        ("starter-gymnopedie.ogg", null),
        ("starter-moonlight.ogg", "starter-moonlight-disc.png"),
    ];

    private static string Shipped(string appFolder, string file) =>
        Path.Combine(appFolder, "Assets", "Sounds", file);

    /// <summary>Whether this build has any of them. One built without them offers nothing.</summary>
    public static bool IsShipped(string appFolder) =>
        Records.Any(r => File.Exists(Shipped(appFolder, r.Track)));

    /// <summary>Copies them into <paramref name="folder"/>, ready to import, each in a folder of its
    /// own. They are three records, and a record is a folder: the wizard's preview finds a sleeve per
    /// source folder, so three in one folder would all show the first one's cover.</summary>
    /// <returns>How many landed. A failure is noted and leaves fewer. The wizard's scan then says it
    /// found no music, as it does for any folder.</returns>
    public static int Unpack(string appFolder, string folder)
    {
        int copied = 0;
        try
        {
            foreach (var (track, disc) in Records)
            {
                var from = Shipped(appFolder, track);
                if (!File.Exists(from))
                    continue;

                var record = Directory.CreateDirectory(
                    Path.Combine(folder, Path.GetFileNameWithoutExtension(track)));
                File.Copy(from, Path.Combine(record.FullName, track), overwrite: true);
                copied++;

                if (disc is not null && File.Exists(Shipped(appFolder, disc)))
                    File.Copy(Shipped(appFolder, disc), Path.Combine(record.FullName, "disc.png"), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Faults.Note("starter", $"unpacking into '{folder}'", ex);
        }

        return copied;
    }

    /// <summary>Takes the unpacked copies away once the import is done. An import that copies leaves
    /// them behind. A failure costs a few megabytes in a temporary folder, so it is only noted.</summary>
    public static void Clear(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Faults.Note("starter", $"clearing '{folder}'", ex);
        }
    }
}
