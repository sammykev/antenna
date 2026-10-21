using System.Reflection;
using Antenna.Core.Licensing;
using Microsoft.UI.Dispatching;

namespace Antenna.App.Services;

/// <summary>
/// The account and paywall: a 7-day trial, then a monthly or yearly plan bought on the Antenna site. The app signs in
/// with a short code and keeps a licence the site signed, checked offline and refreshed in the background (every 6 h,
/// and at least every 14 days). Builds without the site's key (local builds, CI) run without accounts.
/// </summary>
internal static class Licensing
{
    private static readonly string Server = Metadata("LicenceServer");
    private static readonly string PublicKey = Metadata("LicencePublicKey");
    private static DispatcherQueueTimer? _timer;
    private static CancellationTokenSource? _signIn;

    /// <summary>Raised on the UI thread whenever the licence changes.</summary>
    public static event Action? Changed;

    public static bool Enabled => Server.Length > 0 && PublicKey.Length > 0 && !App.SmokeTest;

    public static LicenceClient Client { get; } = new(AppServices.Http, Server.Length > 0 ? Server : "https://localhost");

    public static LicencePayload? Licence => LicenceCheck.Read(AppSettings.LicenceToken, PublicKey);

    public static LicenceStatus Status => Enabled ? LicenceCheck.Evaluate(Licence, DeviceId, DateTimeOffset.UtcNow) : LicenceStatus.Active;

    /// <summary>This PC's id for the account's device list; made once.</summary>
    public static string DeviceId
    {
        get
        {
            if (AppSettings.LicenceDevice is { Length: > 0 } id)
                return id;
            id = "win-" + Guid.NewGuid().ToString("N");
            AppSettings.LicenceDevice = id;
            return id;
        }
    }

    /// <summary>"Monthly until 21 Nov", "Free trial: 5 days left"…</summary>
    public static string Summary
    {
        get
        {
            var l = Licence;
            if (l is null)
                return "Not signed in";
            var days = Math.Ceiling((l.UntilTime - DateTimeOffset.UtcNow).TotalDays);
            return l.Plan switch
            {
                _ when Status == LicenceStatus.Expired => l.Plan == "trial" || l.Plan == "none" ? "Your free trial has ended" : "Your plan has ended",
                "trial" => $"Free trial: {days} day{(days == 1 ? "" : "s")} left",
                "monthly" => $"Monthly plan until {l.UntilTime.LocalDateTime:d MMM yyyy}",
                "yearly" => $"Yearly plan until {l.UntilTime.LocalDateTime:d MMM yyyy}",
                "granted" => $"Complimentary access until {l.UntilTime.LocalDateTime:d MMM yyyy}",
                _ => "No active plan",
            } + (l.Email.Length > 0 ? $" · {l.Email}" : "");
        }
    }

    public static void Start(DispatcherQueue queue)
    {
        if (!Enabled)
            return;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromHours(6);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>Swaps the licence for a fresh one: payments and changes made on the site arrive this way.</summary>
    public static async Task RefreshAsync()
    {
        if (!Enabled || AppSettings.LicenceToken is not { Length: > 0 } token)
            return;
        try
        {
            AppSettings.LicenceToken = await Client.RefreshAsync(token);
            Log.Info($"licence: {Summary}");
        }
        catch (LicenceServerException ex) when (ex.Code is "signed_out" or "bad_licence")
        {
            Log.Info($"licence: {ex.Code}, signing out");
            AppSettings.LicenceToken = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LicenceServerException or System.Text.Json.JsonException)
        {
            // Offline or the site is busy: the current licence carries on until its refresh date.
            Log.Info($"licence: refresh failed: {ex.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>Gets a sign-in code to show, then waits (in the background) for the user to enter it on the site.</summary>
    public static async Task<LicenceClient.SignInStart> SignInAsync(Action<string?> finished)
    {
        _signIn?.Cancel();
        var cts = _signIn = new CancellationTokenSource();
        var start = await Client.StartAsync(DeviceId, Environment.MachineName, "windows", cts.Token);
        _ = Task.Run(async () =>
        {
            string? error = "The code expired. Get a new one.";
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresIn);
                while (DateTimeOffset.UtcNow < deadline && !cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(start.Interval), cts.Token);
                    if (await Client.PollAsync(start.DeviceCode, cts.Token) is { } token)
                    {
                        AppSettings.LicenceToken = token;
                        Log.Info($"licence: signed in, {Summary}");
                        error = null;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or LicenceServerException or System.Text.Json.JsonException)
            {
                error = ex.Message;
            }
            App.Window.DispatcherQueue.TryEnqueue(() =>
            {
                finished(error);
                Changed?.Invoke();
            });
        });
        return start;
    }

    public static void CancelSignIn() => _signIn?.Cancel();

    public static void SignOut()
    {
        AppSettings.LicenceToken = null;
        Changed?.Invoke();
    }

    private static string Metadata(string key) =>
        typeof(Licensing).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";
}
