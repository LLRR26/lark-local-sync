using System.Windows;
using LarkCloud.App.Service;
using LarkCloud.App.UI;
using LarkCloud.Core;

namespace LarkCloud.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
#if DEBUG
        if (args.Contains("--ui-preview"))
        {
            // Design preview: no tray, no sign-in, no single-instance lock; sample data only.
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            var pv = new System.Windows.Application();
            Theme.Init(pv);
            PreviewModel.Run(pv, args);
            return;
        }
#endif
        using var single = new Mutex(true, @"Local\LarkCloudDrive", out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LarkCloudDrive.Show");
        if (!first)
        {
            show.Set();                          // already running: ask it to show its window
            return;
        }

        Log.Init();
        Log.Info($"{AppPaths.AppTitle} {AppPaths.Version} starting on {Environment.OSVersion}");
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Unexpected error: {e.ExceptionObject}");

        if (Environment.OSVersion.Version.Build < 17763)
        {
            System.Windows.Forms.MessageBox.Show("Lark Cloud Drive needs Windows 10 version 1809 or newer (or Windows 11).", AppPaths.AppTitle,
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return;
        }

        // The tray icon (WinForms) and the windows (WPF) share this one STA thread and its message loop.
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log.Error($"UI error: {e.Exception}"); e.Handled = true; };
        Theme.Init(app);

        var cfg = Config.Load();
        using var svc = new SyncService(cfg);
        using var tray = new TrayApp(svc);
        var listener = new Thread(() =>
        {
            while (show.WaitOne()) tray.ShowFolders();
        }) { IsBackground = true };
        listener.Start();
        app.Run();
        Log.Info("Stopped");
    }
}
