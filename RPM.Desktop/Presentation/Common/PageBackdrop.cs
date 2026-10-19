using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The ground that a detail page lends to the whole window. While a record or an artist is open, the app
/// shows that page's colours, not its own wallpaper.
///
/// <para><b>The artwork is not one of the facts here, and that is deliberate.</b> When something is
/// playing, it paints the whole window with its own blurred artwork. That is a picture of what the listener
/// is hearing, and it outranks a picture of the page they are reading. It outranks it by Z-ORDER. The art
/// display is drawn over the ground, and its dominant fill is opaque, so it covers this without being
/// asked. If this class also tracked the artwork as a fact, the ground under the artwork would be sent back
/// to the app's own backdrop while it was hidden. The next page the reader opened would then fade in from
/// the WALLPAPER, not from the record they had just been looking at. What is behind an opaque element
/// should stay what it was.</para>
///
/// <para><b>Two facts, one answer, and the answer is a question.</b> The page decides what it is dressed
/// in, and it says so PER PAGE, not into one shared slot. A record opened from an artist's page leaves that
/// artist still dressed underneath it. When the record is closed, the artist's colours must come back, not
/// the wallpaper. The shell decides which page the reader is actually looking at, not the view. A page
/// stays marked visible inside a library pane that has been switched to another tab, or folded away.</para>
///
/// <para>The two facts are stated separately by the places that know them, and they are combined here, in
/// <see cref="Showing"/>. That is the only way to read either one. A page's ground cannot be read without
/// saying that the page is on screen. That stops a ground outliving the page it belongs to.</para>
/// </summary>
public static class PageBackdrop
{
    /// <summary>
    /// What one page is standing on, in the layers it is built from rather than as a finished picture:
    /// the window paints its own copy at its own size, and a page's ground is a stack rather than a
    /// brush.
    /// </summary>
    /// <param name="Colour">The bottom layer. A flat colour read off a sleeve, or the gradient an artist's
    /// box is painted in. Null where neither could be read. Then the wallpaper shows through whatever is
    /// above.</param>
    /// <param name="Wash">The sleeve or the photograph, blurred so that it is no longer a clear picture, or
    /// null.</param>
    /// <param name="WashStrength">How much of the wash is let through, from 0 to 1.</param>
    /// <param name="Veil">Flat black drawn INSIDE the wash, from 0 to 1. It takes back the picture's own
    /// exposure. It moves with the picture, rather than sitting over it.</param>
    /// <param name="Weight">Laid over all three layers, or null. It keeps white text readable on a page whose
    /// ground is a lit photograph.</param>
    public sealed record Ground(
        Brush? Colour, ImageSource? Wash, double WashStrength, double Veil, Brush? Weight);

    /// <summary>Every page that is OPEN, and what it is dressed in. The value is null for a page that is open
    /// but has nothing to lend yet. The key is the page itself, because two pages can be open at once. A
    /// record opened from an artist's page leaves that artist standing behind it. Neither page decides which
    /// of the two is worn. A page that is left is removed from here. That removal is what this whole class
    /// depends on. See <see cref="Showing"/>.</summary>
    private static readonly Dictionary<object, Ground?> Open = [];

    private static object? _reading;

    /// <summary>
    /// What the window should be standing in: the page the reader has open, and the ground it is
    /// dressed in. The ground can be null while the page is open and not yet dressed.
    ///
    /// <para><b>Null means two different things, and telling them apart is the point of this class.</b> A
    /// null ANSWER means the reader is off the pages altogether. That is the case when they are back on the
    /// wall, on another view, or when the artwork is over the window. The app's own backdrop belongs there.
    /// A null GROUND means a page that is open but not yet dressed. That is every page for the tenth of a
    /// second that its sleeve takes to come off the disk. In that case the backdrop must NOT come back.
    /// Otherwise a reader going from a record to an artist would see the wallpaper flash between them.</para>
    ///
    /// <para>The page is handed back with its ground, because two changes that look alike here look nothing
    /// alike to the code that paints. When one page fills itself in, for example a photograph lands a moment
    /// after the colour it was read with, the same surface is becoming itself. That change is written
    /// straight through, the same way the page writes its own layers. A different page is a different room.
    /// The window must move to it, not cut to it.</para>
    /// </summary>
    public static (object Page, Ground? Ground)? Showing =>
        _reading is { } page && Open.TryGetValue(page, out var ground) ? (page, ground) : null;

    /// <summary>Raised when the answer above changes. Nothing is reported unless it changes the answer. For
    /// example, a page that redresses itself off screen is not news to the window.</summary>
    public static event Action? Changed;

    /// <summary>A page is open, and it is dressed in <paramref name="ground"/>. It may also be dressed in
    /// nothing yet, which is the case for every page while its sleeve or photograph is still being read.
    /// This is said again on every change. So a page that peels before it repaints goes through
    /// open-and-undressed, rather than through closed.</summary>
    public static void Opened(object page, Ground? ground) => Note(() => Open[page] = ground);

    /// <summary>A page has been left. Only the page can say this. It is the one that knows the difference
    /// between being put away and being slow. It must say so, because a page off screen still holds the
    /// colours it was wearing. Nothing else here can tell that they have stopped being true.</summary>
    public static void Closed(object page) => Note(() => Open.Remove(page));

    /// <summary>Which detail page the reader is looking at, or null for none. This is the shell's answer, not
    /// the view's.</summary>
    public static void Reading(object? page) => Note(() => _reading = page);

    /// <summary>
    /// The page the shell says is being read, whatever it is dressed in.
    ///
    /// <para>This answers the one question that a page being put away must ask before it hands the window
    /// back to the app: am I still the one on screen? A page that was left BECAUSE another page took its
    /// place has already been replaced here. If it handed the window back anyway, it would take the new
    /// page's claim with it. The wallpaper would then flash between two pages that both have a
    /// ground.</para>
    ///
    /// <para>It is asked for rather than remembered, so no caller has to be told what it is part of.</para>
    /// </summary>
    public static object? ReadingPage => _reading;

    /// <summary>Reports a change only when it moves the one answer that anybody reads. Both facts go through
    /// here, because neither one is news on its own.</summary>
    private static void Note(Action change)
    {
        var was = Showing;
        change();
        if (!Equals(was, Showing))
            Changed?.Invoke();
    }
}
