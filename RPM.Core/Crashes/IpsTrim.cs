using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RPM.Core.Crashes;

/// <summary>
/// Makes a macOS .ips report fit a length without breaking its JSON.
///
/// <para>Cutting the text at the length would leave the body unclosed. The server could then read
/// nothing from it: no headline, no crashed thread, and one signature shared by every large crash.
/// Instead this removes what a reader needs least, in order. First it removes the frames and registers
/// of the threads that did not crash. Then it removes the middle of the crashed thread's stack. The
/// header, the exception, the image list and the top of the crashed thread's stack are kept. Text that
/// is not an .ips report is cut.</para>
/// </summary>
internal static class IpsTrim
{
    /// <summary>Frames always kept from the top of the crashed thread: where it crashed.</summary>
    private const int TopFrames = 64;

    /// <summary>Frames always kept from the bottom of the crashed thread: how the thread started.</summary>
    private const int BottomFrames = 16;

    /// <summary>The body's own note that it was made smaller, so a reader in rpm.Admin knows.</summary>
    private const string NoteKey = "rpmTrimmed";

    public static string Fit(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var newline = text.IndexOf('\n');
        if (newline < 0)
            return Cut(text, max);

        JsonObject? body;
        try { body = JsonNode.Parse(text[(newline + 1)..]) as JsonObject; }
        catch (JsonException) { body = null; }
        if (body?["threads"] is not JsonArray threads)
            return Cut(text, max);

        var header = text[..newline];
        var faulting = body["faultingThread"] is JsonValue value && value.TryGetValue<int>(out var index) ? index : -1;

        body[NoteKey] = "Frames of the threads that did not crash were removed to fit the size limit.";
        for (var i = 0; i < threads.Count; i++)
        {
            if (i != faulting && threads[i] is JsonObject thread)
            {
                thread.Remove("frames");
                thread.Remove("threadState");
            }
        }

        var fitted = Join(header, body);
        if (fitted.Length <= max)
            return fitted;

        if (faulting >= 0 && faulting < threads.Count
            && threads[faulting] is JsonObject crashed && crashed["frames"] is JsonArray frames)
        {
            body[NoteKey] = "Frames of the threads that did not crash, and the middle of the crashed thread's "
                + "stack, were removed to fit the size limit.";

            // Each pass removes about half of the frames that may go, starting just above the bottom
            // frames.
            while (fitted.Length > max && frames.Count > TopFrames + BottomFrames)
            {
                var remove = Math.Max(1, (frames.Count - TopFrames - BottomFrames) / 2);
                var from = frames.Count - BottomFrames - remove;
                for (var i = 0; i < remove; i++)
                    frames.RemoveAt(from);
                fitted = Join(header, body);
            }
        }

        // Still too long: the image list or another section is the cause. Cut the text.
        return fitted.Length <= max ? fitted : Cut(fitted, max);
    }

    /// <summary>The header line and the body, written compactly. The relaxed encoder keeps characters such
    /// as '+' and '"' as they are, so the body does not grow from escaping.</summary>
    internal static string Join(string header, JsonObject body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            body.WriteTo(writer);
        return header + "\n" + Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}
