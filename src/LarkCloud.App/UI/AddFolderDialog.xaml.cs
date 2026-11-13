using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>Add a folder in three steps: choose the Lark folder, choose where it appears on this PC, confirm. Modal.
/// Every call to Lark runs in the background with a progress message, so the window never freezes.</summary>
internal sealed partial class AddFolderDialog : Window
{
    sealed class Item
    {
        public required string Token;
        public required string Name;
        public string? Url;
        public bool Loaded;
        public string Kind = "drive";          // drive | wikiroot | wikispace | wikinode | group
        public string? Space;
        public bool Selectable => Kind is "drive" or "wikispace" or "wikinode";
        public string Key => Kind switch
        {
            "wikispace" => $"wiki:{Space}/",
            "wikinode" => $"wiki:{Space}/{Token}",
            _ => Token,
        };
    }

    readonly IAppModel _m;
    readonly IFolderSource _src;
    string? _host, _rootToken;
    bool _pcEdited, _closed, _adding;
    int _step = 1, _busy;
    Action? _retry;
    int _pcCheckId;
    (string? Error, string? Warning) _pc = ("Choose a folder on this PC.", null);

    public bool Added { get; private set; }

    public AddFolderDialog(IAppModel model)
    {
        _m = model;
        _src = model.OpenFolderSource();
        InitializeComponent();
        Theme.Attach(this, mica: false);
        Tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(Tree_Expanded));
        Closed += (_, _) => _closed = true;
        Loaded += (_, _) => { Theme.Changed += ShowStep; ShowStep(); LoadRoot(); };
        Unloaded += (_, _) => Theme.Changed -= ShowStep;
        Closing += (_, e) => { if (_adding) e.Cancel = true; };
    }

    Item? Selected => (Tree.SelectedItem as TreeViewItem)?.Tag is Item { Selectable: true } it ? it : null;

    // ---------------------------------------------------------------- background work
    void SetBusy(int delta, string? what)
    {
        _busy += delta;
        var on = _busy > 0;
        FootBusy.Visibility = TreeBusy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (what != null) { FootText.Text = TreeBusyText.Text = what; }
        if (!on) { FootText.Text = ""; }
        TreeBusy.Visibility = on && Tree.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    async Task Run<T>(Func<T> work, string what, Action<T> done, Action? retry = null)
    {
        SetBusy(+1, what);
        LoadError.Visibility = Visibility.Collapsed;
        try
        {
            var r = await Task.Run(work);
            if (_closed) return;
            SetBusy(-1, null);
            done(r);
        }
        catch (Exception ex)
        {
            if (_closed) return;
            SetBusy(-1, null);
            ShowLoadError(ex, what, retry);
        }
    }

    void ShowLoadError(Exception ex, string what, Action? retry)
    {
        string text, forIt = "";
        if (ex is LarkApiException { Code: 99991679 })
        {
            if (what.Contains("wiki", StringComparison.OrdinalIgnoreCase)) { text = "Wiki spaces aren't turned on for this app. Ask your IT team to turn them on."; forIt = "For IT: wiki:wiki:readonly (README, Optional features)"; }
            else if (what.Contains("Search", StringComparison.OrdinalIgnoreCase)) { text = "Searching isn't turned on for this app. Ask your IT team, or paste the folder's link instead."; forIt = "For IT: search:docs:read (README, Optional features)"; }
            else { text = "Lark says this app isn't allowed to do that yet. Ask your IT team."; forIt = "For IT: see README, Optional features"; }
        }
        else if (ex is LarkApiException { IsPermission: true }) text = "You don't have access to that in Lark.";
        else text = $"Couldn't reach Lark: {ex.Message}";
        ShowMessage(text, forIt, retry);
    }

    void ShowMessage(string text, string forIt = "", Action? retry = null)
    {
        LoadErrorText.Text = text;
        LoadErrorIt.Text = forIt;
        LoadErrorIt.Visibility = forIt.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _retry = retry;
        LoadRetry.Visibility = retry == null ? Visibility.Collapsed : Visibility.Visible;
        LoadError.Visibility = Visibility.Visible;
        TreeEmpty.Visibility = Visibility.Collapsed;
    }

    void LoadRetry_Click(object sender, RoutedEventArgs e) { var r = _retry; LoadError.Visibility = Visibility.Collapsed; r?.Invoke(); }

    // ---------------------------------------------------------------- the tree
    bool Used(Item it) => it.Selectable && _m.Pairs().Any(p =>
        p.FolderToken == it.Key || (it.Kind == "drive" && it.Token == _rootToken && (p.FolderToken == "" || p.FolderToken == _rootToken)));

    static string GlyphOf(Item it) => it.Kind switch { "wikiroot" or "wikispace" => "", "wikinode" => "", "group" => "", _ => StatusSummary.GlyphFolder };

    TreeViewItem Node(Item it, bool expandable = true)
    {
        var used = Used(it);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Style = (Style)FindResource("IconText"), Text = GlyphOf(it), FontSize = 16, Margin = new Thickness(0, 0, 10, 0), Foreground = Theme.Brush("TextSecondary") });
        head.Children.Add(new TextBlock { Style = (Style)FindResource(it.Selectable ? "BodyText" : "BodyStrongText"), Text = it.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(used ? "TextSecondary" : "TextPrimary") });
        if (used)
        {
            head.Children.Add(new TextBlock { Style = (Style)FindResource("IconText"), Text = "", FontSize = 12, Margin = new Thickness(12, 0, 4, 0), Foreground = Theme.Brush("Success") });
            head.Children.Add(new TextBlock { Style = (Style)FindResource("CaptionText"), Text = "Already on this PC", VerticalAlignment = VerticalAlignment.Center });
        }
        var n = new TreeViewItem { Header = head, Tag = it };
        AutomationProperties.SetName(n, used ? $"{it.Name}, already on this PC" : it.Name);
        if (expandable) n.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "Loading…", Style = (Style)FindResource("BodySecondaryText") }, Focusable = false, IsEnabled = false });
        return n;
    }

    TreeViewItem Note(string text) => new()
    {
        Header = new TextBlock { Text = text, Style = (Style)FindResource("BodySecondaryText"), TextWrapping = TextWrapping.Wrap },
        Focusable = false,
    };

    static string? HostOf(IEnumerable<string?> urls) => urls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : null).FirstOrDefault(h => h != null);

    void LoadRoot()
    {
        _ = Run(() => (_src.RootToken, _src.ChildFolders(_src.RootToken)), "Loading your Lark folders…", r =>
        {
            _rootToken = r.RootToken;
            _host = HostOf(r.Item2.Select(e => e.Url));
            var root = Node(new Item { Token = r.RootToken, Name = "My folders", Loaded = true });
            root.Items.Clear();
            foreach (var e in r.Item2) root.Items.Add(Node(new Item { Token = e.Token, Name = e.Name, Url = e.Url }));
            Tree.Items.Add(root);
            Tree.Items.Add(Node(new Item { Token = "", Name = "Wiki spaces", Kind = "wikiroot" }));
            root.IsExpanded = true;
            root.Focus();
            if (r.Item2.Count == 0) root.Items.Add(Note("No folders in My folders yet. Use “New folder in Lark…”."));
        }, LoadRoot);
    }

    void Tree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: Item it } n) LoadChildren(n, it);
    }

    void LoadChildren(TreeViewItem n, Item it)
    {
        if (it.Loaded || it.Kind == "group") return;
        it.Loaded = true;
        void Retry() { it.Loaded = false; LoadChildren(n, it); }
        switch (it.Kind)
        {
            case "wikiroot":
                _ = Run(() => _src.WikiSpaces(), "Loading wiki spaces…", spaces =>
                {
                    n.Items.Clear();
                    foreach (var s in spaces.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                        n.Items.Add(Node(new Item { Token = s.Token, Name = s.Name, Kind = "wikispace", Space = s.Token }));
                    if (spaces.Count == 0) n.Items.Add(Note("No wiki spaces you can open."));
                }, Retry);
                break;
            case "wikispace" or "wikinode":
                _ = Run(() => _src.WikiNodes(it.Space!, it.Kind == "wikinode" ? it.Token : null), $"Opening wiki {it.Name}…", nodes =>
                {
                    n.Items.Clear();
                    foreach (var w in nodes.Where(w => w.HasChild))           // pages with sub-pages become folders
                        n.Items.Add(Node(new Item { Token = w.NodeToken, Name = w.Title, Kind = "wikinode", Space = it.Space }));
                }, Retry);
                break;
            default:
                _ = Run(() => _src.ChildFolders(it.Token), $"Opening {it.Name}…", kids =>
                {
                    n.Items.Clear();
                    foreach (var e in kids) n.Items.Add(Node(new Item { Token = e.Token, Name = e.Name, Url = e.Url }));
                    _host ??= HostOf(kids.Select(e => e.Url));
                }, Retry);
                break;
        }
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => UpdateButtons();

    // ---------------------------------------------------------------- the three ways to find a folder
    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (BrowseRow == null) return;
        BrowseRow.Visibility = ModeBrowse.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SharedRow.Visibility = ModeShared.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LinkRow.Visibility = ModeLink.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NewFolderBar.Visibility = Visibility.Collapsed;
        if (ModeShared.IsChecked == true) SearchBox.Focus();
        else if (ModeLink.IsChecked == true) LinkBox.Focus();
    }

    void Hint_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LinkHint.Visibility = LinkBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static void EnterOnly(KeyEventArgs e, Action act) { if (e.Key == Key.Enter) { e.Handled = true; act(); } }
    void SearchBox_KeyDown(object sender, KeyEventArgs e) => EnterOnly(e, Search);
    void LinkBox_KeyDown(object sender, KeyEventArgs e) => EnterOnly(e, UseLink);
    void Search_Click(object sender, RoutedEventArgs e) => Search();
    void UseLink_Click(object sender, RoutedEventArgs e) => UseLink();

    void Search()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return;
        _ = Run(() => _src.SearchFolders(q), "Searching Lark…", found =>
        {
            var group = Node(new Item { Token = "", Name = $"Search results for “{q}”", Kind = "group", Loaded = true }, expandable: false);
            foreach (var f in found) group.Items.Add(Node(new Item { Token = f.Token, Name = f.Name + (f.Owner != null ? $"  ({f.Owner})" : ""), Url = f.Url }));
            if (found.Count == 0) group.Items.Add(Note("No folders found. Try another word, or paste the folder's link."));
            _host ??= HostOf(found.Select(f => f.Url));
            Tree.Items.Insert(0, group);
            group.IsExpanded = true;
            if (found.Count > 0 && group.Items[0] is TreeViewItem first) first.IsSelected = true; else group.IsSelected = true;
            Tree.Focus();
        });
    }

    void UseLink()
    {
        var text = LinkBox.Text.Trim();
        var tok = Config.TokenFromLink(text);
        if (tok.Length == 0) { ShowMessage("That doesn't look like a Lark folder link. Copy it from the address bar or the Share button in Lark."); return; }
        _ = Run(() => _src.FolderName(tok), "Checking the link…", name =>
        {
            var n = Node(new Item { Token = tok, Name = name, Url = text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? text : null });
            Tree.Items.Add(n);
            n.IsSelected = true;
            n.BringIntoView();
            Tree.Focus();
        });
    }

    // ---------------------------------------------------------------- new folder in Lark
    void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Kind: "drive" } parent)
        {
            ShowMessage("First select the Lark folder to create the new one in.");
            return;
        }
        NewFolderPrompt.Text = $"Name of the new folder inside “{parent.Name}”";
        NewFolderName.Text = "";
        NewFolderBar.Visibility = Visibility.Visible;
        NewFolderName.Focus();
    }

    void NewFolderName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; CreateFolder(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CancelNewFolder_Click(sender, e); }
    }

    void CancelNewFolder_Click(object sender, RoutedEventArgs e) { NewFolderBar.Visibility = Visibility.Collapsed; NewFolderButton.Focus(); }
    void CreateFolder_Click(object sender, RoutedEventArgs e) => CreateFolder();

    void CreateFolder()
    {
        var name = NewFolderName.Text.Trim();
        if (name.Length == 0 || Tree.SelectedItem is not TreeViewItem { Tag: Item { Kind: "drive" } parent } node) return;
        NewFolderBar.Visibility = Visibility.Collapsed;
        _ = Run(() => _src.CreateFolder(parent.Token, name), "Creating the folder in Lark…", tok =>
        {
            if (!parent.Loaded) { node.Items.Clear(); parent.Loaded = true; }
            var url = _host != null ? $"https://{_host}/drive/folder/{tok}" : null;
            var child = Node(new Item { Token = tok, Name = name, Url = url, Loaded = true }, expandable: false);
            node.Items.Add(child);
            node.IsExpanded = true;
            child.IsSelected = true;
            child.BringIntoView();
            Tree.Focus();
        });
    }

    // ---------------------------------------------------------------- steps
    void ShowStep()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 3 ? "Add folder" : "Next";
        Dot(Dot1, Num1, Lbl1, 1);
        Dot(Dot2, Num2, Lbl2, 2);
        Dot(Dot3, Num3, Lbl3, 3);
        AutomationProperties.SetName(Steps, $"Step {_step} of 3");
        if (_step == 2) Step2Intro.Text = $"“{CleanName(Selected)}” will appear in a folder on this PC. Choose which one.";
        if (_step == 3) FillSummary();
        UpdateButtons();
    }

    void Dot(Border dot, TextBlock num, TextBlock label, int n)
    {
        var done = n < _step;
        var cur = n == _step;
        Motion.Paint(dot, Border.BackgroundProperty, cur ? Theme.Brush("AccentFill") : done ? Theme.Brush("SuccessBg") : Brushes.Transparent);
        Motion.Paint(dot, Border.BorderBrushProperty, cur ? Brushes.Transparent : done ? Theme.Brush("Success") : Theme.Brush("ControlStroke"));
        num.Style = (Style)FindResource(done ? "IconText" : "CaptionText");
        num.Text = done ? "" : n.ToString();
        if (done) num.FontSize = 11;
        Motion.Paint(num, TextBlock.ForegroundProperty, cur ? Theme.Brush("OnAccent") : done ? Theme.Brush("Success") : Theme.Brush("TextSecondary"));
        Motion.Paint(label, TextBlock.ForegroundProperty, Theme.Brush(cur ? "TextPrimary" : "TextSecondary"));
        label.FontWeight = cur ? FontWeights.SemiBold : FontWeights.Normal;
    }

    static string CleanName(Item? it) => it == null ? "" : Regex.Replace(it.Name, @"  \(.*\)$", "");

    void UpdateButtons()
    {
        if (_closed || NextButton == null) return;
        var used = Selected is { } it && Used(it);
        if (_step == 1)
        {
            NextButton.IsEnabled = Selected != null && !used && !_adding;
            if (Selected is { } s)
            {
                PickedIcon.Text = used ? StatusSummary.GlyphCritical : "";
                PickedIcon.Foreground = Theme.Brush(used ? "Critical" : "Success");
                Picked.Foreground = Theme.Brush("TextPrimary");
                Picked.Text = used ? $"“{CleanName(s)}” is already on this PC." : $"Selected: {CleanName(s)}";
            }
            else
            {
                PickedIcon.Text = "";
                Picked.Text = "Select a folder to continue.";
                Picked.Foreground = Theme.Brush("TextSecondary");
            }
        }
        else if (_step == 2) NextButton.IsEnabled = _pc.Error == null && !_adding;
        else NextButton.IsEnabled = !_adding;
        BackButton.IsEnabled = !_adding;
        CancelButton.IsEnabled = !_adding;
    }

    void GoTo(int step)
    {
        var forward = step > _step;
        _step = step;
        ShowStep();
        // the new step slides in from the right (Next) or the left (Back) while fading, 250 ms
        Motion.Reveal(step switch { 1 => Step1, 2 => Step2, _ => Step3 }, 250, dx: forward ? 32 : -32, strong: true);
        (step switch { 1 => Head1, 2 => Head2, _ => Head3 }).Focus();
        if (step == 2)
        {
            if (!_pcEdited && Selected is { } it) PcBox.Text = Config.DefaultLocalFor(CleanName(it));
            CheckPc();
            PcBox.Focus();
            PcBox.CaretIndex = PcBox.Text.Length;
        }
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && NextButton.IsEnabled) GoTo(2);
        else if (_step == 2 && NextButton.IsEnabled) GoTo(3);
        else if (_step == 3) _ = AddAsync();
    }

    void Back_Click(object sender, RoutedEventArgs e) { if (!_adding && _step > 1) GoTo(_step - 1); }

    // ---------------------------------------------------------------- step 2: where on this PC
    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose or create a folder", InitialDirectory = Directory.Exists(PcBox.Text) ? PcBox.Text : null };
        if (d.ShowDialog(this) == true) { _pcEdited = true; PcBox.Text = d.FolderName; }
    }

    void PcBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PcBox.IsKeyboardFocused) _pcEdited = true;
        CheckPc();
    }

    /// <summary>The check looks at the disk (it can count a big folder), so it runs in the background and only the newest answer counts.</summary>
    async void CheckPc()
    {
        var id = ++_pcCheckId;
        var path = PcBox.Text;
        var used = Selected is { } it && Used(it);
        if (used) { _pc = ("That Lark folder is already on this PC.", null); }
        else
        {
            await Task.Delay(200);
            if (id != _pcCheckId || _closed) return;
            FootBusy.Visibility = Visibility.Visible;
            FootText.Text = "Checking the folder…";
            var cfg = _m.Settings;
            var r = await Task.Run(() => Ui.CheckPcFolder(path, cfg));
            if (id != _pcCheckId || _closed) return;
            FootBusy.Visibility = _busy > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_busy == 0) FootText.Text = "";
            _pc = r;
        }
        var (err, warn) = _pc;
        PcNote.Background = Theme.Brush(err != null ? "CriticalBg" : warn != null ? "CautionBg" : "Card");
        PcNote.BorderBrush = Theme.Brush("CardStroke");
        PcNoteIcon.Text = err != null ? StatusSummary.GlyphCritical : warn != null ? StatusSummary.GlyphCaution : "";
        PcNoteIcon.Foreground = Theme.Brush(err != null ? "Critical" : warn != null ? "Caution" : "TextSecondary");
        PcNoteText.Text = err ?? warn ?? "The folder is created if it doesn't exist. It also shows in File Explorer's left-hand panel.";
        AutomationProperties.SetHelpText(PcBox, PcNoteText.Text);
        UpdateButtons();
    }

    // ---------------------------------------------------------------- step 3: confirm and add
    void FillSummary()
    {
        var it = Selected;
        SumLark.Text = CleanName(it) + (it is { Kind: "wikispace" or "wikinode" } ? "  (wiki)" : "");
        SumPc.Text = SafeFull(PcBox.Text);
        SumNotes.Children.Clear();
        void Add(string t) => SumNotes.Children.Add(new TextBlock { Text = t, Style = (Style)FindResource("BodyText"), Margin = new Thickness(0, SumNotes.Children.Count == 0 ? 0 : 6, 0, 0) });
        if (it is { Kind: not "drive" }) Add("Wiki spaces are read-only on this PC for now: you can browse and open everything, but changes are saved to My folders › “Lark Cloud – changes not saved” instead of the wiki.");
        if (_pc.Warning != null) Add(_pc.Warning);
        SumNote.Visibility = SumNotes.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SumNote.Background = Theme.Brush("CautionBg");
        SumNote.BorderBrush = Theme.Brush("CardStroke");
        SumNoteIcon.Text = StatusSummary.GlyphCaution;
        SumNoteIcon.Foreground = Theme.Brush("Caution");
        AddError.Visibility = Visibility.Collapsed;
    }

    static string SafeFull(string p)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim())); } catch { return p.Trim(); }
    }

    async Task AddAsync()
    {
        if (Selected is not { } it) return;
        var url = it.Kind switch
        {
            "wikispace" => Pair.WikiUrl(_host, it.Space!, null),
            "wikinode" => Pair.WikiUrl(_host, it.Space!, it.Token),
            _ => it.Url ?? (_host != null ? $"https://{_host}/drive/folder/{it.Token}" : it.Token),
        };
        var pair = new Pair { FolderUrl = url, FolderName = CleanName(it), LocalFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(PcBox.Text.Trim())) };
        _adding = true;
        AddError.Visibility = Visibility.Collapsed;
        FootBusy.Visibility = Visibility.Visible;
        FootText.Text = "Adding the folder…";
        UpdateButtons();
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(pair.LocalFolder);
                _m.AddPair(pair);
            });
            Log.Info($"Added folder: Lark “{pair.Label}” ↔ {pair.LocalFolder}");
            Added = true;
            _adding = false;
            Ui.Open(pair.LocalFolder);
            Close();
        }
        catch (Exception ex)
        {
            _adding = false;
            if (_closed) return;
            FootBusy.Visibility = Visibility.Collapsed;
            FootText.Text = "";
            AddErrorText.Text = $"Couldn't add the folder: {ex.Message}";
            AddError.Visibility = Visibility.Visible;
            UpdateButtons();
        }
    }

#if DEBUG
    /// <summary>Design preview only: jump to a step with the first folder selected and a path typed in.</summary>
    internal void PreviewStep(int step, string path)
    {
        if (Tree.Items.Count > 0 && Tree.Items[0] is TreeViewItem root && root.Items.Count > 0 && root.Items[0] is TreeViewItem first)
        {
            first.IsSelected = true;
            if (step >= 2) { _pcEdited = true; PcBox.Text = path; GoTo(step); }
        }
    }
#endif
}
