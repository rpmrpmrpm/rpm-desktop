namespace RPM.Fingerprint;

/// <summary>
/// ffmpeg exited with an error.
///
/// <para>The message is a short summary of ffmpeg's output (<see cref="FfmpegSaid.Short"/>). The logs
/// and the failure classification read it. <see cref="Output"/> is the full output, kept for the cloud
/// log and for a report the user chooses to send, because the line that explains the failure is
/// sometimes one that the summary left out.</para>
///
/// <para><see cref="Exception.ToString"/> is not overridden, so a logged exception prints only the
/// short message, not the large output that <see cref="FfmpegSaid"/> keeps out of the console.</para>
/// </summary>
public sealed class FfmpegFailedException(string message, string? output) : InvalidOperationException(message)
{
    /// <summary>Everything ffmpeg wrote to stderr, untrimmed. Empty when it wrote nothing.</summary>
    public string Output { get; } = output ?? "";
}
