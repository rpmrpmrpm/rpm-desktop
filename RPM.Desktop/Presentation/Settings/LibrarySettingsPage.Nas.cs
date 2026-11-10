using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Nas;
using RPM.Core.Sharing;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services.Nas;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// The NAS rows under the music folder: the space on the NAS the library is on, "Move collection to" a
/// NAS, or "Use" for a library that is already in a NAS's libraries folder.
/// </summary>
public sealed partial class LibrarySettingsPage
{
    private static readonly Windows.UI.Color NasAmber = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD4, 0x66);
    private NasDiscovery? _nasDiscovery;
    private NasLibraryService? _nasService;
    private CancellationTokenSource? _move;

    private void InitNas()
    {
        _nasDiscovery = _services.GetService<NasDiscovery>();
        _nasService = _services.GetService<NasLibraryService>();
        if (_nasDiscovery is not { } discovery)
            return;
        discovery.Changed += OnNasChanged;
        Unloaded += (_, _) =>
        {
            discovery.Changed -= OnNasChanged;
            _move?.Cancel();
        };
        discovery.Start();
        discovery.Ask();
        _ = ShowNasAsync();
    }

    // Raised on a background thread. The callback must not throw: on WinUI an exception there ends the app.
    private void OnNasChanged() => DispatcherQueue.TryEnqueue(async () =>
    {
        try { await ShowNasAsync(); }
        catch (Exception ex) { RPM.Core.Diagnostics.Scope.Said("nas", $"the NAS rows could not be shown: {ex.Message}"); }
    });

    private async Task ShowNasAsync()
    {
        if (_nasDiscovery is null || _nasService is null || _move is not null)
            return;
        var machines = _nasDiscovery.Machines.Where(n => n.SameVersion).ToList();
        var rows = new List<UIElement>();
        foreach (var nas in machines)
        {
            var hello = await NasLibraryService.HelloAsync(nas, CancellationToken.None);
            rows.Add(NasRow(nas, hello));
        }

        if (_move is not null)
            return;
        NasPanel.Children.Clear();
        foreach (var row in rows)
            NasPanel.Children.Add(row);
    }

    private UIElement NasRow(DiscoveredNas nas, PeerHello? hello)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Format("Nas_Location", nas.Name),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        });
        var line = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(line);

        if (hello?.Nas is not { } space)
        {
            line.Text = Loc.Format("Nas_Unreachable", nas.Name);
            line.Foreground = new SolidColorBrush(NasAmber);
            return panel;
        }

        var limit = space.LimitBytes ?? space.UsedBytes + space.FreeBytes;
        var spaceText = Loc.Format("Nas_Space", ByteSize.Human(space.UsedBytes), ByteSize.Human(limit));

        if (_settings.LibraryNas?.DeviceId == nas.DeviceId)
        {
            line.Text = spaceText;
            return panel;
        }

        if (_nasService!.OpenLibraryOn(space) is { } here)
        {
            line.Text = spaceText;
            panel.Children.Add(Action(Loc.Format("Nas_Use", nas.Name), "\uf00c", async button =>
                await UseAsync(nas, hello, here, button, line)));
            return panel;
        }

        line.Text = Loc.Format("Nas_MoveBody", nas.Name) + " " + spaceText;
        panel.Children.Add(Action(Loc.Format("Nas_Move", nas.Name), "\uf233", async button =>
            await MoveAsync(nas, hello, button, line)));
        return panel;
    }

    private static Button Action(string text, string glyph, Func<Button, Task> onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon
        {
            Style = (Style)Application.Current.Resources["FaIcon"],
            Glyph = glyph,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new RpmButton { Content = content, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 0, 16, 0) };
        button.Click += async (_, _) => await onClick(button);
        return button;
    }

    private async Task UseAsync(DiscoveredNas nas, PeerHello hello, NasLibraryEntry here, Button button, TextBlock line)
    {
        button.IsEnabled = false;
        try
        {
            NasPrepared? prepared = null;
            var login = await NasLoginDialog.ShowAsync(XamlRoot, nas, [], async (credentials, _) =>
            {
                var (problem, ready) = await _nasService!.PrepareAsync(nas, hello, credentials, here);
                prepared = ready;
                return problem;
            });
            if (login is null || prepared is null)
                return;
            _nasService!.CompleteUse(prepared);
            await ShowNasAsync();
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("nas", $"using the library on the NAS failed: {ex.Message}");
            line.Text = Loc.Format("Nas_OpenFailed", nas.Name);
            line.Foreground = new SolidColorBrush(NasAmber);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task MoveAsync(DiscoveredNas nas, PeerHello hello, Button button, TextBlock line)
    {
        button.IsEnabled = false;
        var from = _settings.LibraryPath ?? "";
        try
        {
            var login = await NasLoginDialog.ShowAsync(XamlRoot, nas, [],
                (credentials, _) => NasLibraryService.CheckLoginAsync(nas, hello, credentials));
            if (login is null)
                return;

            _move = new CancellationTokenSource();
            line.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
            var progress = new Progress<MoveProgress>(p => line.Text = Loc.Format("Nas_MoveProgress", p.Copied, p.Total));
            var (problem, move, ready) = await _nasService!.MoveToNasAsync(nas, hello, login, progress, _move.Token);

            if (ready is not null && _nasService.Open(ready) is NasOpenResult.Opened)
            {
                PathText.Text = _settings.LibraryPath;
                line.Text = Loc.Format("Nas_MoveDone", nas.Name, from);
                return;
            }

            line.Foreground = new SolidColorBrush(NasAmber);
            line.Text = move switch
            {
                MoveResult.NoRoom room => Loc.Format("Nas_MoveNoRoom", nas.Name, ByteSize.Human(room.Needed - room.Free)),
                MoveResult.Stopped or MoveResult.Failed => Loc.Get("Nas_MoveStopped"),
                _ => problem switch
                {
                    NasOpenResult.WrongLogin => Loc.Get("Nas_LoginWrong"),
                    NasOpenResult.Unreachable => Loc.Format("Nas_Unreachable", nas.Name),
                    _ => Loc.Format("Nas_OpenFailed", nas.Name),
                },
            };
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("nas", $"the move to the NAS failed: {ex.Message}");
            line.Foreground = new SolidColorBrush(NasAmber);
            line.Text = Loc.Get("Nas_MoveStopped");
        }
        finally
        {
            _move?.Dispose();
            _move = null;
            button.IsEnabled = true;
        }
    }
}
