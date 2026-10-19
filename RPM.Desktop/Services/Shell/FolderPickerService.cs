using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Storage.Pickers;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Opens a native folder picker and returns the chosen folder's full path.
/// The picker must be created on the UI thread. On macOS,
/// this goes through NSOpenPanel with ObjC interop.
/// </summary>
public sealed class FolderPickerService
{
    private readonly DispatcherQueue _dispatcherQueue;

    public FolderPickerService(DispatcherQueue dispatcherQueue) => _dispatcherQueue = dispatcherQueue;

    /// <summary>Pick a folder. <paramref name="canCreateDirectories"/> puts a "New Folder" button in
    /// the macOS panel, so a destination folder can be created without leaving the app.</summary>
    public async Task<string?> PickFolderAsync(bool canCreateDirectories = false)
    {
        try
        {
            return OperatingSystem.IsMacOS()
                ? await PickViaNSOpenPanelAsync(canCreateDirectories)
                : await PickViaWinRtAsync();
        }
        catch (Exception ex)
        {
            return Failed("folder", ex);
        }
    }

    private async Task<string?> PickViaWinRtAsync()
    {
        return await _dispatcherQueue.ExecuteAsync(async () =>
        {
            var picker = Pickers.Owned<FolderPicker>();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        });
    }

    /// <summary>Pick a single image file (png/jpg/webp/…) and return its full path. Same platform
    /// split as the folder picker: NSOpenPanel on macOS, FileOpenPicker elsewhere.</summary>
    public async Task<string?> PickImageFileAsync()
    {
        try
        {
            return OperatingSystem.IsMacOS()
                ? await PickFileViaNSOpenPanelAsync(ImageExtensions)
                : await PickImageViaWinRtAsync();
        }
        catch (Exception ex)
        {
            return Failed("image", ex);
        }
    }

    /// <summary>The image kinds the background picker accepts, as bare lowercase extensions.</summary>
    public static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    private async Task<string?> PickImageViaWinRtAsync()
    {
        return await _dispatcherQueue.ExecuteAsync(async () =>
        {
            var picker = Pickers.Owned<FileOpenPicker>();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            foreach (var ext in ImageExtensions)
                picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        });
    }

    /// <summary>Pick a native library that the user has downloaded. At present this is only libdvdcss.
    /// The user must supply it, because rpm cannot ship it. The platform split is the same as for the
    /// other pickers.</summary>
    public async Task<string?> PickNativeLibraryAsync()
    {
        try
        {
            return OperatingSystem.IsMacOS()
                ? await PickFileViaNSOpenPanelAsync(NativeLibraryExtensions)
                : await PickNativeLibraryViaWinRtAsync();
        }
        catch (Exception ex)
        {
            return Failed("library", ex);
        }
    }

    /// <summary>What a loadable native library is called, per platform.</summary>
    public static readonly string[] NativeLibraryExtensions =
        OperatingSystem.IsWindows() ? [".dll"] : OperatingSystem.IsMacOS() ? [".dylib"] : [".so"];

    private async Task<string?> PickNativeLibraryViaWinRtAsync()
    {
        return await _dispatcherQueue.ExecuteAsync(async () =>
        {
            // Downloads, because that is where the file will be: the user has just fetched it.
            var picker = Pickers.Owned<FileOpenPicker>();
            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            foreach (var ext in NativeLibraryExtensions)
                picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        });
    }

    /// <summary>
    /// A picker that would not open, written down and answered as "nothing picked".
    ///
    /// <para>Every caller is an <c>async void</c> click handler, so there is no catch above this one. An
    /// exception from a picker reaches <c>Application.UnhandledException</c>. This app does not mark that
    /// as handled, on purpose. So the process ends, and anything that was playing stops. A cancelled dialog
    /// already returns nothing, so every call site handles that case. The reason is written to the ring.
    /// On the WinUI head, these failures came from a missing window handle.</para>
    /// </summary>
    private static string? Failed(string what, Exception ex)
    {
        try
        {
            RPM.Core.Diagnostics.Scope.Said(
                "window", $"{what} picker failed: {ex.GetType().Name}: {ex.Message}");
        }
        catch { }
        return null;
    }

    private async Task<string?> PickFileViaNSOpenPanelAsync(IReadOnlyList<string> extensions)
    {
        return await _dispatcherQueue.ExecuteAsync(async () =>
        {
            await Task.CompletedTask; // satisfy async signature

            var panel = objc_msgSend(objc_getClass("NSOpenPanel"), sel_registerName("openPanel"));
            objc_msgSend_bool(panel, sel_registerName("setCanChooseDirectories:"), false);
            objc_msgSend_bool(panel, sel_registerName("setCanChooseFiles:"), true);
            objc_msgSend_bool(panel, sel_registerName("setAllowsMultipleSelection:"), false);

            var result = objc_msgSend(panel, sel_registerName("runModal"));
            if (result != 1) return null;

            var urls = objc_msgSend(panel, sel_registerName("URLs"));
            var url = objc_msgSend_idx(urls, sel_registerName("objectAtIndex:"), 0);
            var pathPtr = objc_msgSend(url, sel_registerName("path"));
            var pathStr = Marshal.PtrToStringUTF8(objc_msgSend(pathPtr, sel_registerName("UTF8String")));

            if (string.IsNullOrEmpty(pathStr))
                return null;
            // The panel is not extension-filtered (that path wants NSArray/UTType interop); gate here.
            var ext = Path.GetExtension(pathStr).ToLowerInvariant();
            return extensions.Contains(ext) ? pathStr : null;
        });
    }

    private async Task<string?> PickViaNSOpenPanelAsync(bool canCreateDirectories)
    {
        return await _dispatcherQueue.ExecuteAsync(async () =>
        {
            await Task.CompletedTask; // satisfy async signature

            var panel = objc_msgSend(objc_getClass("NSOpenPanel"), sel_registerName("openPanel"));
            objc_msgSend_bool(panel, sel_registerName("setCanChooseDirectories:"), true);
            objc_msgSend_bool(panel, sel_registerName("setCanChooseFiles:"), false);
            objc_msgSend_bool(panel, sel_registerName("setCanCreateDirectories:"), canCreateDirectories);
            objc_msgSend_bool(panel, sel_registerName("setAllowsMultipleSelection:"), false);

            var result = objc_msgSend(panel, sel_registerName("runModal"));
            if (result != 1) return null;

            var urls = objc_msgSend(panel, sel_registerName("URLs"));
            var url = objc_msgSend_idx(urls, sel_registerName("objectAtIndex:"), 0);
            var pathPtr = objc_msgSend(url, sel_registerName("path"));
            var pathStr = Marshal.PtrToStringUTF8(objc_msgSend(pathPtr, sel_registerName("UTF8String")));

            return string.IsNullOrEmpty(pathStr) ? null : pathStr;
        });
    }

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend(nint target, nint selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(nint target, nint selector, bool arg);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_idx(nint target, nint selector, nint index);
}
