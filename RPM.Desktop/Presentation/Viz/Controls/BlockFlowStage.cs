using System.Diagnostics;
using System.Numerics;
using RPM.Core.Audio;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// A flight through a field of turning blocks, drawn as real instanced geometry.
///
/// The same picture can be made by raymarching a distance field in a full-screen fragment shader. The
/// ShaderToy version does that. But a raymarch costs work per pixel (260 steps each), so at a retina full
/// screen it becomes too slow. This draws the blocks instead: one cube mesh and a few thousand instances,
/// each placed and turned in the vertex shader. The cost is per vertex, so the frame rate hardly depends on
/// the resolution. The fill is a handful of small opaque quads.
///
/// The field is a lattice that scrolls with the camera. Instance <c>i</c> maps to a lattice cell, and its
/// depth row is measured from the camera's current cell. So blocks are never created or destroyed. The
/// lattice is renumbered as the camera moves, and the far row becomes the near one. Everything a cell owns
/// (size, height, spin, whether it is one of the lit cells and which frequency band lights it) is computed
/// from its coordinates. So no per-frame CPU work is needed to keep a thousand blocks individual.
///
/// The music drives it in two ways. The broadband swell (fast attack, slow release, the same envelope the
/// ribbon sea uses) raises the light and the block scale. Each lit block burns with its own band. So a
/// bright top end lights a different part of the field than a bass line does.
/// </summary>
public sealed partial class BlockFlowStage : GlCanvas, IVizStage
{
    private const int Cols = 40;         // lattice cells across
    private const int Rows = 64;         // and rows ahead of the camera
    private const int Instances = Cols * Rows;
    private const float Cell = 0.62f;    // spacing between cells, in world units
    private const float NearSkip = 1.4f; // the nearest row starts this far ahead, so no block passes the lens

    private readonly ShaderAudioTexture _audio = new(SpectrumAnalyzer.FrameSize);
    private readonly float[] _wave = new float[SpectrumAnalyzer.FrameSize];
    private readonly byte[] _rgba = new byte[ShaderAudioTexture.Width * ShaderAudioTexture.Height * 4];
    private readonly float[] _spec = new float[ShaderAudioTexture.Width * ShaderAudioTexture.Height];

    private readonly Stopwatch _clock = new();
    private readonly float[] _viewProj = new float[16];

    private RPaMpModel? _model;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdog;
    private bool _running;
    private bool _glReady;
    private bool _depth;                         // the framebuffer came with a depth buffer
    private double _lastAnalysis, _lastFrame;
    private float _swell;                        // enveloped broadband level: fast attack, slow release
    private double _swellHold;
    private double _flown;                       // distance travelled, summed per frame (so speed can change)

    private uint _vao, _vbo, _program, _tex;
    private int _uViewProj, _uCamZ, _uTime, _uSwell, _uAudio, _uCam, _uRes;
    private string _versionDef = "#version 300 es";

    public BlockFlowStage() : base(static () => Common.GlHost.Window())
    {
        Unloaded += (_, _) => Stop();
    }

    public string? RendererInfo { get; private set; }
    public string? LastError { get; private set; }
    public event Action? CompileStateChanged;

    public void SetActive(bool on)
    {
        if (on) Start(); else Stop();
    }

    private void Start()
    {
        if (_running) return;
        _model ??= App.Services.GetRequiredService<RPaMpService>().Transport;
        _running = true;
        if (!_clock.IsRunning) _clock.Start();
        CompositionTarget.Rendering += OnCompositionTick;

        _watchdog ??= DispatcherQueue.CreateTimer();
        _watchdog.Interval = TimeSpan.FromMilliseconds(8);
        _watchdog.IsRepeating = true;
        _watchdog.Tick -= OnWatchdog;
        _watchdog.Tick += OnWatchdog;
        _watchdog.Start();
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnCompositionTick;
        _watchdog?.Stop();
        _clock.Stop();
    }

    /// <summary>The frame source is the window's composition tick. It is not <c>Invalidate</c> called from
    /// inside <c>RenderOverride</c>, because the tree drops those requests while it is busy. The watchdog asks
    /// again for a frame that is overdue, so one lost request costs one frame only.</summary>
    private void OnCompositionTick(object? sender, object e)
    {
        if (_running) Invalidate();
    }

    private void OnWatchdog(object? sender, object e)
    {
        if (_running && _clock.Elapsed.TotalSeconds - _lastFrame > 0.022)
            Invalidate();
    }

    // ── GL lifecycle ──

    protected override void Init(GL gl) => EnsureGlResources(gl);

    private void EnsureGlResources(GL gl)
    {
        if (_glReady) return;

        var slVersion = gl.GetStringS(StringName.ShadingLanguageVersion) ?? "";
        _versionDef = slVersion.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase)
            ? "#version 300 es"
            : "#version 330";
        RendererInfo = $"{gl.GetStringS(StringName.Renderer)} - {gl.GetStringS(StringName.Version)}";
        DispatcherQueue?.TryEnqueue(() => CompileStateChanged?.Invoke());

        BuildProgram(gl);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, CubeMesh(), BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        }
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);

        _tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);   // Uno can leave a pixel buffer bound; the upload would read it
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            ShaderAudioTexture.Width, ShaderAudioTexture.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _rgba);

        _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
        _uCamZ = gl.GetUniformLocation(_program, "uCamZ");
        _uTime = gl.GetUniformLocation(_program, "uTime");
        _uSwell = gl.GetUniformLocation(_program, "uSwell");
        _uAudio = gl.GetUniformLocation(_program, "uAudio");
        _uCam = gl.GetUniformLocation(_program, "uCam");
        _uRes = gl.GetUniformLocation(_program, "uResolution");

        // Solid blocks need a depth buffer. The framebuffer from Uno normally has one. If it does not, the
        // draw order alone must hide the blocks behind others. That is why the instances are numbered far row
        // first (see the vertex shader), so the field is painted back to front.
        gl.GetInteger((GLEnum)0x0D56, out int depthBits);   // GL_DEPTH_BITS
        _depth = depthBits > 0;

        _glReady = true;
    }

    protected override void OnDestroy(GL gl)
    {
        if (_program != 0) gl.DeleteProgram(_program);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        if (_tex != 0) gl.DeleteTexture(_tex);
        _program = _vbo = _vao = _tex = 0;
        _glReady = false;
    }

    protected override void RenderOverride(GL gl)
    {
        EnsureGlResources(gl);

        double now = _clock.Elapsed.TotalSeconds;
        double dt = _lastFrame > 0 ? Math.Min(0.1, now - _lastFrame) : 0.0;
        _lastFrame = now;


        // Audio analysis runs at about 40 Hz. The flight moves every frame, from the clock. Only the spectrum
        // and the swell update at this slower rate, which keeps the CPU work per frame very small.
        if (now - _lastAnalysis >= 0.025)
        {
            _lastAnalysis = now;

            bool playing = _model is { IsPlaying: true } m && m.CopyPlayFrame(_wave);
            if (!playing) Array.Clear(_wave);
            _audio.Update(_wave);

            var d = _audio.Data;
            for (int i = 0; i < d.Length; i++)
            {
                float target = d[i];
                float k = target > _spec[i] ? 0.35f : 0.10f;   // rise fast, fall slow
                _spec[i] += (target - _spec[i]) * k;
                byte b = (byte)(_spec[i] + 0.5f);
                int j = i * 4;
                _rgba[j] = b; _rgba[j + 1] = 0; _rgba[j + 2] = 0; _rgba[j + 3] = 255;
            }

            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
            gl.BindTexture(TextureTarget.Texture2D, _tex);
            gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0,
                ShaderAudioTexture.Width, ShaderAudioTexture.Height,
                PixelFormat.Rgba, PixelType.UnsignedByte, _rgba);

            // Broadband swell: it rises fast, holds for a short time at the peak, then falls slowly. This is
            // the slow dynamic. The light, the block scale and the flight speed all follow it.
            const int b0 = 2, b1 = 220;
            float sum = 0f;
            for (int i = b0; i < b1; i++) sum += _spec[i];
            float level = sum / ((b1 - b0) * 255f);
            if (level > _swell) { _swell += (level - _swell) * 0.35f; _swellHold = now + 0.35; }
            else if (now >= _swellHold) { _swell += (level - _swell) * 0.010f; }
        }

        // The flight. Distance is summed over the frames instead of read from the clock. That lets the music
        // change the speed without the field jumping.
        _flown += dt * (1.30 + _swell * 1.10);
        float camZ = (float)_flown;

        Span<int> vp = stackalloc int[4];
        gl.GetInteger(GetPName.Viewport, vp);
        float w = Math.Max(1, vp[2]), h = Math.Max(1, vp[3]);

        // Camera: it glides above the sheet of blocks and looks down along it. So the field runs across the
        // frame as a band, with clear sky above it, and nothing is flown straight through. It sways slowly
        // and re-aims over a long period, so the view keeps changing without a cut.
        float t = (float)now;
        // The sheet rolls, using the same two sines as the vertex shader. The camera follows the height of
        // the sheet. Otherwise the sway would drift out of the band and leave the screen empty.
        float eyeX = MathF.Sin(t * 0.13f) * 1.8f;
        float aimX = MathF.Sin(t * 0.11f + 1.3f) * 2.6f;
        var eye = new Vector3(eyeX, Sheet(eyeX, camZ, t) + 0.55f + MathF.Sin(t * 0.19f) * 0.30f, camZ);
        var lookAt = new Vector3(aimX, Sheet(aimX, camZ + 7.5f, t) + 0.10f, camZ + 7.5f);
        // The roll tilts the band across the frame, so it is not flat along the horizon.
        var up = new Vector3(MathF.Sin(t * 0.05f) * 0.42f, 1f, 0f);

        var view = Matrix4x4.CreateLookAt(eye, lookAt, up);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.1f, w / h, 0.05f, Rows * Cell + 6f);
        var viewProj = view * proj;
        WriteMatrix(viewProj, _viewProj);

        gl.ClearColor(0.012f, 0.020f, 0.043f, 1f);   // the deep navy that the fog fades to
        gl.Clear(ClearBufferMask.ColorBufferBit | (_depth ? ClearBufferMask.DepthBufferBit : 0));

        if (_depth)
        {
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Lequal);
        }
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.Disable(EnableCap.Blend);

        if (_program != 0)
        {
            gl.UseProgram(_program);
            if (_uViewProj >= 0) gl.UniformMatrix4(_uViewProj, 1, false, _viewProj);
            if (_uCamZ >= 0) gl.Uniform1(_uCamZ, camZ);
            if (_uTime >= 0) gl.Uniform1(_uTime, t);
            if (_uSwell >= 0) gl.Uniform1(_uSwell, _swell);
            if (_uCam >= 0) gl.Uniform3(_uCam, eye.X, eye.Y, eye.Z);
            if (_uRes >= 0) gl.Uniform2(_uRes, w, h);
            if (_uAudio >= 0)
            {
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, _tex);
                gl.Uniform1(_uAudio, 0);
            }

            gl.BindVertexArray(_vao);
            gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 36, Instances);
        }

        gl.Disable(EnableCap.CullFace);
        if (_depth) gl.Disable(EnableCap.DepthTest);
        // No Invalidate() here. The composition tick drives the frames (see OnCompositionTick).
    }

    /// <summary>The height of the rolling sheet that the blocks are laid on, in world units. It must match
    /// the two sines in the vertex shader. The camera follows this height, so the band stays in frame.</summary>
    private static float Sheet(float x, float z, float t)
        => MathF.Sin(z * 0.35f + t * 0.30f) * 0.85f + MathF.Sin(x * 0.42f - t * 0.13f) * 0.65f;

    private static void WriteMatrix(Matrix4x4 m, float[] dest)
    {
        dest[0] = m.M11; dest[1] = m.M12; dest[2] = m.M13; dest[3] = m.M14;
        dest[4] = m.M21; dest[5] = m.M22; dest[6] = m.M23; dest[7] = m.M24;
        dest[8] = m.M31; dest[9] = m.M32; dest[10] = m.M33; dest[11] = m.M34;
        dest[12] = m.M41; dest[13] = m.M42; dest[14] = m.M43; dest[15] = m.M44;
    }

    /// <summary>A unit cube as 36 vertices (position and face normal). The triangles are wound
    /// counter-clockwise when seen from outside, so back-face culling removes the far half of each block.</summary>
    private static float[] CubeMesh()
    {
        // face origin, two edge vectors, normal
        (Vector3 o, Vector3 u, Vector3 v, Vector3 n)[] faces =
        [
            (new(-.5f, -.5f,  .5f), new(1, 0, 0), new(0, 1, 0), new(0, 0,  1)),   // front
            (new( .5f, -.5f, -.5f), new(-1, 0, 0), new(0, 1, 0), new(0, 0, -1)),  // back
            (new( .5f, -.5f,  .5f), new(0, 0, -1), new(0, 1, 0), new(1, 0, 0)),   // right
            (new(-.5f, -.5f, -.5f), new(0, 0, 1), new(0, 1, 0), new(-1, 0, 0)),   // left
            (new(-.5f,  .5f,  .5f), new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)),   // top
            (new(-.5f, -.5f, -.5f), new(1, 0, 0), new(0, 0, 1), new(0, -1, 0)),   // bottom
        ];

        var data = new float[36 * 6];
        int k = 0;
        foreach (var (o, u, v, n) in faces)
            foreach (var (su, sv) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 0f), (1f, 1f), (0f, 1f) })
            {
                var p = o + u * su + v * sv;
                data[k++] = p.X; data[k++] = p.Y; data[k++] = p.Z;
                data[k++] = n.X; data[k++] = n.Y; data[k++] = n.Z;
            }
        return data;
    }

    // ── shaders ──

    private void BuildProgram(GL gl)
    {
        string vertex = $$"""
            {{_versionDef}}
            precision highp float;
            layout (location = 0) in vec3 aPos;      // unit cube corner
            layout (location = 1) in vec3 aNormal;   // its face normal

            uniform mat4  uViewProj;
            uniform float uCamZ;      // how far the flight has travelled
            uniform float uTime;
            uniform float uSwell;     // enveloped broadband level
            uniform sampler2D uAudio; // 512x2: row 0 spectrum, row 1 waveform

            const int COLS = {{Cols}};
            const int ROWS = {{Rows}};
            const float CELL = {{GlslSource.Num(Cell)}};
            const float NEAR_SKIP = {{GlslSource.Num(NearSkip)}};

            out vec3 vNormal;
            out vec3 vWorld;
            out vec3 vBase;
            out float vGlow;

            float hash1(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

            void main()
            {
                int id = gl_InstanceID;
                int ix = id % COLS;
                int iz = id / COLS;

                // Depth row counted from the FAR end, so instance 0 is the most distant: with a depth
                // buffer this is free, and without one it is exactly the painter's order that keeps the
                // field readable. The lattice is indexed in CELLS and only turned into world units after,
                // so the camera's own cell is all that scrolls.
                float ci = float(ix) - float(COLS) * 0.5 + 0.5;
                float ck = floor(uCamZ / CELL) + float(ROWS - 1 - iz);
                vec2 c = vec2(ci, ck);

                float h1 = hash1(c);
                float h2 = hash1(c + 17.31);
                float h3 = hash1(c + 41.77);
                float h4 = hash1(c + 73.19);

                // Jittered off the lattice, or flying down it shows up as rows and columns.
                float cx = (ci + (h3 - 0.5) * 0.55) * CELL;
                float cz = (ck + (h4 - 0.5) * 0.55) * CELL + NEAR_SKIP;

                // Height: the blocks drift around a slowly rolling SHEET rather than filling a box, which
                // is what makes the field read as a band you are flying THROUGH: the camera rides at its
                // level, so blocks pass above and below as well as either side.
                float y = (h2 - 0.5) * 1.5
                        + sin(cz * 0.35 + uTime * 0.30) * 0.85
                        + sin(cx * 0.42 - uTime * 0.13) * 0.65;

                // Every block is its own size and turns on its own two axes at its own rate.
                float size = (0.05 + h1 * 0.22) * (1.0 + uSwell * 0.30);
                float a1 = uTime * (0.35 + h3 * 0.55) + h1 * 6.2831;
                float a2 = uTime * (0.28 + h4 * 0.45) + h2 * 6.2831;

                vec3 p = aPos * size;
                vec3 n = aNormal;
                float s1 = sin(a1), c1 = cos(a1);
                p = vec3(p.x * c1 - p.z * s1, p.y, p.x * s1 + p.z * c1);   // spin about Y
                n = vec3(n.x * c1 - n.z * s1, n.y, n.x * s1 + n.z * c1);
                float s2 = sin(a2), c2 = cos(a2);
                p = vec3(p.x, p.y * c2 - p.z * s2, p.y * s2 + p.z * c2);   // tumble about X
                n = vec3(n.x, n.y * c2 - n.z * s2, n.y * s2 + n.z * c2);

                vec3 world = vec3(cx, y, cz) + p;

                // A minority of blocks burn instead of reflecting, each on ITS OWN band: a bright top end
                // lights a different part of the field than a bass line does. h4 picks which, h3 picks where
                // in the spectrum, drifting slowly so the chosen band wanders.
                float band = texture(uAudio, vec2(fract(h3 * 0.8 + uTime * 0.01) * 0.55 + 0.02, 0.25)).x;
                float lit = step(0.78, h4);
                vGlow = lit * pow(band, 2.2) * 1.9;

                // Base tint: the deep blues of the field, a little bluer with distance.
                vBase = mix(vec3(0.05, 0.26, 0.62), vec3(0.03, 0.15, 0.42), h1);

                vNormal = normalize(n);
                vWorld = world;
                gl_Position = uViewProj * vec4(world, 1.0);
            }
            """;

        string fragment = $$"""
            {{_versionDef}}
            precision highp float;
            in vec3 vNormal;
            in vec3 vWorld;
            in vec3 vBase;
            in float vGlow;

            uniform vec3  uCam;
            uniform float uSwell;
            uniform vec2  uResolution;

            out vec4 fragColor;

            void main()
            {
                vec3 n = normalize(vNormal);
                vec3 toCam = uCam - vWorld;
                float dist = length(toCam);
                vec3 vdir = toCam / max(dist, 0.001);

                // One lamp, riding just above and behind the camera, so the field lights up as it is flown
                // into and falls dark behind: the whole sense of travelling through it.
                vec3 lightPos = uCam + vec3(0.0, 3.2, 2.0);
                vec3 ldir = normalize(lightPos - vWorld);
                float diff = max(dot(n, ldir), 0.0);
                float spec = pow(max(dot(reflect(-ldir, n), vdir), 0.0), 26.0);
                float atten = 5.5 / (1.0 + dist * 0.28 + dist * dist * 0.045);

                vec3 col = vBase * (0.12 + diff * (0.85 + uSwell * 0.6)) * atten;
                col += vec3(0.75, 0.90, 1.0) * spec * atten * 0.45;
                col += vec3(1.0, 0.58, 0.10) * vGlow * clamp(atten, 0.25, 1.4);   // the burning blocks

                // Fog into the background navy, so the lattice has no visible far edge.
                vec3 bg = vec3(0.012, 0.020, 0.043);
                col = mix(bg, col, exp(-dist * 0.055));

                // Vignette, the same shape the reference wore.
                vec2 uv = gl_FragCoord.xy / uResolution;
                col *= uv.x * (1.0 - uv.x) * uv.y * (1.0 - uv.y) * 32.0 * 0.75 + 0.25;

                fragColor = vec4(col, 1.0);
            }
            """;

        try
        {
            // The sources are written in ASCII. Ascii() makes sure of it, in case a non-ASCII character is
            // added later.
            uint vs = Compile(gl, ShaderType.VertexShader, GlslSource.Ascii(vertex));
            uint fs = Compile(gl, ShaderType.FragmentShader, GlslSource.Ascii(fragment));
            uint program = gl.CreateProgram();
            gl.AttachShader(program, vs);
            gl.AttachShader(program, fs);
            gl.LinkProgram(program);
            gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
            gl.DetachShader(program, vs);
            gl.DetachShader(program, fs);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            if (linked != (int)GLEnum.True)
            {
                string log = gl.GetProgramInfoLog(program);
                gl.DeleteProgram(program);
                SetError("Program link failed: " + log);
                return;
            }
            _program = program;
            SetError(null);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);   // A broken stage reports the reason. It does not bring the window down.
        }
    }

    private static uint Compile(GL gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status != (int)GLEnum.True)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException(
                $"The {(type == ShaderType.VertexShader ? "vertex" : "fragment")} shader failed to compile: {log}");
        }
        return shader;
    }

    private void SetError(string? error)
    {
        if (error == LastError) return;
        LastError = error;
        DispatcherQueue?.TryEnqueue(() => CompileStateChanged?.Invoke());
    }
}
