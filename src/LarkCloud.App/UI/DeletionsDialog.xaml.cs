using System.ComponentModel;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>A big batch of deletions is held back until someone decides here: delete them, or put them back.</summary>
internal sealed partial class DeletionsDialog : Window
{
    sealed record Row(string Path, string Where);

    const string InLarkGroup = "Will be deleted in Lark", OnPcGroup = "Will be deleted on this PC";
    const int SearchFrom = 20;

    readonly IAppModel _m;
    readonly string _pairId;
    readonly string? _approval;
    readonly int _here;
    readonly ICollectionView _view;

    public DeletionsDialog(IAppModel model, string pairId)
    {
        _m = model;
        _pairId = pairId;
        InitializeComponent();
        Theme.Attach(this, mica: false);

        var pair = model.Pairs().FirstOrDefault(p => p.Id == pairId);
        var all = model.StatusOf(pairId).PendingDeletes;
        _approval = all.FirstOrDefault()?.Approval;
        var inLark = all.Count(d => d.InLark);
        _here = all.Count - inLark;
        var n = all.Count;

        Heading.Text = $"{n:N0} deletion{(n == 1 ? " is" : "s are")} waiting";
        FolderLine.Text = pair?.Label ?? "";
        FolderLine.Visibility = pair == null ? Visibility.Collapsed : Visibility.Visible;
        if (inLark > 0) Effects.Children.Add(EffectRow("", $"{inLark:N0} item{(inLark == 1 ? " was" : "s were")} deleted on this PC. Deleting them moves them to Lark's trash, for everyone who can see this folder."));
        if (_here > 0) Effects.Children.Add(EffectRow("", $"{_here:N0} item{(_here == 1 ? " was" : "s were")} deleted in Lark. Deleting them removes them from this PC too."));

        var rows = all.OrderBy(d => d.Path, StringComparer.CurrentCultureIgnoreCase).Select(d => new Row(d.Path, d.InLark ? InLarkGroup : OnPcGroup)).ToList();
        _view = CollectionViewSource.GetDefaultView(rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Where)));
        List.ItemsSource = _view;
        if (rows.Count > SearchFrom) SearchRow.Visibility = Visibility.Visible;
        UpdateShown(rows.Count, rows.Count);

        DeleteButton.Content = $"Delete {n:N0} item{(n == 1 ? "" : "s")}";
        AutomationProperties.SetName(DeleteButton, $"Delete {n:N0} item{(n == 1 ? "" : "s")}, for good");
        Loaded += (_, _) => RestoreButton.Focus();       // Enter picks the safe choice
    }

    UIElement EffectRow(string glyph, string text)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Style = (Style)FindResource("IconText"), Text = glyph, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0), Foreground = Theme.Brush("TextSecondary") });
        var t = new TextBlock { Style = (Style)FindResource("BodyText"), Text = text };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    void UpdateShown(int shown, int total)
    {
        Shown.Text = shown == total ? $"{total:N0} items" : $"{shown:N0} of {total:N0}";
        NoMatch.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = Search.Text.Trim();
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Filter = q.Length == 0 ? null : o => ((Row)o).Path.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        UpdateShown(_view.Cast<object>().Count(), ((IEnumerable<Row>)_view.SourceCollection).Count());
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_approval != null) _m.AllowDeletes(_pairId, _approval);
        Close();
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        _m.RestoreDeletes(_pairId);
        Close();
        if (_here > 0)
            ConfirmDialog.Tell(Owner, "Items put back",
                "Items deleted on this PC are shown again from Lark.\n\nFor items deleted in Lark: copies you had downloaded are " +
                "uploaded again; files that were only in the cloud can be restored from Lark's trash.");
    }
}
