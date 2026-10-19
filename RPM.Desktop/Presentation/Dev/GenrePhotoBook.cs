using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RPM.Desktop.Presentation.Dev;

/// <summary>One shelf's entry in <c>scripts/genre-photos.json</c>, as the placer works on it.
///
/// <para>The node it was read from is kept, not a copy of its fields. The file holds a great deal that this
/// tool has no reason to change: the query that found the photograph, why it was chosen over the others,
/// the photographer, and the page. A save that rebuilt each entry from the fields the tool knows would
/// quietly drop all of that.</para>
/// </summary>
internal sealed class GenrePhotoEntry
{
    public required JsonObject Node { get; init; }
    public required string Genre { get; init; }
    public required long Id { get; init; }
    public required string? Source { get; init; }

    /// <summary>The <c>focus</c> the entry was read with. It is kept for the whole session, even after a
    /// frame has been placed over it. The reset goes back to it. An entry that has been placed no longer
    /// has a focus in the file.</summary>
    public required double Focus { get; init; }

    /// <summary>Whether the photograph is mirrored before anything else is done to it.
    ///
    /// <para>This is a property of the SOURCE, not of the frame. It is applied first, so
    /// <see cref="Frame"/>'s across and down are measured on the picture as it will be seen. It is the
    /// other half of the answer to a subject on the wrong side of the band. The window can be moved, or the
    /// picture can be turned round. Nothing in a stage photograph reads as backwards: there is no lettering
    /// and no face that anyone knows. If something does, this is simply left alone.</para>
    /// </summary>
    public bool Flip { get; set; }

    /// <summary>The frame as it was read from the file, or null where the entry has never been placed by
    /// hand.</summary>
    public GenreFrame? Filed { get; set; }

    /// <summary>The frame as it stands in the tool. It is null until the photograph has been measured. The
    /// frame means nothing without the source's size, so it is settled on load and not before.</summary>
    public GenreFrame? Frame { get; set; }

    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }

    /// <summary>Whether what is in <see cref="Frame"/> differs from what is in the file.</summary>
    public bool Changed =>
        Placed != (Filed is not null)
        || Flip != WasFlipped
        || (Placed && Frame is { } now && (Filed is not { } was || !GenreBand.Same(now, was)));

    /// <summary>Whether the file said the picture was mirrored. So a flip that is turned on and then off
    /// again is not a change.</summary>
    public bool WasFlipped { get; set; }

    /// <summary>Whether this entry carries a hand placement. Only an edit sets it. Nothing else does.
    ///
    /// <para><b>It is told, not worked out. That is why it is right.</b> If it were derived from "is there
    /// a frame in hand", every shelf that was only LOOKED at would count as placed. The tool opens each
    /// shelf on the frame that its <c>focus</c> produces. So walking through all thirty-six shelves to see
    /// them would rewrite all thirty-six with numbers that nobody chose. The file would then say that
    /// somebody had decided each of them, which is exactly what the file is there to say.</para>
    /// </summary>
    public bool Placed { get; set; }
}

/// <summary><c>scripts/genre-photos.json</c>, and the photographs it names, opened for editing.
///
/// <para><b>It edits the repository, not the app.</b> That is the whole shape of this tool. The JSON is the
/// source of truth for the genre photographs. <c>gen_genre_photos.py</c> is the only thing that writes the
/// assets. This tool writes numbers into the JSON, for that generator to cut with. Nothing here touches
/// <c>Assets/GenrePhotos</c>. So the generator keeps its single author. It rewrites the assets whole, and a
/// genre taken out of the JSON disappears from both.</para>
///
/// <para><b>Read and written as a tree.</b> The file is round-tripped through <see cref="JsonNode"/> rather
/// than through a model. So the long <c>$comment</c>, the queries, the reasons and the credits come back
/// out exactly as they went in. A save is then a diff of only the numbers that changed.</para>
///
/// <para>DEBUG only. It never appears in a shipped app, because the repository it edits is not
/// there.</para>
/// </summary>
internal sealed class GenrePhotoBook
{
    private readonly JsonObject _document;

    private GenrePhotoBook(string path, string cache, JsonObject document,
                           IReadOnlyList<GenrePhotoEntry> photos)
    {
        Path = path;
        Cache = cache;
        _document = document;
        Photos = photos;
    }

    /// <summary>Where the JSON is, so the tool can say it rather than imply it.</summary>
    public string Path { get; }

    /// <summary>Where the generator keeps the downloaded sources. It is shared with the generator on
    /// purpose. A photograph fetched here is one the generator does not fetch again.</summary>
    public string Cache { get; }

    public IReadOnlyList<GenrePhotoEntry> Photos { get; }

    /// <summary>Finds the repository this build was made in, and opens its book.
    ///
    /// <para>The search starts where the assembly is, and walks up. It is not taken from a setting. A Debug
    /// build runs out of <c>RPM.Desktop/bin/Debug/...</c> inside the tree it was built from. So the tree is
    /// a few levels up, and asking anybody for it would be asking for something already known. It returns
    /// null when there is no tree above. That is a published app, where this tool must never run.</para>
    /// </summary>
    public static GenrePhotoBook? Open()
    {
        if (Repository() is not { } root)
            return null;

        var path = System.IO.Path.Combine(root, "scripts", "genre-photos.json");
        var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                    ?? throw new InvalidDataException($"{path} is not an object.");

        var photos = new List<GenrePhotoEntry>();
        foreach (var node in document["photos"]?.AsArray() ?? [])
        {
            if (node is not JsonObject entry || entry["genre"]?.GetValue<string>() is not { } genre)
                continue;

            photos.Add(new GenrePhotoEntry
            {
                Node = entry,
                Genre = genre,
                Id = entry["id"]?.GetValue<long>() ?? 0,
                Source = entry["source"]?.GetValue<string>(),
                Focus = entry["focus"]?.GetValue<double>() ?? 0.42,
                Filed = Read(entry["frame"] as JsonObject),
                Flip = entry["flip"]?.GetValue<bool>() ?? false,
                WasFlipped = entry["flip"]?.GetValue<bool>() ?? false,
            });

            photos[^1].Placed = photos[^1].Filed is not null;
        }

        return new GenrePhotoBook(
            path, System.IO.Path.Combine(root, "scripts", ".photocache"), document, photos);
    }

    private static GenreFrame? Read(JsonObject? frame)
    {
        if (frame is null)
            return null;

        var across = frame["across"]?.GetValue<double>();
        var down = frame["down"]?.GetValue<double>();
        var height = frame["height"]?.GetValue<double>();
        return across is { } a && down is { } d && height is { } h ? new GenreFrame(a, d, h) : null;
    }

    private static string? Repository()
    {
        var walk = new DirectoryInfo(AppContext.BaseDirectory);
        while (walk is not null)
        {
            if (File.Exists(System.IO.Path.Combine(walk.FullName, "scripts", "genre-photos.json")))
                return walk.FullName;
            walk = walk.Parent;
        }
        return null;
    }

    /// <summary>Gets one photograph's own file. It comes from the generator's cache, or from Pexels into
    /// the cache.
    ///
    /// <para>The cache is keyed by id, not by URL. The generator does the same, for the same reason: a
    /// photograph does not change at Pexels. So a file that is already there is the right file. A placing
    /// session then costs the network nothing at all.</para>
    /// </summary>
    /// <returns>Answered with the bytes, or with null where the entry names no source or the source has no
    /// picture. Unanswered where the source could not be fetched.</returns>
    public async Task<Asked<byte[]?>> SourceAsync(GenrePhotoEntry photo, CancellationToken ct)
    {
        var file = System.IO.Path.Combine(Cache, $"{photo.Id}.jpg");
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > 0)
                return Asked.Answer<byte[]?>(await File.ReadAllBytesAsync(file, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable cache file is fetched again. It is not treated as a fault.
        }

        if (photo.Source is not { Length: > 0 } url)
            return Asked.Answer<byte[]?>(null);

        if (!(await Common.RemoteImage.BytesAsync(url, ct)).Answers(out var bytes, out var why))
            return Asked.Failed<byte[]?>(why);
        if (bytes is not { Length: > 0 })
            return Asked.Answer<byte[]?>(null);

        try
        {
            Directory.CreateDirectory(Cache);
            await File.WriteAllBytesAsync(file, bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The picture is already in hand. If a copy for the generator cannot be saved, it costs one
            // download.
        }

        return Asked.Answer<byte[]?>(bytes);
    }

    /// <summary>Writes every placed frame back into the file. It returns how many entries changed.
    ///
    /// <para><b>A placed entry loses its <c>focus</c>.</b> The two answer the same question: where in the
    /// source the band is taken from. An entry with both would be a file with two authors for one number,
    /// and one of them would be ignored. The reset in the tool still works after a save. The focus figure
    /// is held for the session. An entry that goes back to it has its frame removed, and its focus written
    /// again.</para>
    /// </summary>
    public int Save()
    {
        var moved = 0;

        foreach (var photo in Photos)
        {
            var was = photo.Node["frame"] as JsonObject;

            if (photo.Placed && photo.Frame is { } frame)
            {
                photo.Node["frame"] = new JsonObject
                {
                    ["across"] = Math.Round(frame.Across, 4),
                    ["down"] = Math.Round(frame.Down, 4),
                    ["height"] = Math.Round(frame.Height, 4),
                };
                photo.Node.Remove("focus");
            }
            else
            {
                // Back to the app's own answer. The frame is removed, and the focus it was placed over is
                // written back.
                photo.Node.Remove("frame");
                photo.Node["focus"] = Math.Round(photo.Focus, 4);
            }

            // A mirrored picture says so. A picture that is not mirrored says nothing. So a `false` is not
            // written into every entry that was never turned round.
            if (photo.Flip)
                photo.Node["flip"] = true;
            else
                photo.Node.Remove("flip");

            if (photo.Changed || (was is null) != (photo.Node["frame"] is null))
                moved++;

            photo.Filed = photo.Placed ? photo.Frame : null;
            photo.WasFlipped = photo.Flip;
        }

        File.WriteAllText(Path, _document.ToJsonString(Options) + "\n");
        return moved;
    }

    /// <summary>Written back the way it is written by hand: indented, with an encoder that leaves an
    /// ampersand as an ampersand. The default encoder escapes anything it is unsure of. That would turn
    /// "R&amp;B" and every non-ASCII character in the file into <c>\u</c> escapes, and the save would be a
    /// diff of the whole document.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
