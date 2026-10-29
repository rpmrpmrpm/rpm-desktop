using RPM.Core.Art;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Finds the faces in a photo.
///
/// Runs UltraFace (Assets/OnnxModels/ultraface.onnx) and returns a rectangle for each face, in the same
/// coordinates as the saliency map. <see cref="RPM.Core.Art.SaliencyCrop"/> uses these rectangles to
/// decide where a wide, short strip should cut a photo of a musician.
///
/// A silhouette cannot replace this detector. Inferring faces from the outline of a subject (the top of
/// the head, peaks in the skyline, the point where the body widens into shoulders, how far down a head the
/// eyes sit) fails on ordinary photographs. A hooded jacket has no shoulders to find. A seated singer in a
/// gown has a silhouette twice as tall as her head. A four-piece band has four heads at four heights. A
/// detector finds the faces directly.
///
/// Best-effort, like the saliency model. A missing file or a runtime that will not start makes every later
/// call return <see cref="FaceScan.Unread"/>. The crop then frames the subject as a whole.
/// </summary>
/// <summary>
/// What a look at a photograph returned: the faces in it, and whether anybody actually looked.
///
/// <para>These are two different questions, so the result has two parts. An empty list alone cannot tell
/// "no faces here" apart from "no detector here". Without this split, a caller that refuses a picture with
/// nobody in it (see <c>PlaylistCovers.FramedAsync</c>) would refuse every picture on a machine with no
/// model installed.</para>
///
/// <para>The answer is given per call. It is not read from a flag on the service. Whether the model loads
/// is settled the first time it is needed. A flag read at any other moment could give a different answer to
/// the same question.</para>
/// </summary>
/// <param name="Faces">Where the faces are, in the map's coordinates. An empty list is a real answer when
/// <paramref name="Looked"/> is true: nobody is in this picture.</param>
/// <param name="Looked">Whether the detector ran at all. False for a missing model, a runtime that would not
/// start, and a picture it could not be handed.</param>
public readonly record struct FaceScan(IReadOnlyList<FaceBox> Faces, bool Looked)
{
    /// <summary>Nobody looked. Every caller should carry on as it did before there was a detector. It
    /// should not conclude that the picture is empty.</summary>
    public static FaceScan Unread => new([], false);

    /// <summary>The detector ran, and this is what it saw.</summary>
    public static FaceScan Read(IReadOnlyList<FaceBox> faces) => new(faces, true);

    /// <summary>The detector ran and there is nobody in this photograph. This is the one state where a caller
    /// should change its decision. An empty list alone cannot say this.</summary>
    public bool FoundNobody => Looked && Faces.Count == 0;
}

public sealed partial class FaceService : IDisposable
{
    /// <summary>The model's input size. It is not square, because UltraFace is trained on 4:3.</summary>
    private const int InputWidth = 320;
    private const int InputHeight = 240;

    /// <summary>Detections below this confidence are noise, such as a smudge on a wall. UltraFace is
    /// confident when it is right.</summary>
    private const float MinConfidence = 0.7f;

    /// <summary>Two boxes that overlap by more than this are one face found twice.</summary>
    private const double MaxOverlap = 0.4;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private bool _failed;

    /// <summary>
    /// The faces in an image given as row-major RGBA at <see cref="InputWidth"/>×<see cref="InputHeight"/>.
    /// The boxes are scaled to a <paramref name="mapSize"/>-square map, the one the saliency model produced,
    /// because the crop is chosen in that space.
    /// </summary>
    public async Task<FaceScan> DetectAsync(byte[] rgba, int mapSize, CancellationToken ct = default)
    {
        if (_failed || rgba.Length < InputWidth * InputHeight * 4)
            return FaceScan.Unread;

        if (!await Cancellable.WaitAsync(_gate, ct))
            return FaceScan.Unread;
        try
        {
            var session = EnsureSession();
            if (session is null)
                return FaceScan.Unread;

            return FaceScan.Read(await Task.Run(() => Run(session, rgba, mapSize)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failed = true;
            return FaceScan.Unread;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The size the caller has to hand the image in at.</summary>
    public static (int Width, int Height) InputSize => (InputWidth, InputHeight);

    private InferenceSession? EnsureSession()
    {
        if (_session is not null)
            return _session;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "OnnxModels", "ultraface.onnx");
        if (!File.Exists(path))
        {
            _failed = true;
            return null;
        }

        OnnxLogging.Quieten();

        return _session = OnnxSession.Open(path, new SessionOptions
        {
            IntraOpNumThreads = 2,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        });
    }

    private static List<FaceBox> Run(InferenceSession session, byte[] rgba, int mapSize)
    {
        var input = new DenseTensor<float>([1, 3, InputHeight, InputWidth]);

        for (var y = 0; y < InputHeight; y++)
        {
            for (var x = 0; x < InputWidth; x++)
            {
                var i = (y * InputWidth + x) * 4;
                for (var c = 0; c < 3; c++)
                    input[0, c, y, x] = (rgba[i + c] - 127f) / 128f;   // UltraFace's own normalisation
            }
        }

        var name = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);

        var outputs = results.ToList();
        var scores = outputs.First(v => v.Name.Contains("score")).AsTensor<float>();
        var boxes = outputs.First(v => v.Name.Contains("box")).AsTensor<float>();

        // Every anchor with a confidence at or above the minimum.
        var found = new List<(float Score, float X1, float Y1, float X2, float Y2)>();
        for (var i = 0; i < scores.Dimensions[1]; i++)
        {
            var confidence = scores[0, i, 1];
            if (confidence >= MinConfidence)
                found.Add((confidence, boxes[0, i, 0], boxes[0, i, 1], boxes[0, i, 2], boxes[0, i, 3]));
        }

        // Non-maximum suppression. The model fires several anchors on the same face, so overlapping boxes
        // are dropped. The highest-scoring box is kept.
        var kept = new List<(float X1, float Y1, float X2, float Y2)>();
        foreach (var (_, x1, y1, x2, y2) in found.OrderByDescending(f => f.Score))
        {
            var duplicate = kept.Any(k =>
            {
                var ix = Math.Max(0, Math.Min(k.X2, x2) - Math.Max(k.X1, x1));
                var iy = Math.Max(0, Math.Min(k.Y2, y2) - Math.Max(k.Y1, y1));
                var overlap = ix * iy;
                var union = (k.X2 - k.X1) * (k.Y2 - k.Y1) + (x2 - x1) * (y2 - y1) - overlap;
                return union > 0 && overlap / union > MaxOverlap;
            });

            if (!duplicate)
                kept.Add((x1, y1, x2, y2));
        }

        // The boxes are normalised to 0..1, so they map onto the saliency map's square without needing the
        // photo's shape.
        return kept.Select(k => new FaceBox(
            (int)(k.X1 * mapSize),
            (int)(k.Y1 * mapSize),
            (int)((k.X2 - k.X1) * mapSize),
            (int)((k.Y2 - k.Y1) * mapSize))).ToList();
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _gate.Dispose();
    }
}
