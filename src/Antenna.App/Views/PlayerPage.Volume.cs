using Antenna.App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Antenna.App.Views;

/// <summary>
/// Volume like VLC's: up to 200% (libvlc amplifies past 100%), Ctrl+Up/Down and the mouse wheel,
/// remembered between channels and sessions.
/// </summary>
public sealed partial class PlayerPage
{
    private const int MaxVolume = 200;
    private const int VolumeStep = 5;
    private static readonly SolidColorBrush Normal = new(Colors.White);
    private static readonly SolidColorBrush Boosted = new(ColorHelper.FromArgb(255, 0xFF, 0xB0, 0x20));

    private bool _volumeReady;

    /// <summary>Puts the remembered volume on the slider (and so on the player).</summary>
    private void RestoreVolume()
    {
        if (_volumeReady)
            return;
        VolumeSlider.Value = AppSettings.PlayerVolume;
        ShowVolume(AppSettings.PlayerVolume);
        _volumeReady = true;
    }

    private void ShowVolume(int volume)
    {
        // The slider reports its starting value while the page is still loading, before the label exists.
        if (VolumeText is null)
            return;
        VolumeText.Text = $"{volume}%";
        // Amber past 100%: boosted audio can distort, so make it obvious.
        VolumeText.Foreground = volume > 100 ? Boosted : Normal;
    }

    private void ChangeVolume(int by)
    {
        var volume = Math.Clamp((int)VolumeSlider.Value + by, 0, MaxVolume);
        VolumeSlider.Value = volume;
        if (_muted && by > 0)
            ToggleMute();
        ShowStatusBriefly(volume > 100 ? $"Volume {volume}% (boosted)" : $"Volume {volume}%");
        ShowControls();
    }

    private void VolumeUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () => ChangeVolume(VolumeStep));

    private void VolumeDown_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () => ChangeVolume(-VolumeStep));

    private void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        if (delta != 0)
            ChangeVolume(delta > 0 ? VolumeStep : -VolumeStep);
        e.Handled = true;
    }
}
