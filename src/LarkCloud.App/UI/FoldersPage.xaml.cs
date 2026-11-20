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

/// <summary>One folder as the list shows it. Updated in place every second so the list never rebuilds.</summary>
internal sealed class FolderRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    string _name = "", _path = "", _glyph = "", _text = "";
    Brush _brush = Brushes.Gray;

    public string Id { get; }
    public Pair Pair { get; private set; }
    public PairStatus Status { get; private set; } = new();
    public FolderRow(Pair p) { Id = p.Id; Pair = p; }

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Path { get => _path; private set => Set(ref _path, value); }
    public string StatusGlyph { get => _glyph; private set => Set(ref _glyph, value); }
    public string StatusText { get => _text; private set => Set(ref _text, value); }
    public Brush StatusBrush { get => _brush; private set => Set(ref _brush, value); }
    /// <summary>True while this folder syncs: its status glyph turns.</summary>
    public bool IsBusy => !Pair.Paused && Status.State is SyncState.Syncing or SyncState.Starting;
    public string OpenLabel => $"Open folder {Name}";
    public string MoreLabel => $"More actions for {Name}";

    void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        if (prop == nameof(Name)) { PropertyChanged?.Invoke(this, new(nameof(OpenLabel))); PropertyChanged?.Invoke(this, new(nameof(MoreLabel))); }
    }

    public void Update(Pair p, PairStatus st)
    {
        Pair = p;
        Status = st;
        Name = p.Label;
        Path = p.LocalFolder;

        string glyph, key, text;
        if (p.Paused || st.State == SyncState.Paused) (glyph, key, text) = (StatusSummary.GlyphPaused, "TextSecondary", "Paused");
        else switch (st.State)
            {
                case SyncState.Error: (glyph, key, text) = (StatusSummary.GlyphCritical, "Critical", st.Message); break;
                case SyncState.Attention:
                    (glyph, key) = (StatusSummary.GlyphCaution, "Caution");
                    text = st.Items.Count > 0 ? $"{st.Items.Count:N0} file{(st.Items.Count == 1 ? "" : "s")} can't sync"
                        : st.PendingDeletes.Count > 0 ? $"{st.PendingDeletes.Count:N0} deletion{(st.PendingDeletes.Count == 1 ? " is" : "s are")} waiting for you"
                        : st.Message;
                    break;
                case SyncState.Syncing or SyncState.Starting: (glyph, key, text) = (StatusSummary.GlyphBusy, "Accent", st.Message); break;
                default: (glyph, key, text) = (StatusSummary.GlyphOk, "Success", st.LastFull == null ? "Up to date" : $"Up to date · checked {Ui.Ago(st.LastFull)}"); break;
            }
        if (st.ViewOnly) text += " · view only";
        if (st.Downloading > 0) text += $" · opening {st.Downloading:N0} file{(st.Downloading == 1 ? "" : "s")}";
        if (p.DocCopies) text += " · offline doc copies on";
        StatusGlyph = glyph;
        StatusBrush = Theme.Brush(key);
        StatusText = text;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));     // same value is a no-op for the spinner
    }
}

internal sealed partial class FoldersPage : UserControl, IPage
{
    readonly IAppModel _m;
    readonly ObservableCollection<FolderRow> _rows = new();
    readonly HashSet<string> _leaving = new();       // rows fading out before they are removed
    bool _primed;                                    // the first fill (and a page that is not showing) does not animate

    public FoldersPage(IAppModel model)
    {
        _m = model;
        InitializeComponent();
        List.ItemsSource = _rows;
        AddButton.Visibility = model.LockFolders ? Visibility.Collapsed : Visibility.Visible;
        EmptyAdd.Visibility = AddButton.Visibility;
        Intro.Text = model.LockFolders
            ? "Your IT team manages these folders."
            : "Files show here and download only when you open them. In File Explorer, right-click a file or folder for “Always keep on this device” or “Free up space”.";
        EmptyText.Text = model.LockFolders
            ? "Your IT team chooses which folders appear here. They show up when IT adds them."
            : "Add a Lark folder and it appears in File Explorer. Files download only when you open them.";
        Loaded += (_, _) => Theme.Changed += Refresh;       // status colours are resolved per row; don't outlive the window
        Unloaded += (_, _) => Theme.Changed -= Refresh;
    }

    Window? Owner => Window.GetWindow(this);
    FolderRow? Selected => List.SelectedItem as FolderRow;

    public void Refresh()
    {
        var pairs = _m.Pairs();
        // keep rows in step with the folders without clearing the list (keeps selection and scroll position)
        var animate = _primed && IsVisible;
        for (var i = _rows.Count - 1; i >= 0; i--)
            if (pairs.All(p => p.Id != _rows[i].Id)) Leave(_rows[i], animate);
        for (var i = 0; i < pairs.Count; i++)
        {
            var row = _rows.FirstOrDefault(r => r.Id == pairs[i].Id);
            if (row == null)
            {
                _rows.Insert(Math.Min(i, _rows.Count), row = new FolderRow(pairs[i]));
                if (animate) Arrive(row);
            }
            else if (_rows.IndexOf(row) != i) _rows.Move(_rows.IndexOf(row), Math.Min(i, _rows.Count - 1));
            row.Update(pairs[i], _m.StatusOf(pairs[i].Id));
        }
        if (List.SelectedItem == null && _rows.Count > 0) List.SelectedIndex = 0;
        var any = _rows.Count > 0;
        List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        AddButton.Visibility = any && !_m.LockFolders ? Visibility.Visible : Visibility.Collapsed;   // the empty state has its own Add button
        _primed = true;
    }

    /// <summary>A folder that was just added fades in once its row exists.</summary>
    void Arrive(FolderRow row) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (List.ItemContainerGenerator.ContainerFromItem(row) is UIElement c) Motion.Reveal(c, 200, dy: 8);
    });

    /// <summary>A folder that is gone fades out (120 ms), then leaves the list; selection and scroll position are not touched.</summary>
    void Leave(FolderRow row, bool animate)
    {
        if (_leaving.Contains(row.Id)) return;
        if (!animate || List.ItemContainerGenerator.ContainerFromItem(row) is not UIElement c) { _rows.Remove(row); return; }
        _leaving.Add(row.Id);
        Motion.FadeOut(c, () => { _rows.Remove(row); _leaving.Remove(row.Id); Refresh(); });
    }

    // ---------------------------------------------------------------- actions
    void Add_Click(object sender, RoutedEventArgs e) => _m.AddFolder();

    static FolderRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as FolderRow;

    void OpenFolder_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) OpenFolder(r); }

    static void OpenFolder(FolderRow r) { if (Directory.Exists(r.Pair.LocalFolder)) Ui.Open(r.Pair.LocalFolder); }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } r) return;
        List.SelectedItem = r;
        ShowMenu(r, (Button)sender);
    }

    void ShowMenu(FolderRow r, FrameworkElement anchor)
    {
        var p = r.Pair;
        var st = r.Status;
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
                new[] { new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.None) },
        };
        MenuItem Item(string text, string glyph, Action act, bool check = false, bool isOn = false)
        {
            var mi = new MenuItem
            {
                Header = text,
                Icon = new TextBlock { Style = (Style)FindResource("IconText"), Text = glyph },
                IsCheckable = check,
                IsChecked = isOn,
            };
            mi.Click += (_, _) => act();
            return mi;
        }

        if (p.LarkUrl is { } url) menu.Items.Add(Item("Open in Lark", "\uE774", () => Ui.Open(url)));
        menu.Items.Add(p.Paused || st.State == SyncState.Paused
            ? Item("Resume this folder", "\uE768", () => { _m.SetPaused(p.Id, false); Refresh(); })
            : Item("Pause this folder", "\uE769", () => { _m.SetPaused(p.Id, true); Refresh(); }));
        menu.Items.Add(Item("Free up space…", "\uE753", () => FreeUp(r)));
        menu.Items.Add(Item("Offline doc copies", "\uE8A5", () => ToggleCopies(r), check: true, isOn: p.DocCopies));
        menu.Items.Add(Item("Check often\u2026", "\uE72C", () => new CheckOftenDialog(_m.Settings, p) { Owner = Owner }.ShowDialog()));
        if (st.Items.Count > 0 || st.Info.Count > 0 || st.PendingDeletes.Count > 0) menu.Items.Add(new Separator());
        if (st.Items.Count > 0 || st.Info.Count > 0) menu.Items.Add(Item("Files that can't sync", "\uE7BA", () => _m.ShowProblems(p.Id)));
        if (st.PendingDeletes.Count > 0) menu.Items.Add(Item("Review deletions", "\uE74D", () => _m.ShowDeletions(p.Id)));
        if (!_m.LockFolders && !p.FromIt)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Rename on this PC…", "", () => Rename(r)));
            menu.Items.Add(Item("Remove from this PC…", "\uE711", () => Remove(r)));
        }
        menu.IsOpen = true;
    }

    void FreeUp(FolderRow r)
    {
        if (ConfirmDialog.Ask(Owner, $"Free up space in “{r.Name}”?",
                "Downloaded copies of files are removed from this PC. The files stay visible and download again when you open them. " +
                "Files marked “Always keep on this device” are kept.", "Free up space"))
            _m.FreeUpSpace(r.Id);
    }

    void ToggleCopies(FolderRow r)
    {
        var on = !r.Pair.DocCopies;
        if (on && !ConfirmDialog.Ask(Owner, $"Show read-only copies of the Lark docs and sheets in “{r.Name}”?",
                "Next to each document's shortcut you'll get “Name (Lark copy).docx / .xlsx / .pptx” that opens in Word, Excel or PowerPoint, " +
                "also offline once downloaded. They're refreshed when the doc changes in Lark. Edit the doc itself in Lark.\n\n" +
                "Lark has to export every doc once, so the first check takes longer. This needs the export permission " +
                "(drive:export:readonly) in the Lark developer console.", "Turn on"))
            return;
        _m.SetDocCopies(r.Id, on);
        Refresh();
    }

    static readonly System.Text.RegularExpressions.Regex ReservedName =
        new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Plain-language reason the new folder name can't be used, or null. Checked before anything is touched.</summary>
    string? CheckNewName(FolderRow r, string name, string full)
    {
        if (name.Length == 0) return "Type a name for the folder.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A folder name can't contain any of these characters: \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ') || ReservedName.IsMatch(name)) return "Windows doesn't allow that name. Try a different one.";
        if (Directory.Exists(full) || File.Exists(full)) return $"“{name}” already exists in that location. Choose a different name.";
        return Ui.CheckPcFolder(full, _m.Settings, r.Id).Error;
    }

    /// <summary>Renames only the folder on this PC; the Lark folder keeps its name.</summary>
    void Rename(FolderRow r)
    {
        var old = r.Pair.LocalFolder.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(old);
        if (parent == null) return;
        var name = Path.GetFileName(old);
        while (true)
        {
            if (ConfirmDialog.AskText(Owner, $"Rename “{r.Name}” on this PC",
                    "Only the folder on this PC gets the new name. The folder in Lark keeps its name.", "Rename", name) is not { } input) return;
            name = input.Trim();
            if (string.Equals(name, Path.GetFileName(old), StringComparison.Ordinal)) return;
            var full = Path.Combine(parent, name);
            var err = string.Equals(name, Path.GetFileName(old), StringComparison.OrdinalIgnoreCase)
                ? "Choose a name that differs by more than upper or lower case." : CheckNewName(r, name, full);
            if (err == null) { Run(full); return; }
            ConfirmDialog.Tell(Owner, "Can't use that name", err);
        }

        void Run(string full)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Task.Run(() => _m.RenameFolder(r.Id, full)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                Mouse.OverrideCursor = null;
                Refresh();
                if (t.IsFaulted)
                {
                    Log.Warn($"Rename {old} -> {full} failed: {t.Exception?.GetBaseException().Message}");
                    ConfirmDialog.Tell(Owner, "Couldn't rename the folder",
                        "Another program may be using the folder or something in it. Close it and try again. " +
                        $"Your folder was left as it was, and it keeps syncing.\n\n{t.Exception?.GetBaseException().Message}");
                }
            }));
        }
    }

    void Remove(FolderRow r)
    {
        if (!ConfirmDialog.Ask(Owner, $"Remove “{r.Name}” from this PC?",
                $"Nothing is deleted in Lark. Files you've downloaded stay in\n{r.Pair.LocalFolder}\nas normal files; files that are only in Lark are removed from this PC.",
                "Remove", destructive: true))
            return;
        Mouse.OverrideCursor = Cursors.Wait;
        Task.Run(() => _m.RemoveFolder(r.Id)).ContinueWith(_ => Dispatcher.BeginInvoke(() => { Mouse.OverrideCursor = null; Refresh(); }));
    }

    // ---------------------------------------------------------------- keyboard and mouse on the list
    void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem && Selected is { } r) { OpenFolder(r); e.Handled = true; }
    }

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && Ancestor<ButtonBase>(d) == null && Selected is { } r) OpenFolder(r);
    }

    void List_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && Ancestor<ListBoxItem>(d) is { } item) item.IsSelected = true;
    }

    /// <summary>Right-click, the Apps key and Shift+F10 all open the same menu as the ⋯ button.</summary>
    void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        ShowSelectedMenu();
    }

    void ShowSelectedMenu()
    {
        if (Selected is not { } r || List.ItemContainerGenerator.ContainerFromItem(r) is not DependencyObject c) return;
        if (Descendant<Button>(c, b => b.Name == "MoreButton") is { } more) ShowMenu(r, more);
    }

    internal static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var o = d; o != null; o = o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is T t) return t;
        return null;
    }

    internal static T? Descendant<T>(DependencyObject d, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t && match(t)) return t;
            if (Descendant(c, match) is { } found) return found;
        }
        return null;
    }

    /// <summary>For the debug design preview.</summary>
    internal void OpenFirstMenu()
    {
        List.UpdateLayout();
        ShowSelectedMenu();
    }
}
