using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Tests;

/// <summary>
/// The ANGLE pair (libEGL.dylib, libGLESv2.dylib) that every OpenGL visualizer runs on. If the pair
/// is missing, a Mac build produces an app whose visualizers open black. So FetchRpmAngle in
/// RPM.Desktop.csproj fails every macOS build that does not have the pair pinned in
/// scripts/tool-versions.txt. The MSBuild tests
/// run that target against a scratch folder and a scratch pins file, with the download skipped, so
/// they need neither the real pair nor the network. The last two read the real pair that this
/// test's own build copied beside it.
/// </summary>
public sealed class AngleTests : IDisposable
{
    private const string Rid = "osx-arm64";

    private readonly string _scratch = Directory.CreateTempSubdirectory("rpm-angle-").FullName;

    private string Angle => Path.Combine(_scratch, "angle");

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    [Fact]
    public void A_mac_build_with_the_pinned_pair_passes_and_a_publish_names_it()
    {
        var egl = WriteLib("libEGL.dylib", "the pinned libEGL");
        var gles = WriteLib("libGLESv2.dylib", "the pinned libGLESv2");
        WritePins(("LIBEGL", egl), ("LIBGLESV2", gles));

        var (exit, output) = Check(Rid, "-p:_IsPublishing=true");

        Assert.True(exit == 0, output);
        Assert.Contains($"Publishing libEGL.dylib for {Rid} (SHA-256 {egl})", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Publishing libGLESv2.dylib for {Rid} (SHA-256 {gles})", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fetch is skipped here, as RpmSkipAngleFetch allows; the check is not.</summary>
    [Fact]
    public void A_mac_build_without_libGLESv2_fails()
    {
        var egl = WriteLib("libEGL.dylib", "the pinned libEGL");
        WritePins(("LIBEGL", egl), ("LIBGLESV2", Sha256("a libGLESv2 that was never downloaded")));

        var (exit, output) = Check(Rid);

        Assert.NotEqual(0, exit);
        Assert.Contains($"{Path.Combine(Angle, "libGLESv2.dylib")} is missing", output);
    }

    [Fact]
    public void A_mac_build_with_a_file_that_is_not_the_pinned_one_fails()
    {
        WriteLib("libEGL.dylib", "libEGL copied out of some other app");
        var gles = WriteLib("libGLESv2.dylib", "the pinned libGLESv2");
        var pinned = Sha256("the pinned libEGL");
        WritePins(("LIBEGL", pinned), ("LIBGLESV2", gles));

        var (exit, output) = Check(Rid);

        Assert.NotEqual(0, exit);
        Assert.Contains($"pins ANGLE_LIBEGL_SHA256_OSX_ARM64={pinned}", output);
    }

    [Fact]
    public void A_mac_build_for_a_rid_with_no_pin_fails()
    {
        var egl = WriteLib("libEGL.dylib", "the pinned libEGL");
        var gles = WriteLib("libGLESv2.dylib", "the pinned libGLESv2");
        WritePins(("LIBEGL", egl), ("LIBGLESV2", gles));

        var (exit, output) = Check("osx-x64");

        Assert.NotEqual(0, exit);
        Assert.Contains("pins no ANGLE_LIBEGL_SHA256_OSX_X64", output);
    }

    /// <summary>The Windows build draws its visualizers without ANGLE, so it neither fetches nor
    /// ships the pair, and a pins file without it is not an error there.</summary>
    [Fact]
    public void A_windows_build_has_no_angle_pair()
    {
        WritePins();

        var (exit, output) = Check("win-x64");

        Assert.True(exit == 0, output);
        Assert.Empty(Items("RpmAngle", "-p:RpmToolsRid=win-x64"));
    }

    /// <summary>A Native AOT app has no deps.json, so a native under runtimes/ is never found in a
    /// published build; beside the executable is the one place both kinds of build search.</summary>
    [Fact]
    public void A_mac_build_ships_the_pair_beside_the_executable_and_nowhere_else()
    {
        var links = Items("Content", $"-p:RpmToolsRid={Rid}")
            .Where(c => c.GetProperty("Identity").GetString()!.Contains($"{Path.DirectorySeparatorChar}angle{Path.DirectorySeparatorChar}"))
            .Select(c => c.GetProperty("Link").GetString())
            .Order()
            .ToList();

        Assert.Equal(new[] { "libEGL.dylib", "libGLESv2.dylib" }, links);
    }

    /// <summary>A RID that the fetch script can download for but tool-versions.txt does not pin gives
    /// a Mac build that always fails. This test finds it here instead of at release time.
    /// release-mac.sh builds both.</summary>
    [Fact]
    public void Every_rid_the_fetch_script_knows_has_its_pair_pinned()
    {
        var scripts = Path.Combine(ReleaseToolsTests.RepoRoot(), "scripts");
        var rids = Regex.Matches(File.ReadAllText(Path.Combine(scripts, "fetch-angle.sh")),
                @"^\s*(osx-[a-z0-9]+)\)\s+ASSET=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Order().ToList();
        var pins = File.ReadAllText(Path.Combine(scripts, "tool-versions.txt"));

        Assert.Equal(new[] { "osx-arm64", "osx-x64" }, rids);
        Assert.Matches(@"(?m)^ANGLE_REPO=\S+\s*$", pins);
        Assert.Matches(@"(?m)^ANGLE_RELEASE=\S+\s*$", pins);
        var missing = rids
            .SelectMany(rid => new[] { "LIBEGL", "LIBGLESV2" }.Select(file => $"ANGLE_{file}_SHA256_{Key(rid)}"))
            .Where(key => !Regex.IsMatch(pins, $@"(?m)^{key}=[0-9a-f]{{64}}\s*$"))
            .ToList();
        Assert.True(missing.Count == 0, "Not pinned in tool-versions.txt: " + string.Join(", ", missing));
    }

    /// <summary>The pair this test's build copied beside it is the pinned one for this Mac. An output
    /// folder can hold an older pair with a newer file date, which PreserveNewest would keep.</summary>
    [Fact]
    public void The_pair_beside_this_assembly_is_the_pinned_one()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var key = Key(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64");
        var pins = File.ReadAllText(Path.Combine(ReleaseToolsTests.RepoRoot(), "scripts", "tool-versions.txt"));
        foreach (var (file, pin) in new[] { ("libEGL.dylib", "LIBEGL"), ("libGLESv2.dylib", "LIBGLESV2") })
        {
            var expected = Regex.Match(pins, $@"(?m)^ANGLE_{pin}_SHA256_{key}=([0-9a-f]{{64}})\s*$").Groups[1].Value;
            var shipped = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, file))));
            Assert.Equal(expected, shipped);
        }
    }

    /// <summary>
    /// Uno's own EGL setup (EglHelper, which MacOSNativeOpenGLWrapper calls) against the pair beside
    /// this assembly, found by name the way the app finds it, then what every visualizer does: a
    /// "#version 300 es" program, an RGBA8 texture upload with no unpack buffer bound, and a draw into
    /// a framebuffer that is read back. Uno asks for an ES 2 context, and the visualizers' shaders work
    /// only because ANGLE answers with the newest compatible version, so that is checked as well.
    /// </summary>
    [Fact]
    public void The_pair_beside_this_assembly_draws_a_300_es_shader_through_Uno()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var egl = typeof(Microsoft.UI.Xaml.UIElement).Assembly.GetType("Uno.UI.Helpers.EglHelper", throwOnError: true)!;
        object? Egl(string name, params object?[] args) =>
            egl.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);

        var display = (nint)Egl("EglGetDisplay", (nint)0)!;
        Assert.NotEqual(0, display);
        var context = (ITuple)Egl("InitializeGles2Context", display, null)!;
        var (surface, glContext) = ((nint)context[0]!, (nint)context[1]!);
        try
        {
            Assert.True((bool)Egl("EglMakeCurrent", display, surface, surface, glContext)!);
            var gl = GL.GetApi(name => (nint)Egl("EglGetProcAddress", name)!);

            Assert.StartsWith("OpenGL ES 3.", gl.GetStringS(StringName.Version));
            Assert.Contains("ANGLE", gl.GetStringS(StringName.Renderer));

            var program = Link(gl,
                """
                #version 300 es
                void main() {
                    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
                }
                """,
                """
                #version 300 es
                precision highp float;
                uniform sampler2D uTex;
                out vec4 fragColor;
                void main() { fragColor = texture(uTex, vec2(0.25, 0.5)); }
                """);

            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
            var texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            ReadOnlySpan<byte> texels = [10, 200, 30, 255, 0, 0, 0, 255];
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 2, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, texels);

            var framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            var colour = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, 4, 4);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            gl.Viewport(0, 0, 4, 4);
            gl.ClearColor(0, 0, 0, 0);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.UseProgram(program);
            gl.Uniform1(gl.GetUniformLocation(program, "uTex"), 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            Span<byte> pixel = stackalloc byte[4];
            gl.ReadPixels(1, 1, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            Assert.Equal(new byte[] { 10, 200, 30, 255 }, pixel.ToArray());

            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteRenderbuffer(colour);
            gl.DeleteTexture(texture);
            gl.DeleteProgram(program);
        }
        finally
        {
            Egl("EglMakeCurrent", display, (nint)0, (nint)0, (nint)0);
            Egl("EglDestroySurface", display, surface);
            Egl("EglDestroyContext", display, glContext);
        }
    }

    private static uint Link(GL gl, string vertex, string fragment)
    {
        var program = gl.CreateProgram();
        foreach (var (type, source) in new[] { (ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment) })
        {
            var shader = gl.CreateShader(type);
            gl.ShaderSource(shader, source);
            gl.CompileShader(shader);
            gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
            Assert.True(compiled != 0, $"{type}: {gl.GetShaderInfoLog(shader)}");
            gl.AttachShader(program, shader);
            gl.DeleteShader(shader);
        }
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        Assert.True(linked != 0, gl.GetProgramInfoLog(program));
        return program;
    }

    private string WriteLib(string name, string content)
    {
        Directory.CreateDirectory(Angle);
        File.WriteAllText(Path.Combine(Angle, name), content);
        return Sha256(content);
    }

    /// <summary>A pins file for <see cref="Rid"/> only.</summary>
    private void WritePins(params (string File, string Sha256)[] pins) =>
        File.WriteAllLines(Path.Combine(_scratch, "tool-versions.txt"),
            pins.Select(p => $"ANGLE_{p.File}_SHA256_{Key(Rid)}={p.Sha256}").Prepend("# ANGLE"));

    private (int Exit, string Output) Check(string rid, params string[] extra) =>
        ReleaseToolsTests.MsBuild(new[]
        {
            "-t:FetchRpmAngle",
            "-p:RpmSkipAngleFetch=true",
            $"-p:RpmToolsRid={rid}",
            $"-p:RpmAngleDir={Angle}{Path.DirectorySeparatorChar}",
            $"-p:RpmToolVersions={Path.Combine(_scratch, "tool-versions.txt")}",
        }.Concat(extra).ToArray());

    private static List<JsonElement> Items(string type, params string[] properties)
    {
        var (exit, output) = ReleaseToolsTests.MsBuild([$"-getItem:{type}", .. properties]);
        Assert.True(exit == 0, output);
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("Items").TryGetProperty(type, out var items)
            ? items.EnumerateArray().Select(i => i.Clone()).ToList()
            : [];
    }

    private static string Key(string rid) => rid.ToUpperInvariant().Replace('-', '_');

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
}
