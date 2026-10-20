using System.Diagnostics;
using System.Globalization;
using RPM.Fingerprint;

namespace RPM.Core.Audio;

/// <summary>
/// One decoding ffmpeg process, read as a PCM source. It is the player's own decoder, and the source
/// that a <see cref="Dj.Deck"/> plays a record from. Besides seeking, it can stretch time
/// (<see cref="Options.TempoRatio"/>), which is how a deck is matched to the tempo of another record.
/// ffmpeg's <c>atempo</c> is a WSOLA stretch, so the track speeds up without a rise in pitch and the
/// mix stays in key.
/// </summary>
public sealed class FfmpegPcmSource : IPcmSource
{
    /// <summary>
    /// How close to 1 a tempo ratio must be for the deck to play without a stretch.
    ///
    /// <para>The value is one hundred-thousandth. A smaller pull would be inaudible as a change of
    /// pitch, but it would still move the beat grid: a ratio of 1.001 moves the grids of two records
    /// about 30 ms apart over a sixteen-bar changeover. That is twice
    /// <c>TransitionPlanner.MaxDriftSeconds</c>, so the two kicks would not line up. The tolerance is
    /// small enough that the drift it allows (about a third of a millisecond over the longest
    /// changeover) does not matter. Decks that are not stretched, which is every deck outside a
    /// changeover, use no filter.</para>
    /// </summary>
    public const double UnstretchedRatio = 1e-5;

    public sealed record Options
    {
        /// <summary>A file path or a URL (ffmpeg's <c>-i</c> accepts both). When <see cref="Input"/> is
        /// set, this is used only to name the source in logs.</summary>
        public required string Source { get; init; }

        /// <summary>
        /// Audio bytes to decode, used instead of letting ffmpeg open <see cref="Source"/> itself.
        /// This class takes ownership: it copies from the stream on its own thread and disposes it with
        /// the process.
        ///
        /// <para>This is how an internet radio station is played. The app owns the connection, so the
        /// station's interleaved ICY metadata can be extracted and shown as "now playing" (see
        /// <see cref="Radio.IcyStream"/>) and is not discarded inside ffmpeg's http protocol.</para>
        /// </summary>
        public Stream? Input { get; init; }

        /// <summary>Extra ffmpeg arguments, placed before <c>-i</c>. A network source puts its reconnect
        /// and user-agent flags here, because they are input options and have no effect after the
        /// input.</summary>
        public IReadOnlyList<string>? InputArgs { get; init; }

        public int SampleRate { get; init; } = 44100;
        public int Channels { get; init; } = 2;

        /// <summary>Where to start, in seconds of the original track (before any stretch).</summary>
        public double StartSeconds { get; init; }

        /// <summary>How long to decode for, in original seconds. Null decodes to the end.</summary>
        public double? LengthSeconds { get; init; }

        /// <summary>Playback speed: 1 = as recorded, 1.05 = 5% faster and 5% shorter, pitch
        /// unchanged. The value is limited to what <c>atempo</c> can do in one pass (0.5 to 2.0). A
        /// ratio within <see cref="UnstretchedRatio"/> of 1 is ignored, so an unstretched deck uses no
        /// filter.</summary>
        public double TempoRatio { get; init; } = 1.0;

        /// <summary>
        /// One more audio filter for ffmpeg to run while it decodes, given as a filtergraph fragment
        /// (<c>"adeclick=threshold=4:method=save"</c>). Null means no filter.
        ///
        /// <para>The filter runs before the caller receives any sample, so it comes before everything
        /// the caller does in managed code. The vinyl de-click must come before the noise gate, because
        /// a click has energy in every frequency bin at once and would hold the gate open around
        /// itself.</para>
        ///
        /// <para>When a stretch is also requested, the filter is joined to <c>atempo</c> with a comma.
        /// It is not added as a second <c>-filter:a</c>, because ffmpeg uses only the last one given and
        /// silently ignores the others.</para>
        /// </summary>
        public string? AudioFilter { get; init; }

        public string? Label { get; init; }
    }

    private readonly Options _options;
    private Process? _process;
    private Stream? _stdout;
    private Thread? _feeder;
    private byte[] _scratch = [];
    private volatile bool _ended;
    private volatile bool _disposed;

    /// <summary>ffmpeg's error output, read as it arrives so that a full pipe cannot stall ffmpeg. It
    /// is kept because its last line describes why a broken stream failed.</summary>
    private Task<string>? _errors;

    /// <summary>How the decode ended. It is worked out once and stored. <see cref="PcmState.Playing"/>
    /// means it has not been worked out yet.</summary>
    private PcmState _state = PcmState.Playing;
    private string? _fault;
    private readonly object _endGate = new();

    /// <summary>How long to wait for the process to exit after its output closed, and for its stderr to be
    /// handed over, before treating the end as normal. Normally it has already exited, because ffmpeg
    /// closes stdout as it exits, so the wait costs nothing. On a busy machine the exit and the hand-over
    /// can take more than half a second, and a shorter wait reads a stream that was cut off as one that
    /// ended. The wait is only paid in full when ffmpeg does not exit.</summary>
    private const int ExitGraceMs = 2000;

    public FfmpegPcmSource(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Start();
    }

    public string Label => _options.Label ?? Path.GetFileNameWithoutExtension(_options.Source);

    /// <summary>True once the decoder has no more audio: the file ended, or the decoder never
    /// started.</summary>
    public bool Ended => _ended;

    /// <summary>
    /// Why the decode ran out of audio: the track is over, or the bytes stopped arriving.
    ///
    /// <para>
    /// This is <see cref="PcmState.Failed"/> only for a source read over the network, on purpose.
    /// The only evidence is what ffmpeg reported when it exited (see <see cref="Settle"/>). That is a
    /// clear answer to "did the connection break" but an unreliable answer to "is this file valid":
    /// many playable files report a bad tail after all the music has been delivered, and treating that
    /// as a failure would make the player try to open them again three times. A local file therefore
    /// always ends normally, and the check is used where it is needed: a stream from a computer that
    /// left the network in the middle of a record.
    /// </para>
    /// </summary>
    public PcmState State
    {
        get
        {
            if (!_ended) return PcmState.Playing;

            lock (_endGate)
            {
                if (_state is not PcmState.Playing) return _state;
                _state = Settle(out _fault);
                return _state;
            }
        }
    }

    /// <summary>The reason when <see cref="State"/> is <see cref="PcmState.Failed"/>: the last message
    /// from ffmpeg.</summary>
    public string? Fault
    {
        get
        {
            _ = State;                       // works out the state if it has not been worked out yet
            lock (_endGate) return _fault;
        }
    }

    /// <summary>
    /// Works out how a decoder that has run out of audio ended. Called once, under the gate.
    ///
    /// <para>The exit code alone is not enough. A connection reset in the middle of a track is reported
    /// as <c>Error during demuxing: Connection reset by peer</c>, and then ffmpeg exits with code
    /// <b>zero</b> after delivering only the part of the record that it managed to read. So the
    /// message is what is trusted. At <c>-loglevel error</c> ffmpeg prints nothing for a decode that
    /// went well, so any line of output is meaningful.</para>
    /// </summary>
    private PcmState Settle(out string? fault)
    {
        fault = null;

        var process = _process;
        if (_disposed || process is null || !IsNetwork(_options.Source) || _options.Input is not null)
            return PcmState.Ended;

        try
        {
            // The output has already closed, so this wait is a formality. But the exit code and the
            // last of stderr cannot be read until the process has actually exited.
            process.WaitForExit(ExitGraceMs);
            if (!process.HasExited)
                return PcmState.Ended;
        }
        catch
        {
            return PcmState.Ended;
        }

        var complaint = LastComplaint();
        if (complaint is null && process.ExitCode == 0)
            return PcmState.Ended;

        fault = complaint ?? $"ffmpeg exited {process.ExitCode}";
        return PcmState.Failed;
    }

    /// <summary>
    /// Everything ffmpeg wrote to stderr once the decode has ended, or null while it is still
    /// running or if it wrote nothing.
    ///
    /// <para>This is the whole output, not only the last line. A filter can print its own report. For
    /// example, adeclick reports the share of samples it judged to be clicks. That report is not an error.
    /// ffmpeg also prints ordinary closing messages after the report, so the report is not always the last
    /// line. This is read only after the stream has ended, with the same time limit, for the reasons
    /// given at <see cref="LastComplaint"/>.</para>
    /// </summary>
    public string? Said
    {
        get
        {
            if (!_ended || _errors is not { } errors)
                return null;
            try
            {
                if (!errors.Wait(ExitGraceMs) || !errors.IsCompletedSuccessfully)
                    return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
            return errors.Result;
        }
    }

    /// <summary>
    /// The last thing ffmpeg wrote to stderr, or null when it wrote nothing.
    ///
    /// <para>The process having exited does not mean its stderr has been read.
    /// <see cref="StreamReader.ReadToEndAsync()"/> completes on the thread pool, and on a busy machine
    /// that can happen well after the exit. Checking whether the task had finished, instead of waiting
    /// for it, would read a stream that was cut off mid-record as a record that simply ended. That is
    /// the fault this class must detect, and it would only show under load.</para>
    ///
    /// <para>The wait is limited by the same grace as the exit. The pipe is already closed when this
    /// is called, so the wait is only a hand-over and not a read. If the wait does not finish, the
    /// result is the same as ffmpeg saying nothing.</para>
    /// </summary>
    private string? LastComplaint()
    {
        if (_errors is not { } errors)
            return null;

        try
        {
            if (!errors.Wait(ExitGraceMs) || !errors.IsCompletedSuccessfully)
                return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        var line = errors.Result
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    /// <summary>Whether a source is read over the network and not from a disk: a presigned cloud URL,
    /// or a track served by another computer on this network.</summary>
    public static bool IsNetwork(string? source) =>
        source is not null &&
        (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         source.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ffmpeg's own reconnect arguments, for a source that is read over the network. This is the first
    /// response to a connection that drops briefly: ffmpeg reopens the input at the byte it had reached
    /// and the decode continues, inside the decode-ahead buffer. The listener hears nothing and the
    /// code above is not told.
    ///
    /// <para>Null for a file, where these arguments have no meaning.</para>
    /// </summary>
    public static IReadOnlyList<string>? ReconnectArgs(string? source) =>
        IsNetwork(source)
            ?
            [
                "-reconnect", "1",
                "-reconnect_streamed", "1",
                "-reconnect_delay_max", "5",
            ]
            : null;

    private void Start()
    {
        var inv = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo(ToolPaths.Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");

        // A filter that reports its own results needs a log level that lets its messages through. adeclick
        // reports the share of samples it took to be clicks at INFO level, and `error` hides that.
        //
        // The level is raised only when a filter was requested. At `error` level anything ffmpeg prints is
        // a fault. That is why the single line that Settle reads can be trusted (see LastComplaint). This
        // is safe because Settle reads stderr only for a network source or a piped one, and the filter
        // is used only for the vinyl render, which reads a local file.
        bool filtered = !string.IsNullOrWhiteSpace(_options.AudioFilter);
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add(filtered ? "info" : "error");
        if (filtered)
            psi.ArgumentList.Add("-nostats");   // the progress line is not needed
        foreach (var arg in _options.InputArgs ?? [])
            psi.ArgumentList.Add(arg);
        if (_options.StartSeconds > 0.01 && _options.Input is null)
        {
            // Request an accurate seek explicitly: a deck cued at a bar line must open exactly on that
            // sample. A seek to the nearest frame boundary would put the whole record a few
            // milliseconds out, and a beat-matched changeover is timed to the sample.
            psi.ArgumentList.Add("-accurate_seek");
            psi.ArgumentList.Add("-ss");

            // Six decimal places, not three. One millisecond is up to 22 samples at 44.1 kHz. A rounding
            // error of that size happens at every cue-in, and again at each decoder reopen during a tempo
            // change. The error is the same every time, so it does not average out. The record would land
            // beside the beat, not on it.
            psi.ArgumentList.Add(_options.StartSeconds.ToString("F6", inv));
        }
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(_options.Input is null ? _options.Source : "pipe:0");
        psi.RedirectStandardInput = _options.Input is not null;
        if (_options.LengthSeconds is double length)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(length.ToString("F3", inv));
        }

        // Use one -filter:a that carries every filter. ffmpeg keeps only the last of repeated
        // options, so a second one would silently discard the first.
        double ratio = Math.Clamp(_options.TempoRatio, 0.5, 2.0);
        var chain = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(_options.AudioFilter))
            chain.Add(_options.AudioFilter!);
        if (Math.Abs(ratio - 1.0) > UnstretchedRatio)
            chain.Add($"atempo={ratio.ToString("F6", inv)}");
        if (chain.Count > 0)
        {
            psi.ArgumentList.Add("-filter:a");
            psi.ArgumentList.Add(string.Join(",", chain));
        }

        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(_options.Channels.ToString(inv));
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(_options.SampleRate.ToString(inv));
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("-");

        try
        {
            _process = Process.Start(psi);
            if (_process is null) { _ended = true; return; }
            _errors = _process.StandardError.ReadToEndAsync();   // read continuously so a full pipe cannot block ffmpeg
            _stdout = _process.StandardOutput.BaseStream;
            if (_options.Input is { } input)
                StartFeeding(input, _process.StandardInput.BaseStream);
        }
        catch
        {
            _ended = true;
        }
    }

    /// <summary>
    /// Copies the caller's bytes into ffmpeg's stdin on a separate thread. It must be a separate
    /// thread: the write blocks when ffmpeg is behind, and the read blocks when the station is slow.
    /// Doing either on the thread that reads the decoded audio can deadlock the process.
    /// </summary>
    private void StartFeeding(Stream input, Stream stdin)
    {
        _feeder = new Thread(() =>
        {
            var block = new byte[64 * 1024];
            try
            {
                int n;
                while (!_ended && (n = input.Read(block, 0, block.Length)) > 0)
                    stdin.Write(block, 0, n);
            }
            catch { /* the station disconnected, or we are shutting down; either way the decode ends */ }
            finally
            {
                // Closing stdin tells ffmpeg that the input has ended. Without it, a station that goes
                // quiet would leave the process waiting forever.
                try { stdin.Dispose(); } catch { }
                try { input.Dispose(); } catch { }
            }
        })
        { IsBackground = true, Name = "ffmpeg feed" };
        _feeder.Start();
    }

    public int Read(float[] buffer, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_ended || _stdout is null || frames <= 0)
            return 0;

        int wantBytes = frames * _options.Channels * sizeof(float);
        if (_scratch.Length < wantBytes)
            _scratch = new byte[wantBytes];

        int got = 0;
        try
        {
            while (got < wantBytes)
            {
                int n = _stdout.Read(_scratch, got, wantBytes - got);
                if (n <= 0) break;
                got += n;
            }
        }
        catch { _ended = true; }

        if (got <= 0)
        {
            _ended = true;
            return 0;
        }

        int samples = got / sizeof(float);
        int read = samples / _options.Channels;
        if (read <= 0)
        {
            _ended = true;
            return 0;
        }

        Buffer.BlockCopy(_scratch, 0, buffer, 0, read * _options.Channels * sizeof(float));
        if (read < frames)
            _ended = true;         // a short block is the end of the file
        return read;
    }

    public void Dispose()
    {
        // Set before the kill below, and read by Settle: a decoder that we stop ourselves exits with
        // a non-zero code, and that is not a broken stream.
        _disposed = true;
        _ended = true;
        var proc = _process;
        _process = null;
        _stdout = null;
        try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
        try { proc?.Dispose(); } catch { }
    }
}
