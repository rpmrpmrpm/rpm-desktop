namespace RPM.Desktop.Services.Updates;

/// <summary>
/// Whether the question may be put to the reader for the version that is ready.
/// </summary>
/// <remarks>
/// A reader who chooses Later has chosen it for one version. That update installs when they quit, and the
/// next release asks again. So what is remembered is the VERSION they were offered, not the fact that they
/// said no. A flag would silence every release after the first, and the app would keep downloading updates
/// that nobody was ever told about.
/// </remarks>
public static class UpdateAsk
{
    /// <summary>Whether <paramref name="ready"/> may be offered, given the version the reader last put off.
    /// Anything at or below the deferred version is not offered again. That includes a version older than
    /// it. This is what a settings file looks like when it was written by a machine that was further
    /// ahead.</summary>
    public static bool Due(Version ready, Version? deferred) => deferred is null || ready > deferred;

    /// <summary>The deferred version as it was saved, or null when nothing was saved. Null also covers text
    /// that cannot be read as a version. Such text counts as nothing, not as a version that happens to be
    /// zero.
    ///
    /// <para>Velopack's versions follow SemVer and can carry a suffix (<c>0.2.0-beta.1</c>), which
    /// <see cref="Version"/> cannot parse. The three numbers before the suffix are what the app calls
    /// itself. So those are the numbers that are read and compared.</para></summary>
    public static Version? Deferred(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        var numbers = stored.Split('-', '+')[0];
        return Version.TryParse(numbers, out var version) ? version : null;
    }
}
