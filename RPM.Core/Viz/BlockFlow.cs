using System.Numerics;
using RPM.Core.Audio;

namespace RPM.Core.Viz;

/// <summary>
/// A flight through a field of turning blocks, lit by the track.
///
/// <para>
/// The desktop draws this as instanced geometry: one cube mesh, with a few thousand copies placed and
/// turned in a vertex shader. Uno has no iOS build of that control, so a phone cannot do the same. A
/// phone has Skia, which takes a list of triangles with a colour at each corner. So the placing, the
/// turning and the lighting happen here, once per block per frame. The head passes the result to
/// <c>DrawVertices</c> in a single call.
/// </para>
///
/// <para>
/// The field is a lattice that scrolls with the camera. A block is never created or destroyed. The
/// lattice renumbers as the flight moves, and the far row becomes the near one. A cell's properties
/// are its size, its height, how it spins, whether it is one of the lit blocks, and which band lights
/// it. All of these are hashed from its coordinates, exactly as in the shader. So each block stays
/// individual, with no per-frame bookkeeping at all.
/// </para>
///
/// <para>
/// There are fewer blocks than on the desktop, which uses forty by sixty-four. The corners here are
/// transformed on the CPU, not the GPU. A five-inch screen cannot show that many blocks as anything but
/// noise. The painter's order draws the far row first. The blocks are opaque and convex, so a depth
/// buffer would only settle ties that the lattice order already settles.
/// </para>
/// </summary>
public sealed class BlockFlow
{
    /// <summary>Lattice cells across.</summary>
    public const int Cols = 20;

    /// <summary>The number of rows, running ahead of the camera.</summary>
    public const int Rows = 36;

    /// <summary>Spacing between cells, in world units.</summary>
    private const float Cell = 0.62f;

    /// <summary>The nearest row starts this far ahead, so nothing flies through the lens.</summary>
    private const float NearSkip = 1.4f;

    /// <summary>
    /// How fast the field fades into the background. The value comes from this field's own depth, not
    /// from the desktop's number. This lattice is shorter. A density tuned for 64 rows would leave the
    /// last of the 36 rows still lit. That would draw a straight edge across the picture where the
    /// world stops. This value puts the far row at a tenth of its brightness, which is where the
    /// desktop's own far row is.
    /// </summary>
    private const float Fog = 2.3f / (Rows * Cell);

    /// <summary>Every block can show three of its six faces, and a face is two triangles.</summary>
    public const int MaxVertices = Cols * Rows * 3 * 6;

    /// <summary>The navy that everything fades into. The head clears the frame to this colour.</summary>
    public static readonly (float R, float G, float B) Background = (0.012f, 0.020f, 0.043f);

    private readonly ShaderAudioTexture _audio;
    private readonly float[] _spec = new float[ShaderAudioTexture.Width * ShaderAudioTexture.Height];

    private readonly Vector3[] _corners = new Vector3[8];
    private readonly Vector2[] _screen = new Vector2[8];
    private readonly bool[] _behind = new bool[8];

    private float _swell;
    private double _swellHold;
    private double _flown;

    public BlockFlow(int frameSize = 2048) => _audio = new ShaderAudioTexture(frameSize);

    /// <summary>The broadband level, with an envelope: a fast attack, a held peak, then a slow settle. It
    /// lifts the light, the block scale and the speed of the flight together.</summary>
    public float Swell => _swell;

    /// <summary>How far the flight has travelled, in world units.</summary>
    public float Flown => (float)_flown;

    /// <summary>Takes a frame of played samples. The spectrum is smoothed per bin, and the swell gets an
    /// envelope. Called at about 40 Hz. The flight itself runs off the clock.</summary>
    public void Listen(ReadOnlySpan<float> frame, double now)
    {
        _audio.Update(frame);

        var data = _audio.Data;
        for (int i = 0; i < data.Length; i++)
        {
            float target = data[i];
            float k = target > _spec[i] ? 0.35f : 0.10f;   // rise fast, fall slow
            _spec[i] += (target - _spec[i]) * k;
        }

        const int lo = 2, hi = 220;
        float sum = 0f;
        for (int i = lo; i < hi; i++) sum += _spec[i];
        float level = sum / ((hi - lo) * 255f);

        if (level > _swell)
        {
            _swell += (level - _swell) * 0.35f;
            _swellHold = now + 0.35;
        }
        else if (now >= _swellHold)
        {
            _swell += (level - _swell) * 0.010f;
        }
    }

    /// <summary>Moves the flight on. The distance is integrated, not read from the clock. So the music
    /// can change the speed without the field jumping.</summary>
    public void Advance(double seconds)
        => _flown += Math.Clamp(seconds, 0, 0.1) * (1.30 + _swell * 1.10);

    /// <summary>
    /// Builds the frame: every visible face of every block as two triangles, in normalised device
    /// coordinates (x and y in -1..1, y up), with the face's lit colour on each of its corners.
    /// </summary>
    /// <param name="time">Seconds since the stage came up.</param>
    /// <param name="widthOverHeight">The frame's aspect, for the projection.</param>
    /// <param name="xy">Filled with two floats per vertex. Must hold <see cref="MaxVertices"/>
    /// vertices.</param>
    /// <param name="argb">Filled with one packed colour per vertex, same length in vertices as
    /// <paramref name="xy"/>.</param>
    /// <returns>How many vertices were written. This is a multiple of three, and never more than
    /// <see cref="MaxVertices"/>.</returns>
    public int Fill(double time, float widthOverHeight, float[] xy, uint[] argb)
    {
        ArgumentNullException.ThrowIfNull(xy);
        ArgumentNullException.ThrowIfNull(argb);
        if (xy.Length < MaxVertices * 2) throw new ArgumentException($"needs {MaxVertices * 2} floats", nameof(xy));
        if (argb.Length < MaxVertices) throw new ArgumentException($"needs {MaxVertices} colours", nameof(argb));

        float t = (float)time;
        float camZ = (float)_flown;

        // The camera glides above the sheet the blocks lie on, and looks down along it. So the field lies
        // across the frame as a band, with clear sky above it. The camera follows the sheet's height.
        // Otherwise the sway would drift out of the band and the screen would empty. The roll puts the
        // band on a diagonal, instead of flat along the horizon.
        float eyeX = MathF.Sin(t * 0.13f) * 1.8f;
        float aimX = MathF.Sin(t * 0.11f + 1.3f) * 2.6f;

        var eye = new Vector3(eyeX, Sheet(eyeX, camZ, t) + 0.55f + MathF.Sin(t * 0.19f) * 0.30f, camZ);
        var aim = new Vector3(aimX, Sheet(aimX, camZ + 7.5f, t) + 0.10f, camZ + 7.5f);
        var up = new Vector3(MathF.Sin(t * 0.05f) * 0.42f, 1f, 0f);

        var viewProj = Matrix4x4.CreateLookAt(eye, aim, up)
            * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.1f, Math.Max(0.1f, widthOverHeight),
                                                     0.05f, Rows * Cell + 6f);

        var lamp = eye + new Vector3(0f, 3.2f, 2.0f);   // just above and behind the lens

        int verts = 0;

        for (int row = 0; row < Rows; row++)
        {
            // Far row first. The blocks are opaque, so this painter's order keeps the field readable
            // without a depth buffer.
            float ck = MathF.Floor(camZ / Cell) + (Rows - 1 - row);

            for (int col = 0; col < Cols; col++)
            {
                float ci = col - Cols * 0.5f + 0.5f;

                float h1 = Hash(ci, ck);
                float h2 = Hash(ci + 17.31f, ck + 17.31f);
                float h3 = Hash(ci + 41.77f, ck + 41.77f);
                float h4 = Hash(ci + 73.19f, ck + 73.19f);

                // Jittered off the lattice, or flying down it shows up as rows and columns.
                float cx = (ci + (h3 - 0.5f) * 0.55f) * Cell;
                float cz = (ck + (h4 - 0.5f) * 0.55f) * Cell + NearSkip;

                // Height: the blocks drift around a slowly rolling sheet and do not fill a box. This makes
                // the field read as a band that is flown through. Blocks pass above and below, as well as
                // to either side.
                float cy = (h2 - 0.5f) * 1.5f + Sheet(cx, cz, t);

                var centre = new Vector3(cx, cy, cz);

                float size = (0.05f + h1 * 0.22f) * (1.0f + _swell * 0.30f);
                float a1 = t * (0.35f + h3 * 0.55f) + h1 * MathF.Tau;
                float a2 = t * (0.28f + h4 * 0.45f) + h2 * MathF.Tau;

                float s1 = MathF.Sin(a1), c1 = MathF.Cos(a1);
                float s2 = MathF.Sin(a2), c2 = MathF.Cos(a2);

                // The eight corners are spun and placed, then projected once each. A cube's faces share
                // corners, so projecting per face would repeat the same arithmetic three times.
                bool anyBehind = false;
                for (int i = 0; i < 8; i++)
                {
                    var p = Spin(Cube[i] * size, s1, c1, s2, c2) + centre;
                    _corners[i] = p;

                    var clip = Vector4.Transform(new Vector4(p, 1f), viewProj);
                    if (clip.W <= 0.001f)
                    {
                        _behind[i] = true;
                        anyBehind = true;
                        _screen[i] = default;
                    }
                    else
                    {
                        _behind[i] = false;
                        _screen[i] = new Vector2(clip.X / clip.W, clip.Y / clip.W);
                    }
                }

                if (anyBehind) continue;   // a block straddling the lens is dropped, not clipped

                // A minority of blocks glow rather than reflect light. Each one is on its own band. So a
                // bright top end lights a different part of the field than a bass line does.
                float glow = 0f;
                if (h4 >= 0.78f)
                {
                    float where = Frac(h3 * 0.8f + t * 0.01f) * 0.55f + 0.02f;
                    float band = Band(where);
                    glow = MathF.Pow(band, 2.2f) * 1.9f;
                }

                var baseTint = Vector3.Lerp(new Vector3(0.05f, 0.26f, 0.62f),
                                            new Vector3(0.03f, 0.15f, 0.42f), h1);

                for (int f = 0; f < 6; f++)
                {
                    var normal = Spin(FaceNormals[f], s1, c1, s2, c2);

                    var quad = Faces[f];
                    var world = (_corners[quad[0]] + _corners[quad[1]]
                               + _corners[quad[2]] + _corners[quad[3]]) * 0.25f;

                    var toCam = eye - world;
                    float dist = toCam.Length();
                    if (Vector3.Dot(normal, toCam) <= 0f) continue;   // facing away

                    // The vignette is worked out at the face's own place on the screen. The desktop's
                    // fragment shader does this per pixel. A block is only a few points across, so one
                    // value per face gives the same picture. This matters on this head. The other option
                    // is a full-screen multiply, and Skia's Metal backend can do that blend only by
                    // copying the whole render target every frame.
                    var mid = (_screen[quad[0]] + _screen[quad[1]]
                             + _screen[quad[2]] + _screen[quad[3]]) * 0.25f;

                    uint colour = Shade(baseTint, normal, world, toCam, dist, lamp, glow, Vignette(mid));

                    // Two triangles, wound as they come. Skia does not cull, and the face has already
                    // checked whether it is seen.
                    if (verts + 6 > MaxVertices) return verts;

                    Emit(xy, argb, ref verts, _screen[quad[0]], colour);
                    Emit(xy, argb, ref verts, _screen[quad[1]], colour);
                    Emit(xy, argb, ref verts, _screen[quad[2]], colour);
                    Emit(xy, argb, ref verts, _screen[quad[0]], colour);
                    Emit(xy, argb, ref verts, _screen[quad[2]], colour);
                    Emit(xy, argb, ref verts, _screen[quad[3]], colour);
                }
            }
        }

        return verts;
    }

    /// <summary>One lamp rides just above and behind the camera. So the field lights up as the flight
    /// moves into it, and falls dark behind. Then the field fades into the navy, so the lattice has no
    /// visible far edge.</summary>
    private uint Shade(Vector3 baseTint, Vector3 normal, Vector3 world, Vector3 toCam, float dist,
                       Vector3 lamp, float glow, float vignette)
    {
        var n = Vector3.Normalize(normal);
        var view = toCam / MathF.Max(dist, 0.001f);
        var light = Vector3.Normalize(lamp - world);

        float diffuse = MathF.Max(Vector3.Dot(n, light), 0f);
        var reflected = Vector3.Reflect(-light, n);
        float specular = Pow26(MathF.Max(Vector3.Dot(reflected, view), 0f));
        float atten = 5.5f / (1f + dist * 0.28f + dist * dist * 0.045f);

        var col = baseTint * (0.12f + diffuse * (0.85f + _swell * 0.6f)) * atten;
        col += new Vector3(0.75f, 0.90f, 1.0f) * (specular * atten * 0.45f);
        col += new Vector3(1.0f, 0.58f, 0.10f) * (glow * Math.Clamp(atten, 0.25f, 1.4f));

        var bg = new Vector3(Background.R, Background.G, Background.B);
        col = Vector3.Lerp(bg, col, MathF.Exp(-dist * Fog)) * vignette;

        return 0xFF000000u
             | ((uint)(Math.Clamp(col.X, 0f, 1f) * 255f + 0.5f) << 16)
             | ((uint)(Math.Clamp(col.Y, 0f, 1f) * 255f + 0.5f) << 8)
             | (uint)(Math.Clamp(col.Z, 0f, 1f) * 255f + 0.5f);
    }

    /// <summary>
    /// How much light reaches a place on the screen. The shape matches the desktop shader. It brightens
    /// the middle as much as it darkens the corners.
    /// </summary>
    private static float Vignette(Vector2 ndc)
    {
        var u = ndc.X * 0.5f + 0.5f;
        var v = ndc.Y * 0.5f + 0.5f;
        return Math.Clamp(u * (1f - u) * v * (1f - v) * 32f * 0.75f + 0.25f, 0f, 1.8f);
    }

    /// <summary>The specular exponent of 26, by repeated squaring. x^26 is x^16 times x^8 times x^2. This
    /// needs only a few multiplies. A power function would be called about two thousand times a
    /// frame.</summary>
    private static float Pow26(float x)
    {
        float x2 = x * x;
        float x4 = x2 * x2;
        float x8 = x4 * x4;
        return x8 * x8 * x8 * x2;
    }

    private static void Emit(float[] xy, uint[] argb, ref int verts, Vector2 p, uint colour)
    {
        xy[verts * 2] = p.X;
        xy[verts * 2 + 1] = p.Y;
        argb[verts] = colour;
        verts++;
    }

    /// <summary>Spin about Y, then tumble about X. These are the two rotations of the shader, in the
    /// same order.</summary>
    private static Vector3 Spin(Vector3 p, float s1, float c1, float s2, float c2)
    {
        var a = new Vector3(p.X * c1 - p.Z * s1, p.Y, p.X * s1 + p.Z * c1);
        return new Vector3(a.X, a.Y * c2 - a.Z * s2, a.Y * s2 + a.Z * c2);
    }

    /// <summary>The rolling sheet that the blocks lie on, and the height that the camera rides.</summary>
    private static float Sheet(float x, float z, float t)
        => MathF.Sin(z * 0.35f + t * 0.30f) * 0.85f + MathF.Sin(x * 0.42f - t * 0.13f) * 0.65f;

    /// <summary>The spectrum at a normalised position, read the same way the shader's texture read
    /// works.</summary>
    private float Band(float where)
    {
        int i = (int)(Math.Clamp(where, 0f, 1f) * (ShaderAudioTexture.Width - 1));
        return _spec[i] / 255f;
    }

    /// <summary>The one-line hash from GLSL, kept identical. So each cell has the same size, spin and band
    /// as on the desktop.</summary>
    private static float Hash(float x, float y)
        => Frac(MathF.Sin(x * 127.1f + y * 311.7f) * 43758.5453f);

    private static float Frac(float v) => v - MathF.Floor(v);

    private static readonly Vector3[] Cube =
    [
        new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f),
        new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f),
    ];

    private static readonly int[][] Faces =
    [
        [4, 5, 6, 7],   // front
        [1, 0, 3, 2],   // back
        [5, 1, 2, 6],   // right
        [0, 4, 7, 3],   // left
        [7, 6, 2, 3],   // top
        [0, 1, 5, 4],   // bottom
    ];

    private static readonly Vector3[] FaceNormals =
    [
        new(0, 0, 1), new(0, 0, -1), new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0),
    ];
}
