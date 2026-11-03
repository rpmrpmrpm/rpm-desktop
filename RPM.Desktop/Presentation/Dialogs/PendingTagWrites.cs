using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Tag edits held back from the file the listener is on, and written the moment the player lets go of it.
///
/// <para><b>Why the music is not stopped.</b> Windows will not let a file the player has open be rewritten
/// or moved, so an edit used to stop the player, write, and start the track again where it was: a gap in
/// the song for fixing its spelling, and a paused track left stopped. Now the edit is shown at once (the
/// mirror row takes it, so every list and the track pane read the new words) and the FILE is written, and
/// re-filed by the naming template, when the player has moved off it: the next track, a stop, or the end
/// of the queue.</para>
///
/// <para><b>Kept on disk until written.</b> A held edit is also written to a small file beside the
/// settings, and written to the track the next time the library opens, so an app closed or lost in the
/// middle of the song does not lose it. Only the fields are kept there; a picture chosen for a track that
/// was playing is held in memory only.</para>
/// </summary>
internal static class PendingTagWrites
{
    /// <summary>One file's held edit: the library it belongs to, the file, and what to lay over its
    /// tags.</summary>
    private sealed record Pending(string Root, string Path, Dictionary<TagFieldKind, string> Fields, MassEditResult? Pictures);

    private static readonly List<Pending> Held = [];
    private static bool _hooked;
    private static bool _writing;
    private static DispatcherQueue? _dispatcher;

    /// <summary>Raised on the drawing thread after held edits were written, with every folder they
    /// touched, so the cards and lists showing those records can be read again.</summary>
    public static event Action<IReadOnlyList<string>>? Written;

    /// <summary>Raised on the drawing thread when an edit is held for a file, with the file. What shows
    /// the playing track reads it again then, through <see cref="Overlay"/>, since its file still says
    /// what it said before.</summary>
    public static event Action<string>? HeldFor;

    /// <summary>A file's tags with any edit held for it laid over them: what the file WILL say, for a
    /// surface that reads the playing track's own tags.</summary>
    public static TrackInfo Overlay(TrackInfo info)
    {
        Dictionary<TagFieldKind, string>? fields;
        lock (Held)
            fields = Held.FirstOrDefault(p => Same(p.Path, info.FilePath))?.Fields;
        if (fields is null)
            return info;
        foreach (var (kind, value) in fields)
            info = TagFields.Get(kind).Apply(info, value);
        return info;
    }

    /// <summary>Whether this file has an edit waiting for the player to let go of it.</summary>
    public static bool IsHeld(string path)
    {
        lock (Held)
            return Held.Any(p => Same(p.Path, path));
    }

    /// <summary>Holds an edit for a file the player has open. A second edit to the same file before it
    /// is written is laid over the first, field by field.</summary>
    /// <param name="pictures">The edit's pictures, kept for the write. Its fields are not read: the
    /// fields to write are <paramref name="fields"/>.</param>
    public static void Hold(string root, string path, IReadOnlyDictionary<TagFieldKind, string> fields, MassEditResult? pictures)
    {
        Hook();
        lock (Held)
        {
            var index = Held.FindIndex(p => Same(p.Path, path));
            if (index >= 0)
            {
                var merged = new Dictionary<TagFieldKind, string>(Held[index].Fields);
                foreach (var (kind, value) in fields)
                    merged[kind] = value;
                Held[index] = Held[index] with { Fields = merged, Pictures = pictures ?? Held[index].Pictures };
            }
            else
            {
                Held.Add(new Pending(root, path, new Dictionary<TagFieldKind, string>(fields), pictures));
            }
        }
        Save();
        HeldFor?.Invoke(path);
    }

    /// <summary>Every field the editors offer, as one track's wanted tags give them: the edit a window
    /// that writes its whole row makes, put in the shape a held edit is kept in.</summary>
    public static Dictionary<TagFieldKind, string> AllFields(TrackInfo wanted)
    {
        var row = new TrackRow(wanted);
        return TagFields.All.ToDictionary(f => f.Kind, f => f.Get(row));
    }

    /// <summary>Reads back the edits a previous run held and did not get to write, for the library that
    /// has just opened, and writes those whose files are free.</summary>
    public static void Resume(string root)
    {
        Hook();
        foreach (var kept in Load())
        {
            if (!Same(kept.Root, root) || IsHeld(kept.Path))
                continue;
            var fields = new Dictionary<TagFieldKind, string>();
            foreach (var (name, value) in kept.Fields)
                if (Enum.TryParse<TagFieldKind>(name, out var kind))
                    fields[kind] = value;
            lock (Held)
                Held.Add(new Pending(kept.Root, kept.Path, fields, null));
        }
        _ = WriteFreedAsync();
    }

    /// <summary>Listens to the player once, for every moment it may have let go of a file.</summary>
    private static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        if (App.Services.GetService<PlaybackService>() is not { } playback)
            return;

        // All four can come from the audio thread. The write runs on the drawing thread, as every other
        // edit does, because it re-points rows that may be bound.
        void Freed() => _dispatcher?.TryEnqueue(() => _ = WriteFreedAsync());
        playback.Changed += Freed;
        playback.TrackEnded += Freed;
        playback.TrackChained += Freed;
        playback.Stopped += Freed;
    }

    /// <summary>Writes every held edit whose file the player no longer has open.</summary>
    private static async Task WriteFreedAsync()
    {
        if (_writing)
            return;
        _writing = true;
        try
        {
            var playback = App.Services.GetService<PlaybackService>();
            var library = App.Services.GetRequiredService<Services.Library.LibraryService>();
            var touched = new List<string>();

            while (true)
            {
                Pending? next;
                lock (Held)
                    next = Held.FirstOrDefault(p => playback?.HoldsOpen(p.Path) != true
                                                    && library.IsOpen && Same(library.Root, p.Root));
                if (next is null)
                    break;

                lock (Held)
                    Held.Remove(next);
                Save();

                // Gone since (deleted, or moved by another edit): nothing to write to.
                if (!File.Exists(next.Path))
                    continue;

                TrackInfo info;
                try { info = TagReader.Read(next.Path); }
                catch { continue; }

                var pictures = next.Pictures;
                var edit = new MassEditResult(
                    next.Fields, pictures?.ArtBytes, pictures?.ArtMime, pictures?.FolderArtBytes,
                    pictures?.DiscArtBytes, pictures?.RemoveDiscArt ?? false, pictures?.RemoveArt ?? false);
                touched.AddRange(await MassEdit.WriteToLibraryAsync([new TrackRow(info)], edit));
            }

            if (touched.Count > 0)
                Written?.Invoke([.. touched.Distinct(StringComparer.OrdinalIgnoreCase)]);
        }
        finally
        {
            _writing = false;
        }
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ── on disk ──

    private static string StorePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RPM", "pending-tag-writes.json");

    private static void Save()
    {
        try
        {
            List<PendingWire> wire;
            lock (Held)
                wire = [.. Held.Select(p => new PendingWire(
                    p.Root, p.Path, p.Fields.ToDictionary(f => f.Key.ToString(), f => f.Value)))];

            if (wire.Count == 0)
            {
                if (File.Exists(StorePath))
                    File.Delete(StorePath);
                return;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(wire, PendingTagWritesJson.Default.ListPendingWire));
        }
        catch
        {
            // The edit is still held in memory and still written when the file is free. Only its survival
            // past a crash is lost.
        }
    }

    private static List<PendingWire> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(StorePath), PendingTagWritesJson.Default.ListPendingWire) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>A held edit as it is kept on disk.</summary>
internal sealed record PendingWire(string Root, string Path, Dictionary<string, string> Fields);

[JsonSerializable(typeof(List<PendingWire>))]
internal sealed partial class PendingTagWritesJson : JsonSerializerContext;
