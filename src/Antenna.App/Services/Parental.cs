using Antenna.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App.Services;

/// <summary>
/// PIN-protected categories. Locked categories are hidden everywhere until the PIN is entered;
/// they stay unlocked until "Lock now" or the app restarts.
/// </summary>
internal static class Parental
{
    private static bool _unlocked;

    public static event Action? Changed;

    public static bool IsPinSet => AppSettings.ParentalPinHash is not null;

    public static bool IsLocked => IsPinSet && !_unlocked;

    public static void Apply() => AppServices.Library.ParentalLockActive = IsLocked;

    public static void SetPin(string? pin)
    {
        AppSettings.ParentalPinHash = pin is null ? null : PinHasher.Hash(pin);
        _unlocked = false;
        Apply();
        Changed?.Invoke();
    }

    public static bool TryUnlock(string pin)
    {
        if (!PinHasher.Verify(pin, AppSettings.ParentalPinHash))
            return false;
        _unlocked = true;
        Apply();
        Changed?.Invoke();
        return true;
    }

    public static void Lock()
    {
        _unlocked = false;
        Apply();
        Changed?.Invoke();
    }

    /// <summary>Asks for the PIN if needed. True once unlocked (or if no PIN is set).</summary>
    public static async Task<bool> EnsureUnlockedAsync(XamlRoot root)
    {
        if (!IsLocked)
            return true;
        var pin = await AskPinAsync(root, "Enter parental PIN", "This category is locked.");
        if (pin is null)
            return false;
        if (TryUnlock(pin))
            return true;
        await new ContentDialog { XamlRoot = root, Title = "Wrong PIN", CloseButtonText = "OK" }.ShowAsync();
        return false;
    }

    public static async Task<string?> AskPinAsync(XamlRoot root, string title, string? message = null)
    {
        var box = new PasswordBox { PlaceholderText = "4–8 digits", MaxLength = 8 };
        box.InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.NumericPin) } };
        var panel = new StackPanel { Spacing = 8 };
        if (message is not null)
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = !PinHasher.IsValidPin(box.Password);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }
}
