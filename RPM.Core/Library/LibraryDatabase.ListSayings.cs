using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Library;

/// <summary>A song the listener said fits, or does not fit, one kind of generated list.</summary>
/// <param name="RpmId">The song, by the id every device shares.</param>
public sealed record ListVerdict(string RpmId, SmartPlaylistKind Kind, TrackVerdict Verdict, long SaidUtcTicks);

/// <summary>An act or a record one kind of generated list must stop offering.</summary>
/// <param name="Subject">The credit for an act, the folder for a record.</param>
public sealed record ListRefusal(RecommendScope Scope, string Subject, SmartPlaylistKind Kind, long SaidUtcTicks);

/// <summary>
/// Everything the listener has said about the generated lists in one library, as it travels to the store
/// (<c>cloud/list-sayings</c>). Sent whole: it is small, and a whole copy cannot drift from the library it came from.
/// </summary>
public sealed record ListSayings(IReadOnlyList<ListVerdict> Verdicts, IReadOnlyList<ListRefusal> Refusals)
{
    public static readonly ListSayings None = new([], []);

    /// <summary>A short hash of the contents, in a fixed order, so a sender can tell whether anything changed since
    /// its last send.</summary>
    public string Fingerprint
    {
        get
        {
            var text = new StringBuilder();
            foreach (var v in Verdicts.OrderBy(v => v.RpmId, StringComparer.Ordinal).ThenBy(v => v.Kind))
                text.Append('v').Append(v.RpmId).Append('|').Append((int)v.Kind).Append('|').Append((int)v.Verdict).Append('\n');
            foreach (var r in Refusals.OrderBy(r => r.Scope).ThenBy(r => r.Subject, StringComparer.Ordinal).ThenBy(r => r.Kind))
                text.Append('r').Append((int)r.Scope).Append('|').Append(r.Subject).Append('|').Append((int)r.Kind).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        }
    }
}

/// <summary>
/// The verdicts and refusals, taken out of a library for the store and put into the store's own library.
///
/// <para>A computer is where the listener says these things, so a computer's library is the source. The store
/// cuts lists for the phones from a library of its own (see the server's list builder), which takes the sayings
/// in whole before it cuts, so a list cut for a phone keeps to what was said on the computer.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>The fingerprint of the sayings the store last took (<see cref="ListSayings.Fingerprint"/>).</summary>
    public const string ListSayingsSentKey = "list_sayings_sent";

    /// <summary>
    /// What the listener has said in this library. A verdict on a song with no cloud id is left out: the store has
    /// nothing to join it to.
    /// </summary>
    public ListSayings ListSayingsToSend()
    {
        using (_readGate.Take())
        {
            var verdicts = new List<ListVerdict>();
            using (var cmd = _read.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT tracks.rpm_id, track_verdict.kind, track_verdict.verdict, track_verdict.said_utc_ticks
                    FROM track_verdict JOIN tracks ON tracks.relative_path = track_verdict.relative_path
                    WHERE tracks.rpm_id IS NOT NULL AND tracks.rpm_id <> ''
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    verdicts.Add(new ListVerdict(
                        reader.GetString(0), (SmartPlaylistKind)reader.GetInt64(1), (TrackVerdict)reader.GetInt64(2), reader.GetInt64(3)));
            }

            var refusals = new List<ListRefusal>();
            using (var cmd = _read.CreateCommand())
            {
                cmd.CommandText = "SELECT scope, subject, kind, said_utc_ticks FROM recommend_refusal";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    refusals.Add(new ListRefusal(
                        (RecommendScope)reader.GetInt64(0), reader.GetString(1), (SmartPlaylistKind)reader.GetInt64(2), reader.GetInt64(3)));
            }

            return new ListSayings(verdicts, refusals);
        }
    }

    /// <summary>
    /// Replaces this library's verdicts and refusals with <paramref name="said"/>. A verdict on a song this library
    /// does not hold is left out.
    ///
    /// <para>Written straight into the two tables, not through <see cref="SetTrackVerdict"/>: that stamps the
    /// track as edited, which on a computer would send every song back up with the next catalogue pass.</para>
    /// </summary>
    public void AcceptListSayings(ListSayings said)
    {
        ArgumentNullException.ThrowIfNull(said);

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            using (var clear = _connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM track_verdict; DELETE FROM recommend_refusal;";
                clear.ExecuteNonQuery();
            }

            using (var verdict = _connection.CreateCommand())
            {
                verdict.CommandText =
                    """
                    INSERT OR REPLACE INTO track_verdict (relative_path, kind, verdict, said_utc_ticks)
                    SELECT relative_path, $kind, $verdict, $at FROM tracks WHERE rpm_id = $id
                    """;
                var id = verdict.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
                var kind = verdict.Parameters.Add("$kind", Microsoft.Data.Sqlite.SqliteType.Integer);
                var value = verdict.Parameters.Add("$verdict", Microsoft.Data.Sqlite.SqliteType.Integer);
                var at = verdict.Parameters.Add("$at", Microsoft.Data.Sqlite.SqliteType.Integer);
                foreach (var v in said.Verdicts.Where(v => v.Verdict != TrackVerdict.None && !string.IsNullOrEmpty(v.RpmId)))
                {
                    id.Value = v.RpmId;
                    kind.Value = (int)v.Kind;
                    value.Value = (int)v.Verdict;
                    at.Value = v.SaidUtcTicks;
                    verdict.ExecuteNonQuery();
                }
            }

            using (var refusal = _connection.CreateCommand())
            {
                refusal.CommandText =
                    """
                    INSERT OR REPLACE INTO recommend_refusal (scope, subject, kind, said_utc_ticks)
                    VALUES ($scope, $subject, $kind, $at)
                    """;
                var scope = refusal.Parameters.Add("$scope", Microsoft.Data.Sqlite.SqliteType.Integer);
                var subject = refusal.Parameters.Add("$subject", Microsoft.Data.Sqlite.SqliteType.Text);
                var kind = refusal.Parameters.Add("$kind", Microsoft.Data.Sqlite.SqliteType.Integer);
                var at = refusal.Parameters.Add("$at", Microsoft.Data.Sqlite.SqliteType.Integer);
                foreach (var r in said.Refusals.Where(r => !string.IsNullOrWhiteSpace(r.Subject)))
                {
                    scope.Value = (int)r.Scope;
                    subject.Value = r.Subject.Trim();
                    kind.Value = (int)r.Kind;
                    at.Value = r.SaidUtcTicks;
                    refusal.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        // The held refusals are now wrong. See SetRecommendRefusal.
        using (_cuts.Take())
            _refusalsSaid = null;
    }
}
