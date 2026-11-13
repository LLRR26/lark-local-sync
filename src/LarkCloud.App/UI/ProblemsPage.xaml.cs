using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LarkCloud.App.Service;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

internal enum RowAction { ShowInFolder, OpenInLark, UploadWithLark, TryAgain }

/// <summary>One line of the list: a folder heading, the "Good to know" heading, a problem file or a note.
/// Rows are updated in place every second, so the list never rebuilds and keeps its scroll position and selection.</summary>
internal abstract class ProblemItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key { get; init; } = "";
    public abstract string Kind { get; }       // Header | Problem | Note (the list styles rows by this)

    protected void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

internal sealed class ProblemHeader : ProblemItem
{
    string _title = "", _count = "";
    Brush _brush = Brushes.Gray;
    public override string Kind => "Header";
    public string Glyph { get; init; } = StatusSummary.GlyphFolder;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Count { get => _count; set => Set(ref _count, value); }
    public Brush TitleBrush { get => _brush; set => Set(ref _brush, value); }
}

internal sealed class ProblemRow : ProblemItem
{
    string _name = "", _where = "", _reason = "", _l1 = "", _l2 = "";
    Visibility _v1 = Visibility.Collapsed, _v2 = Visibility.Collapsed;

    public string PairId { get; init; } = "";
    public string Rel { get; init; } = "";
    public bool Info { get; init; }
    public override string Kind => Info ? "Note" : "Problem";

    public List<RowAction> Fixes { get; private set; } = new();
    public RowAction? Action1 { get; private set; }
    public RowAction? Action2 { get; private set; }
    /// <summary>What is left for the ⋯ menu (the actions not shown as buttons).</summary>
    public IEnumerable<RowAction> More => Fixes.Where(a => a != Action1 && a != Action2);

    public string Name { get => _name; private set { Set(ref _name, value); Raise(nameof(MoreLabel)); Raise(nameof(AutomationName)); } }
    public string Where { get => _where; private set { Set(ref _where, value); Raise(nameof(WhereVisibility)); } }
    public string Reason { get => _reason; private set { Set(ref _reason, value); Raise(nameof(AutomationName)); } }
    public string Label1 { get => _l1; private set { Set(ref _l1, value); Raise(nameof(Automation1)); } }
    public string Label2 { get => _l2; private set { Set(ref _l2, value); Raise(nameof(Automation2)); } }
    public Visibility Vis1 { get => _v1; private set => Set(ref _v1, value); }
    public Visibility Vis2 { get => _v2; private set => Set(ref _v2, value); }
    public Visibility WhereVisibility => _where.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public FontWeight NameWeight => Info ? FontWeights.Normal : FontWeights.SemiBold;
    public string MoreLabel => $"More actions for {Name}";
    public string AutomationName => $"{Name}. {Reason}";
    public string Automation1 => $"{Label1}, {Name}";
    public string Automation2 => $"{Label2}, {Name}";

    public static string LabelOf(RowAction a) => a switch
    {
        RowAction.ShowInFolder => "Show in folder",
        RowAction.OpenInLark => "Open in Lark",
        RowAction.UploadWithLark => "Upload with Lark…",
        _ => "Try again now",
    };

    public void Update(string name, string where, string reason, List<RowAction> fixes)
    {
        Name = name;
        Where = where;
        Reason = reason;
        Fixes = fixes;
        // The most relevant fixing action is a visible button, and so is the next one if there are only two; the rest wait in ⋯.
        // Notes are quiet: everything is under ⋯.
        var shown = Info ? 0 : fixes.Count <= 2 ? fixes.Count : 1;
        Action1 = shown > 0 ? fixes[0] : null;
        Action2 = shown > 1 ? fixes[1] : null;
        Label1 = Action1 is { } a1 ? LabelOf(a1) : "";
        Label2 = Action2 is { } a2 ? LabelOf(a2) : "";
        Vis1 = Action1 == null ? Visibility.Collapsed : Visibility.Visible;
        Vis2 = Action2 == null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>"Files that can't sync": every stuck file grouped by folder, each with the action that fixes it, then a quieter "Good to know" section.</summary>
internal sealed partial class ProblemsPage : UserControl, IPage
{
    sealed record FolderChoice(string? Id, string Label);

    readonly IAppModel _m;
    readonly ObservableCollection<ProblemItem> _items = new();
    readonly Dictionary<string, ProblemItem> _byKey = new();
    string? _only;
    bool _syncingChoices;
    DateTime _retryUntil;
    readonly HashSet<ProblemItem> _leaving = new();   // rows fading out before they are removed
    bool _primed;                                    // the first fill (and a page that is not showing) does not animate

    public ProblemsPage(IAppModel model)
    {
        _m = model;
        InitializeComponent();
        List.ItemsSource = _items;
        Loaded += (_, _) => Theme.Changed += Refresh;       // heading colours are resolved in code; don't outlive the window
        Unloaded += (_, _) => Theme.Changed -= Refresh;
    }

    Window? Owner => Window.GetWindow(this);

    /// <summary>Show one folder's files only (from a folder's ⋯ menu), or all folders when null.</summary>
    public void SetFilter(string? pairId)
    {
        if (_only == pairId) return;
        _only = pairId;
        Refresh();
    }

    // ---------------------------------------------------------------- live refresh
    public void Refresh()
    {
        var pairs = _m.Pairs();
        if (_only != null && pairs.All(p => p.Id != _only)) _only = null;     // the folder was removed
        SyncChoices(pairs);

        var desired = new List<ProblemItem>();
        int problems = 0, notes = 0;
        var noteRows = new List<ProblemRow>();
        if (_m.SignedIn)
            foreach (var p in pairs)
            {
                if (_only != null && p.Id != _only) continue;
                var st = _m.StatusOf(p.Id);
                var perRel = new Dictionary<string, int>();
                ProblemRow Make(string rel, string msg, bool info)
                {
                    perRel[(info ? "n" : "p") + rel] = perRel.GetValueOrDefault((info ? "n" : "p") + rel) + 1;
                    var key = $"{(info ? "n" : "p")}:{p.Id}|{rel}|{perRel[(info ? "n" : "p") + rel]}";
                    var row = Get(key, () => new ProblemRow { Key = key, PairId = p.Id, Rel = rel, Info = info });
                    var parent = rel.Length == 0 ? "" : PathUtil.Parent(rel).Replace("/", " › ").Replace("\\", " › ");
                    var where = info ? (parent.Length == 0 ? p.Label : $"{p.Label} › {parent}") : (parent.Length == 0 ? "" : $"In {parent}");
                    row.Update(rel.Length == 0 ? "This folder" : PathUtil.Name(rel), where, msg, FixesFor(p, st, rel, info));
                    return row;
                }
                if (st.Items.Count > 0)
                {
                    var key = "h:" + p.Id;
                    var h = Get(key, () => new ProblemHeader { Key = key });
                    h.Title = p.Label;
                    h.Count = $" · {st.Items.Count:N0} file{(st.Items.Count == 1 ? "" : "s")}";
                    h.TitleBrush = Theme.Brush("TextPrimary");
                    desired.Add(h);
                    foreach (var (rel, msg) in st.Items) desired.Add(Make(rel, msg, false));
                    problems += st.Items.Count;
                }
                foreach (var (rel, msg) in st.Info) noteRows.Add(Make(rel, msg, true));
            }
        notes = noteRows.Count;
        if (notes > 0)
        {
            var h = Get("h:notes", () => new ProblemHeader { Key = "h:notes", Glyph = "" });
            h.Title = "Good to know";
            h.Count = $" · {notes:N0}";
            h.TitleBrush = Theme.Brush("TextSecondary");
            desired.Add(h);
            desired.AddRange(noteRows);
        }

        foreach (var k in _byKey.Keys.Where(k => desired.All(d => d.Key != k)).ToList()) _byKey.Remove(k);
        Sync(desired);

        var folder = pairs.FirstOrDefault(p => p.Id == _only)?.Label;
        Intro.Text = !_m.SignedIn ? "Sign in to Lark to see which files can't sync."
            : problems > 0 ? $"{problems:N0} file{(problems == 1 ? "" : "s")} can't sync{(folder != null ? $" in “{folder}”" : "")}. Most problems clear themselves; “Try again now” checks again straight away."
            : "No files are stuck.";

        var empty = desired.Count == 0;
        List.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            var ok = _m.SignedIn;
            EmptyGlyph.Text = ok ? StatusSummary.GlyphOk : StatusSummary.GlyphCritical;
            EmptyGlyph.Foreground = Theme.Brush(ok ? "Success" : "Critical");
            EmptyTitle.Text = ok ? "All files are syncing" : "You're signed out";
            EmptyText.Text = !ok ? "Sign in to Lark and your files start syncing again."
                : folder != null ? $"Nothing in “{folder}” needs your attention."
                : "Nothing needs your attention right now. If a file gets stuck, it shows up here with a way to fix it.";
        }
        Retrying.Visibility = DateTime.UtcNow < _retryUntil && !empty ? Visibility.Visible : Visibility.Collapsed;
    }

    T Get<T>(string key, Func<T> make) where T : ProblemItem
    {
        if (_byKey.TryGetValue(key, out var it) && it is T t) return t;
        t = make();
        _byKey[key] = t;
        return t;
    }

    /// <summary>Bring the list in step with what should be shown by inserting, moving and removing rows (never clearing it).</summary>
    void Sync(List<ProblemItem> desired)
    {
        var animate = _primed && IsVisible;
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], desired[i])) continue;
            var at = _items.IndexOf(desired[i]);
            if (at >= 0) _items.Move(at, i);
            else
            {
                _items.Insert(i, desired[i]);
                if (animate && desired[i] is { } added) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>      // a new row fades in
                {
                    if (List.ItemContainerGenerator.ContainerFromItem(added) is UIElement c) Motion.Reveal(c, 200, dy: 8);
                });
            }
        }
        // Rows left over at the end are the ones that are gone (their keys are dropped, so they never come back): fade, then remove.
        for (var i = _items.Count - 1; i >= desired.Count; i--)
        {
            var gone = _items[i];
            if (_leaving.Contains(gone)) continue;
            if (!animate || desired.Count == 0 || List.ItemContainerGenerator.ContainerFromItem(gone) is not UIElement c) { _items.RemoveAt(i); continue; }
            _leaving.Add(gone);
            Motion.FadeOut(c, () => { _items.Remove(gone); _leaving.Remove(gone); });
        }
        _primed = true;
    }

    /// <summary>The folder chooser lists "All folders" and each folder; shown when there is more than one folder to choose from.</summary>
    void SyncChoices(IReadOnlyList<Pair> pairs)
    {
        var want = new List<FolderChoice> { new(null, "All folders") };
        want.AddRange(pairs.Select(p => new FolderChoice(p.Id, p.Label)));
        var have = Filter.Items.Cast<FolderChoice>().ToList();
        if (!have.SequenceEqual(want))
        {
            _syncingChoices = true;
            Filter.Items.Clear();
            foreach (var c in want) Filter.Items.Add(c);
            _syncingChoices = false;
        }
        var sel = want.FindIndex(c => c.Id == _only);
        if (Filter.SelectedIndex != sel) { _syncingChoices = true; Filter.SelectedIndex = sel; _syncingChoices = false; }
        FilterBox.Visibility = pairs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingChoices || Filter.SelectedItem is not FolderChoice c) return;
        _only = c.Id;
        Refresh();
    }

    // ---------------------------------------------------------------- what can be done with a row
    string? Full(ProblemRow r)
    {
        var p = _m.Pairs().FirstOrDefault(x => x.Id == r.PairId);
        return p == null || r.Rel.Length == 0 ? null : Path.Combine(p.LocalFolder, r.Rel.Replace('/', '\\'));
    }

    static List<RowAction> FixesFor(Pair p, PairStatus st, string rel, bool info)
    {
        var l = new List<RowAction>();
        if (st.UploadHere.ContainsKey(rel) && !info) l.Add(RowAction.UploadWithLark);
        if (st.OpenLinks.GetValueOrDefault(rel) != null) l.Add(RowAction.OpenInLark);
        if (rel.Length > 0) l.Add(RowAction.ShowInFolder);
        if (!info) l.Add(RowAction.TryAgain);
        return l;
    }

    void Do(ProblemRow r, RowAction a)
    {
        var pair = _m.Pairs().FirstOrDefault(p => p.Id == r.PairId);
        switch (a)
        {
            case RowAction.ShowInFolder:
                if (Full(r) is { } f) Ui.ShowInFolder(f);
                break;
            case RowAction.OpenInLark:
                if (_m.StatusOf(r.PairId).OpenLinks.GetValueOrDefault(r.Rel) is { } u) Ui.Open(u);
                break;
            case RowAction.UploadWithLark:
                UploadWithLark(r, pair);
                break;
            case RowAction.TryAgain:
                _m.RetryFailed(r.PairId);
                _retryUntil = DateTime.UtcNow.AddSeconds(4);
                Refresh();
                break;
        }
    }

    void UploadWithLark(ProblemRow r, Pair? pair)
    {
        if (Full(r) is not { } f) return;
        var link = _m.StatusOf(r.PairId).UploadHere.GetValueOrDefault(r.Rel);
        link ??= pair?.LarkUrl;
        if (link != null) Ui.Open(link);
        Ui.ShowInFolder(f);
        ConfirmDialog.Tell(Owner, "Upload this file with Lark",
            "Lark's folder for this file is opening in your browser, and the file is selected in File Explorer.\n\n" +
            $"1. Drag “{Path.GetFileName(f)}” from File Explorer into the Lark page.\n2. When Lark shows it, click “Try again now” here.\n\n" +
            "The app then sees the same file in both places, checks they are identical, and keeps them in sync from then on." +
            (link == pair?.LarkUrl ? $"\n\nOpen the folder “{PathUtil.Parent(r.Rel)}” in Lark first." : ""));
    }

    // ---------------------------------------------------------------- clicks
    static ProblemRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ProblemRow;

    void A1_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { Action1: { } a } r) Do(r, a); }
    void A2_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { Action2: { } a } r) Do(r, a); }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } r) return;
        List.SelectedItem = r;
        ShowMenu(r, (Button)sender);
    }

    void ShowMenu(ProblemRow r, FrameworkElement anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
                new[] { new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.None) },
        };
        void Item(string text, string glyph, Action act)
        {
            var mi = new MenuItem { Header = text, Icon = new TextBlock { Style = (Style)FindResource("IconText"), Text = glyph } };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        foreach (var a in r.More)
            Item(ProblemRow.LabelOf(a), a switch { RowAction.ShowInFolder => "", RowAction.OpenInLark => "", RowAction.UploadWithLark => "", _ => "" }, () => Do(r, a));
        if (Full(r) is { } full && File.Exists(full)) Item("Open file", "", () => Ui.Open(full));
        Item("Copy problem", "", () =>
        {
            try { Clipboard.SetText($"{r.Rel}: {r.Reason}"); } catch { }       // the clipboard can be busy
        });
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- keyboard and mouse on the list
    ProblemRow? Selected => List.SelectedItem as ProblemRow;

    void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem && Selected is { } r && r.Fixes.Contains(RowAction.ShowInFolder)) { Do(r, RowAction.ShowInFolder); e.Handled = true; }
    }

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FoldersPage.Ancestor<ButtonBase>(d) == null && Selected is { } r && r.Fixes.Contains(RowAction.ShowInFolder))
            Do(r, RowAction.ShowInFolder);
    }

    void List_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FoldersPage.Ancestor<ListBoxItem>(d) is { } item && item.DataContext is ProblemRow) item.IsSelected = true;
    }

    /// <summary>Right-click, the Apps key and Shift+F10 open the same menu as the ⋯ button.</summary>
    void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (Selected is not { } r || List.ItemContainerGenerator.ContainerFromItem(r) is not DependencyObject c) return;
        if (FoldersPage.Descendant<Button>(c, b => b.Name == "MoreButton") is { } more) ShowMenu(r, more);
    }
}
