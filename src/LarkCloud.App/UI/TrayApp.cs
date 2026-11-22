using Window = System.Windows.Window;
using System.Windows.Forms;
using System.Windows.Threading;
using LarkCloud.App.Service;
using LarkCloud.Core;
using Microsoft.Win32;

namespace LarkCloud.App.UI;

/// <summary>The icon next to the clock and a short menu. Everything else happens in the main window,
/// which this class feeds (<see cref="IAppModel"/>) and keeps to one instance. The dialogs (add folder, deletions,
/// sign in, feature check) are opened from here too.</summary>
internal sealed class TrayApp : IAppModel, IDisposable
{
    readonly SyncService _svc;
    readonly NotifyIcon _icon;
    readonly TrayMenu _menu;
    readonly Dictionary<string, Window> _open = new();
    bool _modalOpen;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    MainWindow? _main;
    SyncState _shown = (SyncState)(-1);
    int _shownSize;
    readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(110) };   // runs only while syncing, and only with animation effects on
    int _frame;
    readonly Updater _updater = new();

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public TrayApp(SyncService svc)
    {
        _svc = svc;
        _menu = new TrayMenu(MenuStatus, () => _svc.Config.GetPairs(), ShowFolders, _svc.SyncAllNow, TogglePauseAll, Quit, () => _updater.Installed, RestartToUpdate);
        _icon = new NotifyIcon { Icon = TrayIcons.State(SyncState.Starting, TrayIcons.TraySize), Text = AppPaths.AppTitle, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OnLeftClick(); };
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) _menu.ShowAtCursor(); };
        _icon.BalloonTipClicked += (_, _) => ShowMain(AppPage.Problems);

        _svc.StatusChanged += () => { };   // status is polled by the timer (cheap, avoids flooding the UI thread)
        _svc.Notify += m => Ui_(() => { if (_svc.Config.Notifications) Balloon(m, ToolTipIcon.Info); });   // sign-in warnings below always show
        _svc.NeedSignIn += () => Ui_(() => Balloon("Sign in to Lark again to keep your files in sync. Click the Lark icon to sign in.", ToolTipIcon.Warning));
        Theme.Mode = Theme.ModeOf(_svc.Config.Theme);
        _tick.Tick += (_, _) => RefreshIcon();
        _tick.Start();
        _spin.Tick += (_, _) => { var f = TrayIcons.SyncingFrames(_shownSize); _icon.Icon = f[++_frame % f.Length]; };
        Motion.Changed += RefreshIcon;                 // Windows animation effects switched on or off

        if (!StartsWithWindows && !File.Exists(Path.Combine(AppPaths.DataDir, "startup-asked"))) { StartsWithWindows = true; File.WriteAllText(Path.Combine(AppPaths.DataDir, "startup-asked"), "1"); }

        _updater.Ready += v => Ui_(() => Balloon($"Update {v} installed. Restart Lark Cloud Drive to use it.", ToolTipIcon.Info));
        _updater.Start();

        Ui_(FirstRun);
    }

    void Ui_(Action a) => _ui.BeginInvoke(a);

    void FirstRun()
    {
        if (!_svc.Config.IsConfigured)
        {
            ConfirmDialog.Tell(null, "Not set up yet",
                $"{AppPaths.AppTitle} isn't set up yet: the IT config.json next to LarkCloud.exe has no Lark app ID and secret.\n\n{_svc.Config.ItConfigPath}");
            return;
        }
        if (!_svc.Auth.SignedIn && !SignIn()) return;
        _svc.Start();
        if (_svc.Config.GetPairs().Count == 0 && !_svc.Config.LockFolders) ShowAddFolder();
    }

    bool SignIn()
    {
        if (_modalOpen) return false;
        var f = new SignInDialog(() => _svc.Auth.StartDeviceFlowAsync(), (flow, ct) => _svc.Auth.PollDeviceFlowAsync(flow, ct), () => _svc.Auth.UserName);
        ShowModal(f);
        if (f.Success) _svc.SignedIn();
        return f.Success;
    }

    void OnLeftClick()
    {
        var (state, _) = _svc.Combined();
        if (_svc.Auth.SignedIn && state == SyncState.Attention && _svc.Runners.Any(r => r.Status.Items.Count > 0)) ShowMain(AppPage.Problems);
        else ShowMain(AppPage.Folders);       // signed out, or deletions waiting: the Folders page shows the one action to take
    }

    void RefreshIcon()
    {
        var (state, msg) = _svc.Auth.SignedIn ? _svc.Combined() : (SyncState.Error, "Not signed in – click to sign in");
        var size = TrayIcons.TraySize;
        if (state != _shown || size != _shownSize) { _icon.Icon = TrayIcons.State(state, size); _shown = state; _shownSize = size; }
        SetSpinning((state is SyncState.Syncing or SyncState.Starting) && Motion.Enabled);
        var tip = $"{AppPaths.AppTitle}\n{msg}";
        _icon.Text = tip.Length > 127 ? tip[..124] + "…" : tip;
    }

    /// <summary>While syncing the arrow in the tray icon's dot turns: pre-drawn frames cycled by a timer that exists only for that time.</summary>
    void SetSpinning(bool on)
    {
        if (on == _spin.IsEnabled) return;
        if (on) { _frame = 0; _spin.Start(); }
        else { _spin.Stop(); _icon.Icon = TrayIcons.State(_shown, _shownSize); }
    }

    (string, string) MenuStatus() { var s = StatusSummary.Of(this); return (s.Sentence, s.Glyph); }   // same words and icon as the main window

    void TogglePauseAll()
    {
        var pairs = _svc.Config.GetPairs();
        var pause = !pairs.All(p => p.Paused);
        foreach (var p in pairs) _svc.SetPaused(p.Id, pause);
    }

    void Balloon(string text, ToolTipIcon icon)
    {
        _icon.BalloonTipTitle = AppPaths.AppTitle;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(8000);
    }

    // ---------------------------------------------------------------- windows (one of each at a time)
    void ShowOnce(string key, Func<Window> make)
    {
        if (_open.TryGetValue(key, out var f)) { if (f.WindowState == System.Windows.WindowState.Minimized) f.WindowState = System.Windows.WindowState.Normal; f.Activate(); return; }
        f = make();
        _open[key] = f;
        f.Closed += (_, _) => _open.Remove(key);
        f.Show();
        f.Activate();
    }

    /// <summary>A dialog the person must answer first. Sits over the main window when that is showing, else in the middle of the screen.</summary>
    void ShowModal(Window w)
    {
        if (_main is { IsVisible: true }) w.Owner = _main; else w.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        _modalOpen = true;
        try { w.ShowDialog(); } finally { _modalOpen = false; }
    }

    /// <summary>Open (or bring forward) the main window on a page.</summary>
    public void ShowMain(AppPage page, string? problemsOf = null) => Ui_(() =>
    {
        if (_main == null)
        {
            _main = new MainWindow(this);
            _main.Closed += (_, _) => _main = null;
        }
        if (page == AppPage.Problems) _main.ShowProblems(problemsOf); else _main.Navigate(page);
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == System.Windows.WindowState.Minimized) _main.WindowState = System.Windows.WindowState.Normal;
        _main.Activate();
    });

    public void ShowFolders() => ShowMain(AppPage.Folders);

    public void ShowAddFolder() => Ui_(() =>
    {
        if (_modalOpen) return;
        if (!_svc.Auth.SignedIn && !SignIn()) return;
        ShowModal(new AddFolderDialog(this));
    });

    // ---------------------------------------------------------------- IAppModel
    public bool Configured => _svc.Config.IsConfigured;
    public bool SignedIn => _svc.Auth.SignedIn;
    public string UserName => _svc.Auth.UserName;
    public bool LockFolders => _svc.Config.LockFolders;
    public Config Settings => _svc.Config;
    public void SettingsChanged() { Theme.Mode = Theme.ModeOf(_svc.Config.Theme); _svc.ApplySettings(); }
    public IReadOnlyList<Pair> Pairs() => _svc.Config.GetPairs();
    public PairStatus StatusOf(string pairId) => _svc.StatusOf(pairId);
    public void SetPaused(string pairId, bool paused) => _svc.SetPaused(pairId, paused);
    public void SetDocCopies(string pairId, bool on) => _svc.SetDocCopies(pairId, on);
    public void FreeUpSpace(string pairId) => _svc.Runner(pairId)?.FreeUpSpaceNow();
    public void RemoveFolder(string pairId) => _svc.RemovePair(pairId);
    public void RenameFolder(string pairId, string newPath) => _svc.RenamePair(pairId, newPath);
    public void SyncAllNow() => _svc.SyncAllNow();
    void IAppModel.AddFolder() => ShowAddFolder();
    public IFolderSource OpenFolderSource() => new LarkFolderSource(_svc.CreateApi());
    public void AddPair(Pair p) => _svc.AddPair(p);
    public void RetryFailed(string? pairId)
    {
        foreach (var p in _svc.Config.GetPairs())
            if (pairId == null || p.Id == pairId) _svc.Runner(p.Id)?.RetryFailed();
    }
    public void AllowDeletes(string pairId, string approval) => _svc.Runner(pairId)?.AllowDeletes(approval);
    public void RestoreDeletes(string pairId) => _svc.Runner(pairId)?.RestoreDeletes();
    public void ShowProblems(string? pairId) => ShowMain(AppPage.Problems, pairId);
    public void ShowDeletions(string pairId) => Ui_(() =>
    {
        if (_modalOpen) return;
        if (_svc.StatusOf(pairId) is { PendingDeletes.Count: 0 } or { Deleting: true }) return;     // already decided
        ShowModal(new DeletionsDialog(this, pairId));
    });
    public void OpenCheck() => Ui_(() => ShowOnce("check", () => new CheckDialog(run => FeatureCheck.Run(_svc, run), _svc.Auth.UserName)));
    public void OpenLog() { if (Log.FilePath is { } p && File.Exists(p)) Ui.Open(p); }

    public void SignInOrOut()
    {
        if (_svc.Auth.SignedIn)
        {
            if (ConfirmDialog.Ask(_main, "Sign out of Lark?",
                    "Syncing stops until you sign in again. Files that are only in Lark can't be opened until then.", "Sign out"))
                _svc.Auth.SignOut();
        }
        else if (SignIn() && _svc.Runners.Count == 0) _svc.Start();
    }

    // ---------------------------------------------------------------- start with Windows
    public bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppPaths.AppName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(AppPaths.AppName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(AppPaths.AppName, false);
        }
    }

    void Quit()
    {
        var downloading = _svc.Runners.Sum(r => r.Status.Downloading);
        if (!ConfirmDialog.Ask(_main, "Quit Lark Cloud Drive?",
                "While it's closed, files that are only in Lark can't be opened and changes aren't synced." +
                (downloading > 0 ? $"\n\n{downloading} file(s) are being opened right now." : ""), "Quit")) return;
        Shutdown();
    }

    void RestartToUpdate()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--after-update") { UseShellExecute = false });
        }
        catch (Exception e) { Log.Error($"Restart after update failed: {e.Message}"); return; }
        Shutdown();
    }

    void Shutdown()
    {
        _tick.Stop();
        _spin.Stop();
        _icon.Visible = false;
        foreach (var f in _open.Values.ToList()) f.Close();
        _main?.Close();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _updater.Dispose();
        _tick.Stop();
        _spin.Stop();
        Motion.Changed -= RefreshIcon;
        _icon.Dispose();
    }
}
