using System.Collections.Concurrent;
using LarkCloud.App.CloudFiles;
using LarkCloud.Core;

namespace LarkCloud.App.Service;

public enum SyncState { Starting, Idle, Syncing, Attention, Error, Paused }

public sealed class PairStatus
{
    public SyncState State = SyncState.Starting;
    public string Message = "Starting…";
    public DateTime? LastFull;
    public List<(string Rel, string Msg)> Items = new();         // files that can't sync ("" = not about one file)
    public Dictionary<string, string?> UploadHere = new(StringComparer.OrdinalIgnoreCase);
    public List<PendingDelete> PendingDeletes = new();
    public List<(string Rel, string Msg)> Info = new();           // good to know, not a problem
    public Dictionary<string, string?> OpenLinks = new(StringComparer.OrdinalIgnoreCase);
    public bool ViewOnly;
    public int Downloading;
    public PairStatus Clone() => new()
    {
        State = State, Message = Message, LastFull = LastFull, Items = Items.ToList(),
        UploadHere = new(UploadHere, StringComparer.OrdinalIgnoreCase), PendingDeletes = PendingDeletes.ToList(), Downloading = Downloading,
        Info = Info.ToList(), OpenLinks = new(OpenLinks, StringComparer.OrdinalIgnoreCase), ViewOnly = ViewOnly,
    };
}

/// <summary>Keeps one synced folder going: connects it to Windows, reacts to changes on the PC within
/// seconds, checks Lark regularly, and looks after "Always keep on this device" / "Free up space".</summary>
public sealed class PairRunner : IDisposable
{
    public Pair Pair { get; }
    readonly Config _cfg;
    readonly LarkAuth _auth;
    readonly HttpClient _http;
    readonly LarkRequestLimiter _requestLimiter;
    readonly object _lock = new();
    PairStatus _status = new();
    public event Action? Changed;
    public event Action<string>? NeedsAttention;                  // balloon text
    public event Action? NeedSignIn;

    LarkApi? _api;
    RemoteFeatures? _remote;
    EngineOptions? _opts;
    volatile IgnoreRules? _ignore;
    bool _settingsDirty;
    CfLocal? _local;
    StateDb? _state;
    CloudEngine? _engine;
    SyncRoot? _root;
    FileSystemWatcher? _watch, _attrWatch;

    readonly Thread _thread, _maintThread;
    readonly AutoResetEvent _wake = new(false), _maintWake = new(false);
    readonly CancellationTokenSource _shutdown = new();
    volatile bool _stop;
    bool _fullRequested = true, _restore, _retryFailed, _freeAll;
    string? _approvedDeletes;
    readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    DateTime _lastChange = DateTime.MinValue, _nextFull = DateTime.MinValue, _recheckAt = DateTime.MaxValue;
    readonly ConcurrentQueue<string> _maintQueue = new();
    readonly ConcurrentQueue<string> _hydrateQueue = new();
    DateTime _lastSweep = DateTime.MinValue;
    bool _signInWarned;
    bool _fullPaused;                 // a full pass stopped half-way to let a quick pass run; it carries on afterwards
    bool _manual;                     // the next full pass was asked for by the person ("Sync now")
    volatile bool _showProgress;      // this pass shows "Checking Lark…" even after the first check

    public PairRunner(Pair pair, Config cfg, LarkAuth auth, HttpClient http, LarkRequestLimiter requestLimiter)
    {
        Pair = pair;
        _cfg = cfg;
        _auth = auth;
        _http = http;
        _requestLimiter = requestLimiter;
        _thread = new Thread(Loop) { IsBackground = true, Name = "sync " + pair.Id };
        _maintThread = new Thread(Maintenance) { IsBackground = true, Name = "files " + pair.Id };
    }

    public PairStatus Status { get { lock (_lock) return _status.Clone(); } }

    void SetStatus(SyncState s, string msg)
    {
        lock (_lock) { _status.State = s; _status.Message = msg; _status.Downloading = _root?.ActiveDownloads ?? 0; }
        Changed?.Invoke();
    }

    public void Start()
    {
        _thread.Start();
        _maintThread.Start();
    }

    // ---------------------------------------------------------------- commands from the UI
    public void SyncNow() { lock (_lock) _fullRequested = true; _wake.Set(); }
    /// <summary>"Sync now" clicked: same full check, but its progress is shown instead of running quietly.</summary>
    public void SyncNowByUser() { lock (_lock) { _fullRequested = true; _manual = true; } _wake.Set(); }
    public void RetryFailed() { lock (_lock) { _retryFailed = true; _fullRequested = true; } _wake.Set(); }
    public void AllowDeletes(string approval) { lock (_lock) { _approvedDeletes = approval; _fullRequested = true; } _wake.Set(); }
    public void RestoreDeletes() { lock (_lock) { _restore = true; _fullRequested = true; } _wake.Set(); }
    public void FreeUpSpaceNow() { _freeAll = true; _maintWake.Set(); }
    public void SetPaused(bool paused) { Pair.Paused = paused; if (!paused) SyncNow(); else _wake.Set(); }
    public string? LarkFolderToken => _api?.RootToken;

    /// <summary>Turn read-only offline copies of this folder's Lark docs on or off.</summary>
    public void SetDocCopies(bool on)
    {
        Pair.DocCopies = on;
        if (_opts != null) _opts.DocCopies = on;
        SyncNow();
    }

    /// <summary>Settings changed (see Config): picked up by the sync loop between two passes, never in the middle of one,
    /// so a pass always sees one consistent set of options and transfers already running are left alone.</summary>
    public void ApplySettings() { lock (_lock) _settingsDirty = true; _wake.Set(); }

    void ApplyPendingSettings()
    {
        lock (_lock)
        {
            if (!_settingsDirty) return;
            _settingsDirty = false;
            if (_opts == null) return;                                // not set up yet: Setup reads the current values
            _opts.MaxDeletes = _cfg.MaxDeletes;
            _opts.ConfirmMassDeletes = _cfg.ConfirmMassDeletes;
            _opts.MaxTransfers = _cfg.MaxTransfers;
            _opts.DocCopyFormat = _cfg.DocCopyFormat;
            _ignore = _opts.Ignore = new IgnoreRules(_cfg.ExtraIgnore);
            _fullRequested = true;                                    // look at everything once with the new rules
        }
    }

    // ---------------------------------------------------------------- setup
    bool Setup()
    {
        if (_engine != null) return true;
        try
        {
            if (string.IsNullOrEmpty(Pair.LocalFolder)) throw new IOException("No folder on this PC was chosen.");
            var dbPath = Path.Combine(AppPaths.PairDir(Pair.Id), "state.db");
            if (!Directory.Exists(Pair.LocalFolder))
            {
                if (File.Exists(dbPath))
                    throw new DirectoryNotFoundException($"The synced folder “{Pair.LocalFolder}” is missing. Move it back to resume syncing.");
                Directory.CreateDirectory(Pair.LocalFolder);
            }
            var drive = new DriveInfo(Path.GetPathRoot(Pair.LocalFolder)!);
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"“{Pair.LocalFolder}” is on a {drive.DriveFormat} drive. Files on demand only works on NTFS drives (like C:).");
            _api = new LarkApi(_auth, _http, Pair.IsWiki ? "" : Pair.FolderToken, lifetime: _shutdown.Token, limiter: _requestLimiter);
            _local = new CfLocal(Pair.LocalFolder);
            LoadOpened();
            _state = new StateDb(dbPath);
            IRemoteDrive inner = Pair.IsWiki ? new WikiRemote(_api, Pair.WikiSpace!, Pair.WikiNode, Pair.TenantHost) : _api;
            var st = _state;
            _remote = new RemoteFeatures(inner, _api, st.GetMeta, st.SetMeta);
            var idx = new RemoteIndex(_remote, 8);
            _opts = new EngineOptions
            {
                MaxDeletes = _cfg.MaxDeletes, ConfirmMassDeletes = _cfg.ConfirmMassDeletes, DeleteWins = _cfg.DeleteWins, MaxTransfers = _cfg.MaxTransfers, TenantHost = Pair.TenantHost,
                Ignore = _ignore = new IgnoreRules(_cfg.ExtraIgnore), Settle = TimeSpan.FromSeconds(_cfg.SettleSeconds),
                ZipBlocked = _cfg.ZipBlockedFiles, DocCopies = Pair.DocCopies, DocCopyFormat = _cfg.DocCopyFormat, Label = Pair.Label,
                Cancellation = _shutdown.Token,
            };
            _engine = new CloudEngine(_remote, _local, _state, idx, _opts);
            _engine.Progress = OnProgress;
            SyncRoot.Register(Pair);
            _root = new SyncRoot(Pair, _local, () => _remote!);
            _root.StaleFileOpened += SyncNow;
            _root.FetchFailed += (rel, msg) => NeedsAttention?.Invoke($"Couldn't open “{Path.GetFileName(rel)}”: {msg}");
            _root.Connect();
            StartWatchers();
            return true;
        }
        catch (NeedLoginException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"{Pair.Label}: can't start: {ex}");
            SetStatus(SyncState.Error, ex.Message);
            _watch?.Dispose();
            _attrWatch?.Dispose();
            _root?.Dispose();
            _root = null;
            _engine = null;
            _state?.Dispose();
            _state = null;
            return false;
        }
    }

    void StartWatchers()
    {
        _watch = new FileSystemWatcher(Pair.LocalFolder)
        {
            IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void Note(string full)
        {
            if (full.Length <= Pair.LocalFolder.Length) return;
            var rel = full[(Pair.LocalFolder.TrimEnd('\\').Length + 1)..].Replace('\\', '/');
            if (_ignore!.IsIgnored(rel)) return;
            lock (_lock) { _changed.Add(rel); _lastChange = DateTime.UtcNow; }
            _wake.Set();
        }
        _watch.Created += (_, e) => Note(e.FullPath);
        _watch.Changed += (_, e) => Note(e.FullPath);
        _watch.Deleted += (_, e) => Note(e.FullPath);
        _watch.Renamed += (_, e) => { Note(e.OldFullPath); Note(e.FullPath); };
        _watch.Error += (_, _) => SyncNow();                          // too many changes at once: look at everything
        _watch.EnableRaisingEvents = true;

        _attrWatch = new FileSystemWatcher(Pair.LocalFolder)
        {
            IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, NotifyFilter = NotifyFilters.Attributes,
        };
        _attrWatch.Changed += (_, e) => { _maintQueue.Enqueue(e.FullPath); _maintWake.Set(); };
        _attrWatch.Error += (_, _) => { _lastSweep = DateTime.MinValue; _maintWake.Set(); };
        _attrWatch.EnableRaisingEvents = true;
    }

    void OnProgress(string msg)
    {
        bool first;
        lock (_lock) first = _status.LastFull == null;
        // Routine checks of Lark stay quiet once the folder has been checked once.
        if (msg.StartsWith("Checking Lark") && !first && !_showProgress) return;
        SetStatus(SyncState.Syncing, msg);
    }

    // ---------------------------------------------------------------- the sync loop
    void Loop()
    {
        var failures = 0;
        while (!_stop)
        {
            if (Pair.Paused)
            {
                SetStatus(SyncState.Paused, "Paused");
                _wake.WaitOne(TimeSpan.FromSeconds(5));
                continue;
            }
            if (!_auth.SignedIn)
            {
                SetStatus(SyncState.Error, "Sign in to Lark to sync");
                _wake.WaitOne(TimeSpan.FromSeconds(5));
                continue;
            }
            try
            {
                if (!Setup()) { _wake.WaitOne(TimeSpan.FromMinutes(1)); continue; }
                ApplyPendingSettings();
                var now = DateTime.UtcNow;
                bool full, restore = false, retry = false, manual = false;
                string? approval = null;
                List<string>? changed = null;
                TimeSpan? wait = null;
                lock (_lock)
                {
                    full = _fullRequested || now >= _nextFull;
                    var settled = _changed.Count > 0 && now - _lastChange >= TimeSpan.FromSeconds(_cfg.SettleSeconds);
                    // A paused full check waits while the quick pass for the settled changes runs (unless someone asked
                    // for something that needs the full pass: deletions, restore, retry).
                    var quickFirst = full && _fullPaused && settled && _approvedDeletes == null && !_restore && !_retryFailed && _engine!.Index.Complete;
                    if (quickFirst) full = false;
                    if (!full && !settled && now < _recheckAt)
                    {
                        var until = _nextFull;
                        if (_changed.Count > 0) until = Min(until, _lastChange.AddSeconds(_cfg.SettleSeconds));
                        until = Min(until, _recheckAt);
                        wait = until - now;
                    }
                    else
                    {
                        if (!full) changed = _changed.ToList();
                        _changed.Clear();
                        _recheckAt = DateTime.MaxValue;
                        (approval, restore, retry) = (_approvedDeletes, _restore, _retryFailed);
                        manual = full && _manual;
                        if (!quickFirst)
                        {
                            _fullRequested = _restore = _retryFailed = _manual = false;
                            _approvedDeletes = null;
                        }
                        if (full) _fullPaused = false;
                    }
                }
                if (wait is TimeSpan w)
                {
                    _wake.WaitOne(w < TimeSpan.Zero ? TimeSpan.Zero : w > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : w);
                    continue;
                }
                if (retry) _engine!.RetryFailedNow();
                if (restore)
                {
                    _engine!.RestorePending(Status.PendingDeletes);
                    lock (_lock) _status.PendingDeletes.Clear();
                }
                var t0 = DateTime.UtcNow;
                _showProgress = manual;
                if (manual) SetStatus(SyncState.Syncing, "Checking for changes…");
                SyncResult res;
                // A full check may pause when quick work is ready (never when it has to apply deletions / restore).
                Func<bool>? yield = full && approval == null && !restore ? QuickWorkReady : null;
                try { res = _engine!.Run(approvedDeletes: approval, changed: full ? null : changed, shouldYield: yield); }
                finally { _showProgress = false; }
                var took = DateTime.UtcNow - t0;
                failures = 0;
                _signInWarned = false;
                if (res.Interrupted)
                {
                    lock (_lock) { _fullRequested = true; _fullPaused = true; if (manual) _manual = true; }
                    continue;                                             // nothing was decided: the quick pass runs, then this carries on
                }
                AfterPass(res, took);
            }
            catch (Exception) when (_stop) { break; }
            catch (NeedLoginException ex)
            {
                Log.Warn($"{Pair.Label}: {ex.Message}");
                SetStatus(SyncState.Error, "Sign in to Lark again to keep syncing");
                if (!_signInWarned) { _signInWarned = true; NeedSignIn?.Invoke(); }
                _wake.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                failures++;
                var wait = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(failures, 5))));
                Log.Error($"{Pair.Label}: sync pass failed: {ex}");
                _engine?.Index.Invalidate();
                var offline = ex is HttpRequestException or LarkUnreachableException
                              || ex is AggregateException ag && ag.Flatten().InnerExceptions.All(i => i is HttpRequestException or LarkUnreachableException);
                SetStatus(SyncState.Error, offline ? "Can't reach Lark – will try again shortly" : $"Problem: {ex.Message} – will try again");
                if (offline) failures = Math.Min(failures, 2);         // network blips: retry within a minute or so
                lock (_lock) _fullRequested = true;
                _wake.WaitOne(wait);
            }
        }
    }

    bool QuickWorkReady()
    {
        lock (_lock) return _changed.Count > 0 && DateTime.UtcNow - _lastChange >= TimeSpan.FromSeconds(_cfg.SettleSeconds);
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    void AfterPass(SyncResult res, TimeSpan took)
    {
        var now = DateTime.UtcNow;
        if (res.Full)
        {
            var folders = Math.Max(1, _engine!.Index.FolderCount);
            var interval = TimeSpan.FromSeconds(Math.Clamp(60.0 * Math.Ceiling(folders / 100.0), 60, 900));
            var gap = TimeSpan.FromTicks(Math.Max(interval.Ticks, took.Ticks * 3));
            lock (_lock) { _nextFull = now + gap; _status.LastFull = now; }
        }
        if (_engine!.BusyFilesWaiting) lock (_lock) _recheckAt = now.AddSeconds(_cfg.SettleSeconds + 1);
        if (_engine.ConfirmDeletesSoon) lock (_lock) _nextFull = now.AddSeconds(20);
        foreach (var p in _engine.ToHydrate) _hydrateQueue.Enqueue(p);
        if (!_hydrateQueue.IsEmpty) _maintWake.Set();

        var items = res.Failed.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key).ToList();
        items.AddRange(res.Notes.Distinct().Select(n => ("", n)));
        lock (_lock)
        {
            _status.Items = items;
            _status.UploadHere = new(res.UploadHere, StringComparer.OrdinalIgnoreCase);
            _status.OpenLinks = new(res.OpenLinks, StringComparer.OrdinalIgnoreCase);
            _status.Info = res.Info.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key).ToList();
            if (res.Full) _status.ViewOnly = res.ViewOnly;
            if (res.PendingDeletes.Count > 0 || res.Full) _status.PendingDeletes = res.PendingDeletes.ToList();
        }
        if (res.Changed) Log.Info($"{Pair.Label}: {res.Summary()} ({took.TotalSeconds:0.0}s)");
        if (res.Conflicts.Count > 0)
            NeedsAttention?.Invoke($"{res.Conflicts.Count} file(s) in “{Pair.Label}” were changed here and in Lark. Both versions were kept.");

        var st = Status;
        if (st.PendingDeletes.Count > 0)
            SetStatus(SyncState.Attention, $"{st.PendingDeletes.Count:N0} deletions waiting for you to confirm");
        else if (res.Failed.Count > 0)
            SetStatus(SyncState.Attention, $"{res.Failed.Count:N0} file{(res.Failed.Count == 1 ? "" : "s")} can't sync – click to see which");
        else
            SetStatus(SyncState.Idle, "Up to date");
    }

    // ---------------------------------------------------------------- pinning, freeing up space
    void Maintenance()
    {
        while (!_stop)
        {
            _maintWake.WaitOne(TimeSpan.FromMinutes(10));
            if (_stop || _local == null || Pair.Paused) continue;
            try
            {
                while (!_stop && _hydrateQueue.TryDequeue(out var rel)) Hydrate(rel);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (_maintQueue.TryDequeue(out var full)) paths.Add(full);
                foreach (var full in paths) CheckPin(full);
                var freeAll = _freeAll;
                _freeAll = false;
                if (freeAll || DateTime.UtcNow - _lastSweep > TimeSpan.FromHours(1))
                {
                    _lastSweep = DateTime.UtcNow;
                    Sweep(freeAll);
                }
            }
            catch (Exception ex) { Log.Warn($"{Pair.Label}: housekeeping: {ex.Message}"); }
        }
    }

    string RelOf(string full) => full.Length <= Pair.LocalFolder.TrimEnd('\\').Length ? "" : full[(Pair.LocalFolder.TrimEnd('\\').Length + 1)..].Replace('\\', '/');

    void CheckPin(string full)
    {
        var rel = RelOf(full);
        if (rel.Length == 0) return;
        var it = _local!.Stat(rel);
        if (it == null) return;
        if (it.IsDir)
        {
            // Pinning/unpinning a folder applies to everything inside it.
            if (it.Pinned || it.Unpinned)
                foreach (var child in _local.Scan().Where(c => !c.IsDir && PathUtil.IsUnder(c.Path, rel)))
                    Apply(child, it.Pinned);
            return;
        }
        if (it.Pinned || it.Unpinned) Apply(it, it.Pinned);
    }

    void Apply(LocalItem it, bool keep)
    {
        if (!it.IsPlaceholder) return;
        if (keep && !it.OnDisk) Hydrate(it.Path);
        else if (!keep && it.OnDisk && it.InSync) Dehydrate(it.Path);
    }

    void Hydrate(string rel)
    {
        if (_stop) return;
        try { _local!.Hydrate(rel, _shutdown.Token); Log.Info($"Kept on this device: {rel}"); }
        catch (Exception ex) { Log.Warn($"Couldn't download {rel}: {ex.Message}"); }
    }

    void Dehydrate(string rel)
    {
        if (_stop) return;
        try { _local!.Dehydrate(rel); Log.Info($"Freed up space: {rel}"); }
        catch (IOException) { /* open in an app right now: next time */ }
        catch (Exception ex) { Log.Warn($"Couldn't free up space for {rel}: {ex.Message}"); }
    }

    /// <summary>Pinned files that aren't downloaded yet are fetched; unpinned / long-unused ones are freed.</summary>
    void Sweep(bool freeAll)
    {
        var cutoff = _cfg.FreeUpAfterDays > 0 ? DateTime.UtcNow.AddDays(-_cfg.FreeUpAfterDays).Ticks : long.MinValue;
        var freed = 0;
        foreach (var it in _local!.Scan().Where(i => !i.IsDir && i.IsPlaceholder))
        {
            if (_stop) return;
            if (it.Pinned && !it.OnDisk) Hydrate(it.Path);
            else if (it.OnDisk && it.InSync && !it.Pinned && (freeAll || it.Unpinned || it.LastUse < cutoff))
            {
                Dehydrate(it.Path);
                freed++;
            }
        }
        if (freed > 0) Log.Info($"{Pair.Label}: freed up space for {freed:N0} file(s)");
        SaveOpened();
    }

    // When each file was last downloaded on open, so "free up space after N days" counts from then
    // (Windows often doesn't record when a file was last opened).
    string OpenedPath => Path.Combine(AppPaths.PairDir(Pair.Id), "opened.txt");

    void LoadOpened()
    {
        try
        {
            if (!File.Exists(OpenedPath)) return;
            foreach (var line in File.ReadAllLines(OpenedPath))
            {
                var tab = line.IndexOf('\t');
                if (tab > 0 && long.TryParse(line[..tab], out var ticks))
                    _local!.HydratedAt[line[(tab + 1)..]] = new DateTime(ticks, DateTimeKind.Utc);
            }
        }
        catch (Exception ex) { Log.Warn($"{Pair.Label}: {ex.Message}"); }
    }

    void SaveOpened()
    {
        if (_local == null) return;
        try { File.WriteAllLines(OpenedPath, _local.HydratedAt.Select(kv => $"{kv.Value.Ticks}\t{kv.Key}")); }
        catch (Exception ex) { Log.Warn($"{Pair.Label}: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- stopping
    /// <summary>Stop syncing this folder for good: downloaded files stay as normal files, cloud-only ones are removed.</summary>
    public void StopAndCleanUp()
    {
        Dispose();
        try
        {
            var local = _local ?? new CfLocal(Pair.LocalFolder);
            if (Directory.Exists(Pair.LocalFolder))
            {
                foreach (var it in local.Scan().OrderByDescending(i => PathUtil.Depth(i.Path)))
                {
                    if (!it.IsPlaceholder) continue;
                    try
                    {
                        if (it.IsDir || it.OnDisk) local.Revert(it.Path);
                        else File.Delete(local.FullPath(it.Path));
                    }
                    catch (Exception ex) { Log.Warn($"Stop syncing: {it.Path}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Stop syncing {Pair.Label}: {ex.Message}"); }
        SyncRoot.Unregister(Pair);
        try { Directory.Delete(AppPaths.PairDir(Pair.Id), true); } catch { }
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _shutdown.Cancel();
        _wake.Set();
        _maintWake.Set();
        if (_thread.IsAlive) _thread.Join();
        _root?.StopFetches();
        if (_maintThread.IsAlive) _maintThread.Join();
        _watch?.Dispose();
        _attrWatch?.Dispose();
        _root?.Dispose();
        SaveOpened();
        _state?.Dispose();
        _shutdown.Dispose();
    }
}
