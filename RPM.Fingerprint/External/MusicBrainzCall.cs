namespace RPM.Fingerprint.External;

/// <summary>One request to MusicBrainz, as it is reported to the log.</summary>
/// <param name="Path">The path asked for, without the query.</param>
/// <param name="Status">The HTTP status, or 0 when no answer arrived (a timeout or a failed connection).</param>
/// <param name="Waited">How long the request waited for its turn.</param>
/// <param name="Took">How long MusicBrainz took to answer once it was sent.</param>
public readonly record struct MusicBrainzCall(string Path, int Status, TimeSpan Waited, TimeSpan Took);

/// <summary>
/// MusicBrainz could not be asked: a status other than 200 or 404, a timeout, or a failed connection.
///
/// <para>Never "nothing found". A 404 is the only answer that means the service has no such thing, and
/// it comes back as null rather than as this.</para>
/// </summary>
public sealed class MusicBrainzUnreachableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
