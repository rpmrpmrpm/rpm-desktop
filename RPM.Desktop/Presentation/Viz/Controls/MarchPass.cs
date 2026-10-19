using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// What every marched visualizer needs around its own shader: an off-screen buffer to march into, the
/// bloom and tone map that puts the result on the screen, and a governor that keeps the frame inside its
/// time budget.
///
/// <para>A marcher costs work per pixel and per step. A full-screen visualizer on a high-density display has
/// several times too many pixels. So the picture is marched at a fraction of the surface size, and the
/// screen pass enlarges it. That needs a framebuffer. The one rule that is not obvious is this:
/// <c>GLCanvasElement</c> gives the stage a framebuffer of its own, and it composites that texture into the
/// Skia scene. So a pass that binds a different framebuffer must bind the handed one again afterwards.
/// Binding zero draws the finished picture into the window's real default framebuffer. Nothing reads that
/// one, so the element composites the black it was cleared to.</para>
///
/// <para>This is kept in one place because three stages use it. A framebuffer mistake looks the same in all
/// three: a black screen with no error.</para>
/// </summary>
internal sealed class MarchPass
{
    /// <summary>A full-screen triangle. The corners fall outside the viewport and are clipped. It also
    /// avoids the seam that a two-triangle quad leaves along its diagonal.</summary>
    private static readonly float[] Triangle = [-1f, -1f, 3f, -1f, -1f, 3f];

    /// <summary>How much smaller the marched buffer is than the surface, per axis. The pictures are designed
    /// for a factor of two. The governor may use a coarser value on a machine that cannot keep up.</summary>
    private const int BaseScale = 2;
    private const int MaxScale = 4;

    /// <summary>The number of steps a ray may take. The governor changes this before it changes the resolution.
    /// Fewer steps lose distance, so the far end fades out. A coarser buffer lowers everything.</summary>
    public const int BaseSteps = 64;
    private const int MinSteps = 28;

    private uint _vao, _vbo, _screen, _fbo, _target;
    private int _tw, _th;
    private int _sRes, _sSource, _sBloom, _sSat;

    private int _scale = BaseScale;
    private double _frameAvg = 1.0 / 60;
    private int _pressure;

    /// <summary>How many steps a ray may take this frame.</summary>
    public int Steps { get; private set; } = BaseSteps;

    /// <summary>The vertex shader that all of these draw with. It only places the full-screen triangle.</summary>
    public static string Vertex(string versionDef) => $$"""
        {{versionDef}}
        precision highp float;
        layout (location = 0) in vec2 aPos;
        void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
        """;

    /// <summary>Builds the shared objects. It is safe to call more than once, so Init and the first render
    /// can both call it.</summary>
    public void Ensure(GL gl, string versionDef)
    {
        if (_vao != 0)
            return;

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

        _fbo = gl.GenFramebuffer();
        _target = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _target);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _screen = Link(gl, Vertex(versionDef), ScreenSource(versionDef));
        _sRes = gl.GetUniformLocation(_screen, "uResolution");
        _sSource = gl.GetUniformLocation(_screen, "uSource");
        _sBloom = gl.GetUniformLocation(_screen, "uBloom");
        _sSat = gl.GetUniformLocation(_screen, "uSaturate");
    }

    public void Destroy(GL gl)
    {
        if (_screen != 0) gl.DeleteProgram(_screen);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        if (_target != 0) gl.DeleteTexture(_target);
        if (_fbo != 0) gl.DeleteFramebuffer(_fbo);
        _screen = _vbo = _vao = _target = _fbo = 0;
        _tw = _th = 0;
    }

    /// <summary>
    /// Draws one frame. The stage's shader draws into the off-screen buffer. Then the bloom and tone map draw
    /// into the framebuffer that the stage was handed. <paramref name="uniforms"/> is called with the size of
    /// the buffer, once the march program is current. Any other values the picture needs come from the stage.
    /// </summary>
    public void Render(GL gl, uint march, Action<int, int> uniforms, float bloom, float saturate)
    {
        Span<int> vp = stackalloc int[4];
        gl.GetInteger(GetPName.Viewport, vp);
        int width = Math.Max(1, vp[2]), height = Math.Max(1, vp[3]);

        // The framebuffer this stage was handed. It is not zero. See the class comment.
        gl.GetInteger(GetPName.DrawFramebufferBinding, out int handed);
        uint mine = (uint)Math.Max(0, handed);

        EnsureTarget(gl, width, height, mine);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.Viewport(0, 0, (uint)_tw, (uint)_th);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        if (march != 0)
        {
            gl.UseProgram(march);
            uniforms(_tw, _th);
            gl.BindVertexArray(_vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, mine);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        if (_screen != 0)
        {
            gl.UseProgram(_screen);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _target);
            if (_sSource >= 0) gl.Uniform1(_sSource, 0);
            if (_sRes >= 0) gl.Uniform2(_sRes, (float)width, (float)height);
            if (_sBloom >= 0) gl.Uniform1(_sBloom, bloom);
            if (_sSat >= 0) gl.Uniform1(_sSat, saturate);

            gl.BindVertexArray(_vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
    }

    /// <summary>Sizes the marched buffer. It is made again when the surface size or the governor's scale changes.
    /// The format is RGBA8, not a float format. A half-float target that can be drawn into needs an extension
    /// that ANGLE does not guarantee. The marchers write a range-compressed colour, and the screen pass
    /// expands it.</summary>
    private void EnsureTarget(GL gl, int width, int height, uint restoreTo)
    {
        int w = Math.Max(1, width / _scale), h = Math.Max(1, height / _scale);
        if (w == _tw && h == _th)
            return;

        _tw = w;
        _th = h;

        gl.BindTexture(TextureTarget.Texture2D, _target);
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);   // Uno can leave a pixel buffer bound here
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _target, 0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, restoreTo);
    }

    /// <summary>
    /// Keeps the frame inside its time budget. A marcher's cost depends on the pixels and the steps. The
    /// speed of the machine is not known in advance, so the frame time is measured and the work is trimmed.
    /// The steps are reduced first. The far end of the picture then fades, which looks like haze. Only when
    /// the steps are at their minimum does the buffer become coarser, and that lowers the quality of everything.
    ///
    /// <para>A counter of recent slow and fast frames adds hysteresis. One slow frame is a brief slowdown, and it must
    /// not drop the picture. One fast frame must not raise it again.</para>
    /// </summary>
    public void Govern(double frameSeconds)
    {
        if (frameSeconds <= 0 || frameSeconds > 0.5)
            return;

        // The average gives the newest frame a weight of 0.2. A slower average (for example 0.1) would need
        // about a second to show a drop to 30 fps, and that second is the one the user is watching.
        _frameAvg = _frameAvg * 0.8 + frameSeconds * 0.2;

        // Sixty frames a second is the target. The trim starts as soon as the average falls below it. It does not
        // wait until the picture is visibly poor at forty-five. The gap between the two thresholds stops the
        // trim from oscillating. It goes down at 57, and it goes back up only when there is real headroom.
        const double Slow = 1.0 / 57, Fast = 1.0 / 72;

        if (_frameAvg > Slow) _pressure++;
        else if (_frameAvg < Fast) _pressure--;
        else _pressure = 0;

        if (_pressure >= 12)
        {
            _pressure = 0;
            if (Steps > MinSteps) Steps = Math.Max(MinSteps, Steps - 8);
            else if (_scale < MaxScale) { _scale++; _tw = _th = 0; }
        }
        else if (_pressure <= -45)
        {
            _pressure = 0;
            if (_scale > BaseScale) { _scale--; _tw = _th = 0; }
            else if (Steps < BaseSteps) Steps = Math.Min(BaseSteps, Steps + 8);
        }
    }

    /// <summary>Bloom and tone map, shared by every marcher. The samples are taken on a ring, not a box. Eight
    /// samples on a ring give a halo. A 3x3 box gives a cross. A halo is what light around hot metal looks like.</summary>
    private static string ScreenSource(string versionDef) => $$"""
        {{versionDef}}
        precision highp float;

        uniform sampler2D uSource;
        uniform vec2      uResolution;   // the screen, not the buffer
        uniform float     uBloom;
        uniform float     uSaturate;

        out vec4 fragColor;

        const float TAU = 6.28318530718;

        // Back out of the range compression the marcher wrote with. Clamped away from 1, where the
        // inverse runs off to infinity.
        vec3 expand(vec3 c) { return c / max(1.0 - c, 0.004); }

        void main()
        {
            vec2 uv = gl_FragCoord.xy / uResolution;
            vec2 texel = 1.0 / vec2(textureSize(uSource, 0));

            vec3 col = expand(texture(uSource, uv).rgb);

            // Two rings of taps, near and far, so the halo has a core and a spread.
            vec3 halo = vec3(0.0);
            for (int i = 0; i < 8; i++)
            {
                float a = TAU * (float(i) + 0.5) / 8.0;
                vec2 dir = vec2(cos(a), sin(a));
                halo += expand(texture(uSource, uv + dir * texel * 2.5).rgb) * 0.65;
                halo += expand(texture(uSource, uv + dir * texel * 7.0).rgb) * 0.35;
            }
            col += halo * uBloom * 0.045;

            // Tone map, then pull the colour back out of it. A tone map is a curve toward white and it
            // takes the colour with it: everything bright ends up pale, which is why a picture can be
            // both correctly exposed and washed out. Saturating afterwards puts back what the curve took.
            col = 1.0 - exp(-col * 0.95);
            col = clamp(mix(vec3(dot(col, vec3(0.2126, 0.7152, 0.0722))), col, uSaturate), 0.0, 1.0);
            col *= 0.55 + 0.45 * pow(16.0 * uv.x * uv.y * (1.0 - uv.x) * (1.0 - uv.y), 0.22);

            fragColor = vec4(pow(max(col, 0.0), vec3(1.0 / 2.2)), 1.0);
        }
        """;

    /// <summary>Compiles and links a program. On either failure it throws, with the compiler's own message.</summary>
    public static uint Link(GL gl, string vertex, string fragment)
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
            throw new InvalidOperationException("Program link failed: " + log);
        }
        return program;
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
}
