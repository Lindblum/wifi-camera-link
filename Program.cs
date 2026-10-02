using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WiFiCameraLink;

static class AppPaths
{
    public const string Name = "WiFiCameraLink";
    const string OldName = "AnesokLink"; // the app's name before it supported other cameras

    public static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
    public static readonly string ReportDir = Path.Combine(AppDir, "reports");
    public static readonly string LogFile = Path.Combine(AppDir, "app.log");

    /// <summary>
    /// Moves settings, the depth model, snapshots and recordings from the old app name's folders,
    /// once, so nothing is lost or downloaded again after the rename.
    /// </summary>
    public static void MigrateFromOldName()
    {
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos,
        })
        {
            string root = Environment.GetFolderPath(folder);
            string from = Path.Combine(root, OldName), to = Path.Combine(root, Name);
            try
            {
                if (Directory.Exists(from) && !Directory.Exists(to))
                    Directory.Move(from, to);
            }
            catch (IOException)
            {
                // e.g. a file in use; the old folder stays and the app starts fresh in the new one
            }
        }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        // Only one copy may run: two would fight over the Wi-Fi adapter, and closing either one
        // would move Wi-Fi back to the home network under the other.
        using var instance = new Mutex(initiallyOwned: true, @"Local\WiFiCameraLink.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            BringExistingWindowToFront();
            return;
        }

        AppPaths.MigrateFromOldName();
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        finally
        {
            // Safety net if the window didn't get to restore the network itself (e.g. a crash).
            NetworkRestore.TryRestore();
        }
    }

    static void BringExistingWindowToFront()
    {
        var current = Process.GetCurrentProcess();
        var other = Process.GetProcessesByName(current.ProcessName).FirstOrDefault(p => p.Id != current.Id);
        if (other?.MainWindowHandle is { } handle && handle != IntPtr.Zero)
        {
            ShowWindow(handle, 9); // SW_RESTORE, in case it's minimized
            SetForegroundWindow(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
}
