using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace Antenna.App.Services;

/// <summary>
/// Puts problems in front of the user with what's needed to report them: a copyable summary with the
/// recent log, instead of a silent exit or a log file they have to dig out.
/// </summary>
internal static class CrashReport
{
    private static int _fatalShown;

    /// <summary>Version, OS, the error and the last 200 log lines: everything needed to diagnose it.</summary>
    public static string Details(Exception ex, string where) =>
        $"""
        Antenna {Updater.CurrentVersion} on {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})
        Where: {where}

        {ex}

        --- Log (last 200 lines) ---
        {Log.Tail(200)}
        """;

    /// <summary>Copies text to the clipboard. Call on the UI thread.</summary>
    public static void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    /// <summary>
    /// For failures that stop Antenna: a Windows message box (works even when no window could be
    /// created) offering to open the log folder. Ctrl+C copies a message box's text. Shown once.
    /// </summary>
    public static void ShowFatal(Exception ex, string what)
    {
        if (App.SmokeTest || Interlocked.Exchange(ref _fatalShown, 1) == 1)
            return;
        try
        {
            var text = $"""
                Antenna couldn't {what}.

                {ex.GetType().Name}: {ex.Message}

                Details are in the log: {Log.FilePath}
                (Press Ctrl+C to copy this message.)

                Open the log folder now?
                """;
            if (MessageBox(IntPtr.Zero, text, "Antenna", MbYesNo | MbIconError | MbSetForeground) == IdYes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private const uint MbYesNo = 0x4;
    private const uint MbIconError = 0x10;
    private const uint MbSetForeground = 0x10000;
    private const int IdYes = 6;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
