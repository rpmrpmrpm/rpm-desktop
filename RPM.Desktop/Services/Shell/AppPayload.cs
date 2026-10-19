using RPM.Core.Analysis;
using RPM.Core.Startup;
using RPM.Fingerprint;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// What a complete RPM install contains: the tools it shells out to and the models it runs.
///
/// <para>One list, checked at launch (see <see cref="Preflight"/>). It is the installer's manifest
/// as the app understands it. Nothing here is fetched or built on the user's machine, so anything
/// absent means the install is not whole, and the app says so instead of failing later in a way
/// that reads as a bug in a feature.</para>
///
/// <para>Severity is the whole judgement in this file. Required means the app cannot honestly open:
/// without ffmpeg it cannot play, measure or import anything. Optional means one capability is lost
/// and the rest is untouched. The app opens and says what will not work. Moving an item between
/// the two is a one-word edit here, and nothing else changes.</para>
/// </summary>
public static class AppPayload
{
    /// <summary>Where the app's own files live. Everything except the resolved tools is under
    /// here.</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    private static Func<string?> Bundled(params string[] parts) =>
        () => Path.Combine([BaseDirectory, .. parts]);

    /// <summary>
    /// A tool the app resolves rather than simply owns: bundled beside the app, else PATH, else the
    /// well-known install dirs (<see cref="ToolPaths"/>). The resolver hands back the bare name when
    /// it finds nothing, which is not a path. That is the "missing" answer.
    /// </summary>
    private static Func<string?> Resolved(Func<string> resolve) => () =>
    {
        var found = resolve();
        return Path.IsPathRooted(found) ? found : null;
    };

    public static IReadOnlyList<PreflightItem> Manifest =>
    [
        // The tools

        // Everything audio goes through it. Playback decodes with it, import measures with it, and the
        // cloud's canonical copy is muxed by it. Without it the app cannot open a file. A sync that
        // runs without it writes a zero as the length of every track that TagLib cannot measure itself.
        new(Name: OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg",
            PurposeKey: "Preflight_Ffmpeg",
            Locate: Resolved(() => ToolPaths.Ffmpeg),
            MinBytes: 100_000),

        // Chromaprint. Fingerprints are how a track with no usable tags is identified at all, and
        // how the recognition report matches a clip. Nothing else can stand in for it.
        new(Name: OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc",
            PurposeKey: "Preflight_Fpcalc",
            Locate: Resolved(() => ToolPaths.Fpcalc),
            MinBytes: 100_000),

        // The models

        // Beat This! This gives the beat grid and the bar line that the DJ features are built on. This is
        // the float32 file. It is the only one a build ships (see RPM.Desktop.csproj, RpmModel), and
        // BeatModelChoice opens it.
        new(Name: BeatModelChoice.Float32FileName,
            PurposeKey: "Preflight_BeatModel",
            Locate: Bundled("Assets", "OnnxModels", BeatModelChoice.Float32FileName),
            MinBytes: 40_000_000),

        // S-KEY. This gives the musical key. The harmonic changeover is chosen from that key.
        new(Name: "skey.onnx",
            PurposeKey: "Preflight_KeyModel",
            Locate: Bundled("Assets", "OnnxModels", "skey.onnx"),
            MinBytes: 4_000_000),

        // U²-Net finds the subject in the artwork, so a backdrop can be cropped around it. Without it
        // the art still shows, but it is cropped by shape instead of by its subject.
        new(Name: "u2netp.onnx",
            PurposeKey: "Preflight_SaliencyModel",
            Locate: Bundled("Assets", "OnnxModels", "u2netp.onnx"),
            Severity: PreflightSeverity.Optional,
            MinBytes: 2_000_000),

        // UltraFace is not the same kind of model as the one above it. That is why it is on the other
        // side of the line. Saliency is applied to a photograph that has already been chosen. Without it,
        // the picture is cropped by shape, and the worst result is a header cut a little carelessly.
        // Faces do the choosing. See BackdropPreselector, where the shortlist is scored on faces. Without
        // this model every candidate scores the same, the ranking falls back to shape, and the artist
        // header takes whichever wide photograph came first. That is the wrong picture, not a poorer crop
        // of the right one. Nothing on screen shows that anything is missing.
        new(Name: "ultraface.onnx",
            PurposeKey: "Preflight_FaceModel",
            Locate: Bundled("Assets", "OnnxModels", "ultraface.onnx"),
            MinBytes: 500_000),

        // Nothing else in the app's folder is checked, on purpose. This manifest lists what the installer
        // puts down, and the app then finds those files at runtime. The managed assemblies are not listed,
        // because a missing one fails to load long before this check runs. The packaged assets (fonts,
        // artwork, XAML) are not listed either, because they are read through ms-appx and not from the
        // disk. Add to this list only what the installer ships, such as a new model or a new tool.
    ];

    /// <summary>Runs the manifest.</summary>
    public static PreflightReport Check() => Preflight.Check(Manifest);
}
