using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using LarkCloud.App.Service;

namespace LarkCloud.App.UI;

/// <summary>The app icon at the right size for where it is shown (tray, title bar, dialogs), taken from the
/// frames inside Assets/app.ico (16, 24, 32, 48, 64, 256) instead of stretching one bitmap.</summary>
internal static class TrayIcons
{
    static readonly int[] Frames = { 16, 24, 32, 48, 64 };
    static byte[]? _ico;

    static byte[] IcoBytes
    {
        get
        {
            if (_ico != null) return _ico;
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            using var ms = new MemoryStream();
            info.Stream.CopyTo(ms);
            return _ico = ms.ToArray();
        }
    }

    /// <summary>The icon frame that fits <paramref name="px"/> best (the next size up, scaled down by Windows' own rules).</summary>
    public static Icon Frame(int px)
    {
        var f = Frames.FirstOrDefault(x => x >= px);
        if (f == 0) f = Frames[^1];
        return new Icon(new MemoryStream(IcoBytes), new Size(f, f));
    }

    static Icon? _app;
    public static Icon AppIcon => _app ??= Frame(32);

    /// <summary>The largest frame, for the logo in the navigation (scaled down smoothly by WPF).</summary>
    public static BitmapSource Logo
    {
        get
        {
            var dec = BitmapDecoder.Create(new MemoryStream(IcoBytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var best = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
            best.Freeze();
            return best;
        }
    }

    // ---------------------------------------------------------------- native metrics
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    const int SM_CXICON = 11, SM_CXSMICON = 49, WM_SETICON = 0x80;

    /// <summary>Width of a small icon for the tray (it follows the system DPI).</summary>
    public static int TraySize => Math.Max(16, GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem()));

    /// <summary>Give a window crisp title-bar and Alt-Tab icons for the DPI it is on.</summary>
    public static void SetWindowIcons(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        foreach (var (kind, metric) in new[] { (0, SM_CXSMICON), (1, SM_CXICON) })
        {
            var px = GetSystemMetricsForDpi(metric, dpi);
            SendMessage(hwnd, WM_SETICON, kind, Render(px, null).Handle);
        }
    }

    // ---------------------------------------------------------------- tray icon with a status dot
    static readonly Dictionary<(SyncState, int, bool), Icon> Cache = new();

    /// <summary>The status icon; <paramref name="update"/> adds the purple "update waiting" badge in the top-left corner.</summary>
    public static Icon State(SyncState s, int px, bool update = false)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((s, px, update), out var ic)) Cache[(s, px, update)] = ic = Render(px, s, null, update);
            return ic;
        }
    }

    // ---------------------------------------------------------------- "syncing" animation frames
    public const int SpinFrames = 12;
    static readonly Dictionary<(int, bool), Icon[]> Spin = new();

    /// <summary>The syncing icon with the arrow in its blue dot turned a twelfth of a turn further in each frame. Drawn once per icon
    /// size and kept for the life of the app (about a dozen small icons per size), so cycling them creates no new GDI handles.</summary>
    public static Icon[] SyncingFrames(int px, bool update = false)
    {
        lock (Spin)
        {
            if (!Spin.TryGetValue((px, update), out var frames))
                Spin[(px, update)] = frames = Enumerable.Range(0, SpinFrames).Select(i => Render(px, SyncState.Syncing, i * 360f / SpinFrames, update)).ToArray();
            return frames;
        }
    }

    internal static Icon Render(int px, SyncState? state, float? turn = null, bool update = false)
    {
        using var bmp = new Bitmap(px, px);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            using (var frame = Frame(px)) using (var fb = frame.ToBitmap())
                g.DrawImage(fb, 0, 0, px, px);
            if (state is { } s) DrawDot(g, px, s, turn);
            if (update) DrawBadge(g, px);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>"An update is waiting": purple dot with a white ring and an up arrow, top-left (the status dot is bottom-right).</summary>
    static void DrawBadge(Graphics g, int px)
    {
        var d = px * (px < 24 ? 0.34f : 0.42f);             // smaller at 16 px, so the cloud still shows between badge and status dot
        const float x = 0.5f, y = 0.5f;
        using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, x - 1, y - 1, d + 2, d + 2);
        using (var dot = new SolidBrush(Color.FromArgb(124, 58, 237))) g.FillEllipse(dot, x, y, d, d);
        if (d < 9) return;                                   // too small for an arrow; the colour alone says "new"
        using var pen = new Pen(Color.White, Math.Max(1.4f, d / 6.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = x + d / 2, cy = y + d / 2, u = d / 10;
        g.DrawLine(pen, cx, cy + 2.4f * u, cx, cy - 2.4f * u);
        g.DrawLines(pen, new[] { new PointF(cx - 2.2f * u, cy - 0.2f * u), new PointF(cx, cy - 2.4f * u), new PointF(cx + 2.2f * u, cy - 0.2f * u) });
    }

    static void DrawDot(Graphics g, int px, SyncState s, float? turn = null)
    {
        var color = s switch
        {
            SyncState.Idle => Color.FromArgb(15, 150, 15),
            SyncState.Syncing or SyncState.Starting => Color.FromArgb(51, 112, 255),
            SyncState.Attention => Color.FromArgb(242, 153, 0),
            SyncState.Error => Color.FromArgb(217, 48, 37),
            _ => Color.FromArgb(118, 124, 130),
        };
        var d = px * 0.52f;
        var x = px - d - 0.5f;
        var y = px - d - 0.5f;
        using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, x - 1, y - 1, d + 2, d + 2);
        using (var dot = new SolidBrush(color)) g.FillEllipse(dot, x, y, d, d);
        if (d < 9 && turn == null) return;                   // too small for a mark inside; colour only (animation frames always draw the arrow)
        var w = Math.Max(d < 9 ? 1.2f : 1.4f, d / 6.5f);
        using var pen = new Pen(Color.White, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = x + d / 2, cy = y + d / 2, u = d / 10;
        switch (s)
        {
            case SyncState.Idle: g.DrawLines(pen, new[] { new PointF(cx - 2.4f * u, cy), new PointF(cx - 0.6f * u, cy + 2f * u), new PointF(cx + 2.6f * u, cy - 2f * u) }); break;
            case SyncState.Attention or SyncState.Error:
                g.DrawLine(pen, cx, cy - 2.6f * u, cx, cy + 0.8f * u);
                g.DrawLine(pen, cx, cy + 2.6f * u, cx, cy + 2.7f * u); break;
            case SyncState.Paused: g.DrawLine(pen, cx - 1.5f * u, cy - 2.2f * u, cx - 1.5f * u, cy + 2.2f * u); g.DrawLine(pen, cx + 1.5f * u, cy - 2.2f * u, cx + 1.5f * u, cy + 2.2f * u); break;
            default: g.DrawArc(pen, cx - 2.6f * u, cy - 2.6f * u, 5.2f * u, 5.2f * u, 30 + (turn ?? 0), 280); break;
        }
    }
}
