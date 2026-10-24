using System.Text.Json;
using System.Text.Json.Serialization;

using RPM.Core.Net;
using RPM.Core.Radio;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// What has been decided about radio stations since this copy last checked: which ones not to show, whether
/// any logo has changed, and which stations are shown with a different title, tags or description. These
/// are held in <see cref="StationEdits"/>, which RPM.Core reads when a station is drawn.
///
/// <para><b>Why the app has to be told.</b> Everything fetched here is kept for thirty days, and the
/// server's cache headers are not used. <see cref="WebCachePolicy"/> says so on purpose. For a fact that
/// does not change, such as a 1973 record, that is the right choice. A station logo is not that kind of
/// fact. Without this state, a logo replaced in the admin would reach a user who already had the old one
/// for about a month, and a station removed from the listing would never leave it.</para>
///
/// <para><b>One small document, once an hour.</b> The alternative is to check each logo on screen. That
/// would send a flood of requests and make the radio pane slow to open. This document is a couple of
/// hundred bytes in the usual case, because the cache sends the ETag back and the server replies
/// 304.</para>
///
/// <para><b>Read without waiting, always.</b> <see cref="Hidden"/> and <see cref="LogoStamp"/> hold the
/// last answer that arrived, and they are read straight from a field. <c>RadioModel.Show</c> asks for them
/// on the drawing thread. A property there that awaited anything would stall the list. Before the first
/// answer arrives they say "nothing is hidden" and "no stamp", so the app shows the list as it would
/// without this state.</para>
///
/// <para><b>The essential cards' pictures.</b> The answer also lists the stations the admin gave a 16:9
/// card picture for the essential row (<c>cards</c>), with a stamp that moves whenever one changes
/// (<c>cardStamp</c>). The picture itself is served at <c>radio/card/{uuid}</c> (see
/// <see cref="CardAddress"/>). The TV reads the same two fields (core/radio, RadioRepository and
/// StationCards).</para>
/// </summary>
internal static partial class RadioState
{
    /// <summary>One hour. Long enough that it is not a cost on every launch. Short enough that a change in
    /// the admin does not wait a working day.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(1);

    /// <summary>
    /// Kept for one hour, then checked again. Everything else in this cache is kept for thirty days. A
    /// document whose job is to say what has gone stale cannot itself be kept for a month.
    ///
    /// <para>The key is "state-v3" because the answer now includes the card pictures (v2 added the station
    /// edits). An older answer on disk has neither, but its ETag still matches while the admin's file is
    /// unchanged. Under the old key, a 304 would keep serving the answer from before they existed.</para>
    /// </summary>
    private static readonly WebCachePolicy Policy =
        new("radio", Fresh, TimeSpan.FromMinutes(5)) { Vary = "state-v3" };

    /// <summary>Built the same way <see cref="RadioLogoApi"/> builds its URLs: the dev override first, then
    /// the shipped deployment. This way the state and the logos it describes always use the same
    /// server.</summary>
    private static readonly Lazy<HttpClient> Http = new(() =>
        AppCache.Client(Policy, RpmApi.At(null) ?? RpmApi.Deployed, TimeSpan.FromSeconds(15)));

    /// <summary>The whole answer, replaced as one. A reader sees either the old snapshot or the new one,
    /// never a hidden list from one answer with a stamp from another.</summary>
    private sealed record Snapshot(
        string Logos, IReadOnlySet<string> Hidden, IReadOnlySet<string> ByHand,
        IReadOnlySet<string> Cards, string CardStamp)
    {
        public static readonly Snapshot Nothing = new(
            "",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "");
    }

    private static Snapshot _now = Snapshot.Nothing;

    /// <summary>Guards the fetch, not the read: one request however many callers ask at once.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _asked = DateTimeOffset.MinValue;

    /// <summary>Whether this launch has asked the server yet, as opposed to the disk.</summary>
    private static int _askedOnce;

    /// <summary>Raised on the thread the fetch finished on, when the answer changed something.
    /// <c>RadioModel</c> uses it to redraw the list. The first answer usually arrives after the pane
    /// is already up.</summary>
    public static event Action? Changed;

    /// <summary>Stations not to show. Empty until the first answer lands.</summary>
    public static IReadOnlySet<string> Hidden => Volatile.Read(ref _now).Hidden;

    /// <summary>The text added to a logo URL so that a logo changed by hand is requested again. It is empty
    /// where nothing has been changed by hand. That is the usual case, and it costs nothing.</summary>
    public static string LogoStamp => Volatile.Read(ref _now).Logos;

    /// <summary>
    /// Whether somebody chose this station's logo.
    ///
    /// <para>The logo search follows the server's answer for these, and does not measure the logo against
    /// what the directory has filed. A hand-picked logo often loses that comparison. For example, an opaque
    /// 300 px upload ranks below a 512 px picture. The station would keep the old logo, and the upload
    /// would look as though it had failed.</para>
    /// </summary>
    public static bool IsByHand(string? uuid)
        => uuid is { Length: > 0 } && Volatile.Read(ref _now).ByHand.Contains(uuid);

    /// <summary>
    /// The address of this station's card picture, the 16:9 picture the admin chose for its card in the
    /// essential row, or null where it has none (most stations, and every station before the first answer).
    ///
    /// <para>A station is several directory rows folded into one, and the admin saves the picture under
    /// every row of it, so its own uuid is asked first and then the folded ones, as the TV does
    /// (StationCards.addressOf). The stamp goes on the address, as on a logo's (see RadioLogoApi.For): the
    /// app's cache keeps a picture for thirty days, so a changed picture has to be a new address.</para>
    /// </summary>
    public static string? CardAddress(RadioStation station)
    {
        var now = Volatile.Read(ref _now);
        if (now.Cards.Count == 0 || station is null)
            return null;

        var uuid = new[] { station.Uuid }.Concat(station.AlsoFiledAs)
            .FirstOrDefault(u => u is { Length: > 0 } && now.Cards.Contains(u));
        if (uuid is null || !Guid.TryParse(uuid, out var id))
            return null;

        var url = RpmApi.BaseUrl() + $"radio/card/{id:D}";
        return now.CardStamp is { Length: > 0 } stamp ? url + "?v=" + Uri.EscapeDataString(stamp) : url;
    }

    /// <summary>Whether this station has been taken out of the app.</summary>
    public static bool IsHidden(string? uuid)
        => uuid is { Length: > 0 } && Volatile.Read(ref _now).Hidden.Contains(uuid);

    /// <summary>
    /// Whether this ROW has been taken out of the app.
    ///
    /// <para>This is not the same question as <see cref="IsHidden(string?)"/> asked with its Uuid. A
    /// station reaches the list as several directory rows folded into one, and the fold keeps a single
    /// uuid. Reading that one uuid misses a station hidden under any of the others.</para>
    ///
    /// <para><b>Every row behind it has to be hidden, not just one.</b> The admin lists the directory's
    /// rows, and hides the one that was pressed. A row is only gone from here when nothing is left of it.
    /// Hiding one row alone is wrong: "NPO 2" and "NPO Radio 2" fold together, so hiding the first would
    /// take the second off the air too.</para>
    ///
    /// <para>The phone's <c>RadioState.isHidden</c> (in RPMRadio) is a port of this. Change both, so that
    /// the two agree on which stations are hidden.</para>
    /// </summary>
    public static bool IsHidden(RadioStation station)
    {
        var hidden = Volatile.Read(ref _now).Hidden;
        if (hidden.Count == 0 || station is null)
            return false;

        if (!hidden.Contains(station.Uuid))
            return false;

        foreach (var also in station.AlsoFiledAs)
        {
            if (!hidden.Contains(also))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Asks, if it has not been asked within the hour. Nothing waits on it and nothing is reported. A
    /// reader is looking at a list of radio stations, and without the answer the app shows every station
    /// and keeps the logos it already has.
    /// </summary>
    public static void Refresh()
    {
        if (DateTimeOffset.UtcNow - _asked < Fresh)
        {
            Say($"not asking: last answer was {(DateTimeOffset.UtcNow - _asked).TotalMinutes:F0} " +
                $"minutes ago, {Hidden.Count} hidden");
            return;
        }

        _ = Task.Run(AskAsync);
    }

    /// <summary>Diagnostic output, shown when RPM_RADIO_DIAG=1. It matches the [logo] channel. See
    /// <c>RadioModel.Walked</c>.</summary>
    private static void Say(string line)
    {
        if (Diag) Console.WriteLine("[radio-state] " + line);
    }

    private static readonly bool Diag =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_RADIO_DIAG") is "1" or "true";

    private static async Task AskAsync()
    {
        if (!await Gate.WaitAsync(0))
            return;      // somebody is already asking

        try
        {
            if (DateTimeOffset.UtcNow - _asked < Fresh)
                return;

            // The first ask of each launch goes past the cache.
            //
            // Without this, restarting the app would change nothing. _asked is reset on each launch, but
            // the answer is on disk with an hour left to run, so the handler would serve it without asking
            // anybody. Someone who has just changed a logo and restarts to see it would get the state from
            // before the change. A restart is meant to rule that out. This costs one small request per
            // launch. The reply is still saved for the rest of the hour.
            using var request = new HttpRequestMessage(HttpMethod.Get, "radio/state");
            if (Interlocked.Exchange(ref _askedOnce, 1) == 0)
                request.Options.Set(WebCache.BypassOption, true);

            using var response = await Http.Value.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Say($"asked, got http {(int)response.StatusCode}; keeping the last answer");
                // Marked as asked anyway. A server that is unwell should not be asked again on every
                // keystroke in the search box.
                _asked = DateTimeOffset.UtcNow;
                return;
            }

            var read = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(), RadioStateJson.Default.Answer);

            _asked = DateTimeOffset.UtcNow;
            if (read is null)
                return;

            var next = new Snapshot(
                read.Logos ?? "",
                new HashSet<string>(read.Hidden ?? [], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(read.ByHand ?? [], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>((read.Cards ?? []).Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase),
                read.CardStamp ?? "");

            var was = Volatile.Read(ref _now);
            Volatile.Write(ref _now, next);

            // The edits are held in RPM.Core, where Display and the sieve read them. They are replaced
            // after the snapshot and before Changed, so anyone who draws on Changed sees both.
            var edited = StationEdits.Replace(new StationEdits.Set(
                (read.Edits ?? []).Select(e => KeyValuePair.Create(
                    e.Key, new StationEdit(e.Value.Name, e.Value.Tags, e.Value.Description)))));

            var moved = was.Logos != next.Logos
                        || !was.Hidden.SetEquals(next.Hidden)
                        || !was.ByHand.SetEquals(next.ByHand)
                        || !was.Cards.SetEquals(next.Cards)
                        || was.CardStamp != next.CardStamp
                        || edited;

            // This is printed when RPM_RADIO_DIAG=1 is set. The output answers three questions: whether the
            // server was asked, what came back, and whether the list was drawn again. Without them, a
            // station hidden in the admin that is still listed cannot be traced.
            Say($"answered: {next.Hidden.Count} hidden, {next.ByHand.Count} chosen by hand, " +
                $"{StationEdits.Now.Count} edited, {next.Cards.Count} card pictures, stamp \"{next.Logos}\"" +
                (moved ? " (changed, redrawing)" : " (no change)"));

            // Changed is raised only when something actually moved. The hourly ask is the common case, and
            // it almost always gives the same answer. Raising it every time would redraw the list for
            // nothing, once an hour.
            if (moved)
                Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say($"could not ask: {ex.GetType().Name} {ex.Message}");

            // Offline, or the API is not reachable. The last answer stands. With no answer at all, the app
            // shows every station and keeps the logos it already has.
            _asked = DateTimeOffset.UtcNow;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed record Answer
    {
        [JsonPropertyName("logos")] public string? Logos { get; init; }
        [JsonPropertyName("hidden")] public string[]? Hidden { get; init; }
        [JsonPropertyName("byHand")] public string[]? ByHand { get; init; }
        [JsonPropertyName("edits")] public Dictionary<string, EditAnswer>? Edits { get; init; }
        [JsonPropertyName("cards")] public string[]? Cards { get; init; }
        [JsonPropertyName("cardStamp")] public string? CardStamp { get; init; }
    }

    /// <summary>One station's edit as the API sends it. Null values are the directory's own.</summary>
    private sealed record EditAnswer
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("tags")] public string[]? Tags { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    /// <summary>The one answer this reads. A published build is Native AOT, which serializes from this and
    /// never by reflection.</summary>
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(Answer))]
    [JsonSerializable(typeof(EditAnswer))]
    [JsonSerializable(typeof(Dictionary<string, EditAnswer>))]
    private sealed partial class RadioStateJson : JsonSerializerContext;
}
