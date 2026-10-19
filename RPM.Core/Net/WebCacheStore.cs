using Microsoft.Data.Sqlite;

namespace RPM.Core.Net;

/// <summary>One answer kept: the body, what it was, and when it stops being trusted.</summary>
public sealed record WebCacheEntry
{
    /// <summary>The hash of the method and the URL. See <see cref="WebCache.KeyFor"/>.</summary>
    public required string Key { get; init; }

    /// <summary>The URL it came from, kept readable so the store can be inspected by a person.</summary>
    public required string Url { get; init; }

    /// <summary>Which cache this belongs to. See <see cref="WebCachePolicy.Category"/>.</summary>
    public required string Category { get; init; }

    public int Status { get; init; }

    public string? ContentType { get; init; }

    /// <summary>The server's version marker, if it sent one. What makes a stale row cheap to
    /// renew.</summary>
    public string? ETag { get; init; }

    /// <summary>Its <c>Last-Modified</c>, for servers that version by date instead.</summary>
    public string? LastModified { get; init; }

    public long FetchedUtcTicks { get; init; }

    public long ExpiresUtcTicks { get; init; }

    public required byte[] Body { get; init; }
}

/// <summary>
/// Where cached answers live. An interface because the handler is the interesting part and it should be
/// testable without a file on disk, and because the phone may one day want a different backing.
/// </summary>
public interface IWebCacheStore
{
    /// <summary>The entry under this key, fresh or stale, or null. Staleness is the caller's to judge: a
    /// stale row is still worth having when the server is unreachable.</summary>
    WebCacheEntry? Read(string key);

    /// <summary>Files an answer, replacing whatever was under that key.</summary>
    void Write(WebCacheEntry entry);

    /// <summary>Moves an entry's dates forward. Used when the server says "not modified".</summary>
    void Renew(string key, long fetchedUtcTicks, long expiresUtcTicks);

    /// <summary>Deletes everything. This is the user's "clear cache" action.</summary>
    void Clear();

    /// <summary>The space the cache uses on disk, shown under the clear button.</summary>
    long BytesOnDisk();
}

/// <summary>
/// The cache on disk: one SQLite file for the rows, and a folder beside it for the big ones.
///
/// <para>Two kinds of data are kept here, and they are stored differently. Catalogue answers are a few
/// kilobytes of JSON. They go in the database, where a lookup is one indexed read. Pictures are hundreds of
/// kilobytes each. They go in files. A database does not reclaim the space of removed pictures unless it is
/// rebuilt, and a folder of image files needs no such work. The size of an answer alone decides which side
/// it goes to, see <see cref="SpillBytes"/>, so callers do not need to know the rule.</para>
///
/// <para>Nothing here is critical. Every path is best effort. A store that cannot be opened, written or
/// read makes the app slower, but not broken. Callers treat a null result the same way as a cache they have
/// never written to.</para>
/// </summary>
public sealed class WebCacheStore : IWebCacheStore, IDisposable
{
    /// <summary>Bodies above this go to a file. Below it, a blob column is cheaper than an inode.</summary>
    private const int SpillBytes = 64 * 1024;

    /// <summary>The most the cache may use on disk before the oldest answers are dropped. A month of one
    /// person's browsing is tens of megabytes, so this limit is generous. It stops runaway growth. It is
    /// not meant to ration space.</summary>
    private const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>The number of writes between size checks. Counting bytes on every write would read the
    /// whole table for no gain. The cap is a backstop, so being a few megabytes over it does no
    /// harm.</summary>
    private const int WritesPerSweep = 64;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private readonly string _blobDir;
    private int _writes;

    /// <param name="dir">The cache folder. Created if it is not there; everything this store owns lives
    /// inside it and nothing else does, so clearing it is deleting its contents.</param>
    public WebCacheStore(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);

        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "web.db");
        _blobDir = Path.Combine(dir, "blobs");
        Directory.CreateDirectory(_blobDir);

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=NORMAL");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS web_cache (
                key                TEXT PRIMARY KEY,
                url                TEXT NOT NULL,
                category           TEXT NOT NULL,
                status             INTEGER NOT NULL,
                content_type       TEXT,
                etag               TEXT,
                last_modified      TEXT,
                fetched_utc_ticks  INTEGER NOT NULL,
                expires_utc_ticks  INTEGER NOT NULL,
                bytes              INTEGER NOT NULL,
                body               BLOB,
                blob_name          TEXT
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_web_cache_fetched ON web_cache(fetched_utc_ticks)");
    }

    public WebCacheEntry? Read(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        try
        {
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    "SELECT url, category, status, content_type, etag, last_modified, " +
                    "fetched_utc_ticks, expires_utc_ticks, body, blob_name " +
                    "FROM web_cache WHERE key = $key";
                cmd.Parameters.AddWithValue("$key", key);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return null;

                var body = reader.IsDBNull(8)
                    ? ReadBlobFile(reader.IsDBNull(9) ? null : reader.GetString(9))
                    : (byte[])reader.GetValue(8);

                // A row whose file is missing, for example because the folder was emptied or a clear did
                // not finish, is not an answer. It is treated as a miss, and the next fetch replaces it.
                if (body is null)
                    return null;

                return new WebCacheEntry
                {
                    Key = key,
                    Url = reader.GetString(0),
                    Category = reader.GetString(1),
                    Status = reader.GetInt32(2),
                    ContentType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ETag = reader.IsDBNull(4) ? null : reader.GetString(4),
                    LastModified = reader.IsDBNull(5) ? null : reader.GetString(5),
                    FetchedUtcTicks = reader.GetInt64(6),
                    ExpiresUtcTicks = reader.GetInt64(7),
                    Body = body,
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public void Write(WebCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            string? blobName = null;
            if (entry.Body.Length > SpillBytes)
            {
                blobName = BlobName(entry.Key);
                var path = BlobPath(blobName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, entry.Body);
            }

            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT INTO web_cache
                        (key, url, category, status, content_type, etag, last_modified,
                         fetched_utc_ticks, expires_utc_ticks, bytes, body, blob_name)
                    VALUES ($key, $url, $category, $status, $type, $etag, $modified,
                            $fetched, $expires, $bytes, $body, $blob)
                    ON CONFLICT(key) DO UPDATE SET
                        url = excluded.url, category = excluded.category, status = excluded.status,
                        content_type = excluded.content_type, etag = excluded.etag,
                        last_modified = excluded.last_modified,
                        fetched_utc_ticks = excluded.fetched_utc_ticks,
                        expires_utc_ticks = excluded.expires_utc_ticks,
                        bytes = excluded.bytes, body = excluded.body, blob_name = excluded.blob_name
                    """;
                cmd.Parameters.AddWithValue("$key", entry.Key);
                cmd.Parameters.AddWithValue("$url", entry.Url);
                cmd.Parameters.AddWithValue("$category", entry.Category);
                cmd.Parameters.AddWithValue("$status", entry.Status);
                cmd.Parameters.AddWithValue("$type", (object?)entry.ContentType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$etag", (object?)entry.ETag ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$modified", (object?)entry.LastModified ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fetched", entry.FetchedUtcTicks);
                cmd.Parameters.AddWithValue("$expires", entry.ExpiresUtcTicks);
                cmd.Parameters.AddWithValue("$bytes", entry.Body.Length);
                cmd.Parameters.AddWithValue("$body", blobName is null ? entry.Body : DBNull.Value);
                cmd.Parameters.AddWithValue("$blob", (object?)blobName ?? DBNull.Value);
                cmd.ExecuteNonQuery();

                if (++_writes % WritesPerSweep == 0)
                    EvictLocked();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cache that cannot be written is a cache that misses. Nothing else changes.
        }
    }

    public void Renew(string key, long fetchedUtcTicks, long expiresUtcTicks)
    {
        try
        {
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    "UPDATE web_cache SET fetched_utc_ticks = $fetched, expires_utc_ticks = $expires " +
                    "WHERE key = $key";
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$fetched", fetchedUtcTicks);
                cmd.Parameters.AddWithValue("$expires", expiresUtcTicks);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM web_cache";
                    cmd.ExecuteNonQuery();
                }

                // The rows are deleted either way. The file gives its space back only when VACUUM runs.
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "VACUUM";
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            try
            {
                if (Directory.Exists(_blobDir))
                    Directory.Delete(_blobDir, recursive: true);
                Directory.CreateDirectory(_blobDir);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }
    }

    public long BytesOnDisk()
    {
        try
        {
            long total = 0;

            // The database, and its write-ahead log, which holds recent writes that are not yet in the main
            // file.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = new FileInfo(_dbPath + suffix);
                if (file.Exists)
                    total += file.Length;
            }

            if (Directory.Exists(_blobDir))
            {
                foreach (var file in Directory.EnumerateFiles(_blobDir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_dbPath}"));
    }

    /// <summary>Drops the least recently fetched rows until the folder is back under the cap. Runs under
    /// <see cref="_gate"/>.</summary>
    private void EvictLocked()
    {
        if (BytesOnDisk() <= MaxBytes)
            return;

        // Removes a quarter of the rows at a time. This stops a cache that is over the cap from being
        // checked again on every write.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT key, blob_name FROM web_cache
            ORDER BY fetched_utc_ticks ASC
            LIMIT MAX(1, (SELECT COUNT(*) FROM web_cache) / 4)
            """;

        var doomed = new List<(string Key, string? Blob)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                doomed.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        foreach (var (key, blob) in doomed)
        {
            using var delete = _connection.CreateCommand();
            delete.CommandText = "DELETE FROM web_cache WHERE key = $key";
            delete.Parameters.AddWithValue("$key", key);
            delete.ExecuteNonQuery();

            if (blob is null)
                continue;
            try { File.Delete(BlobPath(blob)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }

    private byte[]? ReadBlobFile(string? blobName)
    {
        if (string.IsNullOrEmpty(blobName))
            return null;
        try
        {
            var path = BlobPath(blobName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The first two characters of the key name a subfolder, and the key is the file name. A flat
    /// folder of ten thousand covers is slow to list on common file systems.</summary>
    private static string BlobName(string key) => $"{key[..2]}/{key}.bin";

    private string BlobPath(string blobName) =>
        Path.Combine(_blobDir, blobName.Replace('/', Path.DirectorySeparatorChar));

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
