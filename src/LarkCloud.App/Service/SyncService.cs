using System.Security.Cryptography;
using LarkCloud.App.CloudFiles;
using LarkCloud.Core;

namespace LarkCloud.App.Service;

public sealed class DpapiProtector : ITokenProtector
{
    public byte[] Protect(byte[] d) => ProtectedData.Protect(d, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] d) => ProtectedData.Unprotect(d, null, DataProtectionScope.CurrentUser);
}

/// <summary>All synced folders together, plus the shared sign-in.</summary>
public sealed class SyncService : IDisposable
{
    public Config Config { get; }
    public LarkAuth Auth { get; }
    public HttpClient Http { get; }
    readonly LarkRequestLimiter _requestLimiter = new();
    readonly Dictionary<string, PairRunner> _runners = new();
    readonly object _lock = new();
    public event Action? StatusChanged;
    public event Action<string>? Notify;
    public event Action? NeedSignIn;

    public SyncService(Config cfg)
    {
        Config = cfg;
        Http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 24,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"LarkCloudDrive/{AppPaths.Version}");
        Auth = new LarkAuth(cfg, new TokenStore(new DpapiProtector()), Http);
    }

    public void Start()
    {
        foreach (var p in Config.GetPairs()) StartRunner(p);
    }

    internal LarkApi CreateApi() => new(Auth, Http, "", limiter: _requestLimiter);

    void StartRunner(Pair p)
    {
        var r = new PairRunner(p, Config, Auth, Http, _requestLimiter);
        r.Changed += () => StatusChanged?.Invoke();
        r.NeedsAttention += m => Notify?.Invoke(m);
        r.NeedSignIn += () => NeedSignIn?.Invoke();
        lock (_lock) _runners[p.Id] = r;
        r.Start();
    }

    public List<PairRunner> Runners { get { lock (_lock) return _runners.Values.ToList(); } }
    public PairRunner? Runner(string id) { lock (_lock) return _runners.GetValueOrDefault(id); }

    public PairStatus StatusOf(string id) => Runner(id)?.Status ?? new PairStatus { State = SyncState.Starting, Message = "Starting…" };

    public void AddPair(Pair p)
    {
        Config.AddPair(p);
        StartRunner(p);
        StatusChanged?.Invoke();
    }

    public void RemovePair(string id)
    {
        PairRunner? r;
        lock (_lock) { r = _runners.GetValueOrDefault(id); _runners.Remove(id); }
        r?.StopAndCleanUp();
        Config.RemovePair(id);
        StatusChanged?.Invoke();
    }

    /// <summary>Rename the folder on this PC (same parent). Nothing changes in Lark. Throws if the move fails; the pair then keeps working at the old path.</summary>
    public void RenamePair(string id, string newLocal)
    {
        var p = Config.GetPairs().First(x => x.Id == id);
        PairRunner? r;
        lock (_lock) { r = _runners.GetValueOrDefault(id); _runners.Remove(id); }
        r?.Dispose();
        try
        {
            SyncRoot.Unregister(p);            // Windows won't rename a registered sync root; the new runner registers it again
            Directory.Move(p.LocalFolder, newLocal);
            p.LocalFolder = newLocal;
            Config.Save();
        }
        finally
        {
            StartRunner(p);
            StatusChanged?.Invoke();
        }
    }

    public void SetPaused(string id, bool paused)
    {
        Config.SetPaused(id, paused);
        Runner(id)?.SetPaused(paused);
        StatusChanged?.Invoke();
    }

    public void SetDocCopies(string id, bool on)
    {
        Config.SetDocCopies(id, on);
        Runner(id)?.SetDocCopies(on);
    }

    /// <summary>The person changed a setting: every folder picks it up before its next pass. Nothing is restarted.</summary>
    public void ApplySettings() { foreach (var r in Runners) r.ApplySettings(); }

    public void SyncAllNow() { foreach (var r in Runners) r.SyncNowByUser(); }

    public void SignedIn() { foreach (var r in Runners) r.SyncNow(); }

    /// <summary>The worst state across all folders, for the tray icon.</summary>
    public (SyncState State, string Message) Combined()
    {
        var all = Runners.Select(r => (r.Pair, S: r.Status)).ToList();
        if (all.Count == 0) return (SyncState.Attention, "No folders yet – click to add one");
        var order = new[] { SyncState.Error, SyncState.Attention, SyncState.Syncing, SyncState.Starting, SyncState.Idle, SyncState.Paused };
        var worst = all.OrderBy(x => Array.IndexOf(order, x.S.State)).First();
        if (all.All(x => x.S.State == SyncState.Paused)) return (SyncState.Paused, "Paused");
        if (worst.S.State == SyncState.Idle) return (SyncState.Idle, all.Count == 1 ? "Up to date" : $"All {all.Count} folders up to date");
        return (worst.S.State, all.Count == 1 ? worst.S.Message : $"{worst.Pair.Label}: {worst.S.Message}");
    }

    public void Dispose()
    {
        foreach (var r in Runners) r.Dispose();
        Http.Dispose();
    }
}
