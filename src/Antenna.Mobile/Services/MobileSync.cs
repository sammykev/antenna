using Android.Content;
using Antenna.Core.Sync;

namespace Antenna.Mobile.Services;

/// <summary>The sync file as a document the user picked (Google Drive, OneDrive…), through Android's storage access.</summary>
internal sealed class DocumentSyncTarget(Android.Net.Uri uri) : ISyncTarget
{
    private static ContentResolver Resolver => Platform.AppContext.ContentResolver!;

    public async Task<byte[]?> ReadAsync(CancellationToken ct = default)
    {
        await using var input = Resolver.OpenInputStream(uri) ?? throw new IOException("Couldn't open the sync file.");
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        // A file just created by the picker is empty: nothing synced yet.
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        Stream? output;
        try
        {
            output = Resolver.OpenOutputStream(uri, "wt");
        }
        catch (Java.Lang.Exception)
        {
            output = Resolver.OpenOutputStream(uri, "w");  // some providers don't offer truncate
        }
        await using var stream = output ?? throw new IOException("Couldn't write the sync file.");
        await stream.WriteAsync(data, ct);
    }
}

/// <summary>Syncs with the PC through the shared file: on start, when the app comes back, every 5 minutes and on demand.</summary>
public static class MobileSync
{
    private const string UriKey = "sync.uri";
    private const string PassKey = "sync.pass";

    private static bool _running;
    private static IDispatcherTimer? _timer;

    public static event Action<string?, string?>? Synced;

    public static DateTimeOffset? LastSynced { get; private set; }

    public static string? FileName { get; private set; } = Preferences.Get("sync.name", null as string);

    public static bool HasFile => Preferences.ContainsKey(UriKey);

    public static void Start(IDispatcher dispatcher)
    {
        if (_timer is not null)
            return;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(5);
        _timer.Tick += async (_, _) => await RunAsync();
        _timer.Start();
        _ = RunAsync();
    }

    public static Task<string?> PassphraseAsync() => SecureStorage.Default.GetAsync(PassKey);

    /// <summary>Lets the user create a new sync file, or pick the one the PC made.</summary>
    public static async Task<bool> ChooseFileAsync(bool create)
    {
        var intent = new Intent(create ? Intent.ActionCreateDocument : Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(create ? "application/octet-stream" : "*/*");
        if (create)
            intent.PutExtra(Intent.ExtraTitle, FileSyncTarget.FileName);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        if (await MainActivity.PickDocumentAsync(intent) is not { } uri)
            return false;

        Platform.AppContext.ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        if (Preferences.Get(UriKey, null as string) != uri.ToString())
            SyncService.ResetBase(MobileServices.Library);  // a different file: merge into it, don't delete
        Preferences.Set(UriKey, uri.ToString());
        FileName = DisplayName(uri);
        Preferences.Set("sync.name", FileName);
        return true;
    }

    public static Task SetPassphraseAsync(string passphrase) => SecureStorage.Default.SetAsync(PassKey, passphrase);

    public static async Task RunAsync()
    {
        if (_running || Preferences.Get(UriKey, null as string) is not { } stored || await PassphraseAsync() is not { Length: > 0 } passphrase)
            return;
        _running = true;
        try
        {
            var target = new DocumentSyncTarget(Android.Net.Uri.Parse(stored)!);
            var outcome = await Task.Run(() => SyncService.SyncAsync(MobileServices.Library, target, passphrase, DeviceInfo.Name));
            LastSynced = DateTimeOffset.Now;
            if (outcome.Changes > 0 || outcome.Wrote)
                Log.Info($"Sync: {outcome.Changes} change(s), {outcome.NewSources.Count} new playlist(s), wrote={outcome.Wrote}");
            if (outcome.Changes > 0)
                MobileServices.NotifyChanged();
            Synced?.Invoke(outcome.Changes == 0 ? "Up to date." : $"{outcome.Changes} change(s) from your other devices.", null);
            foreach (var source in outcome.NewSources)
            {
                try { await MobileServices.Refresher.RefreshAsync(source); }
                catch (Exception ex) { Log.Info($"Sync: couldn't load {source.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is SyncPassphraseException or InvalidDataException or IOException or System.Text.Json.JsonException
            or Java.Lang.SecurityException or Java.IO.IOException)
        {
            Log.Info($"Sync failed: {ex.Message}");
            Synced?.Invoke(null, ex is Java.Lang.SecurityException ? "Antenna lost access to the sync file. Choose it again." : ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    public static void TurnOff()
    {
        Preferences.Remove(UriKey);
        Preferences.Remove("sync.name");
        SecureStorage.Default.Remove(PassKey);
        SyncService.ResetBase(MobileServices.Library);
        FileName = null;
        LastSynced = null;
    }

    private static string? DisplayName(Android.Net.Uri uri)
    {
        using var cursor = Platform.AppContext.ContentResolver!.Query(uri, [Android.Provider.IOpenableColumns.DisplayName], null, null, null);
        return cursor is not null && cursor.MoveToFirst() ? cursor.GetString(0) : uri.LastPathSegment;
    }
}
