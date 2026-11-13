using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using LarkCloud.App.Service;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>One result as the list shows it: a glyph and words for the status (never colour alone), what it means, and what to do.</summary>
internal sealed class CheckVm
{
    public CheckItem Item { get; }
    public CheckVm(CheckItem item) => Item = item;

    public string Name => Item.Name;
    public string Detail => Item.Detail;
    public string? Fix => Item.Fix;
    public string? ForIt => Item.ForIt is { } s ? "For IT: " + s : null;
    public Visibility FixVisibility => Item.Fix == null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ForItVisibility => Item.ForIt == null ? Visibility.Collapsed : Visibility.Visible;

    public string Glyph => Item.Status switch
    {
        CheckStatus.Ok => StatusSummary.GlyphOk, CheckStatus.Note => "", CheckStatus.NeedsPermission => StatusSummary.GlyphCaution, _ => StatusSummary.GlyphCritical,
    };
    public Brush Brush => Theme.Brush(Item.Status switch
    {
        CheckStatus.Ok => "Success", CheckStatus.Note => "TextSecondary", CheckStatus.NeedsPermission => "Caution", _ => "Critical",
    });
    public string Words => Item.Status switch
    {
        CheckStatus.Ok => "Available", CheckStatus.Note => "Good to know", CheckStatus.NeedsPermission => "Needs a permission from IT", _ => "Didn't work",
    };
    public string AutomationName => $"{Name}: {Words}. {Detail}";
}

/// <summary>"Check Lark features": runs the check in the background and lists each feature with what works and what IT must enable.</summary>
internal sealed partial class CheckDialog : Window
{
    readonly Action<Action<CheckItem>> _run;
    readonly string _user;
    readonly ObservableCollection<CheckVm> _rows = new();
    bool _running, _closed;

    /// <param name="run">Does the whole check, blocking, calling the callback once per result (it is called off the UI thread).</param>
    public CheckDialog(Action<Action<CheckItem>> run, string userName)
    {
        _run = run;
        _user = userName;
        InitializeComponent();
        Theme.Attach(this, mica: false);
        Results.ItemsSource = _rows;
        Closed += (_, _) => _closed = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } }

    void Run_Click(object sender, RoutedEventArgs e) => Start();

    internal void Start()
    {
        if (_running) return;
        _running = true;
        _rows.Clear();
        NotRun.Visibility = Visibility.Collapsed;
        RunButton.IsEnabled = CopyButton.IsEnabled = false;
        StatusIcon.Visibility = Visibility.Collapsed;
        Busy.Visibility = Visibility.Visible;
        Summary.Text = "Checking… this takes about a minute.";
        Task.Run(() =>
        {
            try { _run(item => Dispatcher.BeginInvoke(() => Add(item))); }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => Add(new CheckItem(CheckStatus.Failed, "The check", "The check stopped: " + ex.Message))); }
            finally { Dispatcher.BeginInvoke(Finished); }
        });
    }

    void Add(CheckItem item)
    {
        if (_closed) return;
        _rows.Add(new CheckVm(item));
        Scroll.ScrollToEnd();
    }

    void Finished()
    {
        _running = false;
        if (_closed) return;
        Busy.Visibility = Visibility.Collapsed;
        RunButton.IsEnabled = CopyButton.IsEnabled = true;
        RunButton.Content = "Check again";
        var bad = _rows.Count(r => r.Item.Status is CheckStatus.NeedsPermission or CheckStatus.Failed);
        StatusIcon.Visibility = Visibility.Visible;
        StatusIcon.Text = bad == 0 ? StatusSummary.GlyphOk : StatusSummary.GlyphCaution;
        StatusIcon.Foreground = Theme.Brush(bad == 0 ? "Success" : "Caution");
        Summary.Text = bad == 0 ? "Everything checked works." : $"{bad} thing{(bad == 1 ? "" : "s")} need{(bad == 1 ? "s" : "")} attention. Copy the results and send them to IT.";
    }

    /// <summary>Plain text for IT: no icons, one result per line.</summary>
    string AsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppPaths.AppTitle} {AppPaths.Version} · signed in as {_user} · {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (var r in _rows)
        {
            sb.AppendLine($"[{r.Words}] {r.Name}: {r.Detail}");
            if (r.Fix != null) sb.AppendLine("    -> " + r.Fix);
            if (r.ForIt != null) sb.AppendLine("    " + r.ForIt);
        }
        return sb.ToString();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(AsText()); } catch { return; }       // the clipboard can be busy
        Summary.Text = "Copied. Paste the results into a message to IT.";
    }
}
