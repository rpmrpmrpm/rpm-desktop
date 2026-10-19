using System.Text;

namespace RPM.Core.Metadata;

/// <summary>
/// The RPM id: a GUID stored in the file's own tags. A row in the library mirror can then still find its
/// file after the user moves files around by hand outside the app. The database is the data source. This id
/// is the lasting link from the file back to its row.
///
/// Each container stores the id in its own way, because there is no tag field that all formats share: ID3v2
/// <c>UFID</c> (owner <c>com.rpm.id</c>) for mp3, wav and aiff, a <c>RPM_ID</c> Vorbis comment for flac,
/// ogg and opus, the freeform atom <c>----:com.rpm.id:id</c> for mp4 and m4a, an APE item for ape, wv and
/// mpc, and an ASF descriptor for wma.
/// </summary>
public static class RpmTag
{
    /// <summary>The UFID owner, and the MP4 freeform "mean". Both formats expect a URL-like namespace
    /// here.</summary>
    public const string Owner = "com.rpm.id";

    /// <summary>Vorbis comment / APE item / ASF descriptor key.</summary>
    public const string FieldName = "RPM_ID";

    /// <summary>MP4 freeform atom "name" (under the <see cref="Owner"/> mean).</summary>
    public const string AppleName = "id";

    /// <summary>
    /// Stored in the library mirror to mean "this file was tried, and it cannot hold an id". This happens
    /// with a container that TagLib refuses (Ogg FLAC), or with a file too corrupt to open. Without this
    /// value such a file would look unstamped on every sync and be read again each time. It is not an id,
    /// so nothing matches it. A file that changes on disk later is tried again.
    /// </summary>
    public const string NoCarrier = "";

    /// <summary>True when the value is a real id. It is false for null (the file was never tried) and for
    /// <see cref="NoCarrier"/> (the file was tried and cannot hold an id).</summary>
    public static bool IsId(string? value) => !string.IsNullOrEmpty(value);

    public static string New() => Guid.NewGuid().ToString("D");

    /// <summary>The id already stamped in the file, or null when it carries none.</summary>
    public static string? Read(TagLib.File file)
    {
        return ReadId3v2(file) ?? ReadXiph(file) ?? ReadApple(file) ?? ReadApe(file) ?? ReadAsf(file);
    }

    /// <summary>
    /// Stamps <paramref name="id"/> into the container's own tag. This does not save the file. The caller
    /// must call <c>file.Save()</c>. Returns false when the format has no tag that can be written (TagLib
    /// refuses to create it). The file is then left untouched.
    /// </summary>
    public static bool Write(TagLib.File file, string id)
    {
        switch (CarrierFor(file))
        {
            case TagLib.TagTypes.Apple:
                if (file.GetTag(TagLib.TagTypes.Apple, true) is not TagLib.Mpeg4.AppleTag apple)
                    return false;
                apple.SetDashBox(Owner, AppleName, id);
                return true;

            case TagLib.TagTypes.Xiph:
                if (Xiph(file, create: true) is not { } xiph)
                    return false;
                xiph.SetField(FieldName, id);
                return true;

            case TagLib.TagTypes.Ape:
                if (file.GetTag(TagLib.TagTypes.Ape, true) is not TagLib.Ape.Tag ape)
                    return false;
                ape.SetValue(FieldName, id);
                return true;

            case TagLib.TagTypes.Asf:
                if (file.GetTag(TagLib.TagTypes.Asf, true) is not TagLib.Asf.Tag asf)
                    return false;
                asf.SetDescriptorString(id, FieldName);
                return true;

            default:
                if (file.GetTag(TagLib.TagTypes.Id3v2, true) is not TagLib.Id3v2.Tag id3)
                    return false;
                TagLib.Id3v2.UniqueFileIdentifierFrame.Get(id3, Owner, true)
                    .Identifier = new TagLib.ByteVector(Encoding.UTF8.GetBytes(id));
                return true;
        }
    }

    /// <summary>
    /// Which tag type carries the id for this container. Formats that accept several (flac takes Xiph *and*
    /// ID3v2; wavpack takes APE *and* ID3v2) get the one their ecosystem actually reads, so ids we stamp
    /// stay visible to other players.
    /// </summary>
    private static TagLib.TagTypes CarrierFor(TagLib.File file) => file switch
    {
        TagLib.Mpeg4.File => TagLib.TagTypes.Apple,
        TagLib.Ogg.File or TagLib.Flac.File => TagLib.TagTypes.Xiph,
        TagLib.Asf.File => TagLib.TagTypes.Asf,
        TagLib.Ape.File or TagLib.WavPack.File or TagLib.MusePack.File => TagLib.TagTypes.Ape,
        _ => TagLib.TagTypes.Id3v2, // mp3, wav, aiff, raw aac: everything else that takes ID3v2
    };

    private static string? ReadId3v2(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag tag)
            return null;
        return Ufid(tag, Owner);
    }

    private static string? Ufid(TagLib.Id3v2.Tag tag, string owner)
    {
        var frame = TagLib.Id3v2.UniqueFileIdentifierFrame.Get(tag, owner, false);
        var bytes = frame?.Identifier?.Data;
        return bytes is { Length: > 0 } ? Trim(Encoding.UTF8.GetString(bytes)) : null;
    }

    private static string? ReadXiph(TagLib.File file)
        => Xiph(file, create: false) is { } tag
            ? Trim(tag.GetFirstField(FieldName))
            : null;

    /// <summary>
    /// The file's Vorbis comment. FLAC hands one back directly, but an Ogg stream carries one comment block
    /// per logical stream and TagLib wraps them in a GroupedComment. The audio stream's is the first, and
    /// it is the one players read.
    /// </summary>
    private static TagLib.Ogg.XiphComment? Xiph(TagLib.File file, bool create)
        => file.GetTag(TagLib.TagTypes.Xiph, create) switch
        {
            TagLib.Ogg.XiphComment comment => comment,
            TagLib.Ogg.GroupedComment grouped => grouped.Comments.FirstOrDefault(),
            _ => null,
        };

    private static string? ReadApple(TagLib.File file)
        => file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag tag
            ? Trim(tag.GetDashBox(Owner, AppleName))
            : null;

    private static string? ReadApe(TagLib.File file)
        => file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag tag
            ? Trim(tag.GetItem(FieldName)?.ToStringArray().FirstOrDefault())
            : null;

    private static string? ReadAsf(TagLib.File file)
        => file.GetTag(TagLib.TagTypes.Asf, false) is TagLib.Asf.Tag tag
            ? Trim(tag.GetDescriptorString(FieldName))
            : null;

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
