namespace LarkCloud.Core;

/// <summary>Simple activity log: %LOCALAPPDATA%\LarkCloud\logs\activity.log (rolled at 5 MB).</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _path;
    public static Action<string>? Sink;          // tests / console

    public static void Init(string? dir = null)
    {
        _path = Path.Combine(dir ?? AppPaths.LogDir, "activity.log");
    }

    public static string? FilePath => _path;

    public static void Info(string msg) => Write("INFO   ", msg);
    public static void Warn(string msg) => Write("WARNING", msg);
    public static void Error(string msg) => Write("ERROR  ", msg);

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level} {msg}";
        Sink?.Invoke(line);
        if (_path == null) return;
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { /* logging must never break syncing */ }
        }
    }
}
