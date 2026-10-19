using System.Diagnostics;
using System.Numerics;
using RPM.Core.Audio;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The room that the record plays in, painted in the record's own colours.
///
/// <para>The room draws two things, and only two. The rest of this visualizer is the sleeve itself, and the
/// sleeve is drawn over the top (<see cref="PegasusChrome"/>). The two things are the ones that must react to
/// sound sixty times a second, which is what GL is for. A third thing is drawn for a few records only. See
/// <b>the mirror ball</b> at the foot of this list.</para>
///
/// <para><b>The figures.</b> Six soft lobes drift on their own slow phases. The whole plane is warped twice
/// before any lobe is measured. So six circles would look like smoke, cloth or nothing with a name, which is
/// intended. The low end swells them. A kick pushes the field outward, and the field falls back. Each lobe has
/// its own phase, so the lobes never breathe in step. Their colour comes from the cover: the dominant tone and
/// the accent. The colour eases toward the new values over about a second, so a change of track dissolves the
/// room instead of cutting it.</para>
///
/// <para><b>The spectrum.</b> Log-spaced bands, mirrored about the middle of the screen. The run goes from
/// behind the record out to the right edge. Bass is at the record and the top end is at the rim. Each band
/// eases toward its reading: fast up, slow down. Each bar reads its own band from a texture with nearest
/// filtering, so a bar is not blended into its neighbours.</para>
///
/// <para><b>The mirror ball.</b> Off for almost every record. It is worth its place on the few records it is
/// on (<see cref="MirrorBall"/> decides which; <see cref="SetMirrorBall"/> is how the stage is told). It is a
/// grid of small hard specks, one to a cell. Each speck stays dark until its facet swings past the light. The
/// low end opens the specks out and blooms them, and the whole picture gives off a soft haze for as long as a
/// ball hangs. The ball is a uniform inside the one shader, not a separate program or surface. So a record
/// without a ball pays one comparison for the whole layer. The ball must not light the words. The chrome says
/// where it is writing (<see cref="SetWordBox"/>), and the ball clears out of that box. A hard white speck on
/// text would make the text unreadable.</para>
///
/// <para>Nothing here draws the cover, the record or the words. Those are XAML over this surface. The record
/// on this screen is the same control that turns in the header, with the same pressing and the same disc art.</para>
/// </summary>
public sealed partial class PegasusStage : GlCanvas, IPegasusRoom
{
    /// <summary>What the room hears: the three envelopes it is lit by, and the run of log-spaced bands
    /// that the spectrum is cut from. All of it is in Core. The phone's room is drawn by a different
    /// compiler on a different graphics stack. It reads the music with the same arithmetic, so the two
    /// rooms cannot drift apart.</summary>
    private readonly RoomBands _room = new(RPM.Desktop.Services.Audio.AudioEngine.Rate);

    /// <summary>The run as GL takes it: one byte of level per band, in R.</summary>
    private readonly byte[] _bandRgba = new byte[RoomBands.Bands * 4];

    private readonly Stopwatch _clock = new();

    private RPaMpModel? _model;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdog;
    private bool _running;
    private bool _glReady;
    private double _lastAnalysis, _lastFrame;

    // The room's colours, and the colours it is heading toward. They are eased, not set at once, so a
    // track change is a dissolve. The palette arrives in one step (the cover is decoded once), and easing
    // hides that step.
    private RoomPalette _palette = RoomPalette.Fallback, _paletteTo = RoomPalette.Fallback;

    // How far the room has drawn in to the middle: 0 for the spread-out composition, 1 for the bare one.
    // It eases rather than switching, so the spectrum leaves and the left of the room comes back over about
    // half a second.
    private float _bare, _bareTo;

    // Whether a mirror ball hangs in the room: 0 for none, 1 for one. This follows the palette's ease, not
    // the composition's. A ball belongs to the record, so it should arrive as the room takes on the colour
    // of the sleeve that calls for it.
    private float _ball, _ballTo;

    // The ball's own clock, in seconds. The room's clock runs at one second a second. This clock does not:
    // it runs faster on the low end, so the ball turns faster under a kick (see BallSpinBass).
    //
    // It is added up per frame rather than worked out from the wall clock. This choice is essential.
    // Everything the ball does is a function of this value. A rate written as `t * speed` is a phase that
    // jumps the instant the speed changes. Every facet would then be somewhere else between one frame and
    // the next, so a kick would cut to another frame of the ball instead of turning it. Adding up the
    // elapsed angle lets the rate change freely, and the phase it produces stays continuous.
    //
    // It is a double, because it is added to sixty times a second for as long as the record is shown. A
    // float accumulator visibly steps within an hour, and the whole layer is drawn from this clock.
    private double _ballSpin;

    // The area that the chrome over the room is writing in, so the ball can keep its light off the words.
    // The box is in fractions of the window, measured from its top-left corner. That is the chrome's own way
    // of thinking about it, and the shader flips it once. The box is handed in rather than worked out here,
    // because only layout knows where a title ends up.
    //
    // There are two figures, not one box that is allowed to go empty. A box eased down to nothing would not
    // fade. Its corners would walk toward the top-left of the screen, and the clear area would wipe across
    // the room on the way out. The geometry only ever eases between places where words really are. Whether
    // there are any words at all is the second figure's job.
    private Vector4 _wordBox, _wordBoxTo;
    private float _words, _wordsTo;

    private uint _vao, _vbo, _program, _bandTex;
    private int _uRes, _uTime, _uBass, _uMid, _uAir, _uGround, _uTint, _uGlow, _uShade, _uBands, _uBare, _uBall;
    private int _uBallSpin, _uWordBox, _uWords;
    private string _versionDef = "#version 300 es";


    public PegasusStage() : base(static () => Common.GlHost.Window())
    {
        Unloaded += (_, _) => Stop();
    }

    public string? RendererInfo { get; private set; }
    public string? LastError { get; private set; }
    public event Action? CompileStateChanged;

    /// <summary>
    /// The low end, enveloped. This is the same figure that the room is lit by.
    ///
    /// <para>It is lent out so the chrome over this stage can be lit by the same beat, rather than measuring
    /// the music a second time. It is already the right number for lighting. It is a held peak with a fast
    /// attack and a slow release. A kick pushes it up and it falls back slowly. The raw level is not used,
    /// because it flickers at whatever rate the drummer is playing.</para>
    /// </summary>
    public float Bass => _room.Bass;

    public void SetActive(bool on)
    {
        if (on) Start(); else Stop();
    }

    /// <summary>
    /// Which composition the stage is drawing for.
    ///
    /// <para>Only the first composition asks something of this stage that the other two do not. The spectrum
    /// and the dim down the left belong to <see cref="PegasusView.Spread"/> alone. The spectrum is a reading
    /// rather than a picture, and it runs out to the edge that the other two compositions try to clear. The
    /// dim holds one side of the room back under the words. In the other two, the words are either under the
    /// cover or gone. So <see cref="PegasusView.Centred"/> and <see cref="PegasusView.Room"/> are the same
    /// stage. What separates them is only what the chrome draws over it, which is nothing at all for the
    /// last one.</para>
    ///
    /// <para>When <paramref name="immediate"/> is true, the change happens at once, without the ease. Use
    /// it when the stage is switched on already in this state, rather than changed into it while it is
    /// being watched.</para>
    /// </summary>
    public void SetView(PegasusView view, bool immediate = false)
    {
        _bareTo = view == PegasusView.Spread ? 0f : 1f;
        if (immediate) _bare = _bareTo;
    }

    /// <summary>
    /// The colours that this track paints the room in: the cover's dominant tone and its vivid accent, as the
    /// art display reads them from the sleeve. Either may be null. A black-and-white photograph has no colour
    /// to give, and then the room keeps the app's own blue.
    ///
    /// <para>The stage only receives these values. It never decodes a cover. The host hands them over from the
    /// decode the header has already done.</para>
    /// </summary>
    public void SetPalette(Windows.UI.Color? tint, Windows.UI.Color? glow, Windows.UI.Color? shade)
        => _paletteTo = RoomPalette.From(Rgb(tint), Rgb(glow), Rgb(shade));

    private static Vector3? Rgb(Windows.UI.Color? c)
        => c is { } v ? new Vector3(v.R / 255f, v.G / 255f, v.B / 255f) : null;

    /// <summary>
    /// Hangs a mirror ball in the room, or takes it down. This stage does not decide which records get one.
    /// See <see cref="MirrorBall"/> and the host that asks for it.
    ///
    /// <para>The stage eases toward the value. It does not swap one program for another. The shader is built
    /// once, and the ball is a uniform inside it. So a record that turns the ball on costs no recompile, no
    /// second surface, and no branch that half the screen takes and half does not. The ball eases in on the
    /// palette's ease, so it dissolves in with the sleeve's own colours.</para>
    /// </summary>
    public void SetMirrorBall(bool on) => _ballTo = on ? 1f : 0f;

    /// <summary>
    /// Where the words are, so the ball's light can be kept off them. See <see cref="IPegasusRoom"/> for what
    /// the box means, and why the room has to be told rather than working it out.
    ///
    /// <para>An empty box removes the clear area without moving it. The geometry stays where it was, and only
    /// the presence eases down. So when the words leave a composition, no dark patch is dragged across the
    /// room behind them.</para>
    /// </summary>
    public void SetWordBox(double left, double top, double right, double bottom)
    {
        bool any = right > left && bottom > top;
        _wordsTo = any ? 1f : 0f;
        if (any)
            _wordBoxTo = new Vector4((float)left, (float)top, (float)right, (float)bottom);
    }

    private void Start()
    {
        if (_running) return;
        _model ??= App.Services.GetRequiredService<RPaMpService>().Transport;
        _room.Tap = _model.CopyPlayFrame;
        _running = true;
        if (!_clock.IsRunning) _clock.Start();
        CompositionTarget.Rendering += OnCompositionTick;
        Services.Shell.FrameWatch.Began("pegasus-stage");

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
        Services.Shell.FrameWatch.Ended("pegasus-stage");
        _watchdog?.Stop();
        _clock.Stop();
    }

    /// <summary>The frame source: the window's composition tick. It is not a self-Invalidate from inside
    /// RenderOverride, because those requests are coalesced away while the tree is busy. A watchdog covers a
    /// frame that goes missing anyway.</summary>
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

    // The room does not answer here. Answering here causes the first-launch flicker.
    //
    // The answer lifts the black cover off the view (see PegasusPlayer.OnStageAnswered). At this point the room
    // has only set itself up. The shaders below have not been compiled by the driver yet. That is the slowest
    // step on a cold driver, and no pixel has been drawn. An answer now would lift the cover off an empty
    // surface. The surface would stay unpainted for as long as the compile took, and then the room would appear
    // in one frame. Lifting a cover to reveal nothing, and then filling it in, is the flicker.
    //
    // The room answers from the first frame it has actually painted. See the end of RenderOverride. A room that
    // cannot compile still answers at once, through SetError. A room that will never paint must not hold the
    // cover for its full four seconds.

        BuildProgram(gl);

        // One quad, already in clip space: everything about this picture is worked out per fragment.
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

        _bandTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _bandTex);
        // Nearest filtering, because there is exactly one bar per band. A bar must read its own band and
        // nothing from the two beside it. Linear filtering blended each bar into its neighbours. Neighbours
        // that share their values stand at the same height, so the run came out in blocks.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // Uno leaves a pixel unpack buffer bound, and it would take the upload.
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            RoomBands.Bands, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, _bandRgba);

        _uRes = gl.GetUniformLocation(_program, "uResolution");
        _uTime = gl.GetUniformLocation(_program, "uTime");
        _uBass = gl.GetUniformLocation(_program, "uBass");
        _uMid = gl.GetUniformLocation(_program, "uMid");
        _uAir = gl.GetUniformLocation(_program, "uAir");
        _uGround = gl.GetUniformLocation(_program, "uGround");
        _uTint = gl.GetUniformLocation(_program, "uTint");
        _uGlow = gl.GetUniformLocation(_program, "uGlow");
        _uShade = gl.GetUniformLocation(_program, "uShade");
        _uBands = gl.GetUniformLocation(_program, "uBands");
        _uBare = gl.GetUniformLocation(_program, "uBare");
        _uBall = gl.GetUniformLocation(_program, "uBall");
        _uBallSpin = gl.GetUniformLocation(_program, "uBallSpin");
        _uWordBox = gl.GetUniformLocation(_program, "uWordBox");
        _uWords = gl.GetUniformLocation(_program, "uWords");

        _glReady = true;
    }

    protected override void OnDestroy(GL gl)
    {
        if (_program != 0) gl.DeleteProgram(_program);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        if (_bandTex != 0) gl.DeleteTexture(_bandTex);
        _program = _vbo = _vao = _bandTex = 0;
        _glReady = false;

        // A lost context takes the room's answer with it. The next context has to compile its own shaders and
        // paint its own first frame. Anything waiting on the room should wait for that, and not be told about a
        // picture the last context drew.
        _painted = false;
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

        // The room changes colour over about a second. The easing does not depend on the frame rate, so it takes
        // the same time on a slow machine as on a fast one.
        float k = (float)(1.0 - Math.Exp(-dt * 2.4));
        _palette = _palette.EasedTo(_paletteTo, k);
        _ball += (_ballTo - _ball) * k;

        // Faster than the colours. A track change is a dissolve and should take its time. A button press is an
        // instruction, so the change must show quickly.
        float c = (float)(1.0 - Math.Exp(-dt * 5.5));
        _bare += (_bareTo - _bare) * c;

        // The clear area under the words moves with the composition, so it uses the composition's own rate. The
        // column changes place between the two views. If the clear area arrived after the words, the words would
        // stand in the light for a moment.
        _wordBox += (_wordBoxTo - _wordBox) * c;
        _words += (_wordsTo - _words) * c;

        // The ball turns, and turns faster under a kick. Its clock is added up here, not read from the room's
        // clock, so the rate can change without the phase jumping. See _ballSpin.
        _ballSpin += dt * (1.0 + BallSpinBass * _room.Bass);

        Span<int> vp = stackalloc int[4];
        gl.GetInteger(GetPName.Viewport, vp);
        float w = Math.Max(1, vp[2]), h = Math.Max(1, vp[3]);

        gl.ClearColor(_palette.Ground.X, _palette.Ground.Y, _palette.Ground.Z, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);

        if (_program != 0)
        {
            gl.UseProgram(_program);
            if (_uRes >= 0) gl.Uniform2(_uRes, w, h);
            if (_uTime >= 0) gl.Uniform1(_uTime, (float)now);
            if (_uBass >= 0) gl.Uniform1(_uBass, _room.Bass);
            if (_uMid >= 0) gl.Uniform1(_uMid, _room.Mid);
            if (_uAir >= 0) gl.Uniform1(_uAir, _room.Air);
            if (_uGround >= 0) gl.Uniform3(_uGround, _palette.Ground.X, _palette.Ground.Y, _palette.Ground.Z);
            if (_uTint >= 0) gl.Uniform3(_uTint, _palette.Tint.X, _palette.Tint.Y, _palette.Tint.Z);
            if (_uGlow >= 0) gl.Uniform3(_uGlow, _palette.Glow.X, _palette.Glow.Y, _palette.Glow.Z);
            if (_uShade >= 0) gl.Uniform3(_uShade, _palette.Shade.X, _palette.Shade.Y, _palette.Shade.Z);
            if (_uBare >= 0) gl.Uniform1(_uBare, _bare);
            if (_uBall >= 0) gl.Uniform1(_uBall, _ball);
            if (_uBallSpin >= 0) gl.Uniform1(_uBallSpin, (float)_ballSpin);
            if (_uWordBox >= 0) gl.Uniform4(_uWordBox, _wordBox.X, _wordBox.Y, _wordBox.Z, _wordBox.W);
            if (_uWords >= 0) gl.Uniform1(_uWords, _words);
            if (_uBands >= 0)
            {
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, _bandTex);
                gl.Uniform1(_uBands, 0);
            }

            gl.BindVertexArray(_vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

            // The room is lit, and this is the first moment it can say so. Everything waiting on it, first of all
            // the black cover over the view, has been waiting for pixels, not for a signal. The flag makes this
            // happen once. Without it, every frame would make a dispatcher hop.
            if (!_painted)
            {
                _painted = true;
                DispatcherQueue?.TryEnqueue(() => CompileStateChanged?.Invoke());
            }
        }
        // No Invalidate() here: the composition tick is the frame source.
    }

    /// <summary>Whether a frame has been drawn. CompileStateChanged waits for this. It is not raised when the
    /// context is merely created. See EnsureGlResources. It is written on the render thread and read on the
    /// layout thread. For a bool, saying so is enough.</summary>
    private volatile bool _painted;

    /// <summary>Whether the room has anything on it yet. A second visit asks this, rather than waiting for an
    /// answer it has already had. It is this and not <see cref="RendererInfo"/>, which is set when the context
    /// is made and says nothing about whether the room was ever lit.</summary>
    public bool HasPainted => _painted;

    /// <summary>Reads the playing block and hands the run over to GL. The measurement itself belongs to
    /// <see cref="RoomBands"/>. The three windows, the fold and the gains are all there, and the phone's room
    /// reads them too.</summary>
    private void Analyse(GL gl)
    {
        _room.Update(_clock.Elapsed.TotalSeconds, _model is { IsPlaying: true });

        // One byte of level per band, in R. The texture is read with nearest filtering, and a bar is drawn per
        // column. So a bar reads its own band and nothing from the two beside it.
        var run = _room.Run;
        for (int band = 0; band < RoomBands.Bands; band++)
        {
            int j = band * 4;
            _bandRgba[j] = (byte)(Math.Clamp(run[band], 0f, 1f) * 255f + 0.5f);
            _bandRgba[j + 1] = 0;
            _bandRgba[j + 2] = 0;
            _bandRgba[j + 3] = 255;
        }

        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, _bandTex);
        gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0,
            RoomBands.Bands, 1, PixelFormat.Rgba, PixelType.UnsignedByte, _bandRgba);
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

            uniform vec2  uResolution;
            uniform float uTime;
            uniform float uBass;           // the low end, enveloped
            uniform float uMid;            // ...and the middle, quicker
            uniform float uAir;            // ...and the top end, quicker again
            uniform vec3  uGround;         // the dark the figures sit on
            uniform vec3  uTint;           // the sleeve's dominant tone
            uniform vec3  uGlow;           // ...and its vivid accent
            uniform vec3  uShade;          // ...and the counter-colour the second family wears
            uniform sampler2D uBands;      // the spectrum, log spaced, one row
            uniform float uBare;           // 1 when the composition has drawn in to the middle
            uniform float uBall;           // 1 when there is a mirror ball hanging in the room
            uniform float uBallSpin;       // ...and how far it has turned, in seconds of its own clock
            uniform vec4  uWordBox;        // where the chrome writes: left, top, right, bottom, in
                                           // fractions of the window from its TOP-left corner
            uniform float uWords;          // ...and whether it is writing anything there at all

            // The room's own figures, and the lobe they are built from. Shared with the phone rather
            // than written twice — see RoomSource in Core.
            {{RoomSource.Glsl}}
            const float COLS = {{GlslSource.Num(Columns)}};
            const float SPEC_INSET = {{GlslSource.Num((float)PegasusLayout.SpectrumInset)}};

            // How tall a bar at full level stands, as a fraction of the window's height, measured from the
            // middle line out to ONE side — the run is mirrored, so the whole of it is twice this.
            const float BAR_REACH = {{GlslSource.Num(BarReach)}};
            // …and how much of that reach is taken back by the time the run arrives at the rim, where the
            // height falls away along a gentle curve: the run stands tallest coming out from behind the
            // record and settles as it travels out. It is a shape, not a filter — nothing is done to the
            // READING, only to how much room the reading is given, which is what keeps a cymbal at the rim
            // as legible as a kick at the record. Nought on Windows, which holds the run to one flat
            // ceiling at BAR_REACH instead; both figures come from the C# side, so this text is the same
            // on every platform and only the numbers baked into it differ.
            const float BAR_DAMP = {{GlslSource.Num(BarDamp)}};
            // The bend of that fall. Over one it is slack near the record and steepens toward the rim,
            // which reads as a curve rather than a wedge; at one it would be a straight taper.
            const float BAR_DAMP_BEND = {{GlslSource.Num(BarDampBend)}};

            // ── the mirror ball ──
            const float BALL_CELLS = {{GlslSource.Num(BallCells)}};
            const float BALL_DRIFT = {{GlslSource.Num(BallDrift)}};
            const float BALL_JITTER = {{GlslSource.Num(BallJitter)}};
            const float BALL_RATE = {{GlslSource.Num(BallRate)}};
            const float BALL_GLINT = {{GlslSource.Num(BallGlint)}};
            const float BALL_LIT = {{GlslSource.Num(BallLit)}};
            const float BALL_SPECK = {{GlslSource.Num(BallSpeck)}};
            const float BALL_BLOOM = {{GlslSource.Num(BallBloom)}};
            // Both figures above are WIDTHS, in cells, which is the only form anybody tuning this thinks
            // in. A Gaussian is written exp(-d*d*k) and wants the other thing, so the two k are worked out
            // here, once, at compile time — and blurring a speck stays a matter of changing its width.
            const float BALL_SHARP = 1.0 / (2.0 * BALL_SPECK * BALL_SPECK);
            const float BALL_BLOOM_SHARP = BALL_SHARP / (BALL_BLOOM * BALL_BLOOM);
            const float BALL_SWELL = {{GlslSource.Num(BallSwell)}};
            const float BALL_GLOW = {{GlslSource.Num(BallGlow)}};
            const float BALL_GLOW_BASS = {{GlslSource.Num(BallGlowBass)}};
            const float BALL_HAZE = {{GlslSource.Num(BallHaze)}};
            const float BALL_HAZE_BASS = {{GlslSource.Num(BallHazeBass)}};
            const float BALL_HAZE_TINT = {{GlslSource.Num(BallHazeTint)}};
            const float BALL_BOWL = {{GlslSource.Num(BallBowl)}};
            const float BALL_BOWL_RIM = {{GlslSource.Num(BallBowlRim)}};
            const float BALL_CLEAR = {{GlslSource.Num(BallClear)}};
            const float WORD_SOFT = {{GlslSource.Num(WordSoft)}};

            // The chrome's own sizing rule, worked out again here from the same numbers — the spectrum has
            // to come out from behind a record this shader cannot see. Both fractions, because the cap on
            // the width is what decides the cover's size on a portrait window and a floor of pixels is not
            // scale-invariant (nor ever reached at any size this runs at). See PegasusLayout.
            const float COVER_FRACTION = {{GlslSource.Num((float)PegasusLayout.CoverFraction)}};
            const float COVER_MAX_W = {{GlslSource.Num((float)PegasusLayout.CoverMaxWidthFraction)}};

            out vec4 fragColor;


            // Three numbers from a cell of the mirror ball's grid — where its facet sits inside the cell,
            // and when it catches the light. Wanted only because they must be the SAME three every frame:
            // a speck that drew itself somewhere new each frame would be a field of static, and the whole
            // effect is specks that hold still and blink. Nothing here is random; it is arithmetic on the
            // cell's own coordinates, which is what makes it repeatable without anything being stored.
            vec3 hash3(vec2 c)
            {
                vec3 q = fract(vec3(c.xyx) * vec3(0.1031, 0.1030, 0.0973));
                q += dot(q, q.yxz + 33.33);
                return fract((q.xxy + q.yzz) * q.zyx);
            }

            void main()
            {
                vec2 uv = gl_FragCoord.xy / uResolution;
                vec2 p = (gl_FragCoord.xy - 0.5 * uResolution) / uResolution.y;
                float t = uTime;

                // ── the room ──
                //
                // Three families of soft figures in the record's colours, drifting on the music — the
                // whole of it is RoomSource in Core, dropped into this program above. The phone paints
                // the same room on Skia, and one picture drawn by two compilers is only one picture for
                // as long as it is one text.
                vec3 col = rpmRoom(p, t, uBass, uMid, uAir, uGround, uTint, uGlow, uShade);

                // ── standing back ──
                //
                // The figures are the room, not the subject, and everything that matters is written over
                // them in white. So the whole wash is taken down, and taken down further along the LEFT,
                // where the title and the artist sit. Dimmed rather than veiled: a grey panel laid over
                // the top would kill the colour, where this simply moves that side of the room away from
                // the light. Applied before the spectrum, which keeps its own brightness.
                col *= 0.62;
                // …and the dim itself lifts away as the composition draws in to the middle. It is there
                // to hold one side of the room back under the words; bare, the words are under the cover
                // and this side of the screen is just room, which should be lit like the rest of it.
                //
                // Kept as a figure rather than applied and forgotten: the mirror ball below is light added
                // to the room, and light added AFTER the dim would sparkle across the very words the dim
                // exists to clear.
                float lit = mix(0.40 + 0.60 * smoothstep(0.02, 0.44, uv.x), 1.0, uBare);
                col *= lit;

                // ── and out from under the words ──
                //
                // How much of the ball's light this fragment may take. The dim above is not enough on its
                // own and was never asked to be: it holds one SIDE of the room back, for the one
                // composition that writes down that side, and it lifts away entirely as the room draws in
                // — which is exactly where the words are centred under the cover with the whole picture
                // lit evenly around them. Nothing was holding anything off them there.
                //
                // And a ball is not the sort of light a dim answers. Everything else in this room is a
                // soft gradient the words were dressed against; a speck is a hard white point a few
                // pixels across, and a title with one sitting on it is not dimmed, it is gone.
                //
                // So the chrome says where it is writing (SetWordBox) and the ball is cleared out of it,
                // with a shoulder either side so the field thins out over the words rather than stopping
                // along a line. Cleared, not extinguished: a rectangle of pure dark in a room full of
                // moving light is a hole cut in the picture, and the little that still lands there is
                // what says the words are IN the room rather than pasted over it.
                //
                // Declared here and WORKED OUT below, inside the first of the two blocks that use it.
                // The whole layer is meant to cost a record without a ball one comparison, and a box
                // measured on every fragment of every other record in the collection would be the same
                // bargain broken quietly. The haze at the foot of the shader reads the figure this sets.
                float clear = lit;

                // ── the mirror ball ──
                //
                // For a handful of records only (see MirrorBall), and off entirely for everything else:
                // the branch is on a uniform, so every fragment on the screen takes it the same way and a
                // record without a ball pays one comparison for the whole layer.
                //
                // What a ball actually does to a room is throw a few dozen small hard specks onto it, each
                // one lit for a moment as its facet swings past the light and dark again before the next.
                // So: a grid of them, one to a cell, mostly dark; and the field TRAVELS sideways rather
                // than orbiting the middle of the screen, because a speck thrown from a turning ball
                // crosses the wall and leaves, and the facet behind it starts another where the first
                // began.
                if (uBall > 0.001)
                {
                    // The clearing under the words, promised above: the box with its y turned the way up
                    // uv is, and a shoulder straddling every edge of it.
                    vec2 lo = vec2(uWordBox.x, 1.0 - uWordBox.w);
                    vec2 hi = vec2(uWordBox.z, 1.0 - uWordBox.y);
                    vec2 in0 = smoothstep(lo - WORD_SOFT, lo + WORD_SOFT, uv);
                    vec2 in1 = 1.0 - smoothstep(hi - WORD_SOFT, hi + WORD_SOFT, uv);
                    clear *= 1.0 - (1.0 - BALL_CLEAR) * in0.x * in0.y * in1.x * in1.y * uWords;

                    // ── the bowl ──
                    //
                    // The rows are not straight. A ball hangs above the room and throws its light outward
                    // and DOWN, so a row of specks leaving it does not cross the wall on the level — it
                    // falls away to either side, and what the eye is given is the underside of a dome:
                    // an upturned bowl, with the room inside it.
                    //
                    // Two terms and one shape. The rows bow with the square of the distance from the
                    // middle, which is the arc; and the cells close up as they go, which is the rim of
                    // that bowl turning away from the viewer. The second is what stops the first reading
                    // as a straight grid that somebody has bent — a curved surface crowds its own detail
                    // toward the edge, and without that this is a banner rather than a dome.
                    //
                    // Done to the PLANE before the grid is cut out of it, so there is nothing here the
                    // cells, the drift or the nine-cell search need to know about: they go on working in
                    // a square grid, and the square grid is what is no longer flat.
                    vec2 b = p;
                    float bx2 = b.x * b.x;
                    b.y -= BALL_BOWL * bx2;
                    b.x *= 1.0 + BALL_BOWL_RIM * bx2;

                    vec2 q = b * BALL_CELLS;
                    // On the ball's own clock, which gains on the low end — so a kick turns it faster and
                    // the specks travel further for it. Not the room's `t`: see uBallSpin.
                    q.x += uBallSpin * BALL_DRIFT;
                    q.y += 0.35 * sin(t * 0.11);   // the ball hangs on a thread; nothing hangs perfectly still

                    vec2 home = floor(q);
                    vec2 f = q - home;

                    // The nine cells around this fragment, and not merely the one it stands in.
                    //
                    // A speck's bloom is wider than its own cell, so the light belonging on this pixel is
                    // as likely to have been thrown by a neighbour as by the cell underfoot. Reading only
                    // the cell underfoot cuts every bloom off flat along that cell's edge — and a hundred
                    // and eighty cuts, all in a line, is the grid drawn across the room in exactly the
                    // places it is brightest. Nine is also all that is ever needed: at this width a speck
                    // is gone well inside one cell of travel, so a tenth would contribute nothing that
                    // could be seen.
                    float spark = 0.0;
                    for (int j = -1; j <= 1; j++)
                    {
                        for (int i = -1; i <= 1; i++)
                        {
                            vec2 at = vec2(float(i), float(j));
                            vec3 h = hash3(home + at);

                            // One speck to a cell, moved off the middle of it so the grid cannot be read.
                            float d = length(f - at - 0.5 - (h.xy - 0.5) * BALL_JITTER);

                            // Dark nearly all of the time, with a flash. The power is the whole difference
                            // between a glint and a pulse: a mirror ball is a dark ball with a moment of
                            // light on it, and a field of things gently brightening and dimming reads as
                            // fog.
                            // On the ball's clock too, and this is where its turning is actually SEEN: a
                            // facet comes round to the light because the ball is turning, so the rate it
                            // blinks at and the rate the field travels at are one rate. Driving only the
                            // drift would have been a field of specks sliding past at a new speed while
                            // each of them blinked at the old one.
                            float tw = pow(sin(uBallSpin * BALL_RATE * (0.7 + h.z) + h.z * 6.2831853)
                                           * 0.5 + 0.5, BALL_GLINT);
                            // …and only some of the facets are ever turned anywhere near the light. Drawn
                            // from a different mix of the cell's numbers than the phase above, or the
                            // specks that flashed would be exactly the ones that flashed early.
                            tw *= step(BALL_LIT, fract(h.z * 31.7 + h.x * 13.1));

                            // The speck itself, opened out by the low end: a kick pushes the light out of
                            // every facet that happens to be lit to catch it.
                            float core = exp(-d * d * BALL_SHARP / (1.0 + uBass * BALL_SWELL));

                            // …and the softer bloom it lays on whatever it landed on, which is where the
                            // low end is actually SEEN — a speck is a few pixels across and the bloom
                            // around it is an inch of wall. It answers to the kick in brightness rather
                            // than in reach: the two are different pictures, and a bloom that grew as well
                            // as brightened swallowed the specks it was supposed to be surrounding.
                            float glow = exp(-d * d * BALL_BLOOM_SHARP)
                                       * (BALL_GLOW + uBass * BALL_GLOW_BASS);

                            spark += tw * (core + glow);
                        }
                    }

                    // White with the sleeve's accent in it — the ball is throwing the ROOM's light about,
                    // so it cannot be a colour the room does not have. The low end is already in the two
                    // terms above, in the size of a speck and the strength of its bloom; what is left out
                    // here is the middle, so the field shimmers with the arrangement while it swells with
                    // the kick, and the two are told apart.
                    col += mix(vec3(1.0), uGlow, 0.30) * spark * uBall * (0.55 + 0.45 * uMid) * clear;
                }

                // ── the spectrum ──
                //
                // It begins at the cover's right edge (plus a little, so it is already going when it
                // appears from behind the record) and runs to the rim. Bass at the record, top end at the
                // edge. Mirrored about the middle, as a wave form is.
                float side = min(uResolution.y * COVER_FRACTION, uResolution.x * COVER_MAX_W);
                float coverHalf = 0.5 * side / uResolution.x;
                float start = 0.5 + coverHalf * (1.0 + SPEC_INSET);
                float sx = (uv.x - start) / max(1e-3, 1.0 - start);

                // Skipped outright once the run has faded out, rather than drawn and multiplied by
                // nothing: this is the expensive half of the shader, and the bare view is exactly the one
                // that has no use for it.
                if (sx > 0.0 && uBare < 0.999)
                {
                    float bar = sx * COLS;
                    float inBar = fract(bar);
                    // Straight through: one column, one band, read at its own texel centre. The curve that
                    // used to sit here re-spaced the columns against the bands, so several of them landed
                    // on the same reading — the log spacing is already in how the bands were folded, and
                    // bending it twice only cost the run its detail.
                    float band = (floor(bar) + 0.5) / COLS;
                    float v = texture(uBands, vec2(band, 0.5)).x;

                    // Never nothing: a resting line, so silence is a spectrum at rest and not an absence.
                    // How far the run may stand is BAR_REACH's to say, and how much of that it gives back
                    // by the rim is BAR_DAMP's — full reach where the bars leave the record, curving down
                    // to a shorter one as they travel out. Where BAR_DAMP is nought the term falls away
                    // and the run is one flat ceiling at BAR_REACH, which is the older reading and the one
                    // Windows kept.
                    float damp = 1.0 - BAR_DAMP * pow(sx, BAR_DAMP_BEND);
                    float halfH = 0.004 + v * BAR_REACH * damp;
                    float d = abs(uv.y - 0.5);
                    float px = 1.2 / uResolution.y;
                    float body = smoothstep(halfH + px, halfH - px, d);

                    // The gap between bars, cut with a soft shoulder so they do not alias into moire as
                    // the window resizes.
                    float slot = smoothstep(0.0, 0.30, inBar) * (1.0 - smoothstep(0.70, 1.0, inBar));

                    // Out from behind the record, rather than starting at a hard line.
                    float emerge = smoothstep(0.0, 0.11, sx);

                    // Held back from white. The bars are a reading of the music, not the subject either —
                    // at full strength they cut the room in half along the middle of the screen.
                    // …and out altogether as the composition draws in: the run leaves by going quiet
                    // rather than by being switched off, so the bars sink back into the room they were
                    // drawn over instead of vanishing off it.
                    float ink = body * slot * emerge * (0.24 + v * 0.76) * (1.0 - uBare);

                    // The bars wear the sleeve's own colours, and travel between them along the run: its
                    // dominant tone where they come out from behind the record, its vivid accent by the
                    // time they reach the rim.
                    //
                    // Lifted well toward white, and further the louder a bar is. They are drawn OVER a
                    // room painted in these very colours, so at full saturation they would sink into it —
                    // what has to read here is light, and the hue is what says whose light it is. A loud
                    // bar burning closer to white is also what gives the run its peaks.
                    // (`tone`, not `bar` — `bar` is the column index a few lines up, and GLSL will not
                    // take the name twice in one scope.)
                    vec3 tone = mix(uTint, uGlow, sx);
                    // …and burning all the way to white at full level. A quiet bar keeps the sleeve's
                    // colour and some of the room showing through it; a loud one arrives as solid white,
                    // which is what gives the run a top rather than a ceiling it leans on.
                    tone = mix(tone, vec3(1.0), 0.62 + v * 0.38);

                    col = mix(col, tone, clamp(ink, 0.0, 1.0));
                }

                // ── the room hazes ──
                //
                // The last thing done to the picture and the only layer that touches all of it: with a
                // ball in the room, everything on the screen — the wash, all three families of figures
                // and the spectrum alike — bleeds a little light. It is what the ball is hanging in. A
                // room with nothing in the air holds its specks and nothing else, and the beams that make
                // one of these rooms what it is only exist because there is something for them to be seen
                // in. Over the spectrum rather than under it, so the run glows with the rest of it.
                //
                // Squaring the colour is the whole trick: the bright parts bleed and the dark parts stay
                // dark, with no second pass over the screen to blur them with. A true bloom wants a blur
                // and a target to put it in, and this shader has one pass and no target — but every
                // figure in this room is already a soft gradient, so lifting the room's own highlights
                // back into it arrives at the same place for the cost of a multiply.
                if (uBall > 0.001)
                {
                    // Breathing on the low end, like everything else the ball does.
                    //
                    // Held off the words along with the specks. The bleed is quadratic and so already
                    // almost nothing wherever the room has been taken DOWN — but the room is not taken
                    // down under the drawn-in composition's words, and a bright sleeve puts a bright wash
                    // straight behind them, which this then lifts brighter still.
                    float breath = uBall * (BALL_HAZE + BALL_HAZE_BASS * uBass);
                    col += col * col * breath * clear;

                    // …and a wash of the record's own accent over the whole screen, so the haze belongs
                    // to the sleeve rather than being a grey film laid across it. Under the same clearing
                    // again, and this one needs it most of the three: it is flat rather than quadratic,
                    // so left alone it lies at full strength over everything written on the screen.
                    col += uGlow * breath * BALL_HAZE_TINT * (0.35 + 0.65 * uBass) * clear;
                }

                // The corners fall away, so the middle of the screen holds the eye and the chrome along
                // the edges has something quiet to sit on.
                float vig = pow(16.0 * uv.x * uv.y * (1.0 - uv.x) * (1.0 - uv.y), 0.20);
                col *= 0.42 + 0.58 * vig;

                // A grain of dither. Eight bits across a gradient this wide and this soft bands visibly,
                // and the bands crawl as the figures drift, which is far more distracting than the noise.
                float grain = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
                col += grain * (1.6 / 255.0);

                fragColor = vec4(max(col, vec3(0.0)), 1.0);
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

    /// <summary>Bars across the spectrum's run: one per band, exactly. Sampling a different number of columns
    /// from the bands made several bars read the same place in the curve and stand at the same height. That
    /// made the run look like blocks. One bar is one frequency band.</summary>
    private const float Columns = RoomBands.Bands;

    /// <summary>
    /// How far a bar at full level reaches from the middle line, as a fraction of the window's height. The run
    /// is mirrored about that line, so a bar at full level takes up twice this.
    ///
    /// <para>Windows uses the taller run. The shorter, damped shape below is used on the other platforms only.
    /// See <see cref="BarDamp"/> for why the two differ.</para>
    /// </summary>
    private static float BarReach => OperatingSystem.IsWindows() ? 0.24f : 0.16f;

    /// <summary>
    /// How much of that reach is given back by the rim, and how the giving back is bent. The run is tallest
    /// where it leaves the record. It settles as it travels out, forming a curve rather than one flat ceiling
    /// across the screen. Only the room that a reading is given changes. The reading itself is untouched, so a
    /// quiet band at the rim still shows its own level, drawn shorter.
    ///
    /// <para>It is zero on Windows. That switches the whole taper off: <c>damp</c> becomes one, and the run
    /// is a single flat ceiling at <see cref="BarReach"/>. The curve is a judgement about how the room reads.
    /// Windows reads it differently, so the value is a number here, not a branch in the shader. The GLSL text
    /// is the same on every platform.</para>
    /// </summary>
    private static float BarDamp => OperatingSystem.IsWindows() ? 0f : 0.30f;

    /// <summary>The bend of that fall. It has no effect where <see cref="BarDamp"/> is zero, because it
    /// multiplies a term that is then zero. So it needs no platform check of its own.</summary>
    private const float BarDampBend = 1.6f;

    // ── the mirror ball ──
    //
    // Every figure below is in cells. It is not in pixels or in fractions of the screen. The cell is the one
    // unit that keeps the layer the same at every scale. The grid is laid out against the window's height, so
    // a speck is the same size on a laptop as on a television. Nothing here needs re-tuning for each display.

    /// <summary>How many cells of the speck grid fit across the height of the window. That gives about ten
    /// rows, and eighteen columns on a wide window. There are roughly 180 facets. Just over half of them can
    /// ever be lit, and each is lit for a fraction of its own cycle. So a dozen or two are on screen at a time,
    /// which is what a ball throws. Many more and it looks like snow.</summary>
    private const float BallCells = 10f;

    /// <summary>How fast the field crosses the room, in cells a second. The rate is slow: about a minute from
    /// one end to the other. The ball turns rather than spins, and each speck is lit only for a moment. So
    /// what this really sets is whether the picture drifts or marches.</summary>
    private const float BallDrift = 0.42f;

    /// <summary>
    /// How much faster the ball turns on a full kick. On a full kick it turns twice as fast as at rest. With no
    /// bass, it turns at its resting speed.
    ///
    /// <para>It follows <see cref="RoomBands.Bass"/>, the same enveloped low end that the room's own bloom is
    /// lit by. So the ball responds to a kick the way the light in the room does: it comes up fast, holds for a
    /// moment, and falls away slowly. The raw level would be the obvious choice, and it is wrong. It flickers at
    /// whatever rate the drummer plays. A ball whose speed flickered would look like a machine with a fault, not
    /// like a room reacting.</para>
    ///
    /// <para>It moves the whole ball, not only the drift. The field travels faster, and every facet comes round
    /// to the light sooner, because these are one motion. See <c>uBallSpin</c> for why the rate is added up over
    /// time rather than multiplied into the clock.</para>
    /// </summary>
    private const float BallSpinBass = 1.00f;

    /// <summary>
    /// How far the rows bow, as a fraction of the window's height at the very edge of a square window. A wide
    /// window shows more of this, because its edges are further out. The ball hangs above the room and throws
    /// light down as well as out. So its rows fall away to either side, and the room is left under a dome.
    /// </summary>
    private const float BallBowl = 0.26f;

    /// <summary>How much the cells close up as they travel out to the rim of that dome, on the same square of
    /// the distance. The figure is a third again at the edge. A curved surface crowds its own detail where it
    /// turns away. Without this, the bow would look like a straight grid that has been bent, not like something
    /// with a shape.</summary>
    private const float BallBowlRim = 0.34f;

    /// <summary>How far a speck may sit from the middle of its own cell, as a fraction of the cell. This is what
    /// keeps the grid from being read as a grid. A speck does not have to be kept small. The nine-cell search
    /// means a speck can sit anywhere, and its bloom is still drawn whole by the neighbouring cells it spills
    /// into.</summary>
    private const float BallJitter = 0.34f;

    /// <summary>How quickly a facet comes round to the light. This is the base rate. Each cell then varies it by
    /// up to half again, using its own numbers, so no two facets blink together.</summary>
    private const float BallRate = 0.9f;

    /// <summary>The power that the blink is raised to. It sets the difference between a mirror ball and fog. At
    /// one, every speck would spend half its time lit, and the room would be a grey haze. At this value, each
    /// speck is visible for about a fifth of its cycle, and dark for the rest.</summary>
    private const float BallGlint = 7f;

    /// <summary>How many of the facets are ever turned close enough to the light to catch it. Just over half. A
    /// full grid where every cell eventually lights is a grid the eye finds.</summary>
    private const float BallLit = 0.45f;

    /// <summary>
    /// How wide a speck is: the standard deviation of its Gaussian, in cells. It is soft-edged but small, about
    /// a fifteenth of a cell. So a speck reads as a point of light with a little air around it, not as a dot
    /// with a rim.
    ///
    /// <para>The value is a width, not a falloff, because a width is what anyone tuning this has in mind. To
    /// blur the ball by half again, multiply this figure by one and a half and change nothing else. The shader
    /// turns the width into the falloff that a Gaussian needs. The bloom follows it, because the bloom is written
    /// as a multiple of it.</para>
    /// </summary>
    private const float BallSpeck = 0.066f;

    /// <summary>The bloom around a speck, as a multiple of the speck's own width. The two keep their proportions
    /// however the speck is re-tuned. That is the only way a setting called "blur" can mean what it says. The
    /// value is well over twice. The speck is the light, and the bloom is the wall it lands on.</summary>
    private const float BallBloom = 2.36f;

    /// <summary>How far the low end opens a speck out: half again its own width on a full kick. This applies to
    /// the core only. The bloom responds to the kick in brightness instead (see <see cref="BallGlowBass"/>). A
    /// bloom that grew as well as brightened would swallow the speck at its middle.</summary>
    private const float BallSwell = 0.9f;

    /// <summary>How bright that bloom is between kicks, compared with the speck's own brightness of one.</summary>
    private const float BallGlow = 0.26f;

    /// <summary>…and how much the low end adds to the bloom. This is the larger of the two figures, by some
    /// way. It is the reactive glow. A room with a ball in it should visibly respond to the bass drum, not just
    /// acknowledge it.</summary>
    private const float BallGlowBass = 0.55f;

    /// <summary>How much of its own light the whole picture bleeds while a ball is up. This is the haze that the
    /// beams are seen in. It is applied to the colour squared. So this figure sets how much the brightest parts
    /// of the screen gain, and the dark two thirds of the screen gain almost nothing.</summary>
    private const float BallHaze = 0.18f;

    /// <summary>…and how much more of it a kick brings. The whole room breathes on the low end, not only the
    /// specks. That is the difference between a ball hanging in a room and a ball pasted onto one.</summary>
    private const float BallHazeBass = 0.28f;

    /// <summary>How much of the sleeve's accent is in that haze. The amount is small. It is enough to make the
    /// glow the record's colour and not a grey film, and not so much that the whole room turns one colour.</summary>
    private const float BallHazeTint = 0.10f;

    /// <summary>
    /// How much of the ball's light still reaches the middle of the box that the chrome is writing in: the
    /// specks, their bloom and the haze alike (see <see cref="SetWordBox"/>).
    ///
    /// <para>The value is a sixth, not zero. The words only need nothing near white to land on them, and this
    /// value is well under that. If the layer were taken out entirely, a rectangle of dead room would be cut out
    /// of the one picture on the screen that is nothing but moving light. The words would then stand in a hole,
    /// not in a room.</para>
    /// </summary>
    private const float BallClear = 0.16f;

    /// <summary>The soft edge of that clear area, as a fraction of the window. A speck that nears the words dims
    /// as it comes, rather than going out at a line. The edge is wide, because nothing here needs to be sharp. The
    /// box is already generous, and a soft edge costs only room that the ball has plenty of.</summary>
    private const float WordSoft = 0.055f;

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
