using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The face that the app's generated pictures are lettered in. It is the app's own font, not the
/// system's.
///
/// <para><b>Why this exists.</b> <see cref="CoverOverlay"/> and <see cref="PlaylistArt"/> both asked
/// Skia for "Segoe UI" Bold. That is a Windows system font. <c>SKTypeface.FromFamilyName</c> does not
/// return null for a family the machine does not have. It returns the nearest match instead. On macOS
/// the request came back as <b>Helvetica</b> at weight 700, with no error. So the same playlist was
/// lettered in different faces, depending on which machine drew it. These pictures do not stay on
/// that machine. A cover is made once, saved in the library, sent to the cloud and shown on the phone.</para>
///
/// <para><b>What it showed up as.</b> Helvetica Bold is wider than Segoe UI Bold. So a long name
/// overran the space the cover gives it, and was sized down to fit. A short name was not. Five covers
/// on one shelf then appeared in two sizes of type. The <c>Gauge</c> exists to stop exactly that.
/// <c>CoverOverlayTests</c> caught it when the suite ran on a Mac.</para>
///
/// <para><b>So the face ships with the app.</b> Cabin Bold is in <c>Assets/Fonts</c>. It is also the
/// face the app's own titles use (<c>RpmDisplayFontBold</c> in Generic.xaml). So a cover is lettered
/// in the app's face on every head, and the picture is the same on all three.</para>
///
/// <para><b>The system is asked only where the shipped face cannot draw a name.</b> It is asked for the
/// app's language and for bold. See <see cref="FaceFor"/> for the measurements. Asked
/// without either, the platform set Simplified Chinese in a Traditional face at regular weight.</para>
///
/// <para><b>Loaded once and never disposed.</b> A typeface is read from disk. A cover is drawn for each
/// list, so opening the face per cover would mean a file read per picture. The face is held for the life
/// of the process, as <see cref="SKTypeface.Default"/> is. So a caller must not dispose what this
/// returns. <see cref="Of"/> says, in its result, which one it handed back for this reason.</para>
/// </summary>
internal static class CoverFace
{
    /// <summary>The app's own bold face, or null if it cannot be read from disk. Read once. A second
    /// attempt would fail the same way. A cover that checks a file each time would pay for a missing
    /// asset on every list.</summary>
    private static readonly SKTypeface? Shipped = Load();

    private static SKTypeface? Load()
    {
        try
        {
            // Read from beside the binary, not through ms-appx. The heads differ in what that URI
            // resolves to. The WinUI head is unpackaged, so it has no package to resolve it through.
            // VinylPressing.LoadNeutralAsync records the same thing. This is called from a
            // worker with no window in reach. The csproj copies the file to the output.
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Cabin-Bold.ttf");
            return File.Exists(path) ? SKTypeface.FromFile(path) : null;
        }
        catch
        {
            // A cover lettered in whatever the system offers looks worse, but it is not broken. The
            // caller falls back to the way it worked before this face existed.
            return null;
        }
    }

    /// <summary>
    /// The face to letter <paramref name="name"/> in, and whether the caller may dispose it.
    /// </summary>
    /// <remarks>
    /// <b>The shipped face is not right for every name.</b> Cabin covers Latin, Greek and Cyrillic.
    /// The app runs in Chinese, and 你的专属歌单 in a face with none of those letters is a row of empty
    /// boxes. So the shipped face is tried first and then checked. A name it cannot draw goes to
    /// whatever the system offers for those letters.
    ///
    /// <para>One face is used for the whole name, not one per character. Skia draws a string with a
    /// single typeface. Two faces at one font size would look like two sizes of letter on one line.</para>
    /// </remarks>
    /// <param name="name">The name that has to be drawable in the face that comes back.</param>
    /// <returns>The face, and <c>Owned</c>, which says whether the caller must dispose it. It is false
    /// for the shipped face and for <see cref="SKTypeface.Default"/>. Both outlive any one picture.</returns>
    public static (SKTypeface Face, bool Owned) Of(string name)
    {
        var letters = name.EnumerateRunes()
                          .Select(rune => rune.Value)
                          .Distinct()
                          .ToArray();

        if (Shipped is { } shipped && letters.All(shipped.ContainsGlyph))
            return (shipped, false);

        var offered = new List<SKTypeface>();

        foreach (var letter in letters)
        {
            if (offered.Any(face => face.ContainsGlyph(letter)))
                continue;

            if (Matched(letter) is { } found)
                offered.Add(found);
        }

        // Keep the face that draws the most letters in the name. In a mixed name, one offered face may
        // cover the Latin and another may cover the rest. The string is set in only one of them.
        var best = offered
            .OrderByDescending(face => letters.Count(face.ContainsGlyph))
            .FirstOrDefault();

        foreach (var face in offered)
        {
            if (!ReferenceEquals(face, best))
                face.Dispose();
        }

        // If nothing was offered, the shipped face is used rather than the system default. It at least
        // draws the Latin. A name with no character that Skia can match is not expected in this app.
        if (best is null)
            return Shipped is { } fallback ? (fallback, false) : (SKTypeface.Default, false);

        return (best, true);
    }

    /// <summary>
    /// The system face this app would set one character in, in the language given. Tests pass a
    /// language, so they ask the same question that the drawing asks.
    /// </summary>
    /// <remarks>
    /// <b>The request is made in the app's language, and in bold.</b> A bare <c>MatchCharacter</c> call
    /// gives neither, and both came back wrong. Measured on Windows with 你的专属歌单, the zh-Hans spelling
    /// of "Your mix":
    ///
    /// <list type="bullet">
    /// <item>Asked without a language, Windows offered <b>Yu Gothic UI</b>, a Japanese face, for four of
    /// the six characters. It has neither 专 nor 单. The face that covered the most characters, and so
    /// won, was Microsoft JhengHei UI, a Traditional Chinese face. So a Simplified name was set in a
    /// Traditional face.</item>
    /// <item>Asked without bold, every match came back at <b>weight 400</b>. The one name on the shelf
    /// that was not Latin was then the only one that was not bold, next to a shipped face that is.</item>
    /// <item>Asked with <c>zh-Hans</c> and bold, Windows gives Microsoft YaHei UI at weight 700, which
    /// covers all six. Asked with <c>zh-Hant</c>, it gives JhengHei, which is the right face for that
    /// language.</item>
    /// </list>
    ///
    /// <para>The language is the app's own, not the string's. These names come from the resource table,
    /// so they are in the app's language. The exception is a mood list narrowed to a genre. That list is
    /// named by the genre as the listener's own tags spell it (see <c>SmartPlaylistFace.Name</c>). It can
    /// be in any language. The app's language is still a better guess than none in that case.</para>
    /// </remarks>
    internal static SKTypeface? FaceFor(int letter, string language) => Matched(letter, language);

    private static SKTypeface? Matched(int letter)
        => Matched(letter, System.Globalization.CultureInfo.CurrentUICulture.Name);

    private static SKTypeface? Matched(int letter, string language)
    {
        var found = SKFontManager.Default.MatchCharacter(
            familyName: null,
            SKFontStyleWeight.Bold,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright,
            [language],
            letter);

        if (found is null || found.FontWeight >= (int)SKFontStyleWeight.SemiBold)
            return found;

        // Windows honours the Bold request above. macOS does not. There the match is made on the
        // character and the language, and it comes back as the family's regular member whatever weight
        // was asked for. For example, PingFang SC comes back at 400, beside Latin names at 700. The
        // family is right, so that family is asked by name for its bold member. If there is no bold
        // member, or it cannot draw the character, the first match is kept.
        var bold = SKFontManager.Default.MatchFamily(
            found.FamilyName,
            new SKFontStyle(SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));

        return bold is not null && bold.ContainsGlyph(letter) ? bold : found;
    }
}
