using System.Net.Http;
using System.Net.Http.Headers;
using LarkCloud.Core;

namespace LarkCloud.App.Service;

/// <summary>Checks the public GitHub releases for a newer LarkCloud.exe, downloads it next to the running exe and swaps it in.
/// The running copy keeps going; the new one is used after a restart. Never throws; everything is logged.</summary>
internal sealed class Updater : IDisposable
{
    const string Api = "https://api.github.com/repos/LLRR26/lark-local-sync/releases/latest";
    static readonly TimeSpan First = TimeSpan.FromMinutes(1), Every = TimeSpan.FromHours(6);

    readonly CancellationTokenSource _stop = new();
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    readonly string _exe = Environment.ProcessPath ?? "";

    /// <summary>Version that is installed and waiting for a restart; null until then.</summary>
    public string? Installed { get; private set; }
    public event Action<string>? Ready;

    public void Start()
    {
#if DEBUG
        return;
#else
        if (!string.Equals(Path.GetFileName(_exe), "LarkCloud.exe", StringComparison.OrdinalIgnoreCase)) return;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LarkCloud/{AppPaths.Version}");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        foreach (var ext in new[] { ".old", ".new" })
            try { File.Delete(_exe + ext); } catch { }
        _ = Task.Run(Loop);
#endif
    }

    async Task Loop()
    {
        try
        {
            await Task.Delay(First, _stop.Token);
            while (Installed == null)
            {
                try { await CheckOnce(); }
                catch (Exception e) when (!_stop.IsCancellationRequested) { Log.Error($"Update check failed: {e.Message}"); }
                await Task.Delay(Every, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task CheckOnce()
    {
        using var resp = await _http.GetAsync(Api, _stop.Token);
        if (!resp.IsSuccessStatusCode) { Log.Info($"Update check: GitHub answered {(int)resp.StatusCode}"); return; }
        if (Update.Newer(await resp.Content.ReadAsStringAsync(_stop.Token), AppPaths.Version) is not var (version, url, size)) return;

        Log.Info($"Update {version} found; downloading");
        var tmp = _exe + ".new";
        try
        {
            using (var r = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _stop.Token))
            {
                r.EnsureSuccessStatusCode();
                await using var src = await r.Content.ReadAsStreamAsync(_stop.Token);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, _stop.Token);
            }
            if (!LooksRight(tmp, size)) { Log.Error("Update download is incomplete or not an exe; will retry later"); File.Delete(tmp); return; }
            Swap(tmp);
        }
        catch (UnauthorizedAccessException e) { Log.Error($"Can't write next to {_exe}, skipping update: {e.Message}"); Cleanup(tmp); return; }
        catch { Cleanup(tmp); throw; }
        Installed = version;
        Log.Info($"Update {version} installed; restart to use it");
        try { Ready?.Invoke(version); } catch (Exception e) { Log.Error($"Update notice failed: {e.Message}"); }
    }

    static bool LooksRight(string path, long size)
    {
        using var f = File.OpenRead(path);
        return f.Length == size && f.ReadByte() == 'M' && f.ReadByte() == 'Z';
    }

    static void Cleanup(string path) { try { File.Delete(path); } catch { } }

    void Swap(string tmp)
    {
        var old = _exe + ".old";
        File.Move(_exe, old, true);              // renaming a running exe is allowed
        try { File.Move(tmp, _exe); }
        catch { File.Move(old, _exe, true); throw; }   // roll back
    }

    public void Dispose() { _stop.Cancel(); _http.Dispose(); }
}
