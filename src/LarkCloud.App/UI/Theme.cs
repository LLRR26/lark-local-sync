using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace LarkCloud.App.UI;

internal enum ThemeMode { System, Light, Dark }

/// <summary>Light/dark theme: follows Windows (or the chosen override), swaps the colour tokens that the styles
/// refer to with DynamicResource, and dresses each window (dark title bar, Mica on Windows 11).
/// A Settings page changes <see cref="Mode"/>; everything open updates at once.</summary>
internal static class Theme
{
    // key, light, dark. Light/dark values follow the Windows 11 Fluent palette; text pairs were checked for 4.5:1.
    static readonly (string Key, string Light, string Dark)[] Table =
    {
        ("Backdrop",        "#F3F3F3", "#202020"),   // window base where Mica isn't available
        ("Card",            "#FFFFFF", "#323232"),
        ("CardStroke",      "#E3E3E3", "#444444"),
        ("Divider",         "#E3E3E3", "#404040"),
        ("Menu",            "#FCFCFC", "#2C2C2C"),
        ("TextPrimary",     "#1A1A1A", "#FFFFFF"),
        ("TextSecondary",   "#5F5F5F", "#CCCCCC"),
        ("TextTertiary",    "#8A8A8A", "#9A9A9A"),   // disabled text only
        ("Accent",          "#3370FF", "#3370FF"),   // Lark blue: selection bar, syncing glyph
        ("AccentFill",      "#2F6BF6", "#2F6BF6"),   // button fill (white text 4.6:1)
        ("AccentHover",     "#2A62E0", "#2A62E0"),
        ("AccentPressed",   "#2352BD", "#2352BD"),
        ("AccentText",      "#1F55D6", "#7AA2FF"),
        ("OnAccent",        "#FFFFFF", "#FFFFFF"),
        ("Control",         "#FFFFFF", "#3C3C3C"),
        ("ControlStroke",   "#D1D1D1", "#505050"),
        ("ControlHover",    "#F5F5F5", "#464646"),
        ("ControlPressed",  "#EBEBEB", "#343434"),
        ("SubtleHover",     "#0F000000", "#17FFFFFF"),
        ("SubtlePressed",   "#08000000", "#0DFFFFFF"),
        ("Selected",        "#14000000", "#1FFFFFFF"),
        ("ScrollThumb",     "#8C8C8C", "#9A9A9A"),
        ("Success",         "#0F7B0F", "#6CCB5F"),
        ("SuccessBg",       "#DFF6DD", "#393D1B"),
        ("Caution",         "#9D5D00", "#FCE100"),
        ("CautionBg",       "#FFF4CE", "#433519"),
        ("Critical",        "#C42B1C", "#FF99A4"),
        ("CriticalBg",      "#FDE7E9", "#442726"),
        ("CriticalFill",    "#C42B1C", "#C42B1C"),
        ("CriticalHover",   "#A92418", "#D63A2B"),
        ("CriticalPressed", "#8E1D13", "#A92418"),
    };

    static readonly ResourceDictionary Tokens = new();
    static ThemeMode _mode = ThemeMode.System;
    static System.Windows.Application? _app;

    public static event Action? Changed;

    public static ThemeMode Mode
    {
        get => _mode;
        set { if (_mode == value) return; _mode = value; Apply(); }
    }

    public static ThemeMode ModeOf(string name) => name switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };

    public static bool IsDark { get; private set; }
    public static bool HighContrast { get; private set; }
    public static bool MicaAvailable => Environment.OSVersion.Version.Build >= 22621;
    public static bool UseMica => MicaAvailable && !HighContrast;

    /// <summary>Call once, before any window is created.</summary>
    public static void Init(System.Windows.Application app)
    {
        _app = app;
        app.Resources.MergedDictionaries.Add(Tokens);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Styles.xaml") });
        Apply();
        Motion.Init(app);
        SystemEvents.UserPreferenceChanged += (_, _) => app.Dispatcher.BeginInvoke(() => { if (Mode == ThemeMode.System || HighContrast != SystemParameters.HighContrast) Apply(); });
    }

    static bool WindowsUsesLight()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int v || v != 0; }
        catch { return true; }
    }

    static void Apply()
    {
        HighContrast = SystemParameters.HighContrast;
        IsDark = Mode == ThemeMode.Dark || (Mode == ThemeMode.System && !WindowsUsesLight());
        foreach (var (key, light, dark) in Table)
            Set(key, HighContrast ? HighContrastColor(key) : Parse(IsDark ? dark : light));
        // Over Mica the content layer is see-through; without it, a solid colour.
        Set("Layer", HighContrast ? SystemColors.WindowColor
            : UseMica ? Parse(IsDark ? "#4C3A3A3A" : "#80FFFFFF") : Parse(IsDark ? "#272727" : "#FBFBFB"));
        Changed?.Invoke();
    }

    static void Set(string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Tokens[key + "Brush"] = b;
        Tokens[key + "Color"] = c;
    }

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>High contrast: only the user's own Windows colours.</summary>
    static Color HighContrastColor(string key) => key switch
    {
        "TextTertiary" => SystemColors.GrayTextColor,
        "Accent" or "AccentFill" or "AccentHover" or "AccentPressed" or "CriticalFill" or "CriticalHover" or "CriticalPressed" => SystemColors.HighlightColor,
        "OnAccent" => SystemColors.HighlightTextColor,
        "CardStroke" or "Divider" or "ControlStroke" or "TextPrimary" or "TextSecondary" or "AccentText"
            or "Success" or "Caution" or "Critical" or "ScrollThumb" => SystemColors.WindowTextColor,
        "SubtleHover" or "SubtlePressed" or "Selected" => Colors.Transparent,
        _ => SystemColors.WindowColor,
    };

    /// <summary>Resolve a token brush now (for code that sets colours directly).</summary>
    public static Brush Brush(string key) => (Brush)Tokens[key + "Brush"];

    // ---------------------------------------------------------------- per window
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }

    /// <summary>Dark title bar, Mica (when asked for and available), the app icon, and live updates when the theme changes.</summary>
    public static void Attach(Window w, bool mica = true)
    {
        Action update = () => Dress(w, mica);
        if (!mica) Motion.AttachDialog(w);          // dialogs open with a fade and a small scale-up
        w.SourceInitialized += (_, _) =>
        {
            TrayIcons.SetWindowIcons(new WindowInteropHelper(w).Handle);
            update();
            Changed += update;
        };
        w.Closed += (_, _) => Changed -= update;
    }

    static void Dress(Window w, bool mica)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, 4);   // 19 = Windows 10 before 20H1
        var src = HwndSource.FromHwnd(hwnd);
        if (mica && UseMica && src?.CompositionTarget != null)
        {
            var type = 2;                                                  // Mica
            DwmSetWindowAttribute(hwnd, 38, ref type, 4);
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            w.Background = Brushes.Transparent;
        }
        else
        {
            if (MicaAvailable) { var none = 1; DwmSetWindowAttribute(hwnd, 38, ref none, 4); }
            w.SetResourceReference(Window.BackgroundProperty, "BackdropBrush");
        }
    }
}

/// <summary>Per-control hover/pressed fills, so one button template serves every button style.</summary>
internal static class Chrome
{
    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached("Hover", typeof(Brush), typeof(Chrome));
    public static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached("Pressed", typeof(Brush), typeof(Chrome));
    public static Brush? GetHover(DependencyObject o) => (Brush?)o.GetValue(HoverProperty);
    public static void SetHover(DependencyObject o, Brush? v) => o.SetValue(HoverProperty, v);
    public static Brush? GetPressed(DependencyObject o) => (Brush?)o.GetValue(PressedProperty);
    public static void SetPressed(DependencyObject o, Brush? v) => o.SetValue(PressedProperty, v);
}
