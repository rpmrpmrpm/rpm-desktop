using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The beat model, run in the cloud, for a computer that cannot run it locally.
///
/// <para>The split is made at the one place where it is cheap. The spectrogram is built on this machine,
/// because that is an FFT and every machine can do it. Only the thirty-second window it produces crosses
/// the network (see <see cref="GridWire"/>). What comes back is the same two curves the local runtime
/// returns, so everything after this point stays the same. <see cref="BeatModelDecoder"/> stitches the
/// windows together and finds the beats, as it does for weights opened from disk.</para>
///
/// <para>That sameness is the point. The graph on the other end is the one that ships with the app. So a
/// slow machine and a fast one get the same grid for the same record. That is what makes it safe to publish
/// the result to the shared pool, where each record is worked out once, and nobody on any machine pays for
/// it again.</para>
///
/// <para>This is best-effort, like every other model in the app. When the user is signed out, offline, or
/// the store does not answer, <see cref="Read"/> returns null, and the analyser keeps its DSP grid.</para>
/// </summary>
public sealed class CloudBeatModel(RpmCloudClient cloud, AuthService auth) : IBeatModel
{
    /// <summary>The shortest record worth sending. Below this there is not enough of it for a window. It is
    /// the same floor the local runtime keeps, so the two agree about which records to decline.</summary>
    private const int MinFrames = BeatModelDecoder.ChunkFrames / 4;

    /// <summary>How many windows are in flight at once. The network is the cost here, not the machine. Two
    /// windows keep a home connection busy, without a set's look-ahead making the listener's connection
    /// slow.</summary>
    private const int AtOnce = 2;

    /// <summary>Set once a window has come back wrong or not at all. The track being read falls back to the
    /// DSP grid, and so does the next track. A store that will not answer one window will not answer the
    /// next eight either. Asking anyway would cost the set's look-ahead time.</summary>
    private bool _failed;

    /// <summary>True when there is any point in asking. Somebody is signed in, and nothing has already
    /// failed in this session.</summary>
    public bool IsAvailable => !_failed && auth.IsLoggedIn;

    /// <inheritdoc />
    public BeatReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
    {
        if (!IsAvailable || sampleRate != MelSpectrogram.SampleRate)
            return null;

        var mel = MelSpectrogram.Compute(mono);
        if (mel.Length < MinFrames)
            return null;

        // All the windows this record needs are asked for together. They are answered in a couple of round
        // trips, not one after another. The decoder needs them ordered by start frame, so they are
        // collected into a map first and read out of it. The decoder never waits for a window that has
        // already arrived.
        var starts = BeatModelDecoder.WindowStarts(mel.Length);
        var answers = new Dictionary<int, (float[] Beat, float[] Downbeat)>(starts.Count);

        try
        {
            Parallel.ForEach(
                starts,
                new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = ct },
                start =>
                {
                    var answer = Ask(mel, start, ct);
                    if (answer is null)
                        throw new WindowRefused();

                    lock (answers)
                        answers[start] = answer.Value;
                });
        }
        // A set that was stopped wants no reading at all. Its cancellation passes through both filters, and
        // is not caught and thrown again.
        catch (Exception ex) when (ex is not OperationCanceledException
                                   && (ex is WindowRefused || ex.InnerException is WindowRefused))
        {
            // A window that came back empty because the set was stopped does not mean the store refuses to
            // answer. Reading it as a refusal is costly, and the cost is hidden. _failed stays set for the
            // whole session, so one stop would send every record read afterwards to the DSP grid. So the
            // cancellation token is checked, not the exception type. What comes out of a Parallel loop is
            // the exception it was wrapped in on the way out.
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            _failed = true;
            Said.Line("analysis", "the store would not read a window; the DSP grid is used from here on");
            return null;
        }

        // The result is read the same way a machine that ran the weights itself would read it. The store
        // returns the model's own numbers and nothing else, so the conversion is done here. It must be the
        // same conversion on every machine, because both readings go into one shared pool.
        return BeatModelDecoder.Decode(
            mel.Length,
            start => answers.TryGetValue(start, out var answer)
                ? answer
                : (new float[BeatModelDecoder.ChunkFrames], new float[BeatModelDecoder.ChunkFrames]),
            ct,
            BeatModelDecoder.Chosen(RPM.Core.Diagnostics.DevSwitch.Read("RPM_BEAT_DECODER")));
    }

    /// <summary>Thrown across the parallel loop when a window does not come back, so that the whole record
    /// stops. It is not stitched together with holes in it.</summary>
    private sealed class WindowRefused : Exception;

    /// <summary>
    /// One window, cut out of the spectrogram and sent.
    ///
    /// <para>Frames outside the record are sent as silence. This is what the local runtime feeds the graph,
    /// and what the model was trained to see at the edges of a track.</para>
    ///
    /// <para><b>A stop is taken between windows, never inside one.</b> The token is read here, before
    /// anything is sent. The send itself cannot be cancelled. A set stopped while a window is on the wire
    /// lets that window arrive. Cancelling the send would abort the socket under the request. The abort
    /// comes back as a <see cref="TaskCanceledException"/> wrapping a socket abort. That is noise in the
    /// debugger, and at <see cref="Read"/> it cannot be told apart from the store refusing to answer.
    /// Treating a cancelled
    /// window as a refusal would send a whole session to the DSP grid each time someone pressed stop. The
    /// cost of letting the window finish is one window already paid for: a couple of seconds, and a third
    /// of a megabyte that nobody reads.</para>
    /// </summary>
    private (float[] Beat, float[] Downbeat)? Ask(float[][] mel, int start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        int chunk = BeatModelDecoder.ChunkFrames;
        int bands = MelSpectrogram.MelBands;

        var window = new float[chunk * bands];
        for (int i = 0; i < chunk; i++)
        {
            int at = start + i;
            if (at < 0 || at >= mel.Length)
                continue;

            mel[at].AsSpan(0, bands).CopyTo(window.AsSpan(i * bands, bands));
        }

        // This is synchronous on purpose. It runs inside a decode that is already on a background thread,
        // with nothing else to do. Every caller of IBeatModel is written that way.
        //
        // It is not cancellable, for the reason above. The loop stops issuing windows as soon as the token
        // trips. This one finishes instead of being cut off the socket.
        //
        // Both directions use single precision. The function therefore reads the window exactly as this
        // machine would have fed it to the model, and the grid published to the pool matches the one a fast
        // machine produces. See GridWire.
        var said = cloud.ReadGridWindowAsync(
                            GridWire.Pack(window, GridPrecision.Single), chunk, bands, GridPrecision.Single,
                            CancellationToken.None)
                        .GetAwaiter().GetResult();

        if (said is not { } answer)
            return null;

        var beat = GridWire.Unpack(answer.Beat, chunk, GridPrecision.Single);
        var downbeat = GridWire.Unpack(answer.Downbeat, chunk, GridPrecision.Single);
        return beat is null || downbeat is null ? null : (beat, downbeat);
    }
}
