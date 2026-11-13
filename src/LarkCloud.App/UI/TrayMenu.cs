using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>The tray icon's right-click menu: a themed WPF ContextMenu opened at the cursor. It is rebuilt on every
/// open, so texts and the folder list are current and the theme is whatever is active now.</summary>
internal sealed class TrayMenu
{
    readonly Func<(string Text, string Glyph)> _status;
    readonly Func<IReadOnlyList<Pair>> _pairs;
    readonly Action _open, _syncNow, _togglePause, _quit;
    Window? _anchor;                 // tiny invisible window at the cursor: gives the popup a foreground owner to lose focus from
    ContextMenu? _menu;

    public TrayMenu(Func<(string Text, string Glyph)> status, Func<IReadOnlyList<Pair>> pairs, Action open, Action syncNow, Action togglePause, Action quit)
    {
        _status = status; _pairs = pairs; _open = open; _syncNow = syncNow; _togglePause = togglePause; _quit = quit;
    }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    const int GwlExStyle = -20, WsExToolWindow = 0x80;
    const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    static readonly IntPtr TopMost = new(-1);

    public void ShowAtCursor() { GetCursorPos(out var p); Show(p.X, p.Y); }

    /// <summary>Open the menu with its corner at a screen position given in physical pixels.</summary>
    public void Show(int px, int py)
    {
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _anchor ??= MakeAnchor();
        _anchor.Show();
        var hwnd = new WindowInteropHelper(_anchor).Handle;
        // 1x1 px at the cursor; the popup is placed relative to it, so WPF applies the DPI of the monitor the cursor is on.
        SetWindowPos(hwnd, TopMost, px, py, 1, 1, SwpNoActivate | SwpShowWindow);
        SetForegroundWindow(hwnd);   // without this a popup from a tray icon stays open when you click elsewhere

        var menu = Build();
        menu.PlacementTarget = _anchor;
        // Custom, not Relative: Relative mirrors to the left when Windows is set to left-handed menus. Tried in order; the first that fits the screen wins
        // (tray icons sit at the screen edge, so usually "above, to the left").
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (size, _, _) => new[]
        {
            new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None),
            new CustomPopupPlacement(new Point(0, -size.Height), PopupPrimaryAxis.None),
            new CustomPopupPlacement(new Point(-size.Width, 0), PopupPrimaryAxis.None),
            new CustomPopupPlacement(new Point(-size.Width, -size.Height), PopupPrimaryAxis.None),
        };
        menu.StaysOpen = false;
        menu.Closed += (_, _) => { if (_menu == menu) { _anchor.Hide(); _menu = null; } };
        _menu = menu;
        menu.IsOpen = true;
    }

    Window MakeAnchor()
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Opacity = 0,
            ShowInTaskbar = false, ShowActivated = true, Width = 1, Height = 1, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Title = "",
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLongPtr(h, GwlExStyle, GetWindowLongPtr(h, GwlExStyle) | WsExToolWindow);   // not in Alt+Tab
        };
        // Another program took the foreground: close. (Our own popup taking it does not count.)
        w.Deactivated += (_, _) => w.Dispatcher.BeginInvoke(() =>
        {
            if (_menu is not { IsOpen: true }) return;
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != Environment.ProcessId) _menu.IsOpen = false;
        }, System.Windows.Threading.DispatcherPriority.Background);
        return w;
    }

    static TextBlock Glyph(string g) => new() { Style = (Style)System.Windows.Application.Current.FindResource("IconText"), Text = g };

    /// <summary>The status line's icon turns while syncing (only while the menu is open and animation effects are on).</summary>
    static TextBlock StatusGlyph(string g) { var t = Glyph(g); if (g == StatusSummary.GlyphBusy) Motion.SetSpin(t, true); return t; }

    ContextMenu Build()
    {
        var m = new ContextMenu();
        MenuItem Item(string text, string glyph, Action act, bool bold = false)
        {
            var mi = new MenuItem { Header = text, Icon = Glyph(glyph), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            // Run after the menu has closed, so a dialog opened by the action doesn't fight the closing popup.
            mi.Click += (_, _) => m.Dispatcher.BeginInvoke(act);
            return mi;
        }

        var (status, statusGlyph) = _status();
        m.Items.Add(new MenuItem { Header = status.Length > 70 ? status[..67] + "…" : status, IsEnabled = false, Icon = StatusGlyph(statusGlyph) });
        m.Items.Add(new Separator());
        m.Items.Add(Item("Open Lark Cloud Drive", "", _open, bold: true));

        var pairs = _pairs();
        var folders = new MenuItem { Header = "Open folder", Icon = Glyph(""), IsEnabled = pairs.Count > 0 };
        foreach (var p in pairs)
        {
            var mi = new MenuItem { Header = p.Label, Icon = Glyph("") };
            mi.Click += (_, _) => m.Dispatcher.BeginInvoke(() => { if (Directory.Exists(p.LocalFolder)) Ui.Open(p.LocalFolder); });
            folders.Items.Add(mi);
        }
        m.Items.Add(folders);

        m.Items.Add(Item("Sync now", "", _syncNow));
        var allPaused = pairs.Count > 0 && pairs.All(p => p.Paused);
        m.Items.Add(allPaused ? Item("Resume syncing", "", _togglePause) : Item("Pause syncing", "", _togglePause));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Quit", "", _quit));
        return m;
    }
}
