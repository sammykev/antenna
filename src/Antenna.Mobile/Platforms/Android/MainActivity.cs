using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Util;

namespace Antenna.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    SupportsPictureInPicture = true, ResizeableActivity = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public const string FixtureExtra = "antenna.fixture";
    public const string WatchExtra = "antenna.watch";
    private const int PickRequest = 4711;
    private static TaskCompletionSource<Android.Net.Uri?>? _pick;

    /// <summary>Runs a document picker (open or create) and returns the chosen document, or null if cancelled.</summary>
    public static Task<Android.Net.Uri?> PickDocumentAsync(Intent intent)
    {
        _pick?.TrySetResult(null);
        _pick = new TaskCompletionSource<Android.Net.Uri?>();
        Platform.CurrentActivity!.StartActivityForResult(intent, PickRequest);
        return _pick.Task;
    }

    /// <summary>True while a video is playing, so leaving the app shrinks it into a picture-in-picture window.</summary>
    public static bool PipWanted { get; private set; }

    /// <summary>Raised when the app goes into (true) or out of (false) picture-in-picture.</summary>
    public static event Action<bool>? PipChanged;

    public static bool InPip => Platform.CurrentActivity is { IsInPictureInPictureMode: true };

    /// <summary>Called by the player as playback starts and stops; on Android 12+ PiP then starts by itself on Home.</summary>
    public static void WantPip(bool wanted, int width = 16, int height = 9)
    {
        PipWanted = wanted;
        if (Platform.CurrentActivity is not { } activity || !OperatingSystem.IsAndroidVersionAtLeast(26))
            return;
        try
        {
            activity.SetPictureInPictureParams(PipParams(wanted, width, height));
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Not allowed in this state (e.g. PiP turned off for Antenna in system settings).
        }
    }

    public static void EnterPip(int width = 16, int height = 9)
    {
        if (Platform.CurrentActivity is not { } activity || !OperatingSystem.IsAndroidVersionAtLeast(26))
            return;
        try
        {
            activity.EnterPictureInPictureMode(PipParams(true, width, height));
        }
        catch (Java.Lang.IllegalStateException)
        {
        }
    }

    private static PictureInPictureParams PipParams(bool autoEnter, int width, int height)
    {
        // Android only accepts ratios between 1:2.39 and 2.39:1.
        var ratio = Math.Clamp(width > 0 && height > 0 ? (double)width / height : 16.0 / 9, 1 / 2.38, 2.38);
        var builder = new PictureInPictureParams.Builder().SetAspectRatio(new Rational((int)Math.Round(ratio * 1000), 1000))!;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            builder.SetAutoEnterEnabled(autoEnter)!.SetSeamlessResizeEnabled(true);
        return builder.Build()!;
    }

    protected override void OnUserLeaveHint()
    {
        base.OnUserLeaveHint();
        // Before Android 12 there's no auto-enter: go into PiP as the user leaves.
        if (PipWanted && !OperatingSystem.IsAndroidVersionAtLeast(31))
            EnterPip();
    }

    public override void OnPictureInPictureModeChanged(bool isInPictureInPictureMode, Configuration? newConfig)
    {
        base.OnPictureInPictureModeChanged(isInPictureInPictureMode, newConfig);
        PipChanged?.Invoke(isInPictureInPictureMode);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    /// <summary>A tapped score notification: Watch opens the channel, anything else the Matches page.</summary>
    private static void HandleIntent(Intent? intent)
    {
        var fixture = intent?.GetStringExtra(FixtureExtra);
        var watch = intent?.GetStringExtra(WatchExtra);
        if (fixture is null && watch is null)
            return;
        intent!.RemoveExtra(FixtureExtra);
        intent.RemoveExtra(WatchExtra);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // On a cold start the shell needs a moment to exist.
            for (var i = 0; Shell.Current is null && i < 40; i++)
                await Task.Delay(100);
            if (Shell.Current is null)
                return;
            if (watch?.Split('|', 2) is [var source, var external]
                && Services.MobileServices.Library.GetItem(source, external, Antenna.Core.MediaKind.Live) is { } entry)
                await Services.Playback.PlayAsync([entry.Item], 0);
            else
                await Shell.Current.GoToAsync("//matches");
        });
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == PickRequest)
            _pick?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }
}
