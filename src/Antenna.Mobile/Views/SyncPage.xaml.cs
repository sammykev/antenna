using Antenna.Mobile.Services;

namespace Antenna.Mobile.Views;

public partial class SyncPage : ContentPage
{
    public SyncPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        MobileSync.Synced += Synced;
        PassBox.Text = await MobileSync.PassphraseAsync() ?? "";
        Show(null, null);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        MobileSync.Synced -= Synced;
    }

    private void Synced(string? summary, string? error) => MainThread.BeginInvokeOnMainThread(() =>
    {
        Busy.IsRunning = false;
        Show(summary, error);
    });

    private async void OpenFile_Clicked(object? sender, EventArgs e) => await ChooseAsync(create: false);

    private async void CreateFile_Clicked(object? sender, EventArgs e) => await ChooseAsync(create: true);

    private async Task ChooseAsync(bool create)
    {
        if (!await SavePassphraseAsync())
            return;
        try
        {
            if (await MobileSync.ChooseFileAsync(create))
                await SyncAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Choosing the sync file failed", ex);
            Show(null, ex.Message);
        }
    }

    private async void SyncNow_Clicked(object? sender, EventArgs e)
    {
        if (await SavePassphraseAsync())
            await SyncAsync();
    }

    private async Task SyncAsync()
    {
        Busy.IsRunning = true;
        StatusText.Text = "Syncing…";
        await MobileSync.RunAsync();
        Busy.IsRunning = false;
    }

    private async Task<bool> SavePassphraseAsync()
    {
        var pass = PassBox.Text ?? "";
        if (pass.Length < 8)
        {
            Show(null, "Enter a passphrase of at least 8 characters, the same one as on your PC.");
            return false;
        }
        await MobileSync.SetPassphraseAsync(pass);
        return true;
    }

    private void Off_Clicked(object? sender, EventArgs e)
    {
        MobileSync.TurnOff();
        PassBox.Text = "";
        Show(null, null);
    }

    private void Show(string? summary, string? error)
    {
        FileText.Text = MobileSync.HasFile ? $"Sync file: {MobileSync.FileName}" : "No sync file chosen yet.";
        SyncNowButton.IsEnabled = OffButton.IsEnabled = MobileSync.HasFile;
        StatusText.Text =
            error is not null ? $"Sync failed: {error}" :
            MobileSync.LastSynced is { } at ? $"Last synced {at:HH:mm}. {summary}" :
            MobileSync.HasFile ? "Syncs when Antenna opens and every few minutes." : "";
    }
}
