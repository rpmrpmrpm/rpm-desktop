using System.Diagnostics;
using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Audio;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// Everything the marched visualizers have in common, which is everything except the shader.
///
/// <para>
/// What makes them visualizers rather than demos is where their numbers come from. A demo cuts on its own
/// clock, against the one piece of music it was made for. These cut on the track itself. The camera holds a
/// shot for two bars and cuts on a beat. The room changes on the eight-bar phrase. The flight jumps forward
/// on each beat and then eases into the next one. A drop opens the whole picture out. All of this is worked
/// out by <see cref="VizDirector"/>, from the playing position and the track's own beat grid
/// (<see cref="TrackAnalysis.BeatTimes"/>). So it is calculated, never accumulated. A seek lands on the picture
/// that the same instant would have had anyway. A track that nobody has analysed still moves, on a clock that
/// stands in for the beat grid it does not have.
/// </para>
///
/// <para>The colour comes from the sound. The three bands of the live spectrum light the picture, and the
/// section's own palette sets the colours. The drawing is done by <see cref="MarchPass"/>. It uses an
/// off-screen buffer at a fraction of the surface size, then bloom and tone map onto the screen.</para>
///
/// <para>A subclass supplies its fragment shader, says which uniforms it wants, and fills them in. It does
/// not touch the GL lifecycle, the deck, the spectrum or the framebuffer.</para>
/// </summary>
public abstract class MarchedStage : GlCanvas, IVizStage
{
    /// <summary>How often the spectrum is measured again. The picture is redrawn every frame. But an FFT every
    /// frame would be about sixty transforms a second, for a reading that does not change that fast.</summary>
    private const double AnalysisInterval = 0.025;

    private readonly ShaderAudioTexture _audio = new(SpectrumAnalyzer.FrameSize);
    private readonly float[] _wave = new float[SpectrumAnalyzer.FrameSize];
    private readonly Stopwatch _clock = new();
    private readonly MarchPass _pass = new();
    private readonly Dictionary<string, int> _where = new(StringComparer.Ordinal);

    private RPaMpModel? _model;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdog;
    private bool _running, _glReady;
    private double _lastFrame, _lastAnalysis;

    // The track being flown through, and what is known about it. It is looked up when the track changes.
    private string? _trackPath;
    private TrackAnalysis? _analysis;

    private uint _march;
    private string _versionDef = "#version 300 es";

    protected MarchedStage() : base(static () => Common.GlHost.Window())
    {
        Unloaded += (_, _) => Stop();
    }

    public string? RendererInfo { get; private set; }
    public string? LastError { get; private set; }
    public event Action? CompileStateChanged;

    /// <summary>The three bands, with an envelope: they rise fast and fall slowly. So the picture lights up
    /// on a hit and then cools down.</summary>
    protected float Bass { get; private set; }
    protected float Mid { get; private set; }
    protected float Air { get; private set; }

    /// <summary>How much halo the screen pass adds around what this stage drew.</summary>
    protected virtual float Bloom => 0.6f;

    /// <summary>How far the screen pass pulls the colour back out after the tone map. A value of one leaves
    /// the colour as the tone curve made it. On a bright picture that is paler than what was drawn.</summary>
    protected virtual float Saturate => 1.0f;

    /// <summary>This stage's fragment shader. It is a whole program. It uses the uniforms that it names in
    /// <see cref="CacheUniforms"/>.</summary>
    protected abstract string MarchSource(string versionDef);

    /// <summary>Called once for each program. It looks up the uniforms that this stage uses, with
    /// <see cref="Where"/>.</summary>
    protected abstract void CacheUniforms(GL gl, uint program);

    /// <summary>Called once per frame, with the shot that the director chose and the size of the buffer being
    /// marched into. It fills in the uniforms and draws nothing.</summary>
    protected abstract void Upload(GL gl, in VizShot shot, int width, int height, int steps);

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

        try
        {
            _pass.Ensure(gl, _versionDef);
            _march = MarchPass.Link(gl, MarchPass.Vertex(_versionDef), MarchSource(_versionDef));
            _where.Clear();
            CacheUniforms(gl, _march);
            SetError(null);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }

        _glReady = true;
    }

    protected override void OnDestroy(GL gl)
    {
        if (_march != 0) gl.DeleteProgram(_march);
        _march = 0;
        _pass.Destroy(gl);
        _where.Clear();
        _glReady = false;
    }

    protected override void RenderOverride(GL gl)
    {
        EnsureGlResources(gl);   // Init may have been skipped. See EnsureGlResources.

        double now = _clock.Elapsed.TotalSeconds;
        _pass.Govern(now - _lastFrame);
        _lastFrame = now;

        if (now - _lastAnalysis >= AnalysisInterval)
        {
            _lastAnalysis = now;
            FollowTheDeck();
            Listen();
        }

        var shot = Direct();
        _pass.Render(gl, _march, (w, h) => Upload(gl, shot, w, h, _pass.Steps), Bloom, Saturate);

        // No Invalidate() here, on purpose. The composition tick, with the watchdog, drives the frames.
    }

    // what the record says

    /// <summary>
    /// The analysis of the track that is playing. It is read again only when the track changes. Only cached
    /// results are used. This asks the service for what it already has, and it takes null when there is no
    /// answer. A visualizer that could start a decode would start one for every track that somebody skips through.
    /// </summary>
    private void FollowTheDeck()
    {
        string? path = _model?.CurrentPath;
        if (path == _trackPath)
            return;

        _trackPath = path;
        _analysis = null;
        if (path is null)
            return;

        try
        {
            _analysis = App.Services.GetRequiredService<TrackAnalysisService>().Cached(path);
        }
        catch
        {
            // No library is open, or the mirror does not answer. The stand-in clock is used instead.
        }
    }

    /// <summary>The shot for this instant. It uses the track's own beat grid where one exists.</summary>
    private VizShot Direct()
    {
        // The playhead is used when there is one. Otherwise the wall clock is used. So the picture still moves
        // while the deck is empty and the gallery is open.
        double seconds = _model is { IsPlaying: true } m ? m.Position : _clock.Elapsed.TotalSeconds;

        var clock = BeatClock.For(_analysis, seconds);
        double bpm = _analysis?.Bpm ?? 0;
        double sinceDrop = _analysis is { } a
            ? VizDirector.BeatsSinceDrop(a.DropSeconds, seconds, bpm)
            : -1;

        return VizDirector.Shot(clock, bpm, sinceDrop);
    }

    /// <summary>Bass, middle and air. They are read from the spectrum row, not from the raw bins. So the
    /// lighting follows loudness the way the ear hears it.</summary>
    private void Listen()
    {
        bool playing = _model is { IsPlaying: true } m && m.CopyPlayFrame(_wave);
        if (!playing) Array.Clear(_wave);
        _audio.Update(_wave);

        var data = _audio.Data;
        float bass = Band(data, 0, 24), mid = Band(data, 24, 140), air = Band(data, 140, 400);

        // Rise fast, fall slowly. A hit should show on the frame where it happens, and fade over the next
        // few frames.
        Bass = Math.Max(bass, Bass * 0.86f);
        Mid = Math.Max(mid, Mid * 0.90f);
        Air = Math.Max(air, Air * 0.92f);

        static float Band(byte[] spectrum, int from, int to)
        {
            int sum = 0;
            for (int x = from; x < to; x++) sum += spectrum[x];
            return sum / (255f * (to - from));
        }
    }

    // uniforms

    /// <summary>Looks up and stores the location of each uniform in the stage's program. A name that the
    /// compiler removed as unused is stored as -1, and it is skipped when a value is set.</summary>
    protected void Where(GL gl, uint program, params string[] names)
    {
        foreach (var name in names)
            _where[name] = gl.GetUniformLocation(program, name);
    }

    protected void Set1(GL gl, string name, float a)
    {
        if (_where.TryGetValue(name, out int at) && at >= 0) gl.Uniform1(at, a);
    }

    protected void Set2(GL gl, string name, float a, float b)
    {
        if (_where.TryGetValue(name, out int at) && at >= 0) gl.Uniform2(at, a, b);
    }

    protected void Set3(GL gl, string name, float a, float b, float c)
    {
        if (_where.TryGetValue(name, out int at) && at >= 0) gl.Uniform3(at, a, b, c);
    }

    protected void Set4(GL gl, string name, float a, float b, float c, float d)
    {
        if (_where.TryGetValue(name, out int at) && at >= 0) gl.Uniform4(at, a, b, c, d);
    }

    /// <summary>The room that the walls are in on this frame. It is the section's room, slid into over its first
    /// two beats. The fold count is left out. Half a fold leaves a seam, so a stage that folds uses the whole
    /// count from <see cref="VizShot.Now"/>.</summary>
    protected static VizRoom Blend(in VizRoom was, in VizRoom now, float t) => new(
        Folds: now.Folds,
        Arm: Lerp(was.Arm, now.Arm, t),
        Twist: Lerp(was.Twist, now.Twist, t),
        Ring: Lerp(was.Ring, now.Ring, t),
        Girth: Lerp(was.Girth, now.Girth, t),
        Cross: Lerp(was.Cross, now.Cross, t));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void SetError(string? error)
    {
        if (error == LastError) return;
        LastError = error;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => DispatcherQueue?.TryEnqueue(() => CompileStateChanged?.Invoke());
}
