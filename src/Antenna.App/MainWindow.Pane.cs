using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Antenna.App;

/// <summary>
/// The menu down the left is just icons until it's used: it opens over the page while the pointer rests on it, or
/// while the keyboard (or a TV remote) is in it, and shrinks back when either leaves. Opening waits a moment, so
/// passing over it on the way somewhere doesn't flash it open.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(300);
    private DispatcherQueueTimer? _paneTimer;
    private FrameworkElement? _pane;
    private bool _pointerOnPane;
    private bool _pendingOpen;

    private void SetUpAutoPane()
    {
        _paneTimer = DispatcherQueue.CreateTimer();
        _paneTimer.IsRepeating = false;
        _paneTimer.Tick += OnPaneTimer;
        NavView.Loaded += (_, _) =>
        {
            // The pane's content in NavigationView's template; found once the template is applied.
            _pane = Find(NavView, "PaneContentGrid") ?? Find(NavView, "PaneRoot");
            if (_pane is null)
                return;
            _pane.PointerEntered += (_, _) => { _pointerOnPane = true; SchedulePane(open: true, OpenDelay); };
            _pane.PointerExited += (_, _) => { _pointerOnPane = false; SchedulePane(open: false, CloseDelay); };
            _pane.GotFocus += (_, _) => { if (FocusCameFromKeys()) SchedulePane(open: true, TimeSpan.Zero); };
            _pane.LostFocus += (_, _) => SchedulePane(open: false, CloseDelay);
        };
    }

    private void SchedulePane(bool open, TimeSpan delay)
    {
        if (_paneTimer is null)
            return;
        _paneTimer.Stop();
        _paneTimer.Interval = delay;
        _pendingOpen = open;
        if (delay == TimeSpan.Zero)
            OnPaneTimer(_paneTimer, null);
        else
            _paneTimer.Start();
    }

    private void OnPaneTimer(DispatcherQueueTimer sender, object? args)
    {
        sender.Stop();
        if (!NavView.IsPaneVisible)
            return;
        if (_pendingOpen)
        {
            NavView.IsPaneOpen = true;
            return;
        }
        // Stay open while the pointer is still on it or something in it has focus (typing a search).
        if (_pointerOnPane || FocusIsInPane())
            return;
        NavView.IsPaneOpen = false;
    }

    private bool FocusIsInPane()
    {
        if (_pane is null || FocusManager.GetFocusedElement(Content.XamlRoot) is not DependencyObject focused)
            return false;
        for (var e = focused; e is not null; e = VisualTreeHelper.GetParent(e))
            if (ReferenceEquals(e, _pane))
                return true;
        return false;
    }

    /// <summary>Keyboard or remote focus opens the menu; a click on an icon already does what it should.</summary>
    private bool FocusCameFromKeys() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is Control { FocusState: FocusState.Keyboard };

    private static FrameworkElement? Find(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } fe && n == name)
                return fe;
            if (Find(child, name) is { } found)
                return found;
        }
        return null;
    }
}
