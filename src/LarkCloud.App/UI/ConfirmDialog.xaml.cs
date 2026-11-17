using System.Windows;

namespace LarkCloud.App.UI;

/// <summary>A small confirmation whose buttons say what will happen ("Remove", "Free up space"), never "OK".
/// <see cref="Tell"/> is the same window with one button, for things that only need reading.</summary>
internal sealed partial class ConfirmDialog : Window
{
    ConfirmDialog(string heading, string message, string confirmText, bool destructive, bool tellOnly = false, string? input = null)
    {
        InitializeComponent();
        Title = LarkCloud.Core.AppPaths.AppTitle;
        Heading.Text = heading;
        Message.Text = message;
        Yes.Content = confirmText;
        Yes.SetResourceReference(StyleProperty, destructive ? "CriticalButton" : "AccentButton");
        // For destructive actions Enter picks the safe choice.
        Yes.IsDefault = !destructive;
        No.IsDefault = destructive;
        if (tellOnly) { No.Visibility = Visibility.Collapsed; Yes.IsDefault = Yes.IsCancel = true; }
        Theme.Attach(this, mica: false);
        if (input != null) Input.Visibility = Visibility.Visible;
        Input.Text = input ?? "";
        Loaded += (_, _) =>
        {
            if (input != null) { Input.Focus(); Input.SelectAll(); }
            else (destructive ? No : Yes).Focus();
        };
    }

    /// <summary>Asks for one line of text (prefilled); null when cancelled.</summary>
    public static string? AskText(Window? owner, string heading, string message, string confirmText, string initial)
    {
        var d = new ConfirmDialog(heading, message, confirmText, false, input: initial);
        Place(d, owner);
        return d.ShowDialog() == true ? d.Input.Text : null;
    }

    void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    static void Place(ConfirmDialog d, Window? owner)
    {
        if (owner is { IsVisible: true }) d.Owner = owner; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public static bool Ask(Window? owner, string heading, string message, string confirmText, bool destructive = false)
    {
        var d = new ConfirmDialog(heading, message, confirmText, destructive);
        Place(d, owner);
        return d.ShowDialog() == true;
    }

    /// <summary>A message with a single "Got it" button.</summary>
    public static void Tell(Window? owner, string heading, string message, string buttonText = "Got it")
    {
        var d = new ConfirmDialog(heading, message, buttonText, false, tellOnly: true);
        Place(d, owner);
        d.ShowDialog();
    }
}
