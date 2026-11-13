using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>Everyday options first, the rest under "Advanced". Each row is a Windows 11 SettingsCard: title, one line of
/// explanation, the control on the right. A setting IT has locked is disabled and says so. A change is saved for this person
/// and applied straight away (<see cref="IAppModel.SettingsChanged"/>), so nothing needs a restart.</summary>
internal sealed partial class SettingsPage : UserControl, IPage
{
    readonly IAppModel _m;
    readonly CheckBox _startup;
    StackPanel _ignoreList = null!;
    TextBox _ignoreBox = null!;
    TextBlock _ignoreError = null!;
    Config Cfg => _m.Settings;

    public SettingsPage(IAppModel model)
    {
        _m = model;
        InitializeComponent();
        AutomationProperties.SetName(AdvancedExpander, "Advanced");
        AutomationProperties.SetHelpText(AdvancedExpander, "Deleting, speed and files to leave out");

        // ---- everyday
        var theme = Combo("Appearance", new[] { ("System default", "system"), ("Light", "light"), ("Dark", "dark") }, Cfg.Theme, v => Put(() => Cfg.SetMine(Config.KeyTheme, v)));
        Lock(Config.KeyTheme, theme);
        Card(Rows, "Appearance", "Match Windows, or always use light or dark.", theme, Cfg.IsLocked(Config.KeyTheme));

        _startup = Toggle("Start when Windows starts", _m.StartsWithWindows);
        _startup.Click += (_, _) => _m.StartsWithWindows = _startup.IsChecked == true;
        Add("Start when Windows starts", "Lark Cloud Drive opens in the tray when you sign in to Windows, so your folders stay up to date.", _startup);

        var notify = Toggle("Show notifications", Cfg.Notifications);
        notify.Click += (_, _) => Put(() => Cfg.SetMine(Config.KeyNotifications, notify.IsChecked == true));
        Lock(Config.KeyNotifications, notify);
        Card(Rows, "Show notifications", "Tell me when something needs a look, like a file changed in two places. Warnings about signing in to Lark always show.", notify, Cfg.IsLocked(Config.KeyNotifications));

        var free = Toggle("Free up space automatically", Cfg.FreeUpAfterDays > 0);
        var freeDays = Combo("Free up space after", Days(7, 14, 30, 60, 90), Cfg.FreeUpAfterDays > 0 ? Cfg.FreeUpAfterDays : 30, v => Put(() => Cfg.SetMine(Config.KeyFreeUp, v)), v => $"{v} days");
        freeDays.IsEnabled = free.IsChecked == true;
        free.Click += (_, _) =>
        {
            freeDays.IsEnabled = free.IsChecked == true;
            Put(() => Cfg.SetMine(Config.KeyFreeUp, free.IsChecked == true ? Selected(freeDays, Days(7, 14, 30, 60, 90)) : 0));
        };
        if (Cfg.IsLocked(Config.KeyFreeUp)) { free.IsEnabled = freeDays.IsEnabled = false; }
        Card(Rows, "Free up space automatically", "Files you haven't opened for a while go back to the cloud, so they stop using disk space. You can still open them any time.",
            Row(free, freeDays), Cfg.IsLocked(Config.KeyFreeUp));

        var docs = Combo("Offline doc copies as", new[] { ("Word, Excel & PowerPoint files", "office"), ("PDF files", "pdf") }, Cfg.DocCopyFormat, v => Put(() => Cfg.SetMine(Config.KeyDocFormat, v)));
        Lock(Config.KeyDocFormat, docs);
        Card(Rows, "Offline doc copies as", "The file type for offline copies of Lark docs. You turn copies on for each folder in its ⋯ menu.", docs, Cfg.IsLocked(Config.KeyDocFormat));

        // ---- advanced
        var confirm = Toggle("Ask before deleting many items", Cfg.ConfirmMassDeletes);
        var many = Combo("Ask when more than this many items are deleted", new[] { 10, 20, 50, 100, 200 }.Select(n => ($"More than {n}", n)).ToArray(), Cfg.MaxDeletes,
            v => Put(() => Cfg.SetMine(Config.KeyMaxDeletes, v)), v => $"More than {v}");
        void SyncMany() => many.IsEnabled = confirm.IsChecked == true && !Cfg.IsLocked(Config.KeyMaxDeletes);
        SyncMany();
        confirm.Click += (_, _) => { SyncMany(); Put(() => Cfg.SetMine(Config.KeyConfirmDeletes, confirm.IsChecked == true)); };
        confirm.IsEnabled = !Cfg.IsLocked(Config.KeyConfirmDeletes);
        Card(AdvancedRows, "Ask before deleting many items", "Wait for your OK before a big clean-up is applied. Held deletions show up as “Review” in the main window.",
            Row(confirm, many), Cfg.IsLocked(Config.KeyConfirmDeletes) || Cfg.IsLocked(Config.KeyMaxDeletes));

        var speed = Combo("Transfer speed", new[] { ("Gentle (2 at a time)", 2), ("Normal (8 at a time)", 8), ("Fast (16 at a time)", 16) }, Cfg.MaxTransfers,
            v => Put(() => Cfg.SetMine(Config.KeyTransfers, v)), v => $"Custom ({v} at a time)");
        Lock(Config.KeyTransfers, speed);
        Card(AdvancedRows, "Transfer speed", "How many files are copied at once. Gentle is kinder to a slow connection.", speed, Cfg.IsLocked(Config.KeyTransfers));

        AdvancedRows.Children.Add(BuildIgnoreCard());

        var check = new Button { Content = "Check…" };
        check.Click += (_, _) => _m.OpenCheck();
        Card(AdvancedRows, "Check Lark features", "Tries each feature with your Lark account and tells you what to turn on. Takes about a minute.", check, false);

        var log = new Button { Content = "Open log" };
        log.Click += (_, _) => _m.OpenLog();
        Card(AdvancedRows, "Activity log", "Opens the log file. IT may ask for it if something goes wrong.", log, false);

        About.Text = $"{AppPaths.AppTitle} · version {AppPaths.Version}";
    }

    /// <summary>One settings row: a title, one line of explanation, and the control on the right.</summary>
    public void Add(string title, string description, FrameworkElement control) => Card(Rows, title, description, control, false);

    // ---------------------------------------------------------------- building blocks
    void Put(Action save) { save(); _m.SettingsChanged(); }

    void Lock(string key, FrameworkElement c) { if (Cfg.IsLocked(key)) c.IsEnabled = false; }

    static (string, int)[] Days(params int[] n) => n.Select(d => ($"{d} days", d)).ToArray();

    static T Selected<T>(ComboBox cb, (string Label, T Value)[] opts) => opts[Math.Max(0, cb.SelectedIndex)].Value;

    CheckBox Toggle(string name, bool on)
    {
        var t = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(t, name);
        return t;
    }

    ComboBox Combo<T>(string name, (string Label, T Value)[] opts, T current, Action<T> pick, Func<T, string>? odd = null)
    {
        var list = opts.ToList();
        if (!list.Any(o => EqualityComparer<T>.Default.Equals(o.Value, current))) list.Add((odd?.Invoke(current) ?? $"{current}", current));
        var cb = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(cb, name);
        foreach (var o in list) cb.Items.Add(o.Label);
        cb.SelectedIndex = list.FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, current));
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) pick(list[cb.SelectedIndex].Value); };
        return cb;
    }

    static StackPanel Row(params FrameworkElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0) items[i].Margin = new Thickness(16, 0, 0, 0);
            p.Children.Add(items[i]);
        }
        return p;
    }

    TextBlock Icon(string glyph, double size = 14)
    {
        var g = new TextBlock { Text = glyph, Style = (Style)FindResource("IconText"), FontSize = size, Foreground = Theme.Brush("TextSecondary") };
        AutomationProperties.SetIsOffscreenBehavior(g, IsOffscreenBehavior.Offscreen);   // decoration: hidden from screen readers
        return g;
    }

    /// <summary>"Set by your IT team" with a lock glyph.</summary>
    StackPanel LockLine(string text = "Set by your IT team")
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var g = Icon("", 12);
        g.Margin = new Thickness(0, 0, 6, 0);
        p.Children.Add(g);
        p.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("CaptionText"), VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    void Card(Panel into, string title, string description, FrameworkElement control, bool locked)
    {
        AutomationProperties.SetName(control, title);
        if (locked) AutomationProperties.SetHelpText(control, "Set by your IT team");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("BodyText") });
        text.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 2, 0, 0) });
        if (locked) text.Children.Add(LockLine());
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(24, 0, 0, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        into.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(16, 12, 16, 12), MinHeight = 64, Margin = new Thickness(0, 0, 0, 4), Child = grid });
    }

    // ---------------------------------------------------------------- files never to sync
    Border BuildIgnoreCard()
    {
        var locked = Cfg.IsLocked(Config.KeyIgnore);
        var col = new StackPanel();
        col.Children.Add(new TextBlock { Text = "Files never to sync", Style = (Style)FindResource("BodyText") });
        col.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 2, 0, 8),
            Text = "Names that are never uploaded or downloaded. Office “file is open” markers, unfinished browser downloads and the Recycle Bin are always left out already.",
        });
        _ignoreList = new StackPanel();
        col.Children.Add(_ignoreList);

        _ignoreBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = !locked };
        AutomationProperties.SetName(_ignoreBox, "File name or pattern to leave out");
        AutomationProperties.SetHelpText(_ignoreBox, "For example *.tmp");
        var hint = new TextBlock
        {
            Text = "e.g. *.tmp", Style = (Style)FindResource("BodyText"), Foreground = Theme.Brush("TextSecondary"),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0),
        };
        AutomationProperties.SetIsOffscreenBehavior(hint, IsOffscreenBehavior.Offscreen);   // decoration: hidden from screen readers
        _ignoreBox.TextChanged += (_, _) => { hint.Visibility = _ignoreBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; _ignoreError.Visibility = Visibility.Collapsed; };
        _ignoreBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddIgnore(); e.Handled = true; } };
        var add = new Button { Content = "Add", Margin = new Thickness(8, 0, 0, 0), IsEnabled = !locked };
        AutomationProperties.SetName(add, "Add to files never to sync");
        add.Click += (_, _) => AddIgnore();
        var entry = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        entry.ColumnDefinitions.Add(new ColumnDefinition());
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        entry.Children.Add(_ignoreBox);
        entry.Children.Add(hint);
        Grid.SetColumn(add, 1);
        entry.Children.Add(add);
        col.Children.Add(entry);

        _ignoreError = new TextBlock { Style = (Style)FindResource("CaptionText"), Foreground = Theme.Brush("Critical"), Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(_ignoreError, AutomationLiveSetting.Assertive);
        col.Children.Add(_ignoreError);
        if (locked) col.Children.Add(LockLine());
        RebuildIgnore();
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4), Child = col };
    }

    void RebuildIgnore()
    {
        _ignoreList.Children.Clear();
        var locked = Cfg.IsLocked(Config.KeyIgnore);
        foreach (var p in Cfg.ItExtraIgnore)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, MinHeight = 32 };
            var g = Icon("", 12);
            g.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(g);
            row.Children.Add(new TextBlock { Text = p, Style = (Style)FindResource("BodyText"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = "Set by your IT team", Style = (Style)FindResource("CaptionText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            _ignoreList.Children.Add(row);
        }
        if (!locked)
            foreach (var p in Cfg.MyExtraIgnore)
            {
                var row = new Grid { MinHeight = 32 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = p, Style = (Style)FindResource("BodyText"), VerticalAlignment = VerticalAlignment.Center });
                var rm = new Button { Style = (Style)FindResource("IconButton"), Content = Icon("") };
                AutomationProperties.SetName(rm, $"Remove {p}");
                rm.ToolTip = $"Remove {p}";
                rm.Click += (_, _) => { Put(() => Cfg.SetMyExtraIgnore(Cfg.MyExtraIgnore.Where(x => !string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))); RebuildIgnore(); _ignoreBox.Focus(); };
                Grid.SetColumn(rm, 1);
                row.Children.Add(rm);
                _ignoreList.Children.Add(row);
            }
        if (_ignoreList.Children.Count == 0)
            _ignoreList.Children.Add(new TextBlock { Text = "Nothing added yet.", Style = (Style)FindResource("CaptionText"), MinHeight = 20 });
    }

    void AddIgnore()
    {
        var p = _ignoreBox.Text.Trim();
        var problem = Config.IgnoreProblem(p);
        if (problem == null && Cfg.ExtraIgnore.Contains(p, StringComparer.OrdinalIgnoreCase)) problem = "That one is already in the list.";
        if (problem != null) { _ignoreError.Text = problem; _ignoreError.Visibility = Visibility.Visible; _ignoreBox.Focus(); return; }
        Put(() => Cfg.SetMyExtraIgnore(Cfg.MyExtraIgnore.Append(p)));
        _ignoreBox.Clear();
        RebuildIgnore();
        _ignoreBox.Focus();
    }

    /// <summary>Used by the debug preview for screenshots.</summary>
    internal void ExpandAdvanced() => AdvancedExpander.IsExpanded = true;

    public void Refresh()
    {
        if (_startup.IsChecked != _m.StartsWithWindows) _startup.IsChecked = _m.StartsWithWindows;
    }
}
