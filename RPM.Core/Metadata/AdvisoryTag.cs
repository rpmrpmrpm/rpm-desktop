using RPM.Core.Models;

namespace RPM.Core.Metadata;

/// <summary>
/// The parental-advisory flag: whether a track's lyrics are explicit. TagLib#'s base <c>Tag</c> has no
/// property for it. So each container stores it where the wider ecosystem (iTunes, Picard, foobar2000,
/// Plex) reads it. That is the <c>rtng</c> atom in mp4 and m4a files. Elsewhere it is an
/// <c>ITUNESADVISORY</c> field: a Vorbis comment in flac, ogg and opus files, a <c>TXXX</c> frame in mp3,
/// wav and aiff files, and an APE item in ape, wv and mpc files.
///
/// <para>The stored value follows iTunes. 1 is explicit, 2 is clean, and absent or 0 means nothing is known.
/// Some older iTunes files store 4 for explicit. It reads the same as 1. Treating it as "no idea" would hide a
/// flag that the file really has.</para>
/// </summary>
public static class AdvisoryTag
{
    /// <summary>Vorbis comment, APE item, or <c>TXXX</c> description.</summary>
    public const string FieldName = "ITUNESADVISORY";

    /// <summary>The MP4 atom that iTunes stores the rating in.</summary>
    public static readonly TagLib.ReadOnlyByteVector AppleAtom = new("rtng");

    /// <summary>MP4 data-box flag for a box that holds a raw integer rather than text.</summary>
    private const uint AppleInteger = 21;

    /// <summary>What the file already says about its lyrics, or <see cref="ContentAdvisory.Unknown"/> when
    /// it says nothing.</summary>
    public static ContentAdvisory Read(TagLib.File file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return ReadApple(file) ?? ReadXiph(file) ?? ReadId3v2(file) ?? ReadApe(file)
               ?? ContentAdvisory.Unknown;
    }

    /// <summary>
    /// Stamps the flag in the container's native carrier. When <paramref name="advisory"/> is
    /// <see cref="ContentAdvisory.Unknown"/>, it clears the flag instead. This does NOT save the file. The
    /// caller calls <c>file.Save()</c>, as it does for every <see cref="TagCarrier"/>.
    /// </summary>
    public static void Write(TagLib.File file, ContentAdvisory advisory)
    {
        ArgumentNullException.ThrowIfNull(file);
        bool clearing = advisory == ContentAdvisory.Unknown;
        var text = ((int)advisory).ToString();

        switch (CarrierFor(file))
        {
            case TagLib.TagTypes.Apple:
                if (file.GetTag(TagLib.TagTypes.Apple, !clearing) is TagLib.Mpeg4.AppleTag apple)
                {
                    if (clearing)
                        apple.ClearData(AppleAtom);
                    else
                        apple.SetData(AppleAtom, [new TagLib.ByteVector((byte)advisory)], AppleInteger);
                }
                break;

            case TagLib.TagTypes.Xiph:
                if (Xiph(file, create: !clearing) is { } xiph)
                {
                    if (clearing)
                        xiph.RemoveField(FieldName);
                    else
                        xiph.SetField(FieldName, text);
                }
                break;

            case TagLib.TagTypes.Ape:
                if (file.GetTag(TagLib.TagTypes.Ape, !clearing) is TagLib.Ape.Tag ape)
                {
                    if (clearing)
                        ape.RemoveItem(FieldName);
                    else
                        ape.SetValue(FieldName, text);
                }
                break;

            default:
                if (file.GetTag(TagLib.TagTypes.Id3v2, !clearing) is TagLib.Id3v2.Tag id3)
                {
                    if (clearing)
                    {
                        // RemoveFrames would take the frame ID. Every user-text frame in the file shares "TXXX":
                        // the ISRC, the MusicBrainz ids, and the replay gain. So only this one frame is removed.
                        if (TagLib.Id3v2.UserTextInformationFrame.Get(id3, FieldName, false) is { } frame)
                            id3.RemoveFrame(frame);
                    }
                    else
                    {
                        TagLib.Id3v2.UserTextInformationFrame.Get(id3, FieldName, true).Text = [text];
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Which tag type carries the flag for this container. This uses <see cref="TagCarrier"/>'s map, which every
    /// extended field shares. The only difference here is that mp4 gets a real atom rather than a freeform box,
    /// because iTunes reads <c>rtng</c>.
    /// </summary>
    private static TagLib.TagTypes CarrierFor(TagLib.File file) => TagCarrier.CarrierFor(file);

    private static ContentAdvisory? ReadApple(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Apple, false) is not TagLib.Mpeg4.AppleTag tag)
            return null;
        foreach (var box in tag.DataBoxes([AppleAtom]))
            if (box.Data.Count > 0)
                return FromNumber(box.Data[0]);
        return null;
    }

    private static ContentAdvisory? ReadXiph(TagLib.File file)
        => Xiph(file, create: false) is { } tag ? Parse(tag.GetFirstField(FieldName)) : null;

    private static TagLib.Ogg.XiphComment? Xiph(TagLib.File file, bool create)
        => file.GetTag(TagLib.TagTypes.Xiph, create) switch
        {
            TagLib.Ogg.XiphComment comment => comment,
            TagLib.Ogg.GroupedComment grouped => grouped.Comments.FirstOrDefault(),
            _ => null,
        };

    private static ContentAdvisory? ReadId3v2(TagLib.File file)
        => file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag tag
            ? Parse(TagLib.Id3v2.UserTextInformationFrame.Get(tag, FieldName, false)?
                .Text?.FirstOrDefault())
            : null;

    private static ContentAdvisory? ReadApe(TagLib.File file)
        => file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag tag
            ? Parse(tag.GetItem(FieldName)?.ToStringArray().FirstOrDefault())
            : null;

    private static ContentAdvisory? Parse(string? value)
        => int.TryParse(value?.Trim(), out var n) ? FromNumber(n) : null;

    /// <summary>
    /// Returns null rather than <see cref="ContentAdvisory.Unknown"/> for a stored zero. That lets a container
    /// with an empty flag pass the search on to the next container, instead of ending the search with "nothing".
    /// </summary>
    private static ContentAdvisory? FromNumber(int value) => value switch
    {
        1 or 4 => ContentAdvisory.Explicit,
        2 => ContentAdvisory.Clean,
        _ => null,
    };
}
