using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RPM.Core.Library;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Cloud;

/// <summary>
/// What the admin changed about one act, as the rpm API's <c>artist/edits</c> answers it: a
/// <see cref="Description"/> shown in place of the encyclopedia's article, and acts to show first
/// (<see cref="Related"/>) or never (<see cref="Unrelated"/>) among the related artists. The pictures the
/// admin added or hid reach the app through <c>artwork</c>, and are not read here.
///
/// <para>The API answers every act, with empty lists and no description where nothing was changed, so a
/// missing field reads as nothing changed.</para>
/// </summary>
public sealed record ArtistEdit
{
    /// <summary>The admin's text about the act, or null to keep the encyclopedia's.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Acts to show first among the related artists, in this order, where the collection holds
    /// them.</summary>
    [JsonPropertyName("related")]
    public IReadOnlyList<string>? Related { get; init; }

    /// <summary>Acts never to show among the related artists.</summary>
    [JsonPropertyName("unrelated")]
    public IReadOnlyList<string>? Unrelated { get; init; }

    /// <summary>Nothing changed: the page as it is without an admin.</summary>
    public static readonly ArtistEdit None = new();

    /// <summary>The admin's text where there is some, trimmed; otherwise null.</summary>
    public string? Text => Description?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The collection's own related acts (<see cref="RelatedArtists.Of"/>, closest first) with the admin's
    /// lists on them: the acts named under <see cref="Related"/> that the collection holds first, in the
    /// admin's order, then the rest without any named under <see cref="Unrelated"/>, at most
    /// <see cref="RelatedArtists.Most"/>. Acts are matched by name as the wall matches them
    /// (<see cref="NameMatch.Squash"/>). The act itself is never one.
    /// </summary>
    /// <param name="computed">The related acts the collection found.</param>
    /// <param name="self">The act whose page it is.</param>
    /// <param name="artists">Every act on the artists wall, where the admin's names are looked up.</param>
    public IReadOnlyList<ArtistWallItem> Relate(
        IReadOnlyList<ArtistWallItem> computed, ArtistWallItem self, IReadOnlyList<ArtistWallItem> artists)
    {
        ArgumentNullException.ThrowIfNull(computed);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(artists);

        var related = Related ?? [];
        var unrelated = Unrelated ?? [];
        if (related.Count == 0 && unrelated.Count == 0)
            return computed;

        var never = unrelated.Select(NameMatch.Squash).ToHashSet(StringComparer.Ordinal);
        never.Add(NameMatch.Squash(self.Name));

        var byName = new Dictionary<string, ArtistWallItem>(StringComparer.Ordinal);
        foreach (var act in artists)
            byName.TryAdd(NameMatch.Squash(act.Name), act);

        var shown = new HashSet<string>(StringComparer.Ordinal);
        var first = new List<ArtistWallItem>();
        foreach (var name in related)
        {
            var key = NameMatch.Squash(name);
            if (!never.Contains(key) && byName.TryGetValue(key, out var act) && shown.Add(key))
                first.Add(act);
        }

        var rest = computed.Where(act =>
        {
            var key = NameMatch.Squash(act.Name);
            return !never.Contains(key) && !shown.Contains(key);
        });
        return [.. first.Concat(rest).Take(RelatedArtists.Most)];
    }
}

/// <summary>
/// The admin's edits by act (<see cref="ArtistEdit"/>), asked once per act and kept for the life of the app.
///
/// <para>A failed ask answers <see cref="ArtistEdit.None"/> and is not kept, so the next page opened for the
/// act asks again. A page with no edits is the page as it is without an admin, which is the right thing to
/// show while the API does not answer. The Android TV app's ArtistEdits.kt does the same.</para>
/// </summary>
/// <param name="http">A client aimed at the rpm API with the app's key (see <see cref="Net.RpmApi.At"/>). The
/// endpoint names no user, so no token is sent.</param>
public sealed partial class ArtistEditsClient(HttpClient http)
{
    private readonly ConcurrentDictionary<string, ArtistEdit> _answered = new(StringComparer.Ordinal);

    /// <summary>The edits kept for <paramref name="artist"/>, or null when none have been answered yet.
    /// Never asks.</summary>
    public ArtistEdit? Peek(string? artist) =>
        _answered.TryGetValue(Key(artist), out var edit) ? edit : null;

    /// <summary>The edits for <paramref name="artist"/>: the kept answer, or the API's. Never throws, except
    /// for <paramref name="ct"/>.</summary>
    public async Task<ArtistEdit> OfAsync(string? artist, CancellationToken ct = default)
    {
        var key = Key(artist);
        if (key.Length == 0)
            return ArtistEdit.None;
        if (_answered.TryGetValue(key, out var kept))
            return kept;

        try
        {
            using var response = await http.GetAsync(
                "artist/edits?name=" + Uri.EscapeDataString(artist!.Trim()), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ArtistEdit.None;

            var edit = await response.Content.ReadFromJsonAsync(ArtistEditsJson.Default.ArtistEdit, ct)
                                     .ConfigureAwait(false) ?? ArtistEdit.None;
            _answered[key] = edit;
            return edit;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, a timeout, a body that does not read: no edits, asked again next time.
            RPM.Core.Diagnostics.Faults.Note("artist-edits", "the admin's edits for an artist", ex);
            return ArtistEdit.None;
        }
    }

    /// <summary>As the API keys an act: the name trimmed and in lower case.</summary>
    private static string Key(string? artist) => (artist ?? "").Trim().ToLowerInvariant();

    /// <summary>The one shape this client reads, as the source generator describes it.</summary>
    [JsonSerializable(typeof(ArtistEdit))]
    private sealed partial class ArtistEditsJson : JsonSerializerContext;
}
