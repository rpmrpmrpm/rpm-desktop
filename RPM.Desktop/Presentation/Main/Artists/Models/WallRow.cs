using RPM.Core.Cloud;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Artists.Models;

/// <summary>
/// One line on an artist's wall, as the page draws it.
///
/// <para>Two kinds of line arrive in the same list. Both are drawn as one card, with two parts
/// swapped. A post is written by a listener, in their own words. A review line is written by the
/// server when a listener reviews or rates one of this artist's records. It is a sentence about the
/// record, with the verdict under it. <see cref="IsPost"/> and <see cref="IsReview"/> are what the
/// template switches on. A kind that a newer server sends, and this build does not know, draws as
/// neither. It does not draw as a post with an empty body.</para>
///
/// <para>This is a view model, not the wire record. The row has to know whether it belongs to the
/// reader. That depends on who is signed in, not on what was written. The row also has to carry the
/// avatar as a URL that the picture control can fetch. See <see cref="Library.Models.CommentRow"/>
/// for the same reason.</para>
/// </summary>
[RpmBindable]
public sealed partial class WallRow(RpmWallEntry entry, string? signedInUserId, Uri? apiBase)
{
    public string EntryId => entry.EntryId;

    public string AuthorName => entry.AuthorDeleted ? Loc.Get("Community_DeletedUser") : entry.AuthorName;

    public bool IsPost => entry.IsPost;

    public bool IsReview => entry.IsReview;

    /// <summary>What the line says. On a post, the author's own words. On a review line, what the
    /// listener did and to which record. The record's title is put into the sentence.</summary>
    public string Line => LineKey.Length == 0 ? entry.Body : Loc.Format(LineKey, AlbumTitle);

    /// <summary>
    /// Which sentence a review line uses, or empty for a post. A post is the author's own words, not
    /// a sentence from the app.
    ///
    /// <para>The choice is made here, not inside <see cref="Line"/>, so it can be read without a
    /// resource table. Which sentence is used is a fact about the row. Looking up the words is a fact
    /// about the app running on screen.</para>
    /// </summary>
    public string LineKey => entry.IsReview
        ? entry.HasBody ? "Artists_WallWroteReview" : "Artists_WallRated"
        : "";

    /// <summary>The record a review line is about, as the entry spells it. Empty on a post.</summary>
    public string AlbumTitle => entry.AlbumTitle ?? "";

    /// <summary>The verdict, in stars. Drawn only on a review line. A post has no rating, and a row of
    /// dim stars under every remark would look like a rating of zero.</summary>
    public double Stars => entry.Stars;

    public bool HasStars => entry.IsReview && entry.Rating > 0;

    /// <summary>The day it happened, in the reader's own format. The wire carries milliseconds since
    /// the epoch, which is not useful to show people.</summary>
    public string When => Strings.DateText.Short(entry.Written.LocalDateTime, System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>True for the reader's own post, the only row with a delete action rather than a
    /// report action. False on every review line, including the reader's own review. A review line
    /// goes when its review goes, and the album page is where reviews are deleted.</summary>
    public bool IsMine { get; } =
        entry.IsPost && signedInUserId is { Length: > 0 } && signedInUserId == entry.UserId;

    public Uri? AvatarUrl { get; } =
        Library.Models.ReviewRow.BuildAvatarUrl(apiBase, entry.UserId, entry.HasAvatar);

    public bool HasAvatar => AvatarUrl is not null;

    /// <summary>The letter shown where a person has no picture, as on the album page's cards.</summary>
    public string Initial => !entry.AuthorDeleted && AuthorName is { Length: > 0 } n ? n[..1].ToUpperInvariant() : "?";
}
