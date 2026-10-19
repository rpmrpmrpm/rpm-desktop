using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Extensions.Api.CastChannel;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using RPM.Core.Metadata;
using RPM.Fingerprint.Catalog;
using RPM.Core.Sharing;

namespace RPM.Tools.AotSmoke;

/// <summary>
/// Runs the app's reflection and native-library paths in a real Native AOT build and reports which
/// of them no longer work.
/// </summary>
/// <remarks>
/// Each check is something the app does that the trimmer cannot see: a type found by an attribute
/// scan, a property found by name, a native library found by file name. A failed check is a feature
/// that does nothing, with no error, in the published app.
/// </remarks>
internal static class Program
{
    private static int _failed;

    /// <param name="args">Optionally <c>--natives &lt;dir&gt;</c>: the folder whose native layout to
    /// audit, so the harness can check the app's publish folder and not only its own.</param>
    private static int Main(string[] args)
    {
        var auditDir = AppContext.BaseDirectory;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--natives")
                auditDir = args[i + 1];

        Console.WriteLine($"AOT smoke — {RuntimeInformation.RuntimeIdentifier}, "
                          + $"dynamic code {(RuntimeFeature.IsDynamicCodeSupported ? "supported" : "OFF (AOT)")}");
        Console.WriteLine(new string('-', 72));

        Check("tags: FLAC through TagReader", TagsFlac);
        Check("tags: every reader TagLib registers", TagReaders);
        Check("protobuf: cast descriptor and round trip", Protobuf);
        Check("sqlite: open and query", Sqlite);
        Check("json: sharing protocol through its context", SharingJsonRoundTrip);
        Check("json: the rpm API's own shapes", CatalogWireRead);
        Check("stacks: a frame still names its method", FrameNames);
        Check($"natives: what ships in {Path.GetFileName(auditDir.TrimEnd(Path.DirectorySeparatorChar))}", () => Natives(auditDir));

        Console.WriteLine(new string('-', 72));
        Console.WriteLine(_failed == 0 ? "all checks passed" : $"{_failed} check(s) FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Func<string> check)
    {
        try
        {
            var note = check();
            Console.WriteLine($"  ok    {name}{(note.Length > 0 ? "  — " + note : "")}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    /// <summary>
    /// The API writes camelCase, the contracts use PascalCase, and a generated context applies no
    /// naming policy unless it is configured to. Nothing throws: every property reads back null, and
    /// the first caller to enumerate a list that should not be null throws. So the body here is the
    /// one the server writes, and the check is that every field is read.
    /// </summary>
    private static string CatalogWireRead()
    {
        const string wire = """
            {"results":[{"source":"Discogs","thumbUrl":"t","fullUrl":"f","width":1425,"height":1400}],
             "sources":["CoverArtArchive","Discogs"],"cached":false,"releaseGroupIds":["rg"]}
            """;

        var read = JsonSerializer.Deserialize(wire, CatalogWireJson.Default.CoverSearchResponse)
            ?? throw new InvalidOperationException("nothing came back");

        if (read.Results is not { Count: 1 } results || read.Sources is not { Count: 2 })
            throw new InvalidOperationException("the answer's lists are missing: the naming policy is off");

        return results[0].Source == "Discogs" && results[0].Dimensions == "1425\u00d71400"
            ? "camelCase held"
            : throw new InvalidOperationException($"read as {results[0].Source} / {results[0].Dimensions}");
    }

    // ── stacks ────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>StackFrame.GetMethod()</c> returns null for every frame in a published build, whatever
    /// IlcGenerateStackTraceData is set to. Code that names a frame through MethodBase therefore
    /// reports nothing in the published app and works under the debugger, so the problem is easy
    /// to miss.
    ///
    /// <para>ExceptionTrace and Gate both name frames, and both read them through
    /// DiagnosticMethodInfo. This checks that it still works here, because if it stops, the two
    /// diagnostics stop reporting without an error.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string FrameNames()
    {
        var frame = new StackTrace(fNeedFileInfo: false).GetFrame(0)
            ?? throw new InvalidOperationException("no frames at all");

        if (DiagnosticMethodInfo.Create(frame) is not { DeclaringTypeName: { } type } named)
            throw new InvalidOperationException("DiagnosticMethodInfo named no method for this frame");

        if (named.Name != nameof(FrameNames))
            throw new InvalidOperationException($"the nearest frame reads as {type}.{named.Name}");

        return frame.GetMethod() is null
            ? "GetMethod() answers null here, which is why this reads names instead"
            : "GetMethod() answers too on this runtime";
    }

    // ── tags ──────────────────────────────────────────────────────────────

    /// <summary>
    /// TagLib picks the class that reads a file by scanning its own assembly for
    /// [SupportedMimeType] and calling Activator.CreateInstance on what it finds. When trimmed, every
    /// read throws "No parameterless constructor defined", TagReader catches it, and the app gets a
    /// track with no title, artist or album, with no error shown.
    /// </summary>
    private static string TagsFlac()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aot-smoke-{Guid.NewGuid():N}.flac");
        try
        {
            File.WriteAllBytes(path, MinimalFlac("Smoke Title", "Smoke Artist", "Smoke Album"));

            var info = TagReader.Read(path);
            if (info.Title != "Smoke Title")
                throw new InvalidOperationException(
                    $"title came back as '{info.Title ?? "<null>"}' — the reader read nothing");
            if (info.Artist != "Smoke Artist")
                throw new InvalidOperationException($"artist came back as '{info.Artist ?? "<null>"}'");

            return $"{info.Title} / {info.Artist}";
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Every reader that TagLib lists can be constructed. The FLAC check above tests only one of
    /// them; a user's library can contain any format, and each format is a separate class found the
    /// same way, by reflection.
    /// </summary>
    private static string TagReaders()
    {
        var broken = new List<string>();
        foreach (var (mime, type) in TagLib.FileTypes.AvailableTypes)
        {
            // The constructor that TagLib itself calls. Checked directly, because a missing one is
            // not noticed until someone opens a file of that kind.
            var ctor = type.GetConstructor([typeof(TagLib.File.IFileAbstraction), typeof(TagLib.ReadStyle)]);
            if (ctor is null)
                broken.Add($"{mime} ({type.Name})");
        }

        if (broken.Count > 0)
            throw new InvalidOperationException(
                $"{broken.Count} reader(s) lost their constructor: {string.Join(", ", broken.Take(5))}");

        return $"{TagLib.FileTypes.AvailableTypes.Count} readers intact";
    }

    /// <summary>A FLAC with a STREAMINFO block and a Vorbis comment, built in code: small, valid, and
    /// it needs no fixture file on disk.</summary>
    private static byte[] MinimalFlac(string title, string artist, string album)
    {
        var f = new List<byte>();
        f.AddRange("fLaC"u8.ToArray());

        // STREAMINFO, not the last block.
        f.Add(0x00);
        f.AddRange([0x00, 0x00, 0x22]);              // 34 bytes
        f.AddRange([0x10, 0x00, 0x10, 0x00]);        // min/max block size 4096
        f.AddRange([0, 0, 0, 0, 0, 0]);              // min/max frame size unknown
        // 20 bits sample rate, 3 bits channels-1, 5 bits bits-per-sample-1, 36 bits total samples.
        ulong packed = ((ulong)44100 << 44) | (1UL << 41) | (15UL << 36) | 44100UL;
        for (int i = 7; i >= 0; i--)
            f.Add((byte)(packed >> (i * 8)));
        f.AddRange(new byte[16]);                    // md5 of the audio (there is no audio)

        // VORBIS_COMMENT, and it is the last block.
        var comment = VorbisComment(title, artist, album);
        f.Add(0x84);
        f.Add((byte)(comment.Length >> 16));
        f.Add((byte)(comment.Length >> 8));
        f.Add((byte)comment.Length);
        f.AddRange(comment);

        return [.. f];
    }

    private static byte[] VorbisComment(string title, string artist, string album)
    {
        var body = new List<byte>();
        void Entry(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            body.AddRange(BitConverter.GetBytes(bytes.Length));   // little endian, per the spec
            body.AddRange(bytes);
        }

        Entry("rpm aot smoke");
        body.AddRange(BitConverter.GetBytes(3));
        Entry($"TITLE={title}");
        Entry($"ARTIST={artist}");
        Entry($"ALBUM={album}");
        return [.. body];
    }

    // ── protobuf ──────────────────────────────────────────────────────────

    /// <summary>
    /// The cast protocol's wire format. The generated parser is safe by itself, but the descriptor
    /// it registers with is built from property NAMES, which protobuf reads by reflection. A failure
    /// here means cast finds speakers and then cannot send them anything.
    /// </summary>
    private static string Protobuf()
    {
        var descriptor = CastChannelReflection.Descriptor;
        if (descriptor.MessageTypes.Count == 0)
            throw new InvalidOperationException("the cast descriptor registered no messages");

        var sent = new CastMessage
        {
            ProtocolVersion = CastMessage.Types.ProtocolVersion.Castv210,
            SourceId = "sender-0",
            DestinationId = "receiver-0",
            Namespace = "urn:x-cast:com.google.cast.tp.connection",
            PayloadType = CastMessage.Types.PayloadType.String,
            PayloadUtf8 = """{"type":"CONNECT"}""",
        };

        var back = CastMessage.Parser.ParseFrom(sent.ToByteArray());
        if (back.PayloadUtf8 != sent.PayloadUtf8 || back.SourceId != sent.SourceId)
            throw new InvalidOperationException("a cast message did not survive a round trip");

        // The reflection part, which is the part that trimming breaks.
        var field = CastMessage.Descriptor.FindFieldByName("source_id");
        if (field is null || (string)field.Accessor.GetValue(back) != "sender-0")
            throw new InvalidOperationException("the descriptor cannot read a field it declares");

        return $"{descriptor.MessageTypes.Count} messages, accessors live";
    }

    // ── sqlite ────────────────────────────────────────────────────────────

    /// <summary>The library is a SQLite file; if its native cannot be found there is no library.</summary>
    private static string Sqlite()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        var version = cmd.ExecuteScalar() as string
                      ?? throw new InvalidOperationException("no version came back");
        return $"sqlite {version}";
    }

    // ── json ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reflection-based serialisation is off in a published build, so every type must be on a
    /// generated context. This takes one that is sent between machines and checks that the text is
    /// still written.
    /// </summary>
    private static string SharingJsonRoundTrip()
    {
        var hello = new PeerHello
        {
            DeviceId = "device-1",
            Name = "Smoke",
            PublicKey = "AAAA",
            Version = 1,
        };

        var json = JsonSerializer.Serialize(hello, SharingJson.Default.PeerHello);
        if (!json.Contains("\"deviceId\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"the wire names changed: {json}");

        var back = JsonSerializer.Deserialize(json, SharingJson.Default.PeerHello)
                   ?? throw new InvalidOperationException("nothing came back");
        if (back.DeviceId != hello.DeviceId)
            throw new InvalidOperationException("a hello did not survive a round trip");

        return json.Length + " bytes, camelCase held";
    }

    // ── natives ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every native library the app ships, loaded the way a P/Invoke loads it, and every native
    /// library that can be found only through a path that a published build does not have.
    ///
    /// <para>A Native AOT build has no deps.json, and <c>runtimes/&lt;rid&gt;/native</c> is a
    /// directory the host learns about FROM deps.json. A library that is only there is not found by
    /// the loader, however correct the build looks, and the feature that uses it (for example the
    /// visualisers) does nothing, with no error.</para>
    /// </summary>
    /// <param name="dir">The folder to audit. The harness's own by default; the script passes the
    /// app's publish folder, which is the layout that is released.</param>
    private static string Natives(string dir)
    {
        string[] suffixes = OperatingSystem.IsWindows() ? [".dll"]
                          : OperatingSystem.IsMacOS() ? [".dylib"]
                          : [".so"];

        var atRoot = Directory.EnumerateFiles(dir)
            .Where(f => suffixes.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();

        // The problem case: shipped, but only in the folder that deps.json would point to.
        var runtimes = Path.Combine(dir, "runtimes");
        var strandedList = new List<string>();
        if (Directory.Exists(runtimes))
        {
            foreach (var f in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (!suffixes.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!atRoot.Contains(name, StringComparer.OrdinalIgnoreCase))
                    strandedList.Add(name);
            }
        }

        var stranded = strandedList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // For this harness's own folder only, check that they load BY NAME, the way a DllImport
        // resolves one. The search path is the calling assembly's directory, so this test on another
        // folder proves nothing and fails on libraries that are fine. For another folder only the
        // layout is checked, above.
        var unloadable = new List<string>();
        if (string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                          Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.Ordinal))
        {
            foreach (var file in atRoot)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (NativeLibrary.TryLoad(name, typeof(Program).Assembly,
                                          DllImportSearchPath.AssemblyDirectory, out var handle))
                    NativeLibrary.Free(handle);
                else
                    unloadable.Add(file);
            }
        }

        if (stranded.Count > 0)
            throw new InvalidOperationException(
                $"only under runtimes/, which a published build never searches: "
                + string.Join(", ", stranded.Take(6)));

        if (unloadable.Count > 0)
            throw new InvalidOperationException(
                $"beside the executable but would not load: {string.Join(", ", unloadable.Take(6))}");

        return unloadable.Count == 0 && atRoot.Count > 0
            ? $"{atRoot.Count} beside the executable, none stranded"
            : $"{atRoot.Count} beside the executable, none stranded under runtimes/";
    }
}
