using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>Pick subfolders of a synced folder that are checked against Lark every ~30 seconds. Each change is saved and applied at once.</summary>
internal sealed partial class CheckOftenDialog : Window
{
    readonly Config _cfg;
    readonly Pair _pair;
    readonly ObservableCollection<string> _items;

    public CheckOftenDialog(Config cfg, Pair pair)
    {
        _cfg = cfg;
        _pair = pair;
        _items = new(pair.CheckOften);
        InitializeComponent();
        Theme.Attach(this, mica: false);
        Heading.Text = $"Check folders in “{pair.Label}” often";
        List.ItemsSource = _items;
        Update();
    }

    void Update()
    {
        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = List.SelectedItem != null;
    }

    void Save()
    {
        _cfg.SetCheckOften(_pair.Id, _items.ToList());
        Update();
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => Update();

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var root = _pair.LocalFolder.TrimEnd('\\', '/');
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to check often", InitialDirectory = Directory.Exists(root) ? root : null };
        if (d.ShowDialog(this) != true) return;
        var rel = Path.GetRelativePath(root, d.FolderName).Replace('\\', '/');
        if (rel == "." || rel == ".." || rel.StartsWith("../") || Path.IsPathRooted(rel))
        {
            ConfirmDialog.Tell(this, "Choose a folder inside this one",
                rel == "." ? "That is the whole synced folder, which is checked on its usual schedule. Pick a folder inside it."
                           : $"Pick a folder inside “{_pair.Label}” ({root}).");
            return;
        }
        if (_items.Any(x => string.Equals(x, rel, StringComparison.OrdinalIgnoreCase))) return;
        _items.Add(rel);
        Save();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not string s) return;
        _items.Remove(s);
        Save();
    }
}
