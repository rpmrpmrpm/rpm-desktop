using System.Diagnostics;
using System.Numerics;
using RPM.Core.Audio;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// A flower of light, and a reading of the music at the same time.
///
/// The petals sit on a phyllotaxis spiral. Petal i is placed at the golden angle times i, at radius
/// sqrt(i/N). This is the packing that a sunflower head uses. It is why the bloom looks organic rather than
/// drawn: no two petals line up into a spoke, and the density is even from the heart to the rim.
///
/// Each petal shows a polar spectrogram. The angle is frequency, from bass to treble once around the wheel.
/// The radius is time. The heart of the flower is the current instant, and the rim is about two and a half
/// seconds ago. So a beat opens at the centre and travels outward as a ring. A held note draws a spiral arm.
/// The last few seconds of the music are on screen at once, turning slowly.
///
/// Petals are camera-facing quads, stretched along their own radius. They are brightest and longest where
/// their band was loudest. The same energy lifts them out of the plane, so the bloom is a relief that
/// undulates rather than a flat disc. They are drawn additively. That means no depth buffer and no sorting.
/// Light simply adds up where petals overlap, and that is what gives the heart its glow.
/// </summary>
public sealed partial class BloomStage : GlCanvas, IVizStage
{
    private const int Petals = 12000;      // instances on the spiral
    private const int Motes = 700;         // and the sparks that drift above it
    private const int Instances = Petals + Motes;
    private const float Radius = 1.62f;    // the bloom's radius in world units
    private const float GoldenAngle = 2.39996323f;

    // The polar spectrogram. Columns are time (the newest is column 0), and rows are the frequency wheel.
    // A petal reads it at (its radius, its angle). So an instant that enters at the heart takes the whole
    // width of this texture to reach the rim.
    private const int HistW = 96;          // time columns: about 2.4 s at the 40 Hz analysis tick
    private const int HistBands = 64;      // the frequency wheel's resolution
    private readonly byte[] _histRgba = new byte[HistW * HistBands * 4];

    private readonly ShaderAudioTexture _audio = new(SpectrumAnalyzer.FrameSize);
    private readonly float[] _wave = new float[SpectrumAnalyzer.FrameSize];
    private readonly float[] _spec = new float[ShaderAudioTexture.Width * ShaderAudioTexture.Height];

    private readonly Stopwatch _clock = new();
    private readonly float[] _viewProj = new float[16];

    private RPaMpModel? _model;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdog;
    private bool _running;
    private bool _glReady;
    private double _lastAnalysis, _lastFrame;
    private float _swell;                  // enveloped broadband level: fast attack, slow release
    private double _swellHold;
    private double _turned;                // the bloom's own rotation, summed per frame so the music can change it

    private uint _vao, _vbo, _program, _histTex;
    private int _uViewProj, _uCam, _uTime, _uSwell, _uTurn, _uHist, _uRes;
    private string _versionDef = "#version 300 es";

    public BloomStage() : base(static () => Common.GlHost.Window())
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

    /// <summary>The frame source is the window's composition tick. It is not Invalidate called from inside
    /// RenderOverride, because the tree drops those requests while it is busy. A watchdog covers the frame
    /// that goes missing anyway.</summary>
    private void OnCompositionTick(object? sender, object e)
    {
        if (_running) Invalidate();
    }

    private void OnWatchdog(object? sender, object e)
    {
        if (_running && _clock.Elapsed.TotalSeconds - _lastFrame > 0.022)
            Invalidate();
    }

    // GL lifecycle

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

        // One quad, in corner coordinates. Everything else about a petal is derived in the vertex shader.
        float[] quad =
        [
            -1f, -1f,  1f, -1f,  1f, 1f,
            -1f, -1f,  1f,  1f, -1f, 1f,
        ];
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, quad, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }
        gl.EnableVertexAttribArray(0);

        _histTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _histTex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        // The frequency wheel wraps around: bass meets bass again all the way round, so the rows repeat.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);   // Uno can leave a pixel buffer bound; the upload would read it
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            HistW, HistBands, 0, PixelFormat.Rgba, PixelType.UnsignedByte, _histRgba);

        _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
        _uCam = gl.GetUniformLocation(_program, "uCam");
        _uTime = gl.GetUniformLocation(_program, "uTime");
        _uSwell = gl.GetUniformLocation(_program, "uSwell");
        _uTurn = gl.GetUniformLocation(_program, "uTurn");
        _uHist = gl.GetUniformLocation(_program, "uHist");
        _uRes = gl.GetUniformLocation(_program, "uResolution");

        _glReady = true;
    }

    protected override void OnDestroy(GL gl)
    {
        if (_program != 0) gl.DeleteProgram(_program);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        if (_histTex != 0) gl.DeleteTexture(_histTex);
        _program = _vbo = _vao = _histTex = 0;
        _glReady = false;
    }

    protected override void RenderOverride(GL gl)
    {
        EnsureGlResources(gl);

        double now = _clock.Elapsed.TotalSeconds;
        double dt = _lastFrame > 0 ? Math.Min(0.1, now - _lastFrame) : 0.0;
        _lastFrame = now;


        if (now - _lastAnalysis >= 0.025)
        {
            _lastAnalysis = now;
            Analyse(gl);
        }

        // The bloom turns slowly, and a loud passage turns it a little faster. The turn is summed over the
        // frames, so a change of speed never makes the angle jump.
        _turned += dt * (0.085 + _swell * 0.22);

        Span<int> vp = stackalloc int[4];
        gl.GetInteger(GetPName.Viewport, vp);
        float w = Math.Max(1, vp[2]), h = Math.Max(1, vp[3]);
        float t = (float)now;

        // The camera drifts around the bloom and rises and falls over it. The relief is seen from changing
        // angles. Near overhead it looks like a mandala. Low down it looks like a landscape of light.
        float orbit = t * 0.043f;
        float pitch = 0.62f + MathF.Sin(t * 0.037f) * 0.34f;          // radians above the plane
        float dist = 2.42f - _swell * 0.20f + MathF.Sin(t * 0.021f) * 0.22f;
        var eye = new Vector3(
            MathF.Cos(orbit) * MathF.Cos(pitch) * dist,
            MathF.Sin(pitch) * dist,
            MathF.Sin(orbit) * MathF.Cos(pitch) * dist);
        var lookAt = new Vector3(0f, 0.10f, 0f);
        var view = Matrix4x4.CreateLookAt(eye, lookAt, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.4f, w / h, 0.05f, 24f);
        WriteMatrix(view * proj, _viewProj);

        gl.ClearColor(0.006f, 0.010f, 0.024f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        // Additive blending: overlapping petals add up into the glow at the heart. Addition gives the same result
        // in any order, so there is nothing to sort and no depth buffer is needed.
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        gl.Disable(EnableCap.CullFace);

        if (_program != 0)
        {
            gl.UseProgram(_program);
            if (_uViewProj >= 0) gl.UniformMatrix4(_uViewProj, 1, false, _viewProj);
            if (_uCam >= 0) gl.Uniform3(_uCam, eye.X, eye.Y, eye.Z);
            if (_uTime >= 0) gl.Uniform1(_uTime, t);
            if (_uSwell >= 0) gl.Uniform1(_uSwell, _swell);
            if (_uTurn >= 0) gl.Uniform1(_uTurn, (float)_turned);
            if (_uRes >= 0) gl.Uniform2(_uRes, w, h);
            if (_uHist >= 0)
            {
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, _histTex);
                gl.Uniform1(_uHist, 0);
            }

            gl.BindVertexArray(_vao);
            gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, Instances);
        }

        gl.Disable(EnableCap.Blend);
        // No Invalidate() here. The composition tick drives the frames.
    }

    /// <summary>Reads the current block of audio, smooths the spectrum, updates the swell envelope, and adds one
    /// new column to the polar spectrogram. The new column is this instant's frequency wheel, at the heart of
    /// the flower.</summary>
    private void Analyse(GL gl)
    {
        bool playing = _model is { IsPlaying: true } m && m.CopyPlayFrame(_wave);
        if (!playing) Array.Clear(_wave);
        _audio.Update(_wave);

        var d = _audio.Data;
        for (int i = 0; i < d.Length; i++)
        {
            float target = d[i];
            float k = target > _spec[i] ? 0.42f : 0.12f;   // rise fast, fall slow
            _spec[i] += (target - _spec[i]) * k;
        }

        // Broadband swell: a fast attack, a short hold at the peak, then a slow release. It drives the
        // scale, the brightness and the turn rate of the whole bloom.
        const int b0 = 2, b1 = 220;
        float sum = 0f;
        for (int i = b0; i < b1; i++) sum += _spec[i];
        float level = sum / ((b1 - b0) * 255f);
        double now = _clock.Elapsed.TotalSeconds;
        if (level > _swell) { _swell += (level - _swell) * 0.35f; _swellHold = now + 0.30; }
        else if (now >= _swellHold) { _swell += (level - _swell) * 0.012f; }

        // Move every row one column outward, then write this instant into column 0. The bands are spaced on a
        // log scale. So each octave gets about the same room on the wheel, and the bass does not crowd the rest.
        for (int band = 0; band < HistBands; band++)
        {
            int lo = (int)(2.0 * Math.Pow(210.0, band / (double)HistBands));
            int hi = (int)(2.0 * Math.Pow(210.0, (band + 1) / (double)HistBands));
            if (hi <= lo) hi = lo + 1;
            if (hi > 460) hi = 460;

            float s = 0f;
            for (int i = lo; i < hi; i++) s += _spec[i];
            float v = s / ((hi - lo) * 255f);
            // A soft knee: quiet detail stays visible, and loud peaks do not simply clip to white. There is no
            // gain before this step. Real music fills far more of the wheel than a test tone does. A boost here
            // would turn the whole flower into one white sheet.
            v = MathF.Min(1f, MathF.Pow(v, 0.85f));

            int row = band * HistW;
            Array.Copy(_histRgba, row * 4, _histRgba, (row + 1) * 4, (HistW - 1) * 4);
            int j = row * 4;
            _histRgba[j] = (byte)(v * 255f + 0.5f);
            _histRgba[j + 1] = 0;
            _histRgba[j + 2] = 0;
            _histRgba[j + 3] = 255;
        }

        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, _histTex);
        gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0,
            HistW, HistBands, PixelFormat.Rgba, PixelType.UnsignedByte, _histRgba);
    }

    private static void WriteMatrix(Matrix4x4 m, float[] dest)
    {
        dest[0] = m.M11; dest[1] = m.M12; dest[2] = m.M13; dest[3] = m.M14;
        dest[4] = m.M21; dest[5] = m.M22; dest[6] = m.M23; dest[7] = m.M24;
        dest[8] = m.M31; dest[9] = m.M32; dest[10] = m.M33; dest[11] = m.M34;
        dest[12] = m.M41; dest[13] = m.M42; dest[14] = m.M43; dest[15] = m.M44;
    }

    // shaders

    private void BuildProgram(GL gl)
    {
        string vertex = $$"""
            {{_versionDef}}
            precision highp float;
            layout (location = 0) in vec2 aCorner;   // the quad, in -1..1

            uniform mat4  uViewProj;
            uniform vec3  uCam;
            uniform float uTime;
            uniform float uSwell;
            uniform float uTurn;      // the bloom's own rotation
            uniform sampler2D uHist;  // polar spectrogram: x = age (0 = now), y = the frequency wheel

            const int   PETALS = {{Petals}};
            const int   MOTES = {{Motes}};
            const float RADIUS = {{GlslSource.Num(Radius)}};
            const float GOLDEN = {{GlslSource.Num(GoldenAngle)}};

            out vec2  vCorner;
            out vec3  vColor;
            out float vEnergy;
            out float vR;          // where on the flower this petal sits, heart 0 to rim 1

            float hash1(float n) { return fract(sin(n * 12.9898) * 43758.5453); }

            void main()
            {
                float i = float(gl_InstanceID);

                // The last few hundred instances are not petals at all: they are motes, drifting up and
                // around the bloom like pollen off it, catching the top end of the music. They cost the
                // same as any other instance and they are what stops the dark around the flower being empty.
                if (gl_InstanceID >= PETALS)
                {
                    float m = float(gl_InstanceID - PETALS);
                    float ha = hash1(m * 1.13), hb = hash1(m * 2.71), hc = hash1(m * 5.37);
                    float ma = ha * 6.2831853 + uTime * (0.02 + hb * 0.05);
                    float mr = RADIUS * (0.35 + hb * 1.25);
                    float rise = fract(hc + uTime * (0.010 + ha * 0.022));   // drifts up, then starts again
                    float my = -0.25 + rise * 1.55;

                    vec3 centre = vec3(cos(ma) * mr, my, sin(ma) * mr);
                    float spark = texture(uHist, vec2(0.04, 0.72 + hb * 0.26)).x;
                    float twinkle = 0.45 + 0.55 * sin(uTime * (1.1 + hc * 2.2) + m);
                    float e = (0.22 + spark * 0.85) * twinkle
                            * smoothstep(0.0, 0.12, rise) * (1.0 - smoothstep(0.72, 1.0, rise));

                    float msize = 0.0060 + hc * 0.0070;
                    vec3 viewDir = normalize(uCam - centre);
                    vec3 mr1 = normalize(cross(viewDir, vec3(0.0, 1.0, 0.0)));
                    vec3 mr2 = normalize(cross(viewDir, mr1));
                    vec3 world = centre + mr1 * (aCorner.x * msize) + mr2 * (aCorner.y * msize);

                    vColor = mix(vec3(0.55, 0.88, 1.0), vec3(1.0), spark * 0.6);
                    vEnergy = e;
                    vR = 1.0;
                    vCorner = aCorner;
                    gl_Position = uViewProj * vec4(world, 1.0);
                    return;
                }

                float f = (i + 0.5) / float(PETALS);     // 0 at the heart, 1 at the rim
                // pow(f, 0.62) rather than sqrt: slightly denser toward the heart, which is what lets the
                // centre gather into a burning core instead of thinning out where the petals converge.
                float rf = pow(f, 0.62);

                // Phyllotaxis, plus a differential turn: the heart runs a little ahead of the rim, which is
                // what curls the arms into a slow swirl instead of a rigid wheel.
                // A whisper of jitter on both: dead-exact phyllotaxis reads as a machined comb, and this is
                // the difference between a lattice and a living thing.
                float jitter = hash1(i * 1.7);
                float ang = i * GOLDEN + uTurn * (1.35 - 0.55 * rf) + (jitter - 0.5) * 0.045;
                float r = RADIUS * (0.055 + 0.945 * rf) * (1.0 + (hash1(i * 3.1) - 0.5) * 0.035);

                // The petal's own reading of the music: its angle is the band, its radius is how long ago.
                float wheel = fract(ang / 6.2831853);
                float age = clamp(rf, 0.0, 1.0);
                float e = texture(uHist, vec2(age, wheel)).x;

                // Soft knee. Additive petals accumulate, so anything close to linear runs to white the
                // moment several loud bands overlap: this keeps the top end climbing but never arriving.
                e = e / (0.55 + e);

                // Older rings ease off, so the flower fades outward instead of ending at a hard rim.
                e *= 1.0 - smoothstep(0.55, 1.0, rf);
                // The heart always burns: a flower needs a centre, and silence should still leave an ember
                // rather than a hole. It breathes with the swell.
                e = max(e, exp(-rf * 6.0) * (0.16 + uSwell * 0.30));
                vEnergy = e;

                // Out of the plane: the ring rises with its energy over a shallow dome that breathes.
                float dome = (1.0 - rf * rf) * (0.10 + uSwell * 0.18);
                float y = dome + e * (0.20 + uSwell * 0.16) + (hash1(i) - 0.5) * 0.010;

                vec3 centre = vec3(cos(ang) * r, y, sin(ang) * r);

                // Petals are stretched along their own radius and grow with energy, so a loud band throws
                // long strokes of light outward and a quiet one leaves fine specks.
                // The innermost petals wear a wide soft halo, which is what fills the convergence with glow instead
                // of leaving the dark pupil that a spiral inevitably has at its origin.
                float size = (0.0105 + 0.0075 * rf) * (0.55 + e * 2.0 + uSwell * 0.25)
                           * (1.0 + 1.9 * exp(-rf * 7.0));
                float longAxis = size * (1.45 + e * 2.3);
                float shortAxis = size * 0.55;

                // The radial direction in world space, made perpendicular to the view so the stretch reads
                // as radial on screen from any angle.
                vec3 radial = vec3(cos(ang), 0.0, sin(ang));
                vec3 viewDir = normalize(uCam - centre);
                radial = normalize(radial - viewDir * dot(radial, viewDir));
                vec3 across = normalize(cross(viewDir, radial));

                // The stroke turns as it goes out: rays at the heart, arcs at the rim. That is the shape a
                // flower's own parastichy spirals make, and it is what stops the disc reading as a comb of
                // spokes. Petals bank into the turn instead of all pointing at the centre.
                float lean = 0.26 + smoothstep(0.02, 0.95, rf) * 0.62;   // never a stroke aimed dead at the centre
                vec3 along = normalize(mix(radial, across, lean));
                vec3 side = normalize(cross(viewDir, along));

                vec3 world = centre + along * (aCorner.x * longAxis) + side * (aCorner.y * shortAxis);

                // Colour by band, the app's own azure through cyan to a hot white at the top end, with the
                // loudest petals burning toward white whatever their band.
                vec3 bass = vec3(0.05, 0.17, 0.95);
                vec3 mid  = vec3(0.02, 0.78, 1.00);
                vec3 high = vec3(0.86, 0.97, 1.00);
                vec3 col = wheel < 0.5 ? mix(bass, mid, wheel * 2.0) : mix(mid, high, (wheel - 0.5) * 2.0);
                col = mix(col, vec3(1.0), clamp(e * e * 0.35, 0.0, 0.40));

                vColor = col;
                vR = rf;
                vCorner = aCorner;
                gl_Position = uViewProj * vec4(world, 1.0);
            }
            """;

        string fragment = $$"""
            {{_versionDef}}
            precision highp float;
            in vec2  vCorner;
            in vec3  vColor;
            in float vEnergy;
            in float vR;

            uniform vec2 uResolution;

            out vec4 fragColor;

            void main()
            {
                // A soft grain of light: a bright core inside a wide, gentle halo. The halo is what fuses
                // neighbouring petals into arms of light rather than a field of dots.
                float d = dot(vCorner, vCorner);
                float core = exp(-d * 8.0);
                float halo = exp(-d * 2.0) * 0.45;
                // Deliberately faint per petal: with twelve thousand of them the light is meant to come from
                // ACCUMULATION, which is what gives soft gradients instead of a flat clipped white.
                // The heart carries more light than the rim, so the flower has a burning centre and fades
                // into the dark rather than glowing evenly like a disc of pixels.
                float heart = 1.0 + 0.85 * exp(-vR * 4.0);
                float a = (core + halo) * (0.045 + vEnergy * 0.42) * heart;

                vec3 col = vColor * (0.55 + vEnergy * 0.95) + vec3(0.50, 0.85, 1.0) * core * vEnergy * 0.25;
                col = mix(col, vec3(0.72, 0.93, 1.0), exp(-vR * 6.0) * 0.45);   // the centre runs white-hot

                // Vignette, so the frame's corners stay dark and the bloom holds the eye.
                vec2 uv = gl_FragCoord.xy / uResolution;
                a *= uv.x * (1.0 - uv.x) * uv.y * (1.0 - uv.y) * 26.0 * 0.6 + 0.34;

                fragColor = vec4(col * a, a);
            }
            """;

        try
        {
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
            SetError(ex.Message);
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
