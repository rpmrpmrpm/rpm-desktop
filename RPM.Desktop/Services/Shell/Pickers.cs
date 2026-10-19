using Windows.Storage.Pickers;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The one place where a WinRT picker is created.
///
/// <para>Needed on the WinUI head, and only there. A WinRT picker is a COM object with no window of its
/// own. An unpackaged app gives it nothing to fall back on, so it must be given the HWND that it will be
/// modal to. Without one, <c>PickSingleFileAsync</c> throws COMException 0x80070578 (Invalid window handle)
/// before any dialog is shown, and Explorer never opens. Uno implements these pickers itself and parents
/// them from its own host. That is why the Skia head never needed this, and why the problem only showed up
/// after the port.</para>
///
/// <para>Every call site takes its picker from here, and does not call <c>new FileOpenPicker()</c>. That
/// compiles, then fails at runtime on one head only. Some call sites did write it, and each one swallowed
/// the exception, so the menu item or button seemed to do nothing. Do not create a picker directly.</para>
/// </summary>
internal static class Pickers
{
    /// <summary>A file picker for the given location and extensions, set to open over the app's main
    /// window. Extensions are bare and lowercase: ".jpg", ".png".</summary>
    public static FileOpenPicker OpenFile(PickerLocationId start, params string[] extensions)
    {
        var picker = Owned<FileOpenPicker>();
        picker.SuggestedStartLocation = start;
        foreach (var extension in extensions)
            picker.FileTypeFilter.Add(extension);
        return picker;
    }

    /// <summary>A picker of any WinRT kind, set to open over the app's main window. Use
    /// <see cref="OpenFile"/> unless the kind is not a file picker.</summary>
    public static T Owned<T>() where T : class, new()
    {
        var picker = new T();
#if RPM_WINUI
        var window = App.AppWindow
            ?? throw new InvalidOperationException("No main window to open a picker over.");
        var hwnd = Platform.NativeWindow.Handle(window)
            ?? throw new InvalidOperationException("The main window has no handle to open a picker over.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
#endif
        return picker;
    }
}
