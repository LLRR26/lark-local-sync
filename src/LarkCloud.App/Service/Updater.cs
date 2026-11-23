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
    /// <summary>An update was downloaded and swapped in. For UI state only (tray badge); there is no notification.</summary>
    public event Action<string>? Ready;
    /// <summary>Checking started or finished. Raised on any thread.</summary>
    public event Action? Changed;

    readonly SemaphoreSlim _one = new(1, 1);           // one check at a time, background or on demand
    volatile bool _checking;
    DateTime? _lastChecked;
    UpdateCheckResult? _last;

    public UpdateStatus Status => new(_checking, _lastChecked, _last, Installed);

    static bool Available(string exe) =>
#if DEBUG
        false;
#else
        string.Equals(Path.GetFileName(exe), "LarkCloud.exe", StringComparison.OrdinalIgnoreCase);
#endif

    public void Start()
    {
        if (!Available(_exe)) return;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LarkCloud/{AppPaths.Version}");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        foreach (var ext in new[] { ".old", ".new" })
            try { File.Delete(_exe + ext); } catch { }
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        try
        {
            await Task.Delay(First, _stop.Token);
            while (Installed == null)
            {
                await CheckNowAsync();
                await Task.Delay(Every, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Checks now (waits if the background check is running) and says what happened. Never throws.</summary>
    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        if (!Available(_exe)) return new(UpdateOutcome.Unavailable, null, "updates aren't available in this build");
        try { await _one.WaitAsync(_stop.Token); }
        catch (OperationCanceledException) { return new(UpdateOutcome.Failed, null, "the app is closing"); }
        try
        {
            if (Installed != null) return new(UpdateOutcome.Waiting, Installed);   // the running image is now <exe>.old: don't download again
            _checking = true; Changed?.Invoke();
            UpdateCheckResult r;
            try { r = await CheckOnce(); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { r = new(UpdateOutcome.Failed, null, "the app is closing"); }
            catch (TaskCanceledException) { r = new(UpdateOutcome.Failed, null, "GitHub took too long to answer"); }
            catch (HttpRequestException e) { Log.Error($"Update check failed: {e.Message}"); r = new(UpdateOutcome.Failed, null, "no internet connection"); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            { Log.Error($"Can't write next to {_exe}, skipping update: {e.Message}"); r = new(UpdateOutcome.Failed, null, "can't write next to LarkCloud.exe"); }
            catch (Exception e) { Log.Error($"Update check failed: {e.Message}"); r = new(UpdateOutcome.Failed, null, "something went wrong (see the log)"); }
            _lastChecked = DateTime.UtcNow; _last = r; _checking = false;
            Changed?.Invoke();
            return r;
        }
        finally { _one.Release(); }
    }

    async Task<UpdateCheckResult> CheckOnce()
    {
        using var resp = await _http.GetAsync(Api, _stop.Token);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Info($"Update check: GitHub answered {(int)resp.StatusCode}");
            return new(UpdateOutcome.Failed, null, $"GitHub answered {(int)resp.StatusCode} – try again later");
        }
        if (Update.Newer(await resp.Content.ReadAsStringAsync(_stop.Token), AppPaths.Version) is not var (version, url, size)) return new(UpdateOutcome.UpToDate, AppPaths.Version);

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
            if (!LooksRight(tmp, size)) { Log.Error("Update download is incomplete or not an exe; will retry later"); File.Delete(tmp); return new(UpdateOutcome.Failed, null, "the download was incomplete – try again"); }
            Swap(tmp);
        }
        catch { Cleanup(tmp); throw; }
        Installed = version;
        Log.Info($"Update {version} installed; restart to use it");
        try { Ready?.Invoke(version); } catch (Exception e) { Log.Error($"Update notice failed: {e.Message}"); }
        return new(UpdateOutcome.Downloaded, version);
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

internal enum UpdateOutcome { UpToDate, Downloaded, Waiting, Failed, Unavailable }

/// <summary>What a check found. <paramref name="Version"/> is the current version (UpToDate) or the new one (Downloaded, Waiting); <paramref name="Reason"/> is a short plain reason for Failed and Unavailable.</summary>
internal sealed record UpdateCheckResult(UpdateOutcome Outcome, string? Version = null, string? Reason = null);

/// <summary>A snapshot for the UI.</summary>
internal sealed record UpdateStatus(bool Checking, DateTime? LastChecked, UpdateCheckResult? Last, string? Installed);
