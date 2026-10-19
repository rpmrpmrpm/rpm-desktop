using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// A plate of sand, and the music playing it.
///
/// This follows Ernst Chladni's experiment. Sand is scattered on a metal plate, and the plate is sounded.
/// The grains move away from the places where the plate is moving, and they gather along the lines where
/// it stays still. The figure that appears is the sound itself, shown by the only thing that stays still.
///
/// A plate does not vibrate in one mode only. It has a whole set of modes, and real sound excites several at
/// once. So the plate's displacement is the sum of its modes, each weighted by how much of the music is at
/// its frequency. The sand settles on the zero lines of that sum. A bass line and a hi-hat excite different
/// modes than a dense chord does. So the figure depends on the balance of this spectrum and no other. Every
/// piece of music draws its own plate, and the plate changes as the arrangement changes.
///
/// There is no interpretation and no decoration here. The spectrum weights the modes. The grains move
/// downhill on the summed standing wave. The level sets how hard they are thrown. The result follows
/// directly from that physics:
///
/// - A steady passage settles into a clean figure, because the sand has time to reach the nodal lines.
/// - A change of arrangement excites different modes, so the whole figure dissolves and forms again.
/// - A loud passage throws the grains into a haze before they fall back.
/// - In silence the sand stays where it lies. Nothing breathes, pulses or fades in. The last figure stays
///   on screen, and that is how silence looks.
///
/// There is no colour, no glow and no camera movement. White sand on a black plate, seen from directly
/// above, because the figure is the whole subject. Anything else would be decoration on top of the physics.
/// </summary>
public sealed partial class ChladniStage : GlCanvas, IVizStage
{
    private const int Grains = 60000;

    private readonly ChladniPlate _plate = new(Grains, SpectrumAnalyzer.FrameSize);
    private readonly float[] _wave = new float[SpectrumAnalyzer.FrameSize];
    private readonly Stopwatch _clock = new();

    private RPaMpModel? _model;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdog;
    private bool _running;
    private bool _glReady;
    private double _lastAnalysis, _lastFrame;

    private uint _vao, _vbo, _program;
    private int _uPointSize, _uInk, _uRes;
    private string _versionDef = "#version 300 es";
    private bool _desktopGl;

    // Two desktop GL switches control drawing points as sprites. OpenGL ES does not have them, because
    // both are always on there. They are written as numbers, because the bindings do not include them
    // in every EnableCap.
    private const EnableCap ProgramPointSize = (EnableCap)0x8642;
    private const EnableCap PointSprite = (EnableCap)0x8861;

    public ChladniStage() : base(static () => Common.GlHost.Window())
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
        _desktopGl = !slVersion.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        _versionDef = _desktopGl ? "#version 330" : "#version 300 es";
        RendererInfo = $"{gl.GetStringS(StringName.Renderer)} - {gl.GetStringS(StringName.Version)}";
        DispatcherQueue?.TryEnqueue(() => CompileStateChanged?.Invoke());

        BuildProgram(gl);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, _plate.Positions, BufferUsageARB.StreamDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }
        gl.EnableVertexAttribArray(0);

        _uPointSize = gl.GetUniformLocation(_program, "uPointSize");
        _uInk = gl.GetUniformLocation(_program, "uInk");
        _uRes = gl.GetUniformLocation(_program, "uResolution");

        _glReady = true;
    }



    protected override void OnDestroy(GL gl)
    {
        if (_program != 0) gl.DeleteProgram(_program);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        _program = _vbo = _vao = 0;
        _glReady = false;
    }

    protected override void RenderOverride(GL gl)
    {
        EnsureGlResources(gl);

        double now = _clock.Elapsed.TotalSeconds;
        float dt = _lastFrame > 0 ? (float)Math.Min(0.05, now - _lastFrame) : 0f;
        _lastFrame = now;


        if (now - _lastAnalysis >= 0.025)
        {
            _lastAnalysis = now;

            // The plate listens to the audio that is playing at the playhead. So the figure follows the
            // sound that is heard, and not audio that the decoder has read ahead.
            bool playing = _model is { IsPlaying: true } m && m.CopyPlayFrame(_wave);
            if (!playing) Array.Clear(_wave);
            _plate.Listen(_wave);
        }

        _plate.Settle(dt);

        Span<int> vp = stackalloc int[4];
        gl.GetInteger(GetPName.Viewport, vp);
        float w = Math.Max(1, vp[2]), h = Math.Max(1, vp[3]);

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, _plate.Positions, BufferUsageARB.StreamDraw);

        // The plate background is flat black, with no vignette and no gradient.
        gl.ClearColor(0.012f, 0.013f, 0.017f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // On desktop GL, a point ignores the shader's gl_PointSize unless ProgramPointSize is on. Also,
        // gl_PointCoord is not defined unless PointSprite is on. With both off, every grain reads its
        // coordinate as the corner, is fully transparent, and is discarded. Then only the clear colour
        // is drawn. The switches are set on every frame, not once, because other code shares this
        // context and may change its state.
        if (_desktopGl)
        {
            gl.Enable(ProgramPointSize);
            gl.Enable(PointSprite);
            // A core-profile context always has sprites on, and it refuses the second switch with
            // INVALID_ENUM. The error is read and cleared here, so it is not reported against whatever
            // code runs next.
            _ = gl.GetError();
        }

        if (_program != 0)
        {
            gl.UseProgram(_program);
            // Grains keep the same size on screen whatever the display density is. So the plate looks the
            // same on a high-density display as on any other.
            if (_uPointSize >= 0) gl.Uniform1(_uPointSize, Math.Max(1.6f, h / 560f));
            if (_uInk >= 0) gl.Uniform1(_uInk, 1f);
            if (_uRes >= 0) gl.Uniform2(_uRes, w, h);

            gl.BindVertexArray(_vao);
            gl.DrawArrays(PrimitiveType.Points, 0, Grains);
        }

        gl.Disable(EnableCap.Blend);
    }




    // shaders

    private void BuildProgram(GL gl)
    {
        string vertex = $$"""
            {{_versionDef}}
            precision highp float;
            layout (location = 0) in vec2 aPos;   // plate coordinates, -1..1

            uniform float uPointSize;
            uniform vec2  uResolution;

            void main()
            {
                // The plate is square and sits inside the frame whatever its shape, because a Chladni figure
                // is square and stretching it would be a lie about the physics.
                float s = min(uResolution.x, uResolution.y) / max(uResolution.x, uResolution.y);
                vec2 p = aPos * 0.92;
                if (uResolution.x > uResolution.y) p.x *= s; else p.y *= s;

                gl_PointSize = uPointSize;
                gl_Position = vec4(p, 0.0, 1.0);
            }
            """;

        string fragment = $$"""
            {{_versionDef}}
            precision highp float;

            uniform float uInk;

            out vec4 fragColor;

            void main()
            {
                // A round grain with one soft pixel at its edge. No glow: sand does not glow.
                vec2 d = gl_PointCoord - 0.5;
                float r = length(d) * 2.0;
                float a = (1.0 - smoothstep(0.72, 1.0, r)) * uInk;
                if (a <= 0.002) discard;
                // Faint per grain on purpose: the tone of the figure is how many grains are piled up, so a
                // settled ridge burns white, its edge softens off, and a dusting reads as grey. A grain drawn
                // at full strength makes every line the same flat white whatever the sand is doing.
                fragColor = vec4(vec3(0.94, 0.945, 0.96), a * 0.34);
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
