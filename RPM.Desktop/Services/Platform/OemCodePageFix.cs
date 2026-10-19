using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace RPM.Desktop.Services.Platform;

/// <summary>Makes the current culture's OEM code page resolvable. The Win32 host's clipboard needs this
/// before it can read anything at all.
///
/// <para>Uno's Win32 clipboard decodes the legacy <c>CF_OEMTEXT</c> format with
/// <c>Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)</c>. For most cultures that
/// returns a real code page (en-US 437, nl-NL 850), and all is well. For a culture that .NET has no legacy
/// code page table for, such as en-NL (English in the Dutch region), it returns <b>1</b>. The ANSI
/// equivalent returns <b>0</b>. These are the Win32 constants <c>CP_OEMCP</c> and <c>CP_ACP</c>. They are
/// sentinels that mean "the system default", not code page numbers. Asking .NET for an encoding with that
/// number throws <c>ArgumentException: 1 is not a supported code page</c>.</para>
///
/// <para>The throw is not contained. It happens while the host builds every clipboard format into one
/// package, so it fails the whole read, not just the OEM part. Windows creates CF_OEMTEXT from Unicode text
/// automatically, so the bad format is present for anything copied. The host caches the encoding in a
/// <c>Lazy</c>. A faulted <c>Lazy</c> caches its exception, so the first failure turns off clipboard
/// reading for the life of the process.</para>
///
/// <para>So this resolves the sentinel the way it asks to be resolved: by asking the OS. It registers an
/// encoding provider that answers for that one value and nothing else. The culture, and so every date and
/// number the app formats, stays as the user set it. Changing the culture to a neighbouring one would also
/// clear the throw. But it would switch the UI's dates and month names to that culture's language. On this
/// machine it would also decode with code page 850, while the OS reports 437.</para>
///
/// <para>Applied once, before anything touches the clipboard. This works around the symptom. It does not
/// fix the underlying defect, which is still worth reporting upstream.</para>
/// </summary>
public static class OemCodePageFix
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static readonly Lazy<bool> s_resolves = new(Repair);

    /// <summary>Whether the culture's OEM code page resolves to a real encoding, after repair if one
    /// was needed. False means the host's clipboard will still throw on every read, and the fallbacks
    /// in <see cref="RPM.Desktop.Presentation.Common.TextBoxClipboard"/> are still required.</summary>
    public static bool CodePageResolves => s_resolves.Value;

    /// <summary>Call once at startup, before any clipboard access, so the host's cached encoding is
    /// built after the provider is in place rather than before it.</summary>
    public static void Apply() => _ = CodePageResolves;

    private static bool Repair()
    {
        // Only the Win32 host decodes CF_OEMTEXT; the others never ask for an OEM code page.
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            // The legacy code pages are not included in .NET by default. Without this, even a correct
            // number like 437 fails to resolve. Registering twice is harmless.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var declared = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            if (Resolves(declared))
                return true;

            var actual = (int)GetOEMCP();
            if (!Resolves(actual))
                return false;

            Encoding.RegisterProvider(new SentinelCodePageProvider(declared, actual));
            return Resolves(declared);
        }
        catch
        {
            // Do not fail startup over this. The clipboard fallbacks cover the failure.
            return false;
        }
    }

    private static bool Resolves(int codePage)
    {
        try { return Encoding.GetEncoding(codePage) is not null; }
        catch { return false; }
    }

    /// <summary>Answers for the single sentinel value this culture reports and declines everything
    /// else, so no other encoding lookup in the process changes behaviour.</summary>
    private sealed class SentinelCodePageProvider(int sentinel, int actual) : EncodingProvider
    {
        public override Encoding? GetEncoding(int codepage) =>
            codepage == sentinel ? Encoding.GetEncoding(actual) : null;

        public override Encoding? GetEncoding(string name) => null;
    }
}
