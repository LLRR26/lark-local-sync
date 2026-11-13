using System.Windows.Controls;

namespace LarkCloud.App.UI;

/// <summary>A small "working on it" ring. The turning is <see cref="Motion.SpinProperty"/> in the XAML: it runs only while the ring is
/// shown and Windows animation effects are on.</summary>
internal sealed partial class Spinner : UserControl
{
    public Spinner() => InitializeComponent();
}
