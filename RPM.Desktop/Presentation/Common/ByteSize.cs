namespace RPM.Desktop.Presentation.Common;

/// <summary>Formats byte counts the way people would say them.</summary>
public static class ByteSize
{
    /// <summary>"12.4 GB", not "13,314,398,208 bytes". Bytes and kilobytes have no decimal places,
    /// because nobody needs a tenth of a kilobyte. Larger units have one decimal place, because the
    /// difference between 12 GB and 12.9 GB is most of a record collection.</summary>
    public static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit switch
        {
            0 or 1 => $"{value:0} {units[unit]}",
            _ => $"{value:0.#} {units[unit]}",
        };
    }
}
