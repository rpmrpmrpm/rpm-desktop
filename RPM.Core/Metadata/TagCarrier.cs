namespace RPM.Core.Metadata;

/// <summary>
/// Where one tag field is stored in each container, and how to read and write it.
///
/// <para>TagLib#'s base <c>Tag</c> covers the fields that all formats agreed on long ago. Many fields added
/// since then have no property. Examples are a disc subtitle, a work, a movement and a barcode. Each
/// container stores these in its own way: an ID3v2 frame in one, a Vorbis comment in another, an iTunes
/// atom in mp4. Each such field is described here as data, so the handling code is written once.</para>
///
/// <para>So the difference between the containers is stated as data. <see cref="VorbisKey"/> is the only
/// value a field must give. It is also used as the APE item, the ASF descriptor and the ID3 <c>TXXX</c>
/// description, unless an override says otherwise. This is the same convention that Picard and foobar2000
/// follow. A field that has a real home in a container names it: <see cref="Id3Frame"/> for the few fields
/// that have a registered frame, and <see cref="AppleAtom"/> for the fields that iTunes stores in an atom
/// of their own.</para>
/// </summary>
/// <param name="VorbisKey">The Vorbis comment name. It is also the APE item, the ASF descriptor and the ID3
/// <c>TXXX</c> description, unless overridden. Upper case by convention.</param>
/// <param name="Id3Frame">A four-character ID3v2 text frame (<c>TSST</c>, <c>TMED</c>, <c>MVNM</c>). Null
/// puts the field in a <c>TXXX</c> user-text frame instead. Most fields belong there.</param>
/// <param name="AppleAtom">A four-character mp4 atom that iTunes reads directly (<c>©wrk</c>, <c>©mvn</c>).
/// Null uses a <c>----:com.apple.iTunes:KEY</c> freeform box, the same one Picard writes.</param>
/// <param name="AppleInteger">The mp4 atom holds a raw integer and not text, for example <c>©mvi</c> or
/// <c>shwm</c>. This is only used together with <see cref="AppleAtom"/>.</param>
/// <param name="AsfName">The WMA descriptor, for the few fields that Windows Media names differently
/// (<c>WM/SetSubTitle</c> rather than <c>DISCSUBTITLE</c>).</param>
public sealed record TagCarrier(
    string VorbisKey,
    string? Id3Frame = null,
    string? AppleAtom = null,
    bool AppleInteger = false,
    string? AsfName = null)
{
    /// <summary>The mp4 freeform "mean". This is the iTunes namespace, so a box that RPM writes stays
    /// visible to other taggers.</summary>
    public const string AppleMean = "com.apple.iTunes";

    /// <summary>The mp4 data box flag for a box that holds a raw integer and not text.</summary>
    private const uint AppleIntegerFlag = 21;

    private string Ape => VorbisKey;
    private string Asf => AsfName ?? VorbisKey;

    /// <summary>
    /// Which tag type carries this field in a given container. It is the tag type that the format's
    /// ecosystem reads. Every carrier uses this, and so does <see cref="AdvisoryTag"/>. So a file gets its
    /// disc subtitle and its work in the same place as everything else.
    /// </summary>
    public static TagLib.TagTypes CarrierFor(TagLib.File file) => file switch
    {
        TagLib.Mpeg4.File => TagLib.TagTypes.Apple,
        TagLib.Ogg.File or TagLib.Flac.File => TagLib.TagTypes.Xiph,
        TagLib.Asf.File => TagLib.TagTypes.Asf,
        TagLib.Ape.File or TagLib.WavPack.File or TagLib.MusePack.File => TagLib.TagTypes.Ape,
        _ => TagLib.TagTypes.Id3v2,
    };

    /// <summary>The value stored in the file, or null when there is none. Every container is checked, not
    /// only the file's native one. A file retagged by another app may hold the field somewhere else.
    /// Reading it back is never the wrong answer.</summary>
    public string? Read(TagLib.File file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return ReadId3v2(file) ?? ReadXiph(file) ?? ReadApple(file) ?? ReadApe(file) ?? ReadAsf(file);
    }

    /// <summary>The value as a number. The result is 0 when the file has no value, or when the value is not
    /// a number.</summary>
    public uint ReadNumber(TagLib.File file)
    {
        // The Apple integer atoms hold raw bytes, not digits. So they are read first. Otherwise the text
        // path would parse an empty string out of them.
        if (AppleInteger && ReadAppleInteger(file) is { } packed)
            return packed;
        var text = Read(file);
        // "3/12" is how ID3 stores a movement number together with its total. The number is the part before
        // the slash.
        var head = text?.Split('/')[0].Trim();
        return uint.TryParse(head, out var n) ? n : 0;
    }

    /// <summary>
    /// Sets the field in the container's own tag. If <paramref name="value"/> is null or blank, the field
    /// is removed. This does not save the file. The caller must call <c>file.Save()</c>, so a write of
    /// twenty fields needs one save and not twenty.
    /// </summary>
    public void Write(TagLib.File file, string? value)
    {
        ArgumentNullException.ThrowIfNull(file);
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        bool keep = value is not null;

        switch (CarrierFor(file))
        {
            case TagLib.TagTypes.Apple:
                if (file.GetTag(TagLib.TagTypes.Apple, keep) is TagLib.Mpeg4.AppleTag apple)
                    WriteApple(apple, value);
                break;

            case TagLib.TagTypes.Xiph:
                if (Xiph(file, create: keep) is { } xiph)
                {
                    if (value is null)
                        xiph.RemoveField(VorbisKey);
                    else
                        xiph.SetField(VorbisKey, value);
                }
                break;

            case TagLib.TagTypes.Ape:
                if (file.GetTag(TagLib.TagTypes.Ape, keep) is TagLib.Ape.Tag ape)
                {
                    if (value is null)
                        ape.RemoveItem(Ape);
                    else
                        ape.SetValue(Ape, value);
                }
                break;

            case TagLib.TagTypes.Asf:
                if (file.GetTag(TagLib.TagTypes.Asf, keep) is TagLib.Asf.Tag asf)
                {
                    if (value is null)
                        asf.RemoveDescriptors(Asf);
                    else
                        asf.SetDescriptorString(value, Asf);
                }
                break;

            default:
                if (file.GetTag(TagLib.TagTypes.Id3v2, keep) is TagLib.Id3v2.Tag id3)
                    WriteId3v2(id3, value);
                break;
        }
    }

    /// <summary>Writes a number. Zero means "not known" for every field that uses this, so zero clears the
    /// field. Zero is never written as a value.</summary>
    public void WriteNumber(TagLib.File file, uint value)
        => Write(file, value == 0 ? null : value.ToString());

    // ID3v2

    private void WriteId3v2(TagLib.Id3v2.Tag id3, string? value)
    {
        if (Id3Frame is { } frame)
        {
            if (value is null)
                id3.SetTextFrame(frame);
            else
                id3.SetTextFrame(frame, value);
            return;
        }

        if (value is null)
        {
            // RemoveFrames would remove by frame ID. Every user-text frame in the file has the same ID,
            // TXXX, including the ISRC, the MusicBrainz ids and the replay gain. So only the frame with
            // this field's description is removed.
            if (TagLib.Id3v2.UserTextInformationFrame.Get(id3, VorbisKey, false) is { } existing)
                id3.RemoveFrame(existing);
        }
        else
        {
            TagLib.Id3v2.UserTextInformationFrame.Get(id3, VorbisKey, true).Text = [value];
        }
    }

    private string? ReadId3v2(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag tag)
            return null;
        if (Id3Frame is not { } frame)
            return Trim(TagLib.Id3v2.UserTextInformationFrame.Get(tag, VorbisKey, false)?.Text?.FirstOrDefault());

        return Trim(TagLib.Id3v2.TextInformationFrame.Get(tag, frame, false)?.Text?.FirstOrDefault())
            ?? ReadForeignTextFrame(tag, frame);
    }

    /// <summary>
    /// Reads a text frame that TagLib does not return as text.
    ///
    /// <para>ID3v2 says that a text frame's ID starts with <c>T</c>. TagLib's frame factory relies on this
    /// rule. When reading, a frame whose ID does not start with <c>T</c> is returned as an
    /// <c>UnknownFrame</c>, and <see cref="TagLib.Id3v2.TextInformationFrame.Get"/> returns null for it.
    /// iTunes defined the movement frames <c>MVNM</c> and <c>MVIN</c> as text frames, which do not follow
    /// the rule. So these frames are written correctly and stay in the file, but they read back as nothing
    /// at all.</para>
    ///
    /// <para>The payload is the same as a normal text frame: one encoding byte, then the string. Decoding
    /// it here keeps the change to the frames that need it. Registering a creator with TagLib's global
    /// factory would change every read, and every read would then depend on that having happened
    /// first.</para>
    /// </summary>
    private static string? ReadForeignTextFrame(TagLib.Id3v2.Tag tag, string frameId)
    {
        foreach (var frame in tag.GetFrames(frameId))
        {
            if (frame is not TagLib.Id3v2.UnknownFrame { Data.Count: > 1 } unknown)
                continue;

            var encoding = unknown.Data[0] switch
            {
                0 => TagLib.StringType.Latin1,
                1 => TagLib.StringType.UTF16,     // carries its own byte-order mark
                2 => TagLib.StringType.UTF16BE,
                _ => TagLib.StringType.UTF8,
            };
            // Some writers add a null terminator to the string. The terminator is not part of the text.
            var text = unknown.Data.Mid(1).ToString(encoding).TrimEnd('\0');
            if (Trim(text) is { } value)
                return value;
        }
        return null;
    }

    // mp4

    /// <summary>An atom name as its four bytes. The copyright sign that starts many names is 0xA9 in the
    /// mp4 single-byte encoding. If the name were passed to TagLib as a UTF-8 string, that sign would take
    /// two bytes. The atom would then never be read back.</summary>
    private static TagLib.ReadOnlyByteVector Atom(string name)
        => new([.. name.Select(c => (byte)c)]);

    private void WriteApple(TagLib.Mpeg4.AppleTag apple, string? value)
    {
        if (AppleAtom is not { } name)
        {
            apple.SetDashBox(AppleMean, VorbisKey, value);
            return;
        }

        var atom = Atom(name);
        if (value is null)
            apple.ClearData(atom);
        else if (AppleInteger)
            apple.SetData(atom, [PackInteger(value)], AppleIntegerFlag);
        else
            apple.SetText(atom, value);
    }

    /// <summary>An integer atom's payload. iTunes writes these big-endian, and as wide as the number needs:
    /// one byte for a movement number, and four bytes for anything larger.</summary>
    private static TagLib.ByteVector PackInteger(string value)
    {
        uint n = uint.TryParse(value.Split('/')[0].Trim(), out var parsed) ? parsed : 0;
        return n <= byte.MaxValue
            ? new TagLib.ByteVector((byte)n)
            : new TagLib.ByteVector([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
    }

    private string? ReadApple(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Apple, false) is not TagLib.Mpeg4.AppleTag tag)
            return null;
        if (AppleAtom is not { } name)
            return Trim(tag.GetDashBox(AppleMean, VorbisKey));
        return AppleInteger
            ? ReadAppleInteger(file)?.ToString()
            : Trim(tag.GetText(Atom(name)).FirstOrDefault());
    }

    private uint? ReadAppleInteger(TagLib.File file)
    {
        if (AppleAtom is not { } name ||
            file.GetTag(TagLib.TagTypes.Apple, false) is not TagLib.Mpeg4.AppleTag tag)
            return null;

        foreach (var box in tag.DataBoxes([Atom(name)]))
        {
            if (box.Data.Count == 0)
                continue;
            uint n = 0;
            // Big-endian, however many bytes iTunes used for the number.
            foreach (var b in box.Data.Take(4))
                n = (n << 8) | b;
            return n;
        }
        return null;
    }

    // The rest

    private static TagLib.Ogg.XiphComment? Xiph(TagLib.File file, bool create)
        => file.GetTag(TagLib.TagTypes.Xiph, create) switch
        {
            TagLib.Ogg.XiphComment comment => comment,
            TagLib.Ogg.GroupedComment grouped => grouped.Comments.FirstOrDefault(),
            _ => null,
        };

    private string? ReadXiph(TagLib.File file)
        => Xiph(file, create: false) is { } tag ? Trim(tag.GetFirstField(VorbisKey)) : null;

    private string? ReadApe(TagLib.File file)
        => file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag tag
            ? Trim(tag.GetItem(Ape)?.ToStringArray().FirstOrDefault())
            : null;

    private string? ReadAsf(TagLib.File file)
        => file.GetTag(TagLib.TagTypes.Asf, false) is TagLib.Asf.Tag tag
            ? Trim(tag.GetDescriptorString(Asf))
            : null;

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
