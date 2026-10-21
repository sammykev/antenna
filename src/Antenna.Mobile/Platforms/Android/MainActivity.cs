using Android.App;
using Android.Content;
using Android.Content.PM;

namespace Antenna.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
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

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == PickRequest)
            _pick?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }
}
