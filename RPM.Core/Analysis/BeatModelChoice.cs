namespace RPM.Core.Analysis;

/// <summary>Which runtime a model is asked to run on.</summary>
public enum ModelRuntime
{
    /// <summary>The processor. It is available everywhere.</summary>
    Cpu,

    /// <summary>DirectML: the graphics card, or the one built into the processor, on Windows.</summary>
    DirectMl,
}

/// <summary>
/// Chooses which beat-model file to open, and which runtime to open it with.
///
/// <para>These are two choices, because the beat model is the only expensive thing the app runs and
/// its cost differs between machines. A thirty second window through <c>beat_this.onnx</c> takes
/// about half a second on an Apple processor and noticeably longer on an ordinary x86 laptop, and a
/// track is nine windows. The quantised weights and the graphics card are two ways to be faster, and
/// neither is known to be the right default for every machine. Both are chosen here, in one place,
/// from the files on disk and the environment, so they can be compared on a machine without
/// rebuilding the app.</para>
///
/// <para>Pure and in <c>RPM.Core</c> so it can be tested. The service that acts on it lives in the
/// desktop app, where the runtime is.</para>
/// </summary>
public static class BeatModelChoice
{
    /// <summary>The exported weights, as <c>scripts/export_beat_this_onnx.py</c> writes them.</summary>
    public const string Float32FileName = "beat_this.onnx";

    /// <summary>
    /// The same weights quantised to eight-bit integers by <c>scripts/quantize_beat_this.py</c>.
    /// The file is 22 MB instead of 79 MB.
    ///
    /// <para>The quantised model reads almost the same beats. On the 661 tracks of GiantSteps Tempo,
    /// Accuracy1 is 81.39 % for both models. Sixty-three tracks read differently at all, and seven
    /// by more than four percent; on six of those seven both models were wrong. See
    /// bench/RESULTS.md.</para>
    ///
    /// <para>It was measured on Apple silicon only. It is not shipped, and it is used only when
    /// asked for by name: see <see cref="Candidates"/>.</para>
    /// </summary>
    public const string Int8FileName = "beat_this.int8.onnx";

    /// <summary>
    /// The model file to open, as a list of one, or empty when there is none.
    ///
    /// <para>This is the float32 weights, always, unless the quantised ones are asked for by name.
    /// Beat grids go into a pool that every user reads, and two reports of a track count as agreeing
    /// only when they are the same text (<c>AnalysisFacts</c>). On the same window the float32
    /// weights give results within a millionth on arm64 and on x64, while the quantised ones differ
    /// by a thousandth. The eight-bit format can also saturate on an x86 processor without VNNI,
    /// where it was never measured. The server's transcoder reads the float32 weights too. So one
    /// set of weights is used everywhere, with no fallback to the other: a fallback would publish
    /// the other model's grids under the same generation.</para>
    ///
    /// <para><paramref name="requested"/> is <c>RPM_BEAT_MODEL</c> from the environment.
    /// <c>int8</c> gives the quantised file or nothing, for measuring it on a working copy. Any other
    /// value gives the float32 file. The quantised file is not in any package (see
    /// RPM.Desktop.csproj).</para>
    /// </summary>
    public static IReadOnlyList<string> Candidates(
        string directory, Func<string, bool> exists, string? requested = null)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var int8 = Path.Combine(directory, Int8FileName);
        var float32 = Path.Combine(directory, Float32FileName);

        return Normalise(requested) switch
        {
            "int8" => exists(int8) ? [int8] : [],
            _ => exists(float32) ? [float32] : [],
        };
    }

    /// <summary>The file to open first, or null when there is none. This is the first entry of
    /// <see cref="Candidates"/>.</summary>
    public static string? Pick(string directory, Func<string, bool> exists, string? requested = null) =>
        Candidates(directory, exists, requested).FirstOrDefault();

    /// <summary>
    /// The runtime to load the model on.
    ///
    /// <para>The processor, unless <c>RPM_ONNX_PROVIDER</c> asks for DirectML and the platform is
    /// Windows, the only platform where the provider exists. DirectML is opt-in because a
    /// transformer over a 1500 frame window needs about a gigabyte while it runs, and on a laptop
    /// whose graphics share system memory it can be slower as easily as faster. To choose, turn it
    /// on, time an import, and keep whichever is faster.</para>
    /// </summary>
    public static ModelRuntime PickRuntime(string? requested, bool isWindows) =>
        isWindows && Normalise(requested) is "dml" or "directml" ? ModelRuntime.DirectMl : ModelRuntime.Cpu;

    private static string Normalise(string? value) => value?.Trim().ToLowerInvariant() ?? "";
}
