using Antenna.App.Services;
using Antenna.Core.Licensing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Antenna.App;

/// <summary>The paywall: covers the window until the PC is signed in and the account has a trial or plan running.</summary>
public sealed partial class MainWindow
{
    private Grid? _gate;
    private TextBlock? _gateTitle;
    private TextBlock? _gateText;
    private TextBlock? _gateCode;
    private StackPanel? _gateButtons;

    internal void SetUpLicenceGate()
    {
        if (!Licensing.Enabled)
            return;
        Licensing.Changed += ShowLicenceGate;
        ShowLicenceGate();
    }

    private void ShowLicenceGate()
    {
        var status = Licensing.Status;
        if (status == LicenceStatus.Active)
        {
            if (_gate is not null)
                _gate.Visibility = Visibility.Collapsed;
            return;
        }
        EnsureGate();
        _gate!.Visibility = Visibility.Visible;
        // Nothing keeps playing behind the paywall.
        Open("home");
        _gateCode!.Visibility = Visibility.Collapsed;
        _gateButtons!.Children.Clear();
        switch (status)
        {
            case LicenceStatus.SignedOut:
                _gateTitle!.Text = "Sign in to Antenna";
                _gateText!.Text = "Start your 7-day free trial, or use your plan, by signing in to your Antenna account. It takes a moment: you'll get a code to enter on the Antenna website.";
                AddGateButton("Sign in", StartSignIn, accent: true);
                break;
            case LicenceStatus.Expired:
                _gateTitle!.Text = Licensing.Licence?.Plan is "trial" or "none" ? "Your free trial has ended" : "Your plan has ended";
                _gateText!.Text = $"Subscribe on the Antenna website to keep watching ({Licensing.Licence?.Email}). Once you've paid, Antenna unlocks by itself within a minute, or press Refresh.";
                AddGateButton("Subscribe", () => OpenSite(Licensing.Client.PricingUrl), accent: true);
                AddGateButton("Refresh", () => _ = Licensing.RefreshAsync());
                AddGateButton("Use another account", Licensing.SignOut);
                break;
            case LicenceStatus.Stale:
                _gateTitle!.Text = "Connect to the internet";
                _gateText!.Text = "Antenna hasn't been able to check your account for a while. Connect to the internet and press Refresh.";
                AddGateButton("Refresh", () => _ = Licensing.RefreshAsync(), accent: true);
                AddGateButton("Sign out", Licensing.SignOut);
                break;
        }
    }

    private async void StartSignIn()
    {
        _gateButtons!.Children.Clear();
        _gateText!.Text = "Getting a code…";
        try
        {
            var start = await Licensing.SignInAsync(error =>
            {
                if (error is not null && _gateText is not null)
                {
                    _gateText.Text = error;
                    _gateCode!.Visibility = Visibility.Collapsed;
                    _gateButtons!.Children.Clear();
                    AddGateButton("Try again", StartSignIn, accent: true);
                }
            });
            _gateCode!.Text = start.UserCode;
            _gateCode.Visibility = Visibility.Visible;
            _gateText.Text = $"Go to {Licensing.Client.LinkUrl("").Split('?')[0]} (it opens now), sign in or create an account, and enter this code. This window carries on by itself.";
            AddGateButton("Open the sign-in page", () => OpenSite(Licensing.Client.LinkUrl(start.UserCode)), accent: true);
            AddGateButton("Cancel", () => { Licensing.CancelSignIn(); ShowLicenceGate(); });
            OpenSite(Licensing.Client.LinkUrl(start.UserCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LicenceServerException or System.Text.Json.JsonException)
        {
            _gateText.Text = $"Couldn't reach the Antenna website: {ex.Message}";
            AddGateButton("Try again", StartSignIn, accent: true);
        }
    }

    private void EnsureGate()
    {
        if (_gate is not null)
            return;
        _gateTitle = new TextBlock { Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        _gateText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, MaxWidth = 520 };
        _gateCode = new TextBlock { FontSize = 44, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 200, Visibility = Visibility.Collapsed };
        _gateButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var panel = new StackPanel { Spacing = 16, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(32) };
        panel.Children.Add(_gateTitle);
        panel.Children.Add(_gateText);
        panel.Children.Add(_gateCode);
        panel.Children.Add(_gateButtons);
        _gate = new Grid { Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] };
        _gate.Children.Add(panel);
        Grid.SetRowSpan(_gate, 2);
        RootGrid.Children.Add(_gate);
    }

    private void AddGateButton(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent)
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => click();
        _gateButtons!.Children.Add(button);
    }

    private static void OpenSite(string url) =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
}
