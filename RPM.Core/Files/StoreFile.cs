namespace RPM.Core.Files;

/// <summary>What reading a store's file came to: the value, and whether the store may be written.</summary>
/// <param name="Value">What was read, or default where there was nothing to read.</param>
/// <param name="MayWrite">False when the file is there and could not be read this time. The store
/// then starts empty for the session and must not be written, or it replaces the real file with the
/// empty one.</param>
public readonly record struct StoreRead<T>(T? Value, bool MayWrite);

/// <summary>
/// How the app reads the small files it keeps its own state in: settings, the session, radio presets
/// and listens, the computers it shares with, and the home banner's photo orders.
///
/// <para><b>Three cases, kept apart.</b> No file: nothing to read, and the store is written as
/// usual. A file that could not be read this time (locked, a disk error): nothing to read, and the
/// store must not be written this session, so the file is there to read on the next launch. A file
/// that was read and does not parse: it is moved aside to <c>&lt;name&gt;.unreadable</c>, so the
/// only copy is kept, and the store is written as usual.</para>
///
/// <para>A store must never write over a file it could not read. If it did, one failed read would
/// replace the user's settings, presets or paired computers with an empty store.</para>
/// </summary>
public static class StoreFile
{
    public static StoreRead<T> Load<T>(string path, Func<string, T?> parse)
    {
        ArgumentNullException.ThrowIfNull(parse);

        if (!File.Exists(path))
            return new StoreRead<T>(default, MayWrite: true);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return new StoreRead<T>(default, MayWrite: false);
        }

        try
        {
            return new StoreRead<T>(parse(text), MayWrite: true);
        }
        catch (Exception)
        {
            try
            {
                File.Move(path, path + ".unreadable", overwrite: true);
                return new StoreRead<T>(default, MayWrite: true);
            }
            catch (Exception)
            {
                // Could not be moved aside either: kept in place, and not written over.
                return new StoreRead<T>(default, MayWrite: false);
            }
        }
    }
}
