using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LarkCloud.App.UI;

/// <summary>A page inside the main window. Add a page: create a UserControl implementing this, add an
/// <see cref="AppPage"/> value, a nav item in MainWindow.xaml, and a line in <see cref="MainWindow.Pages"/>.</summary>
internal interface IPage
{
    /// <summary>Called about once a second while the page is shown; update in place (no rebuilding) so nothing flickers.</summary>
    void Refresh();
}

/// <summary>The one window: navigation on the left, a status header, then the current page.</summary>
internal sealed partial class MainWindow : Window
{
    readonly IAppModel _m;
    readonly Dictionary<AppPage, UserControl> _pages;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    StatusSummary _s = null!;
    enum HeaderAction { None, SignIn, Problems, Deletions }
    HeaderAction _action;
    (string, string)? _stateKey;                      // glyph + sentence last shown: the header fades only when this changes
    bool _syncMine, _sawBusy;                         // "Sync now" was pressed here and its sync has not finished
    DateTime _syncClickedAt;

    public AppPage Page { get; private set; } = AppPage.Folders;

    public MainWindow(IAppModel model)
    {
        _m = model;
        InitializeComponent();
        Logo.Source = TrayIcons.Logo;
        Theme.Attach(this);
        _pages = new()
        {
            [AppPage.Folders] = new FoldersPage(model),
            [AppPage.Problems] = new ProblemsPage(model),
            [AppPage.Settings] = new SettingsPage(model),
        };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _timer.Start(); MoveNavBar(false); };
        NavStack.SizeChanged += (_, _) => MoveNavBar(false);
        Closed += (_, _) => _timer.Stop();
        Navigate(AppPage.Folders);
    }

    public void Navigate(AppPage page)
    {
        Page = page;
        var nav = page switch { AppPage.Problems => NavProblems, AppPage.Settings => NavSettings, _ => NavFolders };
        var changed = PageHost.Content != _pages[page];
        PageHost.Content = _pages[page];
        nav.IsChecked = true;
        Refresh();
        if (!IsLoaded) return;
        MoveNavBar(true);
        if (changed) Motion.Reveal(PageHost, 200, dy: 8, strong: true);      // new page settles up 8 DIPs while fading in
    }

    /// <summary>The accent bar slides (250 ms) to the checked nav item instead of jumping.</summary>
    void MoveNavBar(bool animate)
    {
        var nav = Page switch { AppPage.Problems => NavProblems, AppPage.Settings => NavSettings, _ => NavFolders };
        if (!nav.IsLoaded || nav.ActualHeight == 0) return;
        var y = nav.TransformToAncestor(NavStack).Transform(new Point(0, 0)).Y + (nav.ActualHeight - NavBar.Height) / 2;
        NavBar.Visibility = Visibility.Visible;
        Motion.Animate(NavBarT, TranslateTransform.YProperty, y, 250, true, animate: animate);
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        var page = sender == NavProblems ? AppPage.Problems : sender == NavSettings ? AppPage.Settings : AppPage.Folders;
        if (page != Page || PageHost.Content != _pages[page]) Navigate(page);
    }

    /// <summary>Everything on screen follows the live state; values are only set when they change.</summary>
    public void Refresh()
    {
        if (_pages == null) return;
        _s = StatusSummary.Of(_m);
        var s = _s;

        var onSettings = Page == AppPage.Settings;
        HeaderArea.Visibility = Banners.Visibility = onSettings ? Visibility.Collapsed : Visibility.Visible;

        if (_stateKey != (s.Glyph, s.Sentence))
        {
            if (_stateKey != null) { Motion.Flash(StateGlyph); Motion.Flash(Sentence); }      // the state changed: fade the new one in
            _stateKey = (s.Glyph, s.Sentence);
        }
        StateGlyph.Text = s.Glyph;
        Motion.SetSpin(StateGlyph, s.Tone == Tone.Busy);
        StateGlyph.Foreground = Theme.Brush(s.Tone switch
        {
            Tone.Ok => "Success", Tone.Busy => "Accent", Tone.Caution => "Caution", Tone.Critical => "Critical", _ => "TextSecondary",
        });
        Sentence.Text = s.Sentence;
        Detail.Text = s.Detail;
        SyncNow.IsEnabled = _m.SignedIn && s.HasFolders && !s.AllPaused;
        // The Sync now icon turns while the sync that button started is running; a sync nobody asked for spins only the header.
        if (s.Tone == Tone.Busy) _sawBusy = true;
        else if (_sawBusy || DateTime.UtcNow - _syncClickedAt > TimeSpan.FromSeconds(4)) _syncMine = _sawBusy = false;
        Motion.SetSpin(SyncIcon, _syncMine && s.Tone == Tone.Busy);
        PauseButton.IsEnabled = _m.SignedIn && s.HasFolders;
        PauseGlyph.Text = s.AllPaused ? "\uE768" : "\uE769";
        PauseText.Text = s.AllPaused ? "Resume" : "Pause";

        // The header already says "You're signed out" / "N files can't sync" / "N deletions are waiting": its button is the fix.
        // A banner appears only for something the header doesn't say, and only one: deletions (a decision) before problem files.
        var caution = s.Tone == Tone.Caution;
        var headerProblems = caution && s.ProblemFiles > 0;
        var headerDeletions = caution && s.ProblemFiles == 0 && s.Deletions > 0;
        _action = !_m.SignedIn && _m.Configured ? HeaderAction.SignIn
            : headerProblems && Page != AppPage.Problems ? HeaderAction.Problems
            : headerDeletions ? HeaderAction.Deletions : HeaderAction.None;
        Motion.SetVisible(ActionButton, _action != HeaderAction.None);
        ActionButton.Content = _action == HeaderAction.SignIn ? "Sign in" : "Review";
        AutomationProperties.SetName(ActionButton, _action switch { HeaderAction.SignIn => "Sign in", HeaderAction.Problems => "Review files that can't sync", _ => "Review deletions" });

        var showDeletions = _m.SignedIn && s.Deletions > 0 && !headerDeletions;
        var showProblems = _m.SignedIn && s.ProblemFiles > 0 && !headerProblems && !showDeletions && Page != AppPage.Problems;
        Motion.SetVisible(DeletionsBar, showDeletions);
        DeletionsText.Text = $"{s.Deletions:N0} deletion{(s.Deletions == 1 ? " is" : "s are")} waiting for you.";
        Motion.SetVisible(ProblemsBar, showProblems);
        ProblemsText.Text = $"{s.ProblemFiles:N0} file{(s.ProblemFiles == 1 ? "" : "s")} can't sync.";

        ProblemsBadge.Visibility = s.ProblemFiles > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProblemsCount.Text = s.ProblemFiles > 99 ? "99+" : s.ProblemFiles.ToString();
        AutomationProperties.SetName(NavProblems, s.ProblemFiles > 0 ? $"Files that can't sync, {s.ProblemFiles}" : "Files that can't sync");

        // account
        var signedIn = _m.SignedIn;
        var name = _m.UserName.Trim();
        AccountLine.Text = signedIn ? "Signed in as" : "Not signed in";
        AccountName.Text = signedIn ? (name.Length > 0 ? name : "you") : "";
        AccountName.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        AccountButton.Content = signedIn ? "Sign out" : "Sign in";
        Avatar.SetResourceReference(Border.BackgroundProperty, signedIn && name.Length > 0 ? "AccentFillBrush" : "SelectedBrush");
        AvatarInitial.Text = signedIn && name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "";
        AvatarGlyph.Visibility = signedIn && name.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        ((IPage)_pages[Page]).Refresh();
    }

    void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        _m.SyncAllNow();
        _syncMine = true; _sawBusy = false; _syncClickedAt = DateTime.UtcNow;
        Refresh();
        Motion.SpinOnce(SyncIcon);          // an acknowledging turn even if nothing turns out to need syncing
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        var pause = !_s.AllPaused;
        // "Pause" pauses everything; "Resume" is shown only when everything is paused.
        foreach (var p in _m.Pairs()) _m.SetPaused(p.Id, pause);
        Refresh();
    }

    void Action_Click(object sender, RoutedEventArgs e)
    {
        switch (_action)
        {
            case HeaderAction.SignIn: Account_Click(sender, e); break;
            case HeaderAction.Problems: ReviewProblems_Click(sender, e); break;
            case HeaderAction.Deletions: ReviewDeletions_Click(sender, e); break;
        }
    }

    void Account_Click(object sender, RoutedEventArgs e) { _m.SignInOrOut(); Refresh(); }
    void ReviewProblems_Click(object sender, RoutedEventArgs e) => ShowProblems(null);

    /// <summary>Open the Files that can't sync page, for one folder or all of them.</summary>
    public void ShowProblems(string? pairId)
    {
        ((ProblemsPage)_pages[AppPage.Problems]).SetFilter(pairId);
        Navigate(AppPage.Problems);
    }
    void ReviewDeletions_Click(object sender, RoutedEventArgs e) { if (_s.DeletionsPairId is { } id) _m.ShowDeletions(id); }

    /// <summary>Used by the debug preview to open a row's menu for screenshots.</summary>
    internal FoldersPage FoldersPage => (FoldersPage)_pages[AppPage.Folders];
    internal ProblemsPage ProblemsPage => (ProblemsPage)_pages[AppPage.Problems];
    internal SettingsPage SettingsPage => (SettingsPage)_pages[AppPage.Settings];
}
