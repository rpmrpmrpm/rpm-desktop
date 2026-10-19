using System.ComponentModel;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Shell.Models;

/// <summary>
/// Shell view-model. It is created by dependency injection, so the account service is ready when
/// this object is built. (The page constructor runs during the first navigation, before the host
/// is built.) Owns the header account chip label.
/// </summary>
[RpmBindable]
public sealed partial class ShellModel : INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcher;

    public AuthService Auth { get; }

    public ShellModel(AuthService auth, DispatcherQueue dispatcher)
    {
        Auth = auth;
        _dispatcher = dispatcher;
        Auth.Changed += () => _dispatcher.TryEnqueue(RaiseAll);
    }

    /// <summary>Header chip label: the signed-in name/email, or "Sign in".</summary>
    public string AccountText => Auth.IsLoggedIn
        ? (Auth.DisplayName ?? Auth.Email ?? Loc.Get("Login_Title"))
        : Loc.Get("Login_SignIn");

    // The icon and colour match the other rpm shells. Signed out: cyan sign-in glyph.
    // Signed in: green user glyph.
    public string AccountGlyph => Auth.IsLoggedIn ? "\uF007" : "\uF2F6";

    public Brush AccountGlyphBrush => new SolidColorBrush(Auth.IsLoggedIn
        ? Color.FromArgb(0xFF, 0x00, 0xF1, 0x5E)   // #00f15e green
        : Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF)); // #04c8ff cyan

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new(nameof(AccountText)));
        PropertyChanged?.Invoke(this, new(nameof(AccountGlyph)));
        PropertyChanged?.Invoke(this, new(nameof(AccountGlyphBrush)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
