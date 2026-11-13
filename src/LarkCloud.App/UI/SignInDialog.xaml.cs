using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>Sign in with the person's own Lark account (device flow: approve in the browser). Modal; read <see cref="Success"/> afterwards.</summary>
internal sealed partial class SignInDialog : Window
{
    readonly Func<Task<DeviceFlow>> _start;
    readonly Func<DeviceFlow, CancellationToken, Task> _poll;
    readonly Func<string> _userName;
    readonly Action<string> _open;
    CancellationTokenSource _cts = new();
    DeviceFlow? _flow;

    public bool Success { get; private set; }

    public SignInDialog(Func<Task<DeviceFlow>> start, Func<DeviceFlow, CancellationToken, Task> poll, Func<string> userName, Action<string>? open = null)
    {
        _start = start;
        _poll = poll;
        _userName = userName;
        _open = open ?? Ui.Open;
        InitializeComponent();
        Theme.Attach(this, mica: false);
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    // The code is spaced out a little so it reads letter by letter; the name read aloud and the copied text are the plain code.
    static string Spaced(string code) => string.Join(" ", code.Select(c => c.ToString()));

    void SetStatus(string text, string? glyph = null, string? brush = null)
    {
        Status.Text = text;
        Busy.Visibility = glyph == null ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = glyph == null ? Visibility.Collapsed : Visibility.Visible;
        if (glyph != null) { StatusIcon.Text = glyph; StatusIcon.Foreground = Theme.Brush(brush ?? "Success"); }
    }

    async Task RunAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        ErrorBar.Visibility = Visibility.Collapsed;
        StatusRow.Visibility = Visibility.Visible;
        Retry.Visibility = Visibility.Collapsed;
        Open.Visibility = Visibility.Visible;
        Open.IsDefault = true;
        Open.IsEnabled = Copy.IsEnabled = false;
        Code.Text = "…";
        SetStatus("Contacting Lark…");
        try
        {
            _flow = await _start();
            if (ct.IsCancellationRequested) return;
            var code = _flow.UserCode;
            Code.Text = string.IsNullOrEmpty(code) ? "No code needed" : Spaced(code);
            AutomationProperties.SetName(Code, string.IsNullOrEmpty(code) ? "No code needed" : $"Sign-in code {code}");
            Open.IsEnabled = true;
            Copy.IsEnabled = !string.IsNullOrEmpty(code);
            Open.Focus();
            SetStatus("Waiting for you to approve in the browser…");
            _open(_flow.Url);
            await _poll(_flow, ct);
            Success = true;
            SetStatus($"Signed in as {_userName()}.", "");
            Open.IsEnabled = Copy.IsEnabled = false;
            Log.Info($"Signed in as {_userName()}");
            await Task.Delay(800);
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            ErrorText.Text = ex.Message;
            ErrorBar.Visibility = Visibility.Visible;
            StatusRow.Visibility = Visibility.Collapsed;
            Open.Visibility = Visibility.Collapsed;
            Open.IsDefault = false;
            Retry.Visibility = Visibility.Visible;
            Retry.IsDefault = true;
            Retry.Focus();
            Copy.IsEnabled = false;
            Log.Warn($"Sign-in failed: {ex.Message}");
        }
    }

    void Open_Click(object sender, RoutedEventArgs e) { if (_flow != null) _open(_flow.Url); }

    async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync();

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_flow == null) return;
        try { Clipboard.SetText(_flow.UserCode); } catch { return; }       // the clipboard can be busy; the code is still on screen
        CopyGlyph.Text = "";
        CopyText.Text = "Copied";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); CopyGlyph.Text = ""; CopyText.Text = "Copy code"; };
        t.Start();
    }
}
