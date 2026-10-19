namespace RPM.Core.Metadata;

/// <summary>
/// The tag fields that TagLib#'s base <c>Tag</c> has no property for. Each one is declared once as a
/// <see cref="TagCarrier"/>. They form two groups, for two reasons.
///
/// <para><b>The classical fields.</b> A classical track is not simply a song on a record. It is one movement
/// of one work, played by an ensemble under a conductor, and written by a composer who died long before the
/// soloist was born. If the track is filed only as artist, album and title, all of that is lost. A classical
/// library sorted by artist becomes a list of orchestras. A composer's name can also be filed under the wrong
/// letter, depending on how it was typed. The work and movement fields are what iTunes, Roon and Picard read.
/// The sort names put a composer in one place.</para>
///
/// <para><b>The pressing fields.</b> Barcode, catalogue number and media say which pressing a file came from.
/// These are facts printed on the sleeve, not in the music. A rip is the one moment they are known, and RPM is
/// a record app. The difference between a 1977 UK cut and a 2013 remaster is the whole point.</para>
/// </summary>
public static class ExtendedTags
{
    // Classical fields.

    /// <summary>The composition this track is part of, such as "Symphony No. 5 in C minor, Op. 67". iTunes
    /// stores it in an atom of its own, and shows it above the movement. <c>TIT1</c> is deliberately not used.
    /// TagLib already maps that frame to <see cref="TagLib.Tag.Grouping"/>, and the two would collide.</summary>
    public static readonly TagCarrier Work = new("WORK", AppleAtom: "©wrk");

    /// <summary>This track's movement within the work, such as "I. Allegro con brio".</summary>
    public static readonly TagCarrier MovementName = new("MOVEMENTNAME", Id3Frame: "MVNM", AppleAtom: "©mvn");

    /// <summary>Which movement this is, counted from 1. See <see cref="MovementTag"/>. On ID3 the number and
    /// the total share one frame.</summary>
    public static readonly TagCarrier MovementNumber =
        new("MOVEMENT", Id3Frame: "MVIN", AppleAtom: "©mvi", AppleInteger: true);

    /// <summary>How many movements the work has.</summary>
    public static readonly TagCarrier MovementCount =
        new("MOVEMENTTOTAL", AppleAtom: "©mvc", AppleInteger: true);

    /// <summary>Tells a player to show "Work: Movement" instead of the track title. Nobody edits it by hand.
    /// <see cref="ShowMovementFor"/> decides it from whether the file has a work and a movement to show. That is
    /// the only thing the flag can honestly mean.</summary>
    public static readonly TagCarrier ShowMovement =
        new("SHOWMOVEMENT", AppleAtom: "shwm", AppleInteger: true);

    /// <summary>Who wrote the words. <c>TEXT</c> is the registered ID3 frame for this. WMA calls the same person
    /// the writer.</summary>
    public static readonly TagCarrier Lyricist = new("LYRICIST", Id3Frame: "TEXT", AsfName: "WM/Writer");

    /// <summary>Who arranged this performance of the work.</summary>
    public static readonly TagCarrier Arranger = new("ARRANGER");

    /// <summary>The orchestra or choir, such as "Berliner Philharmoniker". This is separate from the album
    /// artist. On a classical record the album artist is often the composer or the soloist.</summary>
    public static readonly TagCarrier Ensemble = new("ENSEMBLE");

    /// <summary>The featured performers, as the sleeve credits them, such as "Martha Argerich (piano)".</summary>
    public static readonly TagCarrier Soloists = new("SOLOISTS");

    // Pressing fields.

    /// <summary>The EAN or UPC printed on the packaging. It is unique worldwide, so it names one pressing
    /// exactly.</summary>
    public static readonly TagCarrier Barcode = new("BARCODE");

    /// <summary>Printed on the sleeve and stamped in the run-out area. It is the surest way to know that a
    /// pressing is the one in your hands.</summary>
    public static readonly TagCarrier CatalogNumber = new("CATALOGNUMBER");

    /// <summary>
    /// The MusicBrainz disc ID of the CD this came from. It is a hash of the disc's table of contents.
    ///
    /// <para>This is the one identifier on a record that nobody typed. A catalogue number is read off a sleeve,
    /// and a barcode off a box. Either can be entered wrongly, or can belong to a reissue sitting next to it. The
    /// disc ID is computed from where the tracks start on the disc. So two rips of one pressing match, and a rip
    /// of a different pressing does not. Picard writes it under <c>MUSICBRAINZ_DISCID</c>, so a library tagged
    /// with Picard already carries it.</para>
    /// </summary>
    public static readonly TagCarrier MusicBrainzDiscId = new("MUSICBRAINZ_DISCID");

    /// <summary>What it was released on, such as "CD", "12&quot; Vinyl" or "Digital Media". <c>TMED</c> is the
    /// registered ID3 frame.</summary>
    public static readonly TagCarrier Media = new("MEDIA", Id3Frame: "TMED");

    /// <summary>When the recording first came out, as opposed to when this pressing did. A 2013 remaster of a
    /// 1977 album is a 2013 release of a 1977 record. When a shelf is sorted by year, only the first of those two
    /// numbers is useful. <c>TDOR</c> is the ID3v2.4 original-release-time frame.</summary>
    public static readonly TagCarrier OriginalYear = new("ORIGINALYEAR", Id3Frame: "TDOR");

    /// <summary>The title of one disc within a multi-disc set, such as "Disc 2: B-Sides".</summary>
    public static readonly TagCarrier DiscSubtitle =
        new("DISCSUBTITLE", Id3Frame: "TSST", AsfName: "WM/SetSubTitle");

    // Style fields.

    /// <summary>
    /// The style: which corner of its genre a record comes from. For example, "Deep House" is under House, and
    /// "Post-Punk" is under Punk. See <see cref="Subgenres"/>.
    ///
    /// <para><b>It has its own field rather than a second value in the genre list.</b> TagLib's <c>Genres</c> is
    /// an array, and a second slot would be the easy way to do this. But other players would then show the record
    /// as "Rock; Indie Rock". A file that somebody else tagged with two real genres would also seem to have a
    /// style it does not have. The genre tag stays exactly one word, and the style is kept beside it.</para>
    ///
    /// <para>It is written under RPM's own name. <see cref="Style"/> is read as well, and it is never written. See
    /// the note there.</para>
    /// </summary>
    public static readonly TagCarrier Subgenre = new("RPM_SUBGENRE");

    /// <summary>
    /// What other tools call the same thing. It is read as a fallback and never written.
    ///
    /// <para>Discogs calls these styles. The taggers that pull from Discogs, such as Picard's plugins,
    /// foobar2000 and MusicBee, have written them to a <c>STYLE</c> field for years. A library that already has
    /// one carries the answer already. The point of this feature is how many records end up with both halves, so
    /// the field is read.</para>
    ///
    /// <para>It is never written, because RPM's list is its own. RPM's "Deep House" is one of RPM's own names,
    /// with a shelf attached. Writing that into a field that another tool owns would mean RPM deciding what the
    /// other tool's field means.</para>
    /// </summary>
    public static readonly TagCarrier Style = new("STYLE");

    /// <summary>The style a file carries, under RPM's name or under the name the rest of the world uses. RPM's
    /// name is tried first. A file that holds both was written by RPM, and RPM's own value is the one that was
    /// edited.</summary>
    public static string? ReadSubgenre(TagLib.File file) => Subgenre.Read(file) ?? Style.Read(file);

    /// <summary>
    /// Whether a player should show this track as "Work: Movement".
    ///
    /// <para>Nobody edits this value. iTunes uses the flag to mean "this file is part of a larger work". A file
    /// that names both a work and a movement is part of one. A file that names neither is not, whatever a
    /// checkbox once said. Deriving the flag means it cannot fall out of step with the two fields it describes.
    /// It also keeps a switch out of the editor that could only ever be wrong.</para>
    /// </summary>
    public static bool ShowMovementFor(string? work, string? movementName)
        => !string.IsNullOrWhiteSpace(work) && !string.IsNullOrWhiteSpace(movementName);
}

/// <summary>
/// The movement number and its total. They are one fact, stored in two ways.
///
/// <para>mp4 and Vorbis give each half its own field. ID3 packs both into one <c>MVIN</c> frame as "3/4". The
/// track number is packed the same way in <c>TRCK</c>. If each half were written through its own carrier, the
/// second write would overwrite the first. This class does the packing, so neither carrier needs to know about
/// the other.</para>
/// </summary>
public static class MovementTag
{
    /// <summary>Both halves as the file carries them. Each is 0 when the file does not have it.</summary>
    public static (uint Number, uint Count) Read(TagLib.File file)
    {
        ArgumentNullException.ThrowIfNull(file);
        uint number = ExtendedTags.MovementNumber.ReadNumber(file);
        uint count = ExtendedTags.MovementCount.ReadNumber(file);

        // The total half of a packed ID3 frame has no carrier of its own, so it is read from the packed frame.
        if (count == 0 && ExtendedTags.MovementNumber.Read(file) is { } packed &&
            packed.Split('/') is [_, var tail] && uint.TryParse(tail.Trim(), out var parsed))
            count = parsed;

        return (number, count);
    }

    /// <summary>Writes both halves, packed in the way the container wants them. This does NOT save the
    /// file.</summary>
    public static void Write(TagLib.File file, uint number, uint count)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (TagCarrier.CarrierFor(file) == TagLib.TagTypes.Id3v2)
        {
            // One frame, both numbers, using TRCK's convention: "3", or "3/4" when the total is known.
            ExtendedTags.MovementNumber.Write(
                file, number == 0 ? null : count > 0 ? $"{number}/{count}" : number.ToString());
            return;
        }

        ExtendedTags.MovementNumber.WriteNumber(file, number);
        ExtendedTags.MovementCount.WriteNumber(file, count);
    }
}
