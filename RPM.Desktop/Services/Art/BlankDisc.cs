using Microsoft.Extensions.DependencyInjection;

using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// The plain CD that stands in for an album with no picture of its own disc, when the listener has chosen it
/// over the app's vinyl (<see cref="SettingsService.DiscStandIn"/>).
///
/// <para>It is handed out as the bytes of a disc picture, not as a finished image. Each surface that draws a
/// record (the header, Pegasus and the album page) takes it at the point where it would take a folder's
/// disc.png, and sends it through the same cut. The picture has its own transparency and is opaque at four
/// fifths of the radius, so <see cref="DiscStamp.Round"/> hands it back as it is and reads it as a whole CD.
/// From there it is drawn exactly as a real CD picture is: the pressing goes, the label goes, and the
/// <see cref="Presentation.Controls.CdOverlay"/> rim, hub and spindle go on top. This is why there is no
/// separate code path for it on any surface.</para>
///
/// <para>Assets/cd-blank.png is a scan of the data side of a CD, cut round, with the clear centre left
/// transparent.</para>
/// </summary>
public static class BlankDisc
{
    /// <summary>The stand-in's bytes when the listener has chosen the CD, or null when the vinyl is wanted. Null
    /// also when the asset cannot be read, and when there is no running app to ask for the setting (a test of
    /// the artwork pipeline). The record then falls back to the vinyl, which is always there.</summary>
    public static Task<byte[]?> StandInAsync()
        => WantsCd()
            ? Bytes.Value
            : Task.FromResult<byte[]?>(null);

    /// <summary>Whether the listener has chosen the CD. <see cref="App.Services"/> is only there inside the
    /// running app, so it is not asked without one.</summary>
    private static bool WantsCd()
        => App.Current is App
           && App.Services.GetService<SettingsService>() is { DiscStandIn: DiscStandIn.Cd };

    /// <summary>The asset is read once per session. It is the same picture for every album.</summary>
    private static readonly Lazy<Task<byte[]?>> Bytes = new(LoadAsync);

    private static async Task<byte[]?> LoadAsync()
    {
        try
        {
#if RPM_WINUI
            // Read by path, because this head is not packaged and cannot resolve ms-appx:/// through a
            // package. See VinylPressing.LoadNeutralAsync, which reads its asset the same way.
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "cd-blank.png");
            return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
#else
            var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(
                new Uri("ms-appx:///Assets/cd-blank.png"));
            using var stream = await file.OpenStreamForReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return memory.ToArray();
#endif
        }
        catch
        {
            return null;
        }
    }
}
