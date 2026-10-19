namespace RPM.Core.Files;

/// <summary>
/// A file's length and last write time, taken at one moment.
///
/// <para>Two stamps of the same file that differ mean something wrote it in between. The cloud upload
/// takes a stamp before ffmpeg reads a track and another after. A canonical copy made from a file that
/// changed during the read is not trusted. A failure on such a file says nothing about the audio.</para>
/// </summary>
public readonly record struct FileStamp(long Length, long LastWriteUtcTicks)
{
    /// <summary>The stamp as the file stands now, or <see cref="Missing"/> when it cannot be read.</summary>
    public static FileStamp Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : Missing;
        }
        catch (IOException) { return Missing; }
        catch (UnauthorizedAccessException) { return Missing; }
    }

    /// <summary>No file, or none that could be looked at. Never equal to a stamp of a real file.</summary>
    public static readonly FileStamp Missing = new(-1, -1);
}
