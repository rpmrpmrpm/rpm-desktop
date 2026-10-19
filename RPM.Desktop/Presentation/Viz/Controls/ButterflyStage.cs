using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// The spectrum, folded twice.
///
/// One quadrant is drawn, and the frame mirrors it: left to right about the middle, and top to bottom about
/// the middle too. So the bass sits in the centre of the picture, and the treble runs out to both edges.
/// The music's effect is symmetrical. That is why the shape looks like a pair of wings opening and closing,
/// and not like a row of bars.
///
/// Inside the quadrant there is only one measurement, and nothing is drawn on top of it. A column's frequency
/// is its distance from the centre, squared. This spends most of the width on the bottom of the spectrum,
/// where most of the music is, and not on the nearly empty top end. The brightness of a column is the distance
/// from the level that its band is sounding at, scaled by that level. So the height that the band reaches is
/// the dark edge, and the light grows away from it. A band with nothing in it leaves its column black. Loud
/// music is bright and wide open. Quiet music is a thin dark line, and silence is a black frame. The picture
/// is the measurement itself, so there is nothing to show when there is nothing to measure.
///
/// The colour is one vertical ramp. It goes from pale lavender in the middle to full blue at the edges. It is
/// gamma encoded, so the ramp rises evenly and does not wash out in its top half.
/// </summary>
public sealed partial class ButterflyStage : GlCanvas, IVizStage
{
    // A full-screen triangle is used instead of a quad. The corners fall outside the viewport and are clipped.
    // It also avoids the seam that a two-triangle quad leaves along its diagonal.
    private static readonly float[] Triangle =
    [
        -1f, -1f,
         3f, -1f,
        -1f,  3f,
    ];

    /// <summary>How often the spectrum is measured again. The picture is redrawn every frame, but running an
    /// FFT every frame means about sixty transforms a second on the UI thread, for a reading that does not
    /// change that fast. 40 readings a second is more than a person can see as separate, and the texture keeps its
    /// value between them.</summary>
    private const double AnalysisInterval = 0.025;

    private readonly ShaderAudioTexture _audio = new(SpectrumAnalyzer.FrameSize);
    private readonly float[] _wave = new float[SpectrumAnalyzer.FrameSize];

    // The spectrum is uploaded as RGBA8. It is the one format that every ES3 driver accepts for
    // TexSubImage, and it can be filtered. ANGLE, which is used on macOS, refuses single-channel R8 for this
    // upload. The audio byte goes in R, which is what the shader reads. G and B are 0, and A is opaque.
    private readonly byte[] _rgba = new byte[ShaderAudioTexture.Width * ShaderAudioTexture.Height * 4];

    private readonly Stopwatch _clock = new();

    private RPaMpModel? _model;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdog;
    private bool _running;
    private bool _glReady;
    private bool _pending;                 // a reading taken but not yet uploaded
    private double _lastAnalysis, _lastFrame;

    private uint _vao, _vbo, _program, _tex;
    private int _uRes, _uSpectrum;
    private string _versionDef = "#version 300 es";

    public ButterflyStage() : base(static () => Common.GlHost.Window())
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

    /// <summary>Creates the GL objects. It is safe to call more than once. It is called from both
    /// <see cref="Init"/> and the first <see cref="RenderOverride"/>. On macOS the first EglMakeCurrent can
    /// fail against a zero-size surface. Uno then skips <c>Init</c> completely, but it still calls
    /// <c>RenderOverride</c>. So the resources must also be created there, once a valid context is current.</summary>
    private void EnsureGlResources(GL gl)
    {
        if (_glReady) return;

        var slVersion = gl.GetStringS(StringName.ShadingLanguageVersion) ?? "";
        _versionDef = slVersion.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase)
            ? "#version 300 es"
            : "#version 330";
        RendererInfo = $"{gl.GetStringS(StringName.Renderer)} - {gl.GetStringS(StringName.Version)}";
        RaiseStateChanged();

        BuildProgram(gl);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, Triangle, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }
        gl.EnableVertexAttribArray(0);

        _tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _tex);
        // Linear filtering is used across the row on purpose. Without it, 512 texels stretched over the width
        // would show steps. The value between two bins is an interpolated reading, not an invented one.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // Uno's own texture uploads can leave a pixel-unpack buffer bound on this shared context. While one is
        // bound, a Tex(Sub)Image from client memory is read as a byte offset into that buffer. The call fails
        // with GL_INVALID_OPERATION, and nothing is uploaded. So the buffer is unbound before every upload.
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            ShaderAudioTexture.Width, ShaderAudioTexture.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _rgba);

        _uRes = gl.GetUniformLocation(_program, "uResolution");
        _uSpectrum = gl.GetUniformLocation(_program, "uSpectrum");

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
        EnsureGlResources(gl);   // Init may have been skipped. See EnsureGlResources.

        double now = _clock.Elapsed.TotalSeconds;
        _lastFrame = now;

        if (now - _lastAnalysis >= AnalysisInterval)
        {
            _lastAnalysis = now;

            // The audio that is playing at the playhead. So the wings respond to the sound that is heard,
            // and not to audio that the decoder has read ahead.
            bool playing = _model is { IsPlaying: true } m && m.CopyPlayFrame(_wave);
            if (!playing) Array.Clear(_wave);
            _audio.Update(_wave);

            var data = _audio.Data;
            for (int i = 0; i < data.Length; i++)
            {
                int j = i * 4;
                _rgba[j] = data[i];
                _rgba[j + 1] = 0;
                _rgba[j + 2] = 0;
                _rgba[j + 3] = 255;
            }
            _pending = true;
        }

        gl.BindTexture(TextureTarget.Texture2D, _tex);
        if (_pending)
        {
            _pending = false;
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);   // Uno can leave a pixel buffer bound. See above.
            gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0,
                ShaderAudioTexture.Width, ShaderAudioTexture.Height,
                PixelFormat.Rgba, PixelType.UnsignedByte, _rgba);
        }

        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        if (_program != 0)
        {
            gl.UseProgram(_program);

            Span<int> vp = stackalloc int[4];
            gl.GetInteger(GetPName.Viewport, vp);
            if (_uRes >= 0) gl.Uniform2(_uRes, (float)Math.Max(1, vp[2]), (float)Math.Max(1, vp[3]));
            if (_uSpectrum >= 0)
            {
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, _tex);
                gl.Uniform1(_uSpectrum, 0);
            }

            gl.BindVertexArray(_vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        // No Invalidate() here, on purpose. The composition tick, with the watchdog, drives the frames.
    }

    // shaders

    private void BuildProgram(GL gl)
    {
        string vertex = $$"""
            {{_versionDef}}
            precision highp float;
            layout (location = 0) in vec2 aPos;
            void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
            """;

        string fragment = $$"""
            {{_versionDef}}
            precision highp float;

            uniform vec2      uResolution;
            uniform sampler2D uSpectrum;   // 512x2: row 0 the spectrum, row 1 the waveform

            out vec4 fragColor;

            // The ramp is written in the light the eye reads, so it is encoded before it is handed over.
            // Mixing in linear and leaving it there sends the middle of the ramp out far too bright.
            vec3 tint(float y)
            {
                vec3 middle = vec3(170.0, 170.0, 210.0) / 255.0;   // pale lavender, through the centre
                vec3 edge   = vec3( 51.0, 153.0, 255.0) / 255.0;   // full blue, at the top and bottom
                vec3 c = mix(middle, edge, y);
                return pow(c, vec3(1.0 / 2.2));
            }

            void main()
            {
                // Folded twice: 0 at the centre of the frame and 1 at either edge, on both axes. One
                // quadrant is drawn and the frame shows it four times, mirrored.
                vec2 uv = abs((gl_FragCoord.xy / uResolution - 0.5) * 2.0);

                // Squared, so the width is spent on the bottom of the spectrum where music is, rather
                // than half of it on a top end that is nearly always empty.
                float band = uv.x * uv.x;

                // 0.25 is the centre of row 0, the spectrum row.
                float level = texture(uSpectrum, vec2(band, 0.25)).x;

                // Distance from the level this band is sounding at, scaled by that level. The level line
                // is the dark edge, the light grows away from it, and a band with nothing in it is black.
                fragColor = vec4(abs(uv.y - level) * level * tint(uv.y), 1.0);
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
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => DispatcherQueue?.TryEnqueue(() => CompileStateChanged?.Invoke());
}
