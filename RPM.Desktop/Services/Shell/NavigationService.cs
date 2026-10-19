using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Page navigation over the shell's <see cref="Frame"/>. The steps are plain pages, not wizard regions: a model navigates by page type and hands the next
/// page its data as the navigation parameter, and the page builds its own view model from it.
/// </summary>
public sealed class NavigationService
{
    private Frame? _frame;

    /// <summary>The shell hands its Frame over once it is loaded.</summary>
    public void Attach(Frame frame) => _frame = frame;

    public Frame? Frame => _frame;

    public void Navigate(Type page, object? parameter = null) => _frame?.Navigate(page, parameter);

    /// <summary>Navigate and drop the history. It is used by "start over", so Back cannot walk into a
    /// finished run's pages.</summary>
    public void NavigateRoot(Type page, object? parameter = null)
    {
        if (_frame is null) return;
        _frame.Navigate(page, parameter);
        _frame.BackStack.Clear();
    }

    public void Back()
    {
        if (_frame?.CanGoBack == true)
            _frame.GoBack();
    }
}
