#if DEBUG
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LarkCloud.App.Service;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>Debug-only design preview: <c>LarkCloud.exe --ui-preview [--theme light|dark] [--scenario mixed|ok|signedout|empty|locked|settingslocked]
/// [--page folders|problems|settings] [--menu] [--expand] [--shot file.png]</c>. Shows the real main window with clearly fake sample folders.</summary>
internal sealed class PreviewModel : IAppModel
{
    readonly List<Pair> _pairs = new();
    readonly Dictionary<string, PairStatus> _st = new();
    public bool Configured => true;
    public bool SignedIn { get; private set; } = true;
    public string UserName => "Sample User";
    public bool LockFolders { get; }
    public bool StartsWithWindows { get; set; } = true;

    public Config Settings { get; }
    public void SettingsChanged() { Theme.Mode = Theme.ModeOf(Settings.Theme); }

    public PreviewModel(string scenario, string theme = "system")
    {
        // Real Config on throw-away files, so the Settings page is the real thing; "settingslocked" has IT locking some rows.
        var dir = Path.Combine(Path.GetTempPath(), "larkcloud-preview", scenario);
        Directory.CreateDirectory(dir);
        var itPath = Path.Combine(dir, "config.json");
        var userPath = Path.Combine(dir, "user.json");
        File.Delete(userPath);
        File.WriteAllText(itPath, scenario == "settingslocked"
            ? """{"app_id":"x","app_secret":"y","locked_settings":["free_up_after_days","max_transfers","extra_ignore"],"free_up_after_days":14,"max_transfers":4,"extra_ignore":["*.bak","Thumbs.db"]}"""
            : """{"app_id":"x","app_secret":"y"}""");
        Settings = Config.Load(itPath, userPath);
        Settings.SetMine(Config.KeyTheme, theme);
        if (scenario != "settingslocked") Settings.SetMyExtraIgnore(new[] { "*.tmp" });

        LockFolders = scenario == "locked";
        SignedIn = scenario != "signedout";
        if (scenario == "empty") return;
        var now = DateTime.UtcNow;
        void Add(string name, string folder, Action<Pair, PairStatus> tweak)
        {
            var p = new Pair { FolderName = name, FolderUrl = "https://sample.example/drive/folder/x", LocalFolder = $@"C:\Users\Sample\Lark Cloud\{folder}" };
            var s = new PairStatus { State = SyncState.Idle, Message = "Up to date", LastFull = now.AddMinutes(-1) };
            tweak(p, s);
            _pairs.Add(p);
            _st[p.Id] = s;
        }
        Add("Sample – Marketing", "Sample – Marketing", (p, s) =>
        {
            p.DocCopies = true;
            if (scenario == "syncing") { s.State = SyncState.Syncing; s.Message = "Uploading 12 files"; }
        });
        Add("Sample – Finance (shared with you)", "Sample – Finance", (p, s) =>
        {
            if (scenario != "mixed") return;
            s.State = SyncState.Attention; s.ViewOnly = true; s.Message = "3 files can't sync";
            s.Items = new()
            {
                ("Budget 2027.xlsx", "Too large for Lark (over 20 GB)."), ("Reports/Q3/Plan.docx", "The name has a character Lark doesn't allow."),
                ("Reports/Q3/Scan 0012.pdf", "Another program has this file open."), ("Contracts/Lease.pdf", "You can only view this Lark folder, not change it."),
                ("Notes.js", "Lark's filter blocked this file."),
            };
            s.UploadHere["Notes.js"] = "https://sample.example/drive/folder/x";
            s.OpenLinks["Contracts/Lease.pdf"] = "https://sample.example/file/y";
            s.OpenLinks["Notes.js"] = "https://sample.example/file/z";
            s.Info = new() { ("Reports/Empty.txt", "Empty files stay on this PC until they have content.") };
        });
        if (scenario == "deletions")
            Add("Sample – Site plans", "Sample – Site plans", (p, s) =>
            {
                s.State = SyncState.Attention; s.Message = "47 deletions are waiting";
                for (var i = 1; i <= 31; i++) s.PendingDeletes.Add(new PendingDelete { Path = $"Drawings/Level {i % 4}/Sheet {i:00}.dwg", InLark = true, Approval = "preview" });
                for (var i = 1; i <= 16; i++) s.PendingDeletes.Add(new PendingDelete { Path = $"Photos/2026-0{1 + i % 9}/IMG_{4000 + i}.jpg", InLark = false, Approval = "preview" });
            });
        Add("Sample – Site photos", "Sample – Site photos", (p, s) =>
        {
            if (scenario != "mixed") return;
            s.State = SyncState.Syncing; s.Message = "Uploading 12 files"; s.Downloading = 2;
        });
        Add("Sample – Company handbook", "Sample – Company handbook", (p, s) => { p.FromIt = true; if (scenario == "mixed") p.Paused = true; });
    }

    public IReadOnlyList<Pair> Pairs() => _pairs;
    public PairStatus StatusOf(string id) => _st.GetValueOrDefault(id) ?? new PairStatus();
    public void SetPaused(string id, bool paused) { _pairs.First(p => p.Id == id).Paused = paused; }
    public void SetDocCopies(string id, bool on) { _pairs.First(p => p.Id == id).DocCopies = on; }
    public void FreeUpSpace(string id) { }
    public void RemoveFolder(string id) { _pairs.RemoveAll(p => p.Id == id); }
    public void RenameFolder(string id, string newPath) { _pairs.First(p => p.Id == id).LocalFolder = newPath; }
    public void SyncAllNow() { }
    public void AddFolder() { }
    public IFolderSource OpenFolderSource() => new FakeSource();
    public void AddPair(Pair p) { _pairs.Add(p); _st[p.Id] = new PairStatus { State = SyncState.Idle, Message = "Up to date" }; }
    public void RetryFailed(string? pairId) { }
    public void AllowDeletes(string pairId, string approval) { _st[pairId].PendingDeletes.Clear(); }
    public void RestoreDeletes(string pairId) { _st[pairId].PendingDeletes.Clear(); }
    public void ShowProblems(string? pairId) { }
    public void ShowDeletions(string pairId) { }

    /// <summary>Clearly fake Lark folders for the add-folder dialog.</summary>
    sealed class FakeSource : IFolderSource
    {
        static void Wait() => Thread.Sleep(150);
        static RemoteEntry E(string token, string name) => new() { Token = token, Path = name, Type = Kinds.Folder, Url = "https://sample.example/drive/folder/" + token };
        public string RootToken => "root";
        public List<RemoteEntry> ChildFolders(string token) { Wait(); return token == "root" ? new() { E("t1", "Sample – Site plans"), E("x", "Sample – Marketing"), E("t3", "Sample – Archive") } : new() { E(token + "a", "Sample – Level 1"), E(token + "b", "Sample – Level 2") }; }
        public List<FoundFolder> SearchFolders(string query) { Wait(); return new() { new FoundFolder("s1", "Sample – Shared budget", null, "A. Sample") }; }
        public List<FoundFolder> WikiSpaces() { Wait(); return new() { new FoundFolder("w1", "Sample – Company wiki", null) }; }
        public List<WikiNode> WikiNodes(string space, string? parent) { Wait(); return new(); }
        public string FolderName(string token) { Wait(); return "Sample – Linked folder"; }
        public string CreateFolder(string parent, string name) { Wait(); return "new-" + name; }
    }
    public void SignInOrOut() { SignedIn = !SignedIn; }
    public void OpenCheck() { }
    public void OpenLog() { }

    // ---------------------------------------------------------------- entry point
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public static void Run(System.Windows.Application app, string[] args)
    {
        string Arg(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        Motion.ForceReduced = args.Contains("--reduced-motion");
        if (Arg("--tray-frames", "") is { Length: > 0 } trayOut)
        {
            // the 12 syncing tray frames at 16, 20 and 24 px, enlarged 6x on a grey strip, for checking they read at tray size
            var sizes = new[] { 16, 20, 24 };
            using var strip = new System.Drawing.Bitmap(12 * 24 * 6 + 24, sizes.Length * 24 * 6 + 24);
            using (var g = System.Drawing.Graphics.FromImage(strip))
            {
                g.Clear(System.Drawing.Color.FromArgb(32, 32, 32));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                for (var r = 0; r < sizes.Length; r++)
                {
                    var fr = TrayIcons.SyncingFrames(sizes[r]);
                    for (var i = 0; i < fr.Length; i++) { using var bm = fr[i].ToBitmap(); g.DrawImage(bm, 12 + i * 24 * 6, 12 + r * 24 * 6, sizes[r] * 6, sizes[r] * 6); }
                }
            }
            strip.Save(trayOut, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        Theme.Mode = Arg("--theme", "system") switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };
        app.ShutdownMode = ShutdownMode.OnLastWindowClose;
        var model = new PreviewModel(Arg("--scenario", "mixed"), Arg("--theme", "system"));
        var page = Arg("--page", "folders");
        Window w;
        Action? setup = null;
        switch (page)
        {
            case "addfolder1" or "addfolder2" or "addfolder3":
            {
                var d = new AddFolderDialog(model);
                var step = page[^1] - '0';
                setup = () => d.PreviewStep(step, Arg("--path", step == 2 ? @"C:\Windows\Lark Cloud" : @"C:\Users\Sample\Lark Cloud\Sample – Site plans"));
                w = d;
                break;
            }
            case "signin":
                w = new SignInDialog(() => Task.FromResult(new DeviceFlow("d", "QKRM-7P3X", "https://sample.example/sign-in", 5, 600)), (_, ct) => Task.Delay(Timeout.Infinite, ct), () => "Sample User", _ => { });
                break;
            case "check":
            {
                var d = new CheckDialog(report =>
                {
                    void Say(CheckStatus st, string name, string detail, string? fix = null, string? forIt = null) { report(new CheckItem(st, name, detail, fix, forIt)); Thread.Sleep(20); }
                    Say(CheckStatus.Ok, "Sign-in and My folders", "Works.");
                    Say(CheckStatus.Ok, "Upload and rename files", "Works.");
                    Say(CheckStatus.Note, "Empty files", "Lark doesn't accept them. They stay on this PC until they have content.");
                    Say(CheckStatus.NeedsPermission, "Find folders shared with you", "Lark says this app isn't allowed to search yet.", "Ask your IT team to turn on searching for Lark Cloud Drive. You can still paste a folder's link.", "Scope search:docs:read, then add it to extra_scopes in config.json.");
                    Say(CheckStatus.NeedsPermission, "Wiki spaces", "Lark says this app isn't allowed to open wikis yet.", "Ask your IT team to turn on wiki access.", "Scope wiki:wiki:readonly.");
                    Say(CheckStatus.Failed, "Offline copies of Lark docs", "Lark didn't answer in time.");
                }, "Sample User");
                setup = d.Start;
                w = d;
                break;
            }
            case "traymenu":
            {
                // A blank themed area with the real tray menu opened 40 px in from its top-left corner.
                var blank = new Window { Width = 360, Height = 340, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
                blank.SetResourceReference(Window.BackgroundProperty, "BackdropBrush");
                var tm = new TrayMenu(() => { var s = StatusSummary.Of(model); return (s.Sentence, s.Glyph); }, model.Pairs, () => { }, () => { }, () => { }, () => { });
                setup = () => { var o = blank.PointToScreen(new Point(40, 40)); tm.Show((int)o.X, (int)o.Y); };
                w = blank;
                break;
            }
            case "deletions":
                w = new DeletionsDialog(model, model.Pairs().First(p => model.StatusOf(p.Id).PendingDeletes.Count > 0).Id);
                break;
            default:
            {
                var mw = new MainWindow(model);
                mw.Navigate(page switch { "problems" => AppPage.Problems, "settings" => AppPage.Settings, _ => AppPage.Folders });
                if (args.Contains("--expand")) { mw.SettingsPage.ExpandAdvanced(); mw.WindowStartupLocation = WindowStartupLocation.Manual; mw.Left = 40; mw.Top = 0; mw.Height = SystemParameters.WorkArea.Height; }
                w = mw;
                break;
            }
        }
        if (w is not MainWindow) w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
        w.Topmost = true;
        w.Activate();
        var shot = Arg("--shot", "");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (args.Contains("--menu") && w is MainWindow mw) { mw.FoldersPage.OpenFirstMenu(); }
            setup?.Invoke();
            if (args.Contains("--toggle-test") && w is MainWindow tw)
            {
                // flips the first toggle and opens the Advanced expander, then captures 90 ms later (mid-animation)
                var cb = FoldersPage.Descendant<System.Windows.Controls.CheckBox>(tw.SettingsPage, _ => true);
                if (cb != null) cb.IsChecked = !(cb.IsChecked == true);
                tw.SettingsPage.ExpandAdvanced();
                var mid = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                mid.Tick += (_, _) => { mid.Stop(); Capture(w, shot); app.Shutdown(); };
                mid.Start();
                return;
            }
            if (Arg("--spin-shots", "") is { Length: > 0 } spinDir && w is MainWindow smw) { SpinShots(smw, spinDir, Arg("--spin-frames", ""), app); return; }
            if (shot.Length == 0) return;
            var shotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            shotTimer.Tick += (_, _) =>
            {
                shotTimer.Stop();
                Capture(w, shot);
                app.Shutdown();
            };
            shotTimer.Start();
        };
        timer.Start();
        app.Run();
    }

    /// <summary>Two crops of the header glyph 300 ms apart (spin-a.png, spin-b.png), the glyph's angle at each in the log, and optionally
    /// 12 frames 100 ms apart for checking that the glyph turns about a fixed centre.</summary>
    static void SpinShots(MainWindow mw, string dir, string framesDir, System.Windows.Application app)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        void Crop(string path)
        {
            var src = System.Windows.PresentationSource.FromVisual(mw);
            var scale = src.CompositionTarget.TransformToDevice.M11;
            var o = mw.HeaderArea.PointToScreen(new System.Windows.Point(0, 0));
            var wpx = (int)(56 * scale);
            var hpx = (int)(mw.HeaderArea.ActualHeight * scale);
            using var bmp = new System.Drawing.Bitmap(wpx, hpx);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)o.X, (int)o.Y, 0, 0, bmp.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            var ang = mw.StateGlyph.RenderTransform is System.Windows.Media.RotateTransform rt ? rt.Angle : -1;
            log.Add($"{Path.GetFileName(path)} angle={ang:0.0} spinning={Motion.GetSpin(mw.StateGlyph)} glyph={mw.StateGlyph.Text.Length}");
        }
        Crop(Path.Combine(dir, "spin-a.png"));
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Crop(Path.Combine(dir, "spin-b.png"));
            var n = 0;
            if (framesDir.Length == 0) { File.WriteAllLines(Path.Combine(dir, "spin-log.txt"), log); app.Shutdown(); return; }
            Directory.CreateDirectory(framesDir);
            var ft = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(97) };
            ft.Tick += (_, _) =>
            {
                Crop(Path.Combine(framesDir, $"f{n:00}.png"));
                if (++n < 14) return;
                ft.Stop();
                File.WriteAllLines(Path.Combine(framesDir, "log.txt"), log);
                app.Shutdown();
            };
            ft.Start();
        };
        t.Start();
    }

    static void Capture(Window w, string path)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        DwmGetWindowAttribute(hwnd, 9, out var r, Marshal.SizeOf<RECT>());   // extended frame bounds, physical pixels
        using var bmp = new System.Drawing.Bitmap(r.R - r.L, r.B - r.T);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.L, r.T, 0, 0, bmp.Size);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
#endif
