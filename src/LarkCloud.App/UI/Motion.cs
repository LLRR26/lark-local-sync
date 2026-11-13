using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace LarkCloud.App.UI;

/// <summary>The one place motion is decided. Every animation in the app goes through here so Windows' "Animation effects"
/// switch (Settings > Accessibility > Visual effects) is honoured everywhere, and live.
/// With effects off: no rotation, slides, scales or knob travel; only short opacity and colour fades stay.
/// Timings: 100-150 ms feedback, 150-250 ms state and page changes, 180 ms dialogs; exits are faster than entrances.
/// Only opacity and render transforms are animated, never layout properties.</summary>
internal static class Motion
{
    // ---------------------------------------------------------------- the reduced-motion switch
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, out int value, int flags);
    const int SpiGetClientAreaAnimation = 0x1042;

    /// <summary>Debug preview only (--reduced-motion): behave as if Windows animation effects were off.</summary>
    public static bool ForceReduced { get; set; }

    static bool _enabled = Read();
    /// <summary>True when movement (rotation, slides, scales) may run.</summary>
    public static bool Enabled => _enabled && !ForceReduced;
    public static event Action? Changed;

    static bool Read() => !SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var v, 0) || v != 0;

    static void Recheck()
    {
        var now = Read();
        if (now == _enabled) return;
        _enabled = now;
        Changed?.Invoke();
    }

    /// <summary>Call once after the Application exists (Theme.Init does).</summary>
    public static void Init(Application app)
    {
        SystemEvents.UserPreferenceChanged += (_, _) => app.Dispatcher.BeginInvoke(Recheck);
        // Toggle switch knob and Expander chevron/body: driven here so they follow the reduced-motion switch.
        EventManager.RegisterClassHandler(typeof(CheckBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Knob((CheckBox)s, false)));
        EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.CheckedEvent, new RoutedEventHandler((s, _) => Knob((CheckBox)s, true)));
        EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.UncheckedEvent, new RoutedEventHandler((s, _) => Knob((CheckBox)s, true)));
        EventManager.RegisterClassHandler(typeof(Expander), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Chevron((Expander)s, false)));
        EventManager.RegisterClassHandler(typeof(Expander), Expander.ExpandedEvent, new RoutedEventHandler((s, e) => { if (e.OriginalSource == s) Chevron((Expander)s, true); }));
        EventManager.RegisterClassHandler(typeof(Expander), Expander.CollapsedEvent, new RoutedEventHandler((s, e) => { if (e.OriginalSource == s) Chevron((Expander)s, true); }));
    }

    // ---------------------------------------------------------------- easing
    static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static readonly IEasingFunction OutStrong = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ---------------------------------------------------------------- building block
    /// <summary>Move a double property to <paramref name="to"/>. The value is set first and the animation only draws the way there
    /// (FillBehavior.Stop), so an interrupted or removed animation always leaves the final value. Starts from the value shown now.
    /// <paramref name="movement"/> marks spatial motion, which is skipped when animation effects are off.</summary>
    public static void Animate(DependencyObject o, DependencyProperty p, double to, int ms, bool movement = false, IEasingFunction? ease = null, bool animate = true)
    {
        var from = (double)o.GetValue(p);
        o.SetValue(p, to);
        var anim = (IAnimatable)o;
        if (!animate || from == to || (movement && !Enabled)) { anim.BeginAnimation(p, null); return; }
        anim.BeginAnimation(p, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Out, FillBehavior = FillBehavior.Stop });
    }

    static readonly DependencyProperty XfProperty = DependencyProperty.RegisterAttached("Xf", typeof(TransformGroup), typeof(Motion));

    static (ScaleTransform S, TranslateTransform T) Xf(UIElement el)
    {
        if (el.GetValue(XfProperty) is TransformGroup g) return ((ScaleTransform)g.Children[0], (TranslateTransform)g.Children[1]);
        var s = new ScaleTransform();
        var t = new TranslateTransform();
        g = new TransformGroup { Children = { s, t } };
        el.SetValue(XfProperty, g);
        el.RenderTransform = g;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        return (s, t);
    }

    /// <summary>Show something that just arrived: fades from 0 and, when movement is allowed, settles from an offset
    /// (<paramref name="dx"/>, <paramref name="dy"/> DIPs) and/or a scale. Safe to repeat: it always ends fully visible and in place.</summary>
    public static void Reveal(UIElement el, int ms = 200, double dx = 0, double dy = 0, double scale = 1, bool strong = false)
    {
        var ease = strong ? OutStrong : Out;
        el.Opacity = 0;
        Animate(el, UIElement.OpacityProperty, 1, ms, ease: ease);
        if (dx == 0 && dy == 0 && scale == 1) return;
        var (s, t) = Xf(el);
        if (dx != 0) { t.X = dx; Animate(t, TranslateTransform.XProperty, 0, ms, true, ease); }
        if (dy != 0) { t.Y = dy; Animate(t, TranslateTransform.YProperty, 0, ms, true, ease); }
        if (scale != 1)
        {
            s.ScaleX = s.ScaleY = scale;
            Animate(s, ScaleTransform.ScaleXProperty, 1, ms, true, ease);
            Animate(s, ScaleTransform.ScaleYProperty, 1, ms, true, ease);
        }
    }

    /// <summary>Quick fade-in for a value that changed (status sentence and glyph).</summary>
    public static void Flash(UIElement el, int ms = 150) { if (el is FrameworkElement { IsVisible: true }) Reveal(el, ms); }

    /// <summary>Fade an element out, then run <paramref name="done"/> (remove it). Exits are quick.</summary>
    public static void FadeOut(UIElement el, Action done, int ms = 120)
    {
        var a = new DoubleAnimation(el.Opacity, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
        a.Completed += (_, _) => done();
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>Show or hide; a banner or button that appears fades and settles down 8 DIPs, one that goes away just goes.</summary>
    public static void SetVisible(UIElement el, bool visible)
    {
        if (!visible) { el.Visibility = Visibility.Collapsed; return; }
        if (el.Visibility == Visibility.Visible) return;
        el.Visibility = Visibility.Visible;
        if (el is FrameworkElement { IsLoaded: true } f && f.IsVisible) Reveal(el, 200, dy: -8);
    }

    /// <summary>Colour change that fades (button-like fills, step dots). Falls back to a plain set for anything but solid brushes.</summary>
    public static void Paint(DependencyObject o, DependencyProperty p, Brush to, int ms = 200)
    {
        var old = o.GetValue(p) as SolidColorBrush;
        if (old == null || to is not SolidColorBrush target || !(o is FrameworkElement { IsLoaded: true }) || old.Color == target.Color) { o.SetValue(p, to); return; }
        var fromColor = old.Color;
        var toColor = target.Color;
        if (fromColor.A == 0) fromColor = Color.FromArgb(0, toColor.R, toColor.G, toColor.B);   // from "nothing": fade the alpha, don't pass through white
        if (toColor.A == 0) toColor = Color.FromArgb(0, fromColor.R, fromColor.G, fromColor.B);
        var b = new SolidColorBrush(fromColor);
        o.SetValue(p, b);
        var anim = new ColorAnimation(fromColor, toColor, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out, FillBehavior = FillBehavior.HoldEnd };
        anim.Completed += (_, _) => { if (ReferenceEquals(o.GetValue(p), b)) o.SetValue(p, to); };
        b.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    // ---------------------------------------------------------------- dialogs
    /// <summary>A dialog opens with a fade and a small scale-up (180 ms); closing is instant.</summary>
    public static void AttachDialog(Window w) =>
        w.Loaded += (_, _) => { if (w.Content is UIElement root) Reveal(root, 180, scale: 0.96); };

    // ---------------------------------------------------------------- spinning (the "working" glyph)
    public const int SpinMs = 1200;

    static readonly ConditionalWeakTable<FrameworkElement, Spinner_> Spinners = new();

    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached("Spin", typeof(bool), typeof(Motion),
        new PropertyMetadata(false, (d, _) => { if (d is FrameworkElement el) Spinners.GetValue(el, e => new Spinner_(e)).Update(); }));
    public static bool GetSpin(DependencyObject o) => (bool)o.GetValue(SpinProperty);
    public static void SetSpin(DependencyObject o, bool v) => o.SetValue(SpinProperty, v);

    /// <summary>One acknowledging turn, for a button pressed while nothing is spinning yet.</summary>
    public static void SpinOnce(FrameworkElement el)
    {
        var s = Spinners.GetValue(el, e => new Spinner_(e));
        if (Enabled && el.IsVisible && !s.On) s.Turn(false);
    }

    /// <summary>Rotates an element about its own centre, linearly, while <see cref="SpinProperty"/> is true AND it is shown
    /// (visible, loaded, window not minimized) AND animation effects are on. Not running means no animation clock at all.
    /// Re-setting the property to the value it has does nothing, so a 1 s refresh tick never restarts the turn.</summary>
    sealed class Spinner_
    {
        readonly FrameworkElement _el;
        readonly RotateTransform _rot = new();
        Window? _win;
        public bool On { get; private set; }

        public Spinner_(FrameworkElement el)
        {
            _el = el;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = _rot;
            el.IsVisibleChanged += (_, _) => Update();
            el.Loaded += (_, _) => Hook();
            el.Unloaded += (_, _) => { Unhook(); Update(); };
            if (el.IsLoaded) Hook();
        }

        void Hook()
        {
            Unhook();
            _win = Window.GetWindow(_el);
            if (_win != null) _win.StateChanged += OnState;
            Changed += Update;
            Update();
        }

        void Unhook()
        {
            if (_win != null) _win.StateChanged -= OnState;
            _win = null;
            Changed -= Update;
        }

        void OnState(object? s, EventArgs e) => Update();

        public void Update()
        {
            var want = GetSpin(_el) && _el.IsLoaded && _el.IsVisible && Enabled && _win?.WindowState != WindowState.Minimized;
            if (want == On) return;
            On = want;
            if (want) Turn(true);
            else { _rot.BeginAnimation(RotateTransform.AngleProperty, null); _rot.Angle = 0; }
        }

        /// <summary>From the angle it is at (so a one-off turn hands over to endless spinning without a jump).</summary>
        public void Turn(bool forever)
        {
            var a = _rot.Angle % 360;
            _rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(a, a + 360, TimeSpan.FromMilliseconds(SpinMs))
            {
                RepeatBehavior = forever ? RepeatBehavior.Forever : new RepeatBehavior(1),
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

    // ---------------------------------------------------------------- template parts driven in code
    /// <summary>Toggle switch: the knob slides 20 DIPs and the accent track and white knob fade in (150 ms).</summary>
    static void Knob(CheckBox cb, bool animate)
    {
        cb.ApplyTemplate();          // a toggle's IsChecked is often set in code before it is shown: build its parts now
        if (cb.Template?.FindName("KnobHost", cb) is not FrameworkElement host) return;
        if (host.RenderTransform is not TranslateTransform { IsFrozen: false } t) host.RenderTransform = t = new TranslateTransform();   // template transforms are frozen
        var on = cb.IsChecked == true;
        animate &= cb.IsLoaded;
        Animate(t, TranslateTransform.XProperty, on ? 20 : 0, 150, true, animate: animate);
        if (cb.Template.FindName("TrackOn", cb) is UIElement track) Animate(track, UIElement.OpacityProperty, on ? 1 : 0, 150, animate: animate);
        if (cb.Template.FindName("KnobOn", cb) is UIElement knob) Animate(knob, UIElement.OpacityProperty, on ? 1 : 0, 150, animate: animate);
    }

    /// <summary>Expander: the chevron turns 180 degrees and the opened content fades in (200 ms).</summary>
    static void Chevron(Expander ex, bool animate)
    {
        ex.ApplyTemplate();
        if (ex.Template?.FindName("Hd", ex) is not ToggleButton hd) return;
        hd.ApplyTemplate();
        if (hd.Template?.FindName("Chev", hd) is not FrameworkElement chev) return;
        if (chev.RenderTransform is not RotateTransform { IsFrozen: false } rot) chev.RenderTransform = rot = new RotateTransform();   // template transforms are frozen
        animate &= ex.IsLoaded;
        Animate(rot, RotateTransform.AngleProperty, ex.IsExpanded ? 180 : 0, 200, true, animate: animate);
        if (animate && ex.IsExpanded && ex.Template.FindName("Body", ex) is UIElement body) Reveal(body, 200, dy: -8);
    }
}
