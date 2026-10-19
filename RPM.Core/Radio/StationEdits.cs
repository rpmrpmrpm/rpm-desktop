namespace RPM.Core.Radio;

/// <summary>
/// How the admin changed the way one station is shown: its title, its tags, the line on its
/// banner card. Each value is null where the station's own is used.
/// </summary>
/// <param name="Tags">Replaces the directory's tags for which genres the station is listed under.
/// The ads rule still reads the directory's own (<see cref="RadioAds"/>).</param>
public sealed record StationEdit(string? Name, IReadOnlyList<string>? Tags, string? Description);

/// <summary>
/// The station edits the rpm API publishes in <c>radio/state</c>, read whenever a station is drawn.
///
/// <para><b>Read, never written onto the station.</b> <see cref="RadioStation.Name"/> and
/// <see cref="RadioStation.Tags"/> stay exactly as the directory has them, because they are what a
/// favourite is saved under, what two rows of one station are folded by, what the cloud's listens
/// record and what the ads rule reads. <see cref="RadioStation.Display"/>,
/// <see cref="RadioStation.ShownTags"/> and the banner's line ask here instead, so a list drawn
/// after the answer lands shows the edit, whichever list it is and however its stations were
/// made.</para>
///
/// <para><b>Looked up under every row of a folded station.</b> The admin edits one directory row,
/// and the fold keeps whichever row has the best stream (<see cref="RadioResults"/>), so the row
/// that was edited is often in <see cref="RadioStation.AlsoFiledAs"/>. The kept row's own edit wins;
/// after it, the first folded row that has one.</para>
/// </summary>
public static class StationEdits
{
    private static Set _now = Set.None;

    /// <summary>The edits as the last answer had them. <see cref="Set.None"/> until one lands.</summary>
    public static Set Now => Volatile.Read(ref _now);

    /// <summary>This station's edit as things stand, or null where it has none.</summary>
    public static StationEdit? Of(RadioStation station) => Now.Of(station);

    /// <summary>Takes a new answer's edits. Whether they differ from the ones held, so the caller
    /// can draw again only when something moved.</summary>
    public static bool Replace(Set next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var was = Interlocked.Exchange(ref _now, next);
        return !was.SameAs(next);
    }

    /// <summary>One answer's edits, by uuid, compared without regard to case.</summary>
    public sealed class Set
    {
        public static readonly Set None = new([]);

        private readonly Dictionary<string, StationEdit> _byUuid;

        public Set(IEnumerable<KeyValuePair<string, StationEdit>> edits)
        {
            _byUuid = new Dictionary<string, StationEdit>(StringComparer.OrdinalIgnoreCase);
            foreach (var (uuid, edit) in edits)
            {
                if (!string.IsNullOrWhiteSpace(uuid) && edit is not null)
                    _byUuid[uuid.Trim()] = Tidy(edit);
            }
        }

        public int Count => _byUuid.Count;

        /// <summary>The station's edit: its kept row's first, else the first folded row's.</summary>
        public StationEdit? Of(RadioStation station)
        {
            if (_byUuid.Count == 0 || station is null)
                return null;

            if (_byUuid.TryGetValue(station.Uuid, out var own))
                return own;

            foreach (var also in station.AlsoFiledAs)
            {
                if (_byUuid.TryGetValue(also, out var folded))
                    return folded;
            }

            return null;
        }

        /// <summary>
        /// The stations whose tags were edited, with those tags.
        ///
        /// <para>What a genre search adds to the directory's answer. The directory is asked for a
        /// genre by its own tags, so a station the admin filed under a genre it is not tagged with
        /// there is never in that answer and has to be fetched by its uuid
        /// (<see cref="RadioBrowserClient.ByUuidAsync"/>).</para>
        /// </summary>
        public IReadOnlyList<(string Uuid, IReadOnlyList<string> Tags)> Retagged()
            => [.. _byUuid
                .Where(p => p.Value.Tags is { Count: > 0 })
                .Select(p => (p.Key, p.Value.Tags!))];

        /// <summary>The uuids of the retagged stations a genre takes in, in a stable order.</summary>
        public IReadOnlyList<string> RetaggedInto(string genre)
            => [.. Retagged()
                .Where(r => RadioGenres.Is(RadioNames.Genres(r.Tags), genre))
                .Select(r => r.Uuid)
                .Order(StringComparer.OrdinalIgnoreCase)];

        /// <summary>Whether two answers say the same thing about every station.</summary>
        public bool SameAs(Set other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other._byUuid.Count != _byUuid.Count)
                return false;

            foreach (var (uuid, edit) in _byUuid)
            {
                if (!other._byUuid.TryGetValue(uuid, out var theirs)
                    || edit.Name != theirs.Name
                    || edit.Description != theirs.Description
                    || !SameTags(edit.Tags, theirs.Tags))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameTags(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
            => a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

        /// <summary>Blank values as null, so an empty title or line is never drawn, and tags read
        /// as the directory's are (lower case, no blanks, no repeats).</summary>
        private static StationEdit Tidy(StationEdit edit)
        {
            var tags = edit.Tags?
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new StationEdit(
                string.IsNullOrWhiteSpace(edit.Name) ? null : edit.Name.Trim(),
                tags is { Count: > 0 } ? tags : null,
                string.IsNullOrWhiteSpace(edit.Description) ? null : edit.Description.Trim());
        }
    }
}
