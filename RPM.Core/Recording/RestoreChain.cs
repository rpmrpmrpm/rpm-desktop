using RPM.Core.Audio;

namespace RPM.Core.Recording;

/// <summary>
/// The restoration applied to a block of captured audio: the noise gate, then the equaliser. The only
/// way to reach either. Both instances are private to this type and neither is handed out.
///
/// <para>The order is load-bearing, which is why it is a type and not two calls made in the right
/// sequence at each call site. <b>The gate goes FIRST, against audio shaped the way its profile was
/// measured.</b> <see cref="VinylRestorer.MeasureProfileAsync"/> reads the recording raw, with no
/// equaliser and no chain at all. So an equaliser ahead of the gate hands the gate a signal that its
/// profile does not describe. The error goes in the worst direction. Tone lifted at the top means the
/// surface noise arrives louder than the profile says it can be. The gate reads that noise as signal
/// and passes it. Turning the treble up to answer a dull record then makes the hiss worse.</para>
///
/// <para>Tone last is also what a restoration is supposed to do: repair, then remove what is not
/// wanted, then shape what is left. Shaping first means shaping the faults too.</para>
///
/// <para>The de-click is ahead of both, on the decode. See <c>VinylRestorer.DeclickFilter</c> for why
/// a click must not reach the gate.</para>
///
/// <para>The live monitor and the offline render both go through here, so the sound being auditioned
/// cannot drift away from the sound that gets cut. There is one implementation of what the result
/// is.</para>
///
/// <para>The input gain is not part of this. It is applied by the capture pump and baked into the
/// recorded side, because it corrects the level arriving from the turntable rather than treating the
/// recording, and a side captured 20 dB too quiet is not something a later pass can undo.</para>
/// </summary>
public sealed class RestoreChain
{
    private readonly ClickRepair? _clicks;
    private readonly Equalizer? _eq;
    private readonly NoiseReducer? _gate;

    public RestoreChain(RestoreSettings settings, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // First, and that order is the design. A click is broadband, so it has energy in every bin the
        // gate looks at: gate first and the clicks are not only still there, they have dragged the gate
        // open around themselves.
        if (settings.DeclickOn)
            _clicks = new ClickRepair(settings.Clicks, channels);

        if (settings.EqOn)
        {
            _eq = new Equalizer(sampleRate, channels);
            _eq.SetGains(settings.Preamp, settings.Gains, settings.Bass, settings.Treble);
            if (_eq.IsBypassed) _eq = null;   // a flat curve is not worth a filter pass
        }

        if (settings.NoiseApplies)
            _gate = new NoiseReducer(settings.Profile!, settings.Noise, sampleRate, channels);
    }

    /// <summary>True when processing would change nothing.</summary>
    public bool IsBypass => _clicks is null && _eq is null && _gate is null;

    /// <summary>How far the output runs behind the input. Zero unless the gate is on.</summary>
    public int LatencyFrames => (_clicks?.LatencyFrames ?? 0) + (_gate?.LatencyFrames ?? 0);

    /// <summary>How many clicks were mended, and how much of the record that came to. Nought when the
    /// repair is not running.</summary>
    public int ClicksRepaired => _clicks?.RepairCount ?? 0;
    public long ClickSamplesRepaired => _clicks?.RepairedSamples ?? 0;

    /// <summary>Processes <paramref name="frameCount"/> interleaved frames in place.</summary>
    public void Process(float[] interleaved, int frameCount)
    {
        _clicks?.Process(interleaved, frameCount);
        _gate?.Process(interleaved, frameCount);
        _eq?.Process(interleaved, frameCount);
    }

    /// <summary>Clears both stages' memory. Called when the stream restarts, so what was held from one
    /// moment of the record cannot ring into another.</summary>
    public void Reset()
    {
        _clicks?.Reset();
        _eq?.Reset();
        _gate?.Reset();
    }
}
