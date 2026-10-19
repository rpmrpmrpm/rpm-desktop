using RPM.Core.Library;

namespace RPM.Core.Metadata;

/// <summary>
/// A record's release type in the file, under the names MusicBrainz Picard uses, so a file tagged by either app reads
/// the same in both:
/// <list type="bullet">
/// <item>FLAC and Ogg: <c>RELEASETYPE</c>, one field per value. <c>MUSICBRAINZ_ALBUMTYPE</c>, which older taggers and
/// TagLib's own property use, is read too and removed when this writes.</item>
/// <item>MP3: the user text frame <c>MusicBrainz Album Type</c>.</item>
/// <item>MP4: the freeform atom <c>----:com.apple.iTunes:MusicBrainz Album Type</c>.</item>
/// <item>APE: <c>MUSICBRAINZ_ALBUMTYPE</c>.</item>
/// </list>
/// The values are MusicBrainz's words (<see cref="ReleaseTypes.ToTagValues"/>).
/// </summary>
public static class ReleaseTypeTag
{
    public const string XiphField = "RELEASETYPE";
    public const string XiphLegacyField = "MUSICBRAINZ_ALBUMTYPE";
    public const string Id3Description = "MusicBrainz Album Type";
    public const string AppleName = "MusicBrainz Album Type";
    public const string ApeField = "MUSICBRAINZ_ALBUMTYPE";

    /// <summary>The type the file carries, or null when it carries none this app files by. Every container is
    /// asked: a file retagged by another app may hold the field somewhere other than its native one.</summary>
    public static ReleaseType? Read(TagLib.File file)
    {
        ArgumentNullException.ThrowIfNull(file);
        foreach (var values in new[] { ReadXiph(file), ReadId3v2(file), ReadApple(file), ReadApe(file) })
            if (values is { Count: > 0 } && ReleaseTypes.FromTagValues(values) is { } type)
                return type;
        return null;
    }

    /// <summary>Sets the type in the file's native container, or clears it when <paramref name="type"/> is null. Does
    /// not save; the caller owns <c>file.Save()</c>.</summary>
    public static void Write(TagLib.File file, ReleaseType? type)
    {
        ArgumentNullException.ThrowIfNull(file);
        var values = type is { } t ? ReleaseTypes.ToTagValues(t).ToArray() : null;
        bool keep = values is not null;

        switch (TagCarrier.CarrierFor(file))
        {
            case TagLib.TagTypes.Xiph:
                if (file.GetTag(TagLib.TagTypes.Xiph, keep) is TagLib.Ogg.XiphComment xiph)
                {
                    xiph.RemoveField(XiphLegacyField);
                    if (values is null)
                        xiph.RemoveField(XiphField);
                    else
                        xiph.SetField(XiphField, values);
                }
                break;

            case TagLib.TagTypes.Apple:
                if (file.GetTag(TagLib.TagTypes.Apple, keep) is TagLib.Mpeg4.AppleTag apple)
                {
                    if (values is null)
                        apple.SetDashBox(TagCarrier.AppleMean, AppleName, string.Empty);
                    else
                        apple.SetDashBox(TagCarrier.AppleMean, AppleName, string.Join("; ", values));
                }
                break;

            case TagLib.TagTypes.Ape:
                if (file.GetTag(TagLib.TagTypes.Ape, keep) is TagLib.Ape.Tag ape)
                {
                    if (values is null)
                        ape.RemoveItem(ApeField);
                    else
                        ape.SetValue(ApeField, values);
                }
                break;

            case TagLib.TagTypes.Asf:
                // No type is written to WMA: none of the players that read WMA show one.
                break;

            default:
                if (file.GetTag(TagLib.TagTypes.Id3v2, keep) is TagLib.Id3v2.Tag id3)
                {
                    if (values is null)
                    {
                        // Only this frame: every user text frame in the file shares the id "TXXX".
                        if (TagLib.Id3v2.UserTextInformationFrame.Get(id3, Id3Description, false) is { } frame)
                            id3.RemoveFrame(frame);
                    }
                    else
                    {
                        TagLib.Id3v2.UserTextInformationFrame.Get(id3, Id3Description, true).Text = values;
                    }
                }
                break;
        }
    }

    private static IReadOnlyList<string>? ReadXiph(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is not TagLib.Ogg.XiphComment xiph)
            return null;
        var values = xiph.GetField(XiphField);
        return values is { Length: > 0 } ? values : xiph.GetField(XiphLegacyField);
    }

    private static IReadOnlyList<string>? ReadId3v2(TagLib.File file) =>
        file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3
        && TagLib.Id3v2.UserTextInformationFrame.Get(id3, Id3Description, false) is { } frame
            ? frame.Text
            : null;

    private static IReadOnlyList<string>? ReadApple(TagLib.File file) =>
        file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple
        && apple.GetDashBoxes(TagCarrier.AppleMean, AppleName) is { Length: > 0 } boxes
            ? boxes
            : null;

    private static IReadOnlyList<string>? ReadApe(TagLib.File file) =>
        file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag ape && ape.GetItem(ApeField) is { } item
            ? item.ToStringArray()
            : null;
}
