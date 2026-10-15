using Antenna.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Antenna.App;

/// <summary>
/// TV mode, for a PC on the TV: everything 1.5× bigger, the window maximised, arrow keys / D-pad
/// moving focus between tiles, and the controller's B button going back anywhere.
/// </summary>
public sealed partial class MainWindow
{
    public const double TvScale = 1.5;

    private Viewbox? _tvScaler;
    private KeyboardAccelerator? _tvBack;

    public void ApplyTvMode(bool on)
    {
        if (on == _tvScaler is not null)
            return;
        Log.Info($"TV mode {(on ? "on" : "off")}");

        if (on)
        {
            // The pane and pages lay out at 1/1.5 of the window and are scaled up to fill it, so
            // everything grows without anything being cut off.
            RootGrid.Children.Remove(NavView);
            _tvScaler = new Viewbox { Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill, Child = NavView };
            Grid.SetRow(_tvScaler, 1);
            RootGrid.Children.Insert(1, _tvScaler);
            RootGrid.SizeChanged += OnTvSizeChanged;
            SizeTvContent();

            RootGrid.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            _tvBack = new KeyboardAccelerator { Key = Windows.System.VirtualKey.GamepadB };
            _tvBack.Invoked += (_, a) =>
            {
                if (ContentFrame.CanGoBack)
                {
                    ContentFrame.GoBack();
                    a.Handled = true;
                }
            };
            RootGrid.KeyboardAccelerators.Add(_tvBack);
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }
        else
        {
            RootGrid.SizeChanged -= OnTvSizeChanged;
            RootGrid.Children.Remove(_tvScaler);
            _tvScaler!.Child = null;
            _tvScaler = null;
            NavView.Width = double.NaN;
            NavView.Height = double.NaN;
            Grid.SetRow(NavView, 1);
            RootGrid.Children.Insert(1, NavView);
            RootGrid.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Auto;
            if (_tvBack is not null)
                RootGrid.KeyboardAccelerators.Remove(_tvBack);
            _tvBack = null;
        }
    }

    private void OnTvSizeChanged(object sender, SizeChangedEventArgs e) => SizeTvContent();

    private void SizeTvContentIfOn()
    {
        if (_tvScaler is not null)
            SizeTvContent();
    }

    private void SizeTvContent()
    {
        var height = RootGrid.ActualHeight - (AppTitleBar.Visibility == Visibility.Visible ? AppTitleBar.ActualHeight : 0);
        if (RootGrid.ActualWidth <= 0 || height <= 0)
            return;
        NavView.Width = RootGrid.ActualWidth / TvScale;
        NavView.Height = height / TvScale;
    }
}
