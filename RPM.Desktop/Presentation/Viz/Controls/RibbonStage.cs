using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using Silk.NET.OpenGL;
using Probe = RPM.Desktop.Services.Audio.PipelineProbe;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// The built-in audio ribbon. It is drawn as real OpenGL geometry, not as a full-screen fragment shader. It is
/// a subdivided plane, drawn as a stack of contour lines. The plane is displaced in 3D and viewed by a real
/// perspective camera, almost edge-on. Because the shape is real geometry, the depth, twist and contortion
/// come from the projection itself. Flat lines collapse to the horizon, and the displacement lifts and fans
/// them out. The look is meant to match a reference scene made in Blender's EEVEE renderer, without faking
/// the z axis in 2D.
///
/// The lines are contour rows of one surface. Its peaks and troughs form an open sea. A few long directional
/// rollers are summed at low wavenumbers. So each line carries a couple of gentle, flowing curves, not a field
/// of small bumps, and the pattern drifts slowly over time. The music drives the whole sea, not the individual
/// lines. The broadband dynamics of the music set the swell height for every row at once. So a loud passage
/// lifts the entire surface, and the lines follow it. A quiet passage flattens it. Lines are drawn as thin
/// screen-space ribbons. Their cross-profile glows, and it is blended additively into white-hot cores with a
/// deep-cyan bloom.
/// </summary>
public sealed partial class RibbonStage : GlCanvas, IVizStage
{
    private const int Lines = 15;       // frequency bands (contour lines), low bands at the front
    private const int Cols = 150;       // samples along each line's length

    private readonly ShaderAudioTexture _audio = new(SpectrumAnalyzer.FrameSize);
    private readonly float[] _wave = new float[SpectrumAnalyzer.FrameSize];
    private readonly byte[] _rgba = new byte[ShaderAudioTexture.Width * ShaderAudioTexture.Height * 4];
    private readonly float[] _spec = new float[ShaderAudioTexture.Width * ShaderAudioTexture.Height]; // smoothed spectrum

    // A scrolling spectrogram history for the rope wrinkles. Columns are time (the newest is at the left edge).
    // Rows are frequency (bass at row 0, treble at the top). The shader picks the band by depth (bass at the
    // back, treble at the front) and the column by position. So a wrinkle starts at the left, from the music of
    // that band at that instant, and flows right along the line over about 2.5 s.
    private const int RopeHistW = 100;   // time columns
    private const int RopeBands = 16;    // frequency rows (bass to treble)
    private readonly byte[] _ropeRgba = new byte[RopeHistW * RopeBands * 4];

    private readonly Stopwatch _clock = new();

    private RPaMpModel? _model;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdog;   // asks again for an overdue frame
    private bool _running;
    private bool _glReady;
    private double _lastAnalysis, _lastFrame;   // time of the last audio analysis, and of the last frame
    private float _swell;                        // swell envelope: fast attack, slow release
    private double _swellHold;                   // the release pauses until this time (a cooldown after a peak)

    private uint _vao, _vbo, _program, _tex, _ropeTex;
    private int _uTime, _uRes, _uAudio, _uSwell, _uRopeHist;
    private int _vertsPerLine;
    private string _versionDef = "#version 300 es";

    public RibbonStage() : base(static () => Common.GlHost.Window())
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

        // Watchdog. The composition tick is the frame clock. But a request made while the tree is in the middle
        // of a layout pass can still be dropped. Then nothing asks again until something unrelated schedules a
        // frame. The watchdog asks again whenever a frame is overdue. So the longest wait is set by the watchdog,
        // and not by whatever else happens to tick.
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

    private void OnWatchdog(object? sender, object e)
    {
        if (_running && _clock.Elapsed.TotalSeconds - _lastFrame > 0.022)
            Invalidate();
    }

    /// <summary>The frame source. Asking for the next frame from inside <see cref="RenderOverride"/> seems
    /// natural, but it does not work with the compositor. While the tree is busy, those requests are merged
    /// into one and lost. Then the visualizer redraws only when something else schedules a frame. The composition
    /// tick is the frame clock that the rest of the window already uses, so calling Invalidate from here keeps
    /// the visualizer at the display rate.</summary>
    private void OnCompositionTick(object? sender, object e)
    {
        if (_running) Invalidate();
    }

    // ── GL lifecycle ──

    protected override void Init(GL gl) => EnsureGlResources(gl);

    /// <summary>Builds the grid VBO, the shader program and the audio texture. It is safe to call more than once.
    /// It is called from both Init and the first RenderOverride. On macOS, Uno can skip Init when the first
    /// EglMakeCurrent fails on a zero-size surface.</summary>
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

        // Grid geometry: each line is a triangle strip with 2 vertices per column. The strip is expanded into a thin
        // ribbon in screen space. Each vertex has these attributes: u (0 to 1 along the length), v (0 to 1, the row),
        // and side (+1 or -1).
        _vertsPerLine = 2 * Cols;
        var data = new float[Lines * _vertsPerLine * 3];
        int k = 0;
        for (int line = 0; line < Lines; line++)
        {
            float v = line / (float)(Lines - 1);
            for (int c = 0; c < Cols; c++)
            {
                float u = c / (float)(Cols - 1);
                data[k++] = u; data[k++] = v; data[k++] = 1f;
                data[k++] = u; data[k++] = v; data[k++] = -1f;
            }
        }

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        }
        gl.EnableVertexAttribArray(0);

        _tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            ShaderAudioTexture.Width, ShaderAudioTexture.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _rgba);

        _ropeTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _ropeTex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            RopeHistW, RopeBands, 0, PixelFormat.Rgba, PixelType.UnsignedByte, _ropeRgba);

        _uTime = gl.GetUniformLocation(_program, "iTime");
        _uRes = gl.GetUniformLocation(_program, "iResolution");
        _uAudio = gl.GetUniformLocation(_program, "iAudio");
        _uSwell = gl.GetUniformLocation(_program, "iSwell");
        _uRopeHist = gl.GetUniformLocation(_program, "iRopeHist");

        _glReady = true;
    }

    protected override void OnDestroy(GL gl)
    {
        if (_program != 0) gl.DeleteProgram(_program);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        if (_tex != 0) gl.DeleteTexture(_tex);
        if (_ropeTex != 0) gl.DeleteTexture(_ropeTex);
        _program = _vbo = _vao = _tex = _ropeTex = 0;
        _glReady = false;
    }

    protected override void RenderOverride(GL gl)
    {
        EnsureGlResources(gl);

        long tw0 = Probe.Begin(Probe.Stage.Frame);
        double now = _clock.Elapsed.TotalSeconds;
        _lastFrame = now;

        // Audio analysis (FFT, smoothing and texture upload) runs at about 40 Hz. The sea still moves every frame,
        // from the shader time. Only the spectrum updates less often. This keeps the CPU work per frame low.
        if (now - _lastAnalysis >= 0.025)
        {
            _lastAnalysis = now;

            long tCopy = Probe.Begin(Probe.Stage.Copy);
            bool playing = _model is { IsPlaying: true } m && m.CopyPlayFrame(_wave);
            Probe.Work(Probe.Stage.Copy, tCopy);
            if (!playing) Array.Clear(_wave);

            long tFft = Probe.Begin(Probe.Stage.Fft);
            _audio.Update(_wave);
            Probe.Work(Probe.Stage.Fft, tFft);
            // Smoothing over time, so the swell moves smoothly and does not twitch. A fast attack lets a beat rise
            // at once. A slow release lets it settle back gently. It is applied to each bin before the texture upload.
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
            long tUp = Probe.Begin(Probe.Stage.Upload);
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
            gl.BindTexture(TextureTarget.Texture2D, _tex);
            gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0,
                ShaderAudioTexture.Width, ShaderAudioTexture.Height,
                PixelFormat.Rgba, PixelType.UnsignedByte, _rgba);
            Probe.Work(Probe.Stage.Upload, tUp);

            // The swell envelope comes from a wide band that is already smoothed. It rises fast, then settles slowly.
            // A short cooldown hold at the top lets a peak linger before it eases back. This is the slow dynamic of
            // the sea. It is deliberately slow, so that the large motion never twitches. The fine detail is carried
            // by the wrinkles.
            const int b0 = 2, b1 = 220;
            float sumB = 0f;
            for (int i = b0; i < b1; i++) sumB += _spec[i];
            float level = sumB / ((b1 - b0) * 255f);
            if (level > _swell) { _swell += (level - _swell) * 0.35f; _swellHold = now + 0.35; }
            else if (now >= _swellHold) { _swell += (level - _swell) * 0.010f; }

            // Rope spectrogram: for each frequency row, move the row one column to the right, and write this
            // instant's band energy into the left column. The bands are spaced on a log scale, from bass (row 0)
            // to treble (top row). The shader reads one row for each depth. So a wrinkle that starts at the left
            // from that band flows right over about 2.5 s.
            for (int k = 0; k < RopeBands; k++)
            {
                int lo = (int)(2.0 * Math.Pow(200.0, k / (double)RopeBands));
                int hi = (int)(2.0 * Math.Pow(200.0, (k + 1) / (double)RopeBands));
                if (hi <= lo) hi = lo + 1;
                if (hi > 500) hi = 500;
                float s = 0f;
                for (int i = lo; i < hi; i++) s += _spec[i];
                byte bb = (byte)(Math.Clamp(s / ((hi - lo) * 255f), 0f, 1f) * 255f + 0.5f);

                int row = k * RopeHistW;
                Array.Copy(_ropeRgba, row * 4, _ropeRgba, (row + 1) * 4, (RopeHistW - 1) * 4);   // scroll right
                int j0 = row * 4;
                _ropeRgba[j0] = bb; _ropeRgba[j0 + 1] = 0; _ropeRgba[j0 + 2] = 0; _ropeRgba[j0 + 3] = 255;
            }
            gl.BindTexture(TextureTarget.Texture2D, _ropeTex);
            gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0,
                RopeHistW, RopeBands, PixelFormat.Rgba, PixelType.UnsignedByte, _ropeRgba);
        }

        Span<int> vp = stackalloc int[4];
        gl.GetInteger(GetPName.Viewport, vp);

        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        // Additive glow with no depth test. Overlapping lines add up into the bloom.
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);

        if (_program != 0)
        {
            gl.UseProgram(_program);
            if (_uTime >= 0) gl.Uniform1(_uTime, (float)now);
            if (_uRes >= 0) gl.Uniform2(_uRes, (float)vp[2], (float)vp[3]);
            if (_uSwell >= 0) gl.Uniform1(_uSwell, _swell);
            if (_uAudio >= 0)
            {
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, _tex);
                gl.Uniform1(_uAudio, 0);
            }
            if (_uRopeHist >= 0)
            {
                gl.ActiveTexture(TextureUnit.Texture1);
                gl.BindTexture(TextureTarget.Texture2D, _ropeTex);
                gl.Uniform1(_uRopeHist, 1);
            }
            long tDraw = Probe.Begin(Probe.Stage.Draw);
            gl.BindVertexArray(_vao);
            for (int line = 0; line < Lines; line++)
                gl.DrawArrays(PrimitiveType.TriangleStrip, line * _vertsPerLine, (uint)_vertsPerLine);
            Probe.Work(Probe.Stage.Draw, tDraw);
        }

        gl.Disable(EnableCap.Blend);

        // The whole-frame cost, and the pacing line for every stage. Read the two together:
        //   a large gap between frames, but low work in every stage: the render callback itself is being
        //     starved (something else holds the thread). GC counts show whether a collection is the cause.
        //   a large amount of work in one stage: that stage is the cost, and the probe names it.
        //
        // This is reported through the probe only. The probe is off unless RPM_PIPE_DIAG is set in the
        // environment. It does not write its own line every second.
        Probe.Work(Probe.Stage.Frame, tw0);
        Probe.Report();
    }

    private void BuildProgram(GL gl)
    {
        string vert = $$"""
            {{_versionDef}}
            precision highp float;
            layout (location = 0) in vec3 aUVS;   // u along length, v = row (front..back), side = +/-1
            uniform float iTime;
            uniform vec2  iResolution;
            uniform float iSwell;                  // CPU-enveloped macro swell (fast rise, slow settle)
            uniform sampler2D iAudio;              // row 0 (v=0.25) = spectrum
            uniform sampler2D iRopeHist;           // scrolling high-band history: x=0 now (left), x=1 past (right)
            out float vAcross;                     // -1..1 across the ribbon, for the glow profile
            out float vBright;

            // Open sea: a few long directional rollers summed. Deliberately low wavenumbers so each line
            // carries only a couple of gentle, flowing curves, not a field of little bumps. Zero-mean.
            float sea(vec2 p, float t)
            {
                float h = 0.0;
                h += 0.50 * sin(dot(normalize(vec2( 1.00, 0.30)), p) * 1.1 - t * 0.55);
                h += 0.28 * sin(dot(normalize(vec2( 0.60,-0.80)), p) * 1.9 - t * 0.85);
                h += 0.16 * sin(dot(normalize(vec2(-0.35, 1.00)), p) * 3.2 - t * 1.15);
                return h;
            }

            void main()
            {
                float u = aUVS.x, v = aUVS.y, side = aUVS.z;

                // World: rows recede along Z, displacement is up (Y). Widen the rows toward the BACK so the far
                // lines still run off the window sides: perspective pulls their ends inward otherwise, and the
                // end-fade then shows as a black gap. Now every line finishes at the edges like the front ones.
                float wz = (v - 0.5) * 2.2;
                float wx = (u - 0.5) * (3.0 + v * 3.4);

                // Macro sea, driven by the CPU-enveloped swell (iSwell): rises fast, settles slow with a
                // cooldown, so the big motion is smooth and never twitches. Breathing swells the resting
                // amplitude floor with a slow LFO, and an additive phase wobble eases the roll's pace, so the
                // whole sea rises and relaxes like breath rather than travelling at a constant rate.
                float breath = 0.60 + 0.25 * sin(iTime * 0.42) + 0.15 * sin(iTime * 0.27 + 1.3);
                float t = iTime * 0.2 + 0.18 * sin(iTime * 0.33);
                float wy = sea(vec2(wx, wz), t) * (0.16 * breath + iSwell * 1.25);

                // Rope wrinkles that are BORN at the left from the music of the instant, then flow right along
                // the line like a wave sent down a rope. iRopeHist is a scrolling record of the recent high-band
                // energy (x=0 = now at the left edge, x=1 = the past at the right edge). Sampling it at u delays
                // the amplitude by travel time, so a crest carries the music value from when it left the left
                // edge. The carrier sines give the crest its fast shape; per-row phase (wz) breaks lockstep.
                // Wrinkles must read like WATER: neighbouring lines' crests have to LINE UP, not cross. So the
                // carrier is ONE coherent travelling field shared by every row (same wavelength for all; a
                // small wz term shifts the phase gently row-to-row, tilting the crests into a slow diagonal
                // wavefront that drifts over time). Only the AMPLITUDE varies per row: each depth reads its own
                // band (bass at the BACK, treble at the FRONT) delayed by u, so a packet is born at the left
                // and flows right. Same phase everywhere means the peaks and troughs stay aligned like a swell.
                float fcoord = 1.0 - v;                              // front -> treble (top row), back -> bass
                float born  = texture(iRopeHist, vec2(u, fcoord)).x; // this row's band, travel-delayed
                float ramp  = mix(0.06, 0.15, v);                    // treble small (front) .. bass big (back)
                float carrier = sin(wx * 13.0 + wz * 1.6 - iTime * 6.0)
                              + sin(wx * 20.0 + wz * 2.4 - iTime * 8.4) * 0.5;
                wy += carrier * (0.003 + born * ramp);   // coherent field, per-row amplitude from the music

                // Real pinhole camera raised above the sea and pitched ~30 deg downward so we look ONTO the
                // plane: the swell reads as height and the bands recede toward the horizon. Camera sits on a
                // circle at (0, camY, camZ), aimed at the origin; rotate each point into camera space, project.
                float focal = 2.6;
                float pitch = radians(20.0);                     // look-down angle
                float cp = cos(pitch), sp = sin(pitch);
                float dist = 3.2;
                float camY = dist * sp, camZ = -dist * cp;
                float dx = wx;
                float dy = wy - camY;
                float dz = wz - camZ;                            // > 0
                float cy =  dy * cp + dz * sp;                   // camera-space up
                float cz = -dy * sp + dz * cp;                   // camera-space depth (> 0)
                float sx = dx * focal / cz;
                float sy = cy * focal / cz;

                // Aspect-correct (square control -> 1) and expand the line into a thin screen-space ribbon.
                sx *= iResolution.y / iResolution.x;
                sy += side * 0.048;

                gl_Position = vec4(sx, sy, 0.0, 1.0);
                vAcross = side;
                // Fade the line ends so they dissolve into the sea instead of terminating on a hard plane
                // edge: the lit strip is a window onto a wider ocean that carries on past where the lines stop.
                float endFade = smoothstep(0.0, 0.08, u) * smoothstep(1.0, 0.92, u);
                vBright = (0.45 + 0.55 * (1.0 - v)) * (0.8 + 0.5 * iSwell) * endFade;   // front + loud = brighter
            }
            """;

        string frag = $$"""
            {{_versionDef}}
            precision highp float;
            in float vAcross;
            in float vBright;
            out vec4 fragColor;
            void main()
            {
                float g = 1.0 - abs(vAcross);          // 1 at the line centre, 0 at the ribbon edge
                g = g * g;
                float core = pow(g, 6.0);              // white-hot filament
                vec3 col = core * vec3(0.80, 0.95, 1.0) + g * vec3(0.0, 0.34, 1.0);
                fragColor = vec4(col * vBright, 1.0);
            }
            """;

        // A bad shader must not bring the app down. The error is caught and _program stays at 0. The render
        // loop then draws nothing, and the exception does not escape.
        try
        {
            uint vs = Compile(gl, ShaderType.VertexShader, GlslSource.Ascii(vert));
            uint fs = Compile(gl, ShaderType.FragmentShader, GlslSource.Ascii(frag));
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
                LastError = "link: " + gl.GetProgramInfoLog(program);
                gl.DeleteProgram(program);
            }
            else
            {
                _program = program;
                LastError = null;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        DispatcherQueue?.TryEnqueue(() => CompileStateChanged?.Invoke());
    }

    private static uint Compile(GL gl, ShaderType type, string src)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok != (int)GLEnum.True)
            throw new InvalidOperationException($"{type} compile: {gl.GetShaderInfoLog(s)}");
        return s;
    }
}
