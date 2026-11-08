using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Cloud;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Dialogs;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Edits to cloud-only tracks whose sidecars have not been written yet, kept on disk until they are.
///
/// <para><b>Why.</b> Writing a sidecar is a fetch and an upload of about a megabyte, and an edit to a
/// record reaches every track on it. Closing the app before the last upload landed lost the edit for the
/// tracks still waiting: the mirror row kept only the tags it holds, and the sync pass carries only those
/// (see <see cref="RemoteTags.Remember"/>). The app does not wait on anything as it closes, so the edit
/// is written down first, crossed off as each sidecar lands, and the rest are written the next time the
/// library opens.</para>
///
/// <para>Only the tags are kept, as the editors' fields. A picture chosen for a cloud-only record is
/// sent on its own path (see MassEdit.PublishArtAsync).</para>
/// </summary>
internal static class SidecarsOwed
{
    private static readonly object Gate = new();

    /// <summary>Writes an edit down before its sidecar is sent.</summary>
    public static void Owe(string id, TrackInfo edited, string relativePath)
    {
        var fields = PendingTagWrites.AllFields(edited).ToDictionary(f => f.Key.ToString(), f => f.Value);
        lock (Gate)
        {
            var owed = Load();
            owed.RemoveAll(o => o.Id == id);
            owed.Add(new SidecarOwed(id, relativePath, fields));
            Save(owed);
        }
    }

    /// <summary>A sidecar's tags with any edit still owed to it laid over them: what it WILL say, for a
    /// reader that fetched it before the write landed.</summary>
    public static TrackInfo Overlay(string id, TrackInfo info)
    {
        SidecarOwed? due;
        lock (Gate)
            due = Load().FirstOrDefault(o => o.Id == id);
        if (due is null)
            return info;
        foreach (var (name, value) in due.Fields)
            if (Enum.TryParse<TagFieldKind>(name, out var kind))
                info = TagFields.Get(kind).Apply(info, value);
        return info;
    }

    /// <summary>Crosses an edit off once its sidecar is written.</summary>
    public static void Paid(string id)
    {
        lock (Gate)
        {
            var owed = Load();
            if (owed.RemoveAll(o => o.Id == id) > 0)
                Save(owed);
        }
    }

    /// <summary>Writes every sidecar still owed from an earlier run: each fetched, the edit laid over its
    /// tags, and sent. One that fails stays owed for the next run.</summary>
    public static async Task RepayAsync()
    {
        List<SidecarOwed> owed;
        lock (Gate)
            owed = Load();
        if (owed.Count == 0 || App.Services.GetService<RpmCloudClient>() is not { } cloud)
            return;

        foreach (var due in owed)
        {
            try
            {
                // Null: nothing under this id any more, so nothing to write. Either way the edit is paid.
                await cloud.RewriteSidecarAsync(due.Id, sidecar =>
                {
                    var info = sidecar.ToTrackInfo(due.RelativePath);
                    foreach (var (name, value) in due.Fields)
                        if (Enum.TryParse<TagFieldKind>(name, out var kind))
                            info = TagFields.Get(kind).Apply(info, value);
                    return sidecar.WithTags(info) with { RelativePath = due.RelativePath };
                });
                Paid(due.Id);
            }
            catch
            {
                // Offline, or signed out: still owed.
            }
        }
    }

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RPM", "sidecars-owed.json");

    private static List<SidecarOwed> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(StorePath), SidecarsOwedJson.Default.ListSidecarOwed) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void Save(List<SidecarOwed> owed)
    {
        try
        {
            if (owed.Count == 0)
            {
                if (File.Exists(StorePath))
                    File.Delete(StorePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(owed, SidecarsOwedJson.Default.ListSidecarOwed));
        }
        catch
        {
            // The upload still runs; only its survival past a closed app is lost.
        }
    }
}

/// <summary>One sidecar still to be written: whose, where it is filed, and the edit as the editors'
/// fields.</summary>
internal sealed partial record SidecarOwed(string Id, string RelativePath, Dictionary<string, string> Fields);

[JsonSerializable(typeof(List<SidecarOwed>))]
internal sealed partial class SidecarsOwedJson : JsonSerializerContext;
