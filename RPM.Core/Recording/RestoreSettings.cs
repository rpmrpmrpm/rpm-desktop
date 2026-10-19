using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Audio;

namespace RPM.Core.Recording;

/// <summary>
/// The restoration dialled in for one vinyl session: an equaliser curve, a noise gate, and the noise
/// profile the gate works against. Written beside the audio as <c>restore.json</c>.
///
/// <para>This is a separate file from the <c>rip.json</c> in the same folder, on purpose.
/// <c>RipManifest</c> is best-effort. It never throws, and a lost manifest costs a name in a list.
/// These settings decide what the cut tracks sound like, so a file that is present but cannot be read
/// is an error, not something to ignore. A file that is absent is not an error. It means nothing
/// was tuned.</para>
/// </summary>
public sealed partial record RestoreSettings
{
    /// <summary>What it is called inside the session folder.</summary>
    public const string FileName = "restore.json";

    /// <summary>Nothing tuned. What a session with no file reads as.</summary>
    public static readonly RestoreSettings Off = new();

    public bool EqOn { get; init; }

    /// <summary>Pre-amp in dB, as <see cref="Equalizer.SetGains"/> takes it.</summary>
    public double Preamp { get; init; }

    /// <summary>One gain in dB per band, in <see cref="Equalizer.Bands"/> order.</summary>
    public IReadOnlyList<double> Gains { get; init; } = new double[Equalizer.Bands.Length];

    /// <summary>The bass shelf in dB, <see cref="Equalizer.BassHz"/>. A separate control from the bands
    /// and not a twelfth of them: the bands are a curve somebody drew or a preset applied, and this is
    /// a judgement about the whole bottom end that has to survive both.</summary>
    public double Bass { get; init; }

    /// <summary>The treble shelf in dB, <see cref="Equalizer.TrebleHz"/>. As
    /// <see cref="Bass"/>.</summary>
    public double Treble { get; init; }

    public bool NoiseOn { get; init; }

    public NoiseReductionSettings Noise { get; init; } = new();

    /// <summary>Whether the clicks are taken out. Independent of <see cref="NoiseOn"/>: they are two
    /// different faults, and unlike the gate this one needs nothing measured first.</summary>
    public bool DeclickOn { get; init; }

    /// <summary>
    /// How the click repair works, when it is on.
    ///
    /// <para>This is named <c>Clicks</c>, not <c>Declick</c>, on purpose. Files written by earlier
    /// versions may carry a <c>Declick</c> object whose threshold is on ffmpeg's scale, where 2 is
    /// ordinary. On this scale 2 would rebuild most of the record. An unknown property is ignored on
    /// read. So an old file loads with today's defaults, and not with a number that no longer means
    /// what it says. A saved setting must not outlive the setting it configured.</para>
    /// </summary>
    public ClickRepairSettings Clicks { get; init; } = new();

    /// <summary>The measured floor. Null until a stretch of the recording has been sampled.</summary>
    public NoiseProfile? Profile { get; init; }

    /// <summary>The file's shape, and how it is written.</summary>
    /// <remarks>The settings sit on the context rather than on a JsonSerializerOptions instance because
    /// a published build is Native AOT: the generated metadata carries them to the call.</remarks>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(RestoreSettings))]
    private sealed partial class RestoreSettingsJson : JsonSerializerContext;

    /// <summary>True when the equaliser is on and moves something: a band, the pre-amp, or either
    /// shelf. A curve that is on and flat changes nothing.</summary>
    public bool ToneApplies =>
        EqOn && !(Math.Abs(Preamp) < 0.01
                  && Gains.All(g => Math.Abs(g) < 0.01)
                  && Math.Abs(Bass) < 0.01 && Math.Abs(Treble) < 0.01);

    /// <summary>True when the noise gate is on and has a profile to work against. On without a profile,
    /// the gate has nothing to compare the audio with and is skipped.</summary>
    public bool NoiseApplies => NoiseOn && Profile is not null;

    /// <summary>True when applying this would change nothing, so the render can be skipped
    /// entirely.</summary>
    public bool IsBypass => !ToneApplies && !NoiseApplies && !DeclickOn;

    /// <summary>The settings in <paramref name="folder"/>, or null when there are none.</summary>
    /// <exception cref="InvalidDataException">The file is there and cannot be read.</exception>
    public static RestoreSettings? Read(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), RestoreSettingsJson.Default.RestoreSettings)
                ?? throw new InvalidDataException($"{path} is empty");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"could not read {path}: {ex.Message}", ex);
        }
    }

    public void WriteTo(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, RestoreSettingsJson.Default.RestoreSettings));
    }
}
