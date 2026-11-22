using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LarkCloud.Core;

public sealed class EngineOptions
{
    public int MaxDeletes { get; set; } = 20;
    /// <summary>Hold back big batches of deletions until someone confirms them. Off by default: deletions are
    /// applied in the background (they go to Lark's trash / the Recycle Bin, so they can be recovered).</summary>
    public bool ConfirmMassDeletes { get; set; }
    /// <summary>A deletion on either side always wins, even if the other side edited the item meanwhile; the edited
    /// version is in Lark's trash / the Recycle Bin. Off: an edit wins over a delete and the item comes back.</summary>
    public bool DeleteWins { get; set; } = true;
    public int MaxTransfers { get; set; } = 8;
    public TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(3);
    public string? TenantHost { get; set; }
    public string MachineName { get; set; } = Environment.MachineName;
    public IgnoreRules Ignore { get; set; } = new();
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    public TimeSpan RetryFailedAfter { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Store files Lark's security filter blocks as "name (zipped).zip" instead of failing.</summary>
    public bool ZipBlocked { get; set; } = true;
    /// <summary>Show read-only offline copies of Lark docs ("Report (Lark copy).docx").</summary>
    public bool DocCopies { get; set; }
    public string DocCopyFormat { get; set; } = "office";        // "office" (docx/xlsx/pptx) or "pdf"
    public string Label { get; set; } = "Lark";                  // the synced folder's name, for copies of unsaved changes
    public TimeSpan PermissionCheckEvery { get; set; } = TimeSpan.FromHours(1);
    public CancellationToken Cancellation { get; init; }
    /// <summary>Files this big count as "large" uploads: only max(1, MaxTransfers/4) of them run at once, so small edits keep flowing.</summary>
    public long LargeUpload { get; set; } = 64L << 20;
    /// <summary>After this long a pass starts no new uploads; the rest wait for the next pass (so Lark's changes aren't ignored for hours).</summary>
    public TimeSpan UploadBudget { get; set; } = TimeSpan.FromMinutes(20);
    /// <summary>Free bytes on the drive of a folder (the snapshot folder); tests replace it.</summary>
    public Func<string, long> FreeSpace { get; set; } = dir =>
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return long.MaxValue; }                                  // can't tell: don't block uploads
    };
}

/// <summary>
/// Keeps a Lark folder and a "files on demand" folder on the PC the same.
///
/// Lark → PC: new/changed Lark files become placeholders (name, size, date) whose data is only fetched
/// when opened; online docs become .url shortcuts; moves and deletions are applied.
/// PC → Lark: new and edited files are uploaded; moves, renames and deletions are applied in Lark
/// (deleted items go to the Lark trash). A big batch of deletions is held back for review.
///
/// Each pass is a three-way comparison of: Lark now (R), the PC now (L) and what both looked like after
/// the last sync (B, in StateDb).
/// </summary>
public sealed class CloudEngine
{
    readonly IRemoteDrive _remote;
    readonly ICloudLocal _local;
    readonly StateDb _state;
    readonly RemoteIndex _index;
    readonly EngineOptions _opt;
    public Action<string> Progress = _ => { };

    readonly ConcurrentDictionary<string, (string Sig, DateTime Until, string? Msg)> _failed = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string?> _uploadHere = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, long> _sizes = new();
    readonly ConcurrentDictionary<string, (string Token, string Hash)> _pendingMarks = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _noted = new();
    HashSet<string> _unknown = new(StringComparer.OrdinalIgnoreCase);   // couldn't be read on the PC this time
    HashSet<string> _remoteTokens = new();
    string? _bigDeleteSeen;                                          // a huge batch of deletions seen once, waiting to be confirmed
    bool _reviewRequired;
    readonly HashSet<string> _putBack = new(StringComparer.OrdinalIgnoreCase);   // "Put them back": upload again, don't treat as deleted

    bool Unknown(string p)                                           // the path or any folder above it (case-insensitive)
    {
        if (_unknown.Count == 0) return false;
        for (; p.Length > 0; p = PathUtil.Parent(p))
            if (_unknown.Contains(p)) return true;
        return false;
    }
    readonly object _resLock = new();

    SyncResult _res = new();
    Dictionary<string, RemoteEntry> _r = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, LocalItem> _l = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, BaseEntry> _b = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _incompletePaths = new(StringComparer.OrdinalIgnoreCase);   // folders whose Lark listing left an entry out
    HashSet<string>? _fresh;                                           // targeted pass: folders re-listed
    readonly ConcurrentDictionary<string, string> _newFolders = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentBag<string> _dirtyRemote = new();
    public bool BusyFilesWaiting { get; private set; }                 // files still being written: check again soon
    public List<string> ToHydrate { get; } = new();                    // pinned files Lark changed: fetch again
    public bool ConfirmDeletesSoon { get; private set; }               // a big deletion waits for the next full check

    // view-only folders, copies of changes that couldn't be saved, "good to know" notes
    readonly ConcurrentDictionary<string, string> _info = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string?> _openLinks = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> _rescued = new(StringComparer.OrdinalIgnoreCase);   // path|sig -> name of the copy
    readonly ConcurrentDictionary<string, byte> _roFolders = new();                                   // Lark folder tokens we can't edit
    bool? _rootEditable;
    DateTime _roCheckedAt = DateTime.MinValue;
    DateTime _passStart;                                                       // when this pass began (ConfirmUploads tells this pass's uploads from older ones)
    HashSet<string>? _roApplied;                                                                      // files we made read-only

    public CloudEngine(IRemoteDrive remote, ICloudLocal local, StateDb state, RemoteIndex index, EngineOptions opt)
    {
        _remote = remote;
        _local = local;
        _state = state;
        _index = index;
        _opt = opt;
    }

    public RemoteIndex Index => _index;

    public void RetryFailedNow() => _failed.Clear();

    // ================================================================ one pass
    /// <param name="shouldYield">Full passes only: lets the (slow) listing of Lark pause when quick work is waiting.
    /// A paused pass returns <c>Interrupted</c> having decided and changed nothing; the next full pass carries on.</param>
    public SyncResult Run(string? approvedDeletes = null, IReadOnlyCollection<string>? changed = null, Func<bool>? shouldYield = null)
    {
        _opt.Cancellation.ThrowIfCancellationRequested();
        _res = new SyncResult();
        _passStart = _opt.Now();
        if (!RefreshRemote(changed, shouldYield))
            return new SyncResult { Full = false, Interrupted = true };
        _newFolders.Clear();
        _newLocal.Clear();
        while (_dirtyRemote.TryTake(out _)) { }
        BusyFilesWaiting = false;
        ConfirmDeletesSoon = false;
        ToHydrate.Clear();

        _opt.Cancellation.ThrowIfCancellationRequested();
        LoadMaps();

        if (MoveLikeLark() | MoveLikePc())
            LoadMaps();

        Decide(approvedDeletes);

        if (!_dirtyRemote.IsEmpty)
            ConfirmUploads();
        ApplyReadOnly();
        _state.Commit();
        _res.ViewOnly = _rootEditable == false;

        // Problems with items that are gone from both sides (e.g. deleted on the PC before they could upload)
        // are dropped straight away instead of lingering until their retry time.
        foreach (var p in _failed.Keys.ToList())
            if (!_r.ContainsKey(p) && _state.Get(p) == null && _local.Stat(p) == null)
            {
                _failed.TryRemove(p, out _);
                _uploadHere.TryRemove(p, out _);
                _openLinks.TryRemove(p, out _);
                _info.TryRemove(p, out _);
                _res.Failed.Remove(p);
            }
        foreach (var (p, (_, until, msg)) in _failed)
            if (until > _opt.Now() && msg != null && !_res.Failed.ContainsKey(p))
                _res.Failed[p] = msg;
        foreach (var p in _res.Failed.Keys)
        {
            if (_uploadHere.TryGetValue(p, out var u)) _res.UploadHere[p] = u;
            if (_openLinks.TryGetValue(p, out var o)) _res.OpenLinks[p] = o;
        }
        foreach (var (p, (_, until, msg)) in _failed)
            if (msg == null && until > _opt.Now() && _info.TryGetValue(p, out var i)) _res.Info[p] = i;
        return _res;
    }

    bool RefreshRemote(IReadOnlyCollection<string>? changed, Func<bool>? shouldYield)
    {
        if (changed != null && _index.Complete)
        {
            _res.Full = false;
            var folders = new HashSet<string> { _remote.RootToken };
            foreach (var c in changed)
            {
                var p = c.Replace('\\', '/').Trim('/');
                foreach (var q in new[] { p, PathUtil.Parent(p) })
                {
                    var b = _state.Get(q);
                    if (q.Length > 0 && b?.Kind == Kinds.Folder && b.Token != null) folders.Add(b.Token);
                }
                var parent = PathUtil.Parent(p);
                while (parent.Length > 0 && _state.Get(parent)?.Token is not { } t) parent = PathUtil.Parent(parent);
                if (parent.Length > 0 && _state.Get(parent)?.Token is { } pt) folders.Add(pt);
            }
            _index.Refresh(folders);
            _fresh = folders;
            return true;
        }
        else
        {
            _res.Full = true;
            _fresh = null;
            return _index.FullRefresh((done, total) =>
            {
                if (total > 20) Progress($"Checking Lark: {done:N0} of {total:N0} folders…");
            }, shouldYield);
        }
    }

    void LoadMaps()
    {
        // ---- Lark, with each name made Windows-safe and unique within its folder (tracked by Lark ID,
        // so "Report (2).xlsx" on the PC is still the second "Report.xlsx" in Lark).
        _r = new(StringComparer.OrdinalIgnoreCase);
        var root = _remote.RootToken;
        var folderPath = new Dictionary<string, string> { [root] = "" };
        var basePath = new Dictionary<string, string>();
        foreach (var b in _state.All())
            if (b.Token != null) basePath.TryAdd(b.Token, b.Path);
        foreach (var level in _index.Entries().GroupBy(e => PathUtil.Depth(e.Path)).OrderBy(g => g.Key))
        foreach (var siblings in level.GroupBy(e => e.ParentToken ?? root))
        {
            if (!folderPath.TryGetValue(siblings.Key, out var parentLocal)) continue;     // its folder isn't shown
            var cands = new List<(RemoteEntry E, string Name)>();
            foreach (var e in siblings)
            {
                var entry = e.Clone();
                entry.LarkName = e.Name;
                var name = e.Name;
                if (e.IsOnlineDoc) name += ".url";
                else if (e.IsFile && name.Length > SpecialTokens.ZipSuffix.Length && name.EndsWith(SpecialTokens.ZipSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^SpecialTokens.ZipSuffix.Length];       // a blocked file stored zipped: show the real file
                    entry.Token = SpecialTokens.ZipPrefix + e.Token;
                    entry.Size = null;
                }
                else if (!e.IsFile && !e.IsFolder) continue;
                cands.Add((entry, PathUtil.SafeName(name)));
                if (_opt.DocCopies && e.IsOnlineDoc && SpecialTokens.CopyExt(e.Type, _opt.DocCopyFormat) is { } ext)
                {
                    var copy = e.Clone();
                    copy.Type = Kinds.File;
                    copy.Token = SpecialTokens.CopyToken(e.Type, ext, e.Token);
                    copy.Size = null;
                    copy.Url = null;
                    cands.Add((copy, PathUtil.SafeName(e.Name + SpecialTokens.CopyLabel + "." + ext)));
                }
            }
            // Items keep the name they already have on this PC; new ones get the next free name.
            bool Keeps((RemoteEntry E, string Name) c) =>
                basePath.TryGetValue(c.E.Token, out var bp) && string.Equals(PathUtil.Parent(bp), parentLocal, StringComparison.OrdinalIgnoreCase)
                && PathUtil.IsVariant(PathUtil.Name(bp), c.Name);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cands.OrderBy(c => Keeps(c) ? 0 : 1).ThenBy(c => c.E.Token, StringComparer.Ordinal))
            {
                var chosen = Keeps(c) && !taken.Contains(PathUtil.Name(basePath[c.E.Token])) ? PathUtil.Name(basePath[c.E.Token]) : PathUtil.FirstFree(c.Name, taken);
                taken.Add(chosen);
                var path = PathUtil.Join(parentLocal, chosen);
                if (_opt.Ignore.IsIgnored(path)) continue;
                c.E.Path = path;
                _r[path] = c.E;
                if (c.E.IsFolder) folderPath[c.E.Token] = path;
            }
        }

        _incompletePaths = _index.IncompleteFolders().Where(folderPath.ContainsKey).Select(t => folderPath[t]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ---- PC
        RescanLocal();
        _remoteTokens = new HashSet<string>(_r.Values.Select(e => e.Token));

        // ---- last sync
        _b = new(StringComparer.OrdinalIgnoreCase);
        foreach (var b in _state.All()) _b[b.Path] = b;
    }

    void Note(string msg)
    {
        lock (_resLock)
        {
            _res.Notes.Add(msg);
            if (_noted.Add(msg)) Log.Warn(msg);
        }
    }

    // ================================================================ moves
    /// <summary>Items moved or renamed in Lark: move them on the PC too (no re-download).</summary>
    bool MoveLikeLark()
    {
        var any = false;
        var byToken = _r.Values.Where(e => !e.IsOnlineDoc).GroupBy(e => e.Token).ToDictionary(g => g.Key, g => g.First());
        var baseByToken = BaseByToken();
        // Moves that can't be applied yet (their target name is still used by another item that is about to move, or the
        // target folder isn't on the PC yet) wait; the others go first, and the loop goes on while anything moves.
        var todo = _b.Values.Where(b => b.Token != null && b.Kind != Kinds.Link).OrderBy(b => PathUtil.Depth(b.Path)).ToList();
        for (var progress = true; progress && todo.Count > 0;)
        {
            progress = false;
            foreach (var b0 in todo.ToList())
            {
                if (!baseByToken.TryGetValue(b0.Token!, out var b)) { todo.Remove(b0); continue; }      // path may have changed with a parent move
                if (!byToken.TryGetValue(b.Token!, out var r)) { todo.Remove(b0); continue; }
                if (string.Equals(r.Path, b.Path, StringComparison.Ordinal)) { todo.Remove(b0); continue; }
                if (_l.TryGetValue(b.Path, out var li) && li.IsPlaceholder && li.Identity != null && li.Identity != b.Token) continue;
                if (!LocalExists(b.Path) || (LocalExists(r.Path) && !string.Equals(r.Path, b.Path, StringComparison.OrdinalIgnoreCase))) continue;
                // something new took its place (not an item that is itself arriving here from another path)
                if (_r.TryGetValue(b.Path, out var occ) && !string.Equals(r.Path, b.Path, StringComparison.OrdinalIgnoreCase)
                    && !(baseByToken.TryGetValue(occ.Token, out var ob) && !string.Equals(ob.Path, b.Path, StringComparison.Ordinal))) continue;
                if (!LocalExists(PathUtil.Parent(r.Path))) continue;
                try
                {
                    _local.Move(b.Path, r.Path);
                    _state.RenameTree(b.Path, r.Path);
                    var moved = _state.Get(r.Path);
                    if (moved is { Kind: Kinds.File, RMTimePending: false }
                        && moved.RMTime != r.MTime && _local.Stat(r.Path) is { OnDisk: true } local)
                    {
                        // A rename can change the timestamp. Keep the downloaded data only after checking its content.
                        // Not verified (different size, big file, any error) is always safe: the unchanged PC copy is then
                        // refreshed from Lark by normal reconciliation.
                        // A PC copy that was edited can't be compared with Lark's: it stays "changed on both sides" and both
                        // versions are kept. (Same size isn't proof Lark's content is unchanged, and guessing could lose an edit.)
                        try
                        {
                            if (!LocalChanged(local, moved) && SameContent(r, local, SizeOf(r) ?? _remote.ProbeSize(r.Token), MoveCheckMax))
                            {
                                moved.RMTime = r.MTime;
                                _state.Put(moved);
                            }
                        }
                        catch (Exception ex) when (ex is not NeedLoginException && !_opt.Cancellation.IsCancellationRequested)
                        {
                            Log.Info($"{r.Path}: couldn't compare it with Lark after the move ({ex.Message}); it will be refreshed instead");
                        }
                    }
                    Log.Info($"Moved here (moved in Lark): {b.Path} → {r.Path}");
                    _res.Moved++;
                    any = true;
                    progress = true;
                    todo.Remove(b0);
                    MovedLocal(b.Path, r.Path);
                    baseByToken = BaseByToken();
                }
                catch (Exception ex) { todo.Remove(b0); Fail(b.Path, ex, $"move “{b.Path}” to match Lark", "move" + r.Path); }
            }
        }
        return any;
    }

    /// <summary>Items moved or renamed on the PC: move them in Lark too (no re-upload).</summary>
    bool MoveLikePc()
    {
        var any = false;
        var relist = new HashSet<string>();
        var baseByToken = BaseByToken();
        var candidates = _l.Values.Where(l => l.IsPlaceholder && l.Identity != null && !SpecialTokens.IsCopy(l.Identity))
            .Select(l => (L: l, Token: l.Identity!)).ToList();
        // Some apps save by writing a new file, so a renamed file can lose its link to Lark. A new, unlinked file
        // with exactly the same size and date as a file that just disappeared is that file renamed/moved –
        // not "one deleted, one added".
        var vanished = _b.Values.Where(b => b.Kind == Kinds.File && b.Token != null && !SpecialTokens.IsCopy(b.Token) && !_l.ContainsKey(b.Path) && b.LMTime != 0)
            .GroupBy(b => (b.Size, b.LMTime)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        if (vanished.Count > 0)
        {
            var fresh = _l.Values.Where(l => !l.IsDir && !l.IsPlaceholder && !_b.ContainsKey(l.Path) && !Unknown(l.Path)).ToList();
            foreach (var g in fresh.GroupBy(l => (l.Size, l.MTime)).Where(g => g.Count() == 1))
                if (vanished.TryGetValue(g.Key, out var gone)) candidates.Add((g.First(), gone.Token!));
        }
        // A rename chain (a -> c, then b -> a) must go in the order that frees names first: a move whose target name is
        // still taken in Lark by another item waits until that item has moved. What is still waiting when nothing else
        // moves is done anyway.
        var todo = candidates.OrderBy(c => PathUtil.Depth(c.L.Path)).ToList();
        for (var force = false; todo.Count > 0;)
        {
            var progress = false;
            foreach (var cand in todo.ToList())
            {
                var (li, token) = cand;
                if (!baseByToken.TryGetValue(token, out var b) || b.Kind == Kinds.Link) { todo.Remove(cand); continue; }
                if (Unknown(b.Path)) { todo.Remove(cand); continue; }
                if (string.Equals(b.Path, li.Path, StringComparison.Ordinal)) { todo.Remove(cand); continue; }
                // a copy, not a move: the original is still there (a different item under the old name means it was moved away)
                if (_l.TryGetValue(b.Path, out var orig) && !string.Equals(b.Path, li.Path, StringComparison.OrdinalIgnoreCase)
                    && orig.Identity == token) { todo.Remove(cand); continue; }
                var r = _r.Values.FirstOrDefault(e => e.Token == b.Token);
                if (r == null) { todo.Remove(cand); continue; }
                // Lark edited it AND the renamed PC copy was edited: a conflict being kept (e.g. stopped half-way), not a move.
                // Moving it in Lark would put the PC's edit over Lark's: Decide() brings Lark's version down and uploads this one as new.
                if (b.Kind == Kinds.File && !string.Equals(r.Path, li.Path, StringComparison.OrdinalIgnoreCase) && RemoteChanged(r, b) && LocalChanged(li, b) && !(li.IsPlaceholder && !li.OnDisk)) { todo.Remove(cand); continue; }
                if (!force && _r.TryGetValue(li.Path, out var taken) && taken.Token != b.Token) continue;                        // the name is still in use
                todo.Remove(cand);
                progress = true;
                var type = b.Kind == Kinds.Folder ? "folder" : (b.RType ?? "file");
                try
                {
                    // (moved in Lark as well: the item is where Lark has it now, r.Path, and the PC's move wins)
                    var oldParent = PathUtil.Parent(r.Path);
                    var newParent = PathUtil.Parent(li.Path);
                    if (b.Kind == Kinds.Folder && LandsInsideItself(r, newParent))
                    {
                        // Lark moved a folder into this one while the PC moved this one into that one: Lark can't do both.
                        // Lark's move stands (MoveLikeLark follows it next pass); this PC move is undone.
                        _local.Move(li.Path, b.Path);
                        MovedLocal(li.Path, b.Path);
                        Log.Info($"Undid a move on this PC (Lark moved a folder into it meanwhile): {li.Path} → {b.Path}");
                        _res.Moved++;
                        any = true;
                        continue;
                    }
                    ReviveDeletedFolders(newParent);
                    if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
                    {
                        var dest = EnsureRemoteFolder(newParent);
                        _remote.Move(b.Token!, type, dest);
                    }
                    if (!string.Equals(PathUtil.Name(r.Path), PathUtil.Name(li.Path), StringComparison.Ordinal))
                        _remote.Rename(b.Token!, type, PathUtil.Name(li.Path));
                    var editedInLark = !b.RMTimePending && b.Kind == Kinds.File && r.MTime != b.RMTime;   // Lark's date changed before our move: someone edited it there
                    _state.RenameTree(b.Path, li.Path);
                    var mb = _state.Get(li.Path);
                    // adopt Lark's new date for it on the next listing (unless it's much newer: someone else edited it).
                    // An edit made in Lark before this move must stay visible: keep the old date, so the next pass sees the change.
                    if (mb != null && !editedInLark) { mb.RMTime = BaseEntry.PendingMark(_opt.Now()); _state.Put(mb); }
                    relist.Add(FolderTokenOrRoot(oldParent));
                    relist.Add(FolderTokenOrRoot(newParent));
                    var from = r.Path;
                    foreach (var e in _r.Values.Where(e => e.Path == from || PathUtil.IsUnder(e.Path, from)).ToList())
                    {
                        _r.Remove(e.Path);
                        e.Path = li.Path + e.Path[from.Length..];
                        _r[e.Path] = e;
                    }
                    _dirtyRemote.Add(_r.TryGetValue(li.Path, out var moved) && moved.ParentToken != null ? moved.ParentToken : _remote.RootToken);
                    Log.Info($"Moved in Lark (moved on this PC): {b.Path} → {li.Path}");
                    _res.Moved++;
                    any = true;
                    baseByToken = BaseByToken();
                }
                catch (Exception ex) { Fail(li.Path, ex, $"move “{PathUtil.Name(li.Path)}” in Lark", "move" + b.Path); }
            }
            if (!progress) { if (force) break; force = true; }
        }
        any |= MoveShortcutsLikePc(relist);
        if (any)
        {
            try { _index.Refresh(relist); }
            catch (Exception ex) { Log.Warn($"Couldn't re-check Lark after moving ({ex.Message})"); _index.Invalidate(); throw; }   // end the pass: carrying on would read the stale index and see the moved item as deleted in Lark
        }
        return any;
    }

    /// <summary>A Lark doc's shortcut renamed or moved on the PC: rename/move the doc in Lark too.</summary>
    bool MoveShortcutsLikePc(HashSet<string> relist)
    {
        var any = false;
        var byUrl = new Dictionary<string, BaseEntry>(StringComparer.Ordinal);
        foreach (var b in _state.All())
            if (b.Kind == Kinds.Link && LinkUrl(b.Url, b.RType, b.Token) is { } u) byUrl.TryAdd(u, b);
        if (byUrl.Count == 0) return false;
        foreach (var li in _l.Values.Where(l => !l.IsDir && !l.IsPlaceholder && l.Size < 8192 && !_b.ContainsKey(l.Path)
                                                 && l.Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var url = ReadShortcut(li.Path);
            if (url == null || !byUrl.TryGetValue(url, out var b) || LocalExists(b.Path)) continue;
            var r = _r.GetValueOrDefault(b.Path);
            if (r == null || r.Token != b.Token) continue;
            try
            {
                var oldParent = PathUtil.Parent(b.Path);
                var newParent = PathUtil.Parent(li.Path);
                var type = b.RType ?? "docx";
                ReviveDeletedFolders(newParent);
                if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
                    _remote.Move(b.Token!, type, EnsureRemoteFolder(newParent));
                string Bare(string p) => PathUtil.Name(p)[..^4];
                if (!string.Equals(Bare(b.Path), Bare(li.Path), StringComparison.Ordinal))
                    _remote.Rename(b.Token!, type, Bare(li.Path));
                _state.Remove(b.Path);
                b.Path = li.Path;
                b.Size = li.Size;
                b.LMTime = li.MTime;
                b.RMTime = BaseEntry.PendingMark(_opt.Now());
                _state.Put(b);
                _r.Remove(r.Path);
                r.Path = li.Path;
                _r[li.Path] = r;
                relist.Add(FolderTokenOrRoot(oldParent));
                relist.Add(FolderTokenOrRoot(newParent));
                Log.Info($"Moved/renamed the Lark doc (its shortcut was moved on this PC): {b.Path}");
                _res.Moved++;
                any = true;
            }
            catch (Exception ex) { Fail(li.Path, ex, $"move or rename the Lark doc “{PathUtil.Name(li.Path)}”", "move" + b.Path); }
        }
        return any;
    }

    string? ReadShortcut(string rel)
    {
        try
        {
            using var s = _local.OpenForUpload(rel);
            using var rd = new StreamReader(s);
            for (var line = rd.ReadLine(); line != null; line = rd.ReadLine())
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line[4..].Trim();
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Would moving this Lark folder into `newParent` (a PC path) put it inside itself, going by where Lark has things now?</summary>
    bool LandsInsideItself(RemoteEntry folder, string newParent)
    {
        string? tok = null;
        for (var q = newParent; tok == null; q = PathUtil.Parent(q))
        {
            try { tok = FolderToken(q); } catch (InvalidOperationException) { }
        }
        var byTok = _r.Values.GroupBy(e => e.Token).ToDictionary(g => g.Key, g => g.First());
        for (var n = 0; tok != null && tok != _remote.RootToken && n < 1000; n++)
        {
            if (tok == folder.Token) return true;
            tok = byTok.GetValueOrDefault(tok)?.ParentToken;
        }
        return false;
    }

    /// <summary>Something is moved on the PC into a folder (chain) Lark has deleted: the folders on the way are made again in
    /// Lark (the moved item is not deleted with them; whatever else was in them stays deleted).</summary>
    void ReviveDeletedFolders(string folder)
    {
        for (var q = folder; q.Length > 0; q = PathUtil.Parent(q))
            if (_b.TryGetValue(q, out var bq) && bq.Kind == Kinds.Folder && bq.Token != null && !_r.ContainsKey(q) && !_newFolders.ContainsKey(q)
                && !_remoteTokens.Contains(bq.Token) && KnownMissingInLark(q))
            {
                _state.Remove(q);
                _b.Remove(q);
            }
    }

    string FolderTokenOrRoot(string rel)
    {
        try { return FolderToken(rel); } catch (InvalidOperationException) { return _remote.RootToken; }
    }

    Dictionary<string, BaseEntry> BaseByToken()
    {
        var d = new Dictionary<string, BaseEntry>();
        foreach (var b in _state.All())
            if (b.Token != null && b.Kind != Kinds.Link) d.TryAdd(b.Token, b);
        return d;
    }

    bool LocalExists(string rel) => rel.Length == 0 || _l.ContainsKey(rel);

    /// <summary>The PC list follows a move without scanning the whole folder again (a scan per rename is far too slow at 50k files).</summary>
    void MovedLocal(string from, string to)
    {
        foreach (var k in _l.Keys.Where(k => k.Equals(from, StringComparison.OrdinalIgnoreCase) || Under(k, from)).ToList())
        {
            _l.Remove(k);
            if (_local.Stat(to + k[from.Length..]) is { } i && !_opt.Ignore.IsIgnored(i.Path)) _l[i.Path] = i;
        }
    }

    void RescanLocal()
    {
        _l = new(StringComparer.OrdinalIgnoreCase);
        foreach (var i in _local.Scan())
            if (!_opt.Ignore.IsIgnored(i.Path)) _l[i.Path] = i;
        _unknown = new HashSet<string>(_local.UnreadablePaths, StringComparer.OrdinalIgnoreCase);
    }

    // ================================================================ decisions
    sealed record Upload(string Path, string? Existing, bool Conflict = false);

    /// <summary>Only a full pass (Lark listed completely) acts on things missing from Lark: in a quick
    /// pass an item may just have moved to a folder that wasn't re-listed. Nor does anything under a folder whose
    /// listing had to skip an entry: the skipped item is unknown, not deleted.</summary>
    bool KnownMissingInLark(string path) => _fresh == null && !UnderIncomplete(path);

    static bool Under(string rel, string folder) => folder.Length == 0 || rel.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    bool UnderIncomplete(string path) => _incompletePaths.Any(f => Under(path, f));

    /// <summary>This folder, or one inside it, has Lark items we couldn't list: deleting it in Lark would take them along.</summary>
    bool HasIncomplete(string folder) => _incompletePaths.Any(f => Under(folder, f) || f.Equals(folder, StringComparison.OrdinalIgnoreCase) || Under(f, folder));

    static bool LocalChanged(LocalItem l, BaseEntry b) =>
        b.LMTime == BaseEntry.LocalUploadPending || (l.IsPlaceholder && !l.InSync) || l.Size != b.Size || l.MTime != b.LMTime ||
        (l.IsPlaceholder && l.Identity != null && b.Token != null && l.Identity != b.Token);

    /// <summary>How far past our own upload/move a Lark date may be and still count as ours (clocks differ a little).</summary>
    static readonly TimeSpan PendingSlack = TimeSpan.FromMinutes(2);

    static bool RemoteChanged(RemoteEntry r, BaseEntry b) =>
        r.Token != b.Token || (b.PendingSince is { } since ? r.MTimeUtc > since + PendingSlack
                               : !b.RMTimePending && r.MTime != b.RMTime);

    void Decide(string? approvedDeletes)
    {
        var now = _opt.Now();
        CheckEditable(now);
        var localFolders = new List<RemoteEntry>();                       // Lark folders to show on the PC
        var placeholders = new List<(RemoteEntry R, bool Update)>();      // Lark files to show / refresh
        var links = new List<RemoteEntry>();
        var remoteFolders = new List<string>();                           // PC folders to create in Lark
        var uploads = new List<Upload>();
        var bothNew = new List<(RemoteEntry R, LocalItem L)>();
        var conflicts = new List<(RemoteEntry R, LocalItem L)>();
        var deleteHere = new List<string>();
        var deleteInLark = new List<BaseEntry>();
        var relink = new List<(string Path, string Token)>();             // uploaded earlier but not yet marked in sync
        var goneHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);     // folders deleted on the PC
        var goneInLark = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // folders deleted in Lark
        var replacedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool UnderAny(string path, HashSet<string> folders)
        {
            if (folders.Count == 0) return false;
            for (var q = PathUtil.Parent(path); q.Length > 0; q = PathUtil.Parent(q))
                if (folders.Contains(q)) return true;
            return false;
        }

        var baseByToken = BaseByToken();
        _rTok = _r.Values.GroupBy(e => e.Token).ToDictionary(g => g.Key, g => g.First());
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);         // folders that wait for a busy file inside
        var paths = new HashSet<string>(_r.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(_l.Keys);
        paths.UnionWith(_b.Keys);

        foreach (var p in paths.OrderBy(PathUtil.Depth).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            _r.TryGetValue(p, out var R);
            _l.TryGetValue(p, out var L);
            _b.TryGetValue(p, out var B);

            if (Waiting(p, R, L)) continue;
            if (Unknown(p)) continue;                                      // couldn't read it on the PC: leave it alone
            if (UnderAny(p, replacedFolders)) continue;

            // Inside a folder deleted on one side: it all goes with the folder (anything with work Lark never
            // got goes to the Recycle Bin), instead of being re-created or uploaded into a deleted folder.
            if (UnderAny(p, goneHere)) continue;
            if (UnderAny(p, goneInLark))
            {
                if (L != null)
                {
                    if (Busy(L, now))
                    {
                        // still being written: it and the folders around it wait until it settles
                        for (var q = PathUtil.Parent(p); q.Length > 0; q = PathUtil.Parent(q)) keep.Add(q);
                    }
                    else deleteHere.Add(p);
                }
                continue;
            }

            // ---------------- Lark online docs: shortcuts on the PC
            if (R?.IsOnlineDoc == true || B?.Kind == Kinds.Link)
            {
                if (R?.IsOnlineDoc == true)
                {
                    // The shortcut has no content: seeing it present means it has seen the doc's current date.
                    if (L != null && B != null && B.Kind == Kinds.Link && B.Token == R.Token && B.Url == R.Url && B.RMTime != R.MTime
                        && L.Size == B.Size && L.MTime == B.LMTime)
                    {
                        B.RMTime = R.MTime;
                        _state.Put(B);
                    }
                    if (L == null && B != null && B.Kind == Kinds.Link && B.Token == R.Token && B.RMTime == R.MTime)
                        deleteInLark.Add(B);                              // its shortcut was deleted here: the doc goes to Lark's trash
                    else if (L == null || B == null || B.Url != R.Url || B.Kind != Kinds.Link) links.Add(R);
                }
                else if (L != null && B != null && L.Size == B.Size && L.MTime == B.LMTime && KnownMissingInLark(p))
                    deleteHere.Add(p);
                else if (L == null && KnownMissingInLark(p))
                    _state.Remove(p);
                else if (L != null && B != null && (L.Size != B.Size || L.MTime != B.LMTime) && KnownMissingInLark(p))
                {
                    _state.Remove(p);                                     // they edited the shortcut: keep it as their own file
                    if (!L.IsDir) uploads.Add(new Upload(p, null));
                }
                continue;
            }

            // ---------------- folders
            if ((R?.IsFolder ?? false) || (L?.IsDir ?? false) || B?.Kind == Kinds.Folder)
            {
                var rFolder = R?.IsFolder == true;
                var lFolder = L?.IsDir == true;
                if (R != null && !rFolder || L != null && !lFolder)
                {
                    Fail(p, null, "sync", "kind", "A file and a folder have the same name here and in Lark. Rename one of them.");
                    continue;
                }
                if (rFolder && lFolder)
                {
                    if (B == null || !L!.IsPlaceholder || L.Identity != R!.Token) relink.Add((p, R!.Token));
                }
                else if (rFolder && L == null)
                {
                    if (B == null && MovedInLarkNotYetHere(R!, baseByToken)) continue;   // moved in Lark; the PC folder follows next pass
                    if (B == null || B.Token == null) localFolders.Add(R!);
                    else if (B.Token != R!.Token)
                    {
                        _state.RemoveTree(p);
                        localFolders.Add(R);
                        replacedFolders.Add(p);
                        BusyFilesWaiting = true;
                    }
                    else if (!HasIncomplete(p) && (_opt.DeleteWins || FolderDeletableInLark(p))) { deleteInLark.Add(B); goneHere.Add(p); }
                    else localFolders.Add(R!);                            // something new/changed inside in Lark: keep it
                }
                else if (lFolder && R == null)
                {
                    if (B == null && L!.IsPlaceholder && L.Identity != null && KnownMissingInLark(p) && !_remoteTokens.Contains(L.Identity) && !_putBack.Contains(p))
                    { deleteHere.Add(p); goneInLark.Add(p); }                 // came from Lark (has a Lark ID) and is gone there: deleted in Lark
                    else if (B == null) remoteFolders.Add(p);
                    else if (KnownMissingInLark(p))
                    {
                        if (B.Token != null && L!.Identity == B.Token && MoveCanFollow(B.Token)) continue;   // moved in Lark, not deleted: follows next pass
                        if ((!_opt.DeleteWins || (B.Token != null && _remoteTokens.Contains(B.Token))) && EditedInside(p)) { _state.Remove(p); remoteFolders.Add(p); }   // an edit wins: the folder comes back, and each file inside is judged by itself
                        else { deleteHere.Add(p); goneInLark.Add(p); }
                    }
                }
                else if (B != null && L == null && R == null && KnownMissingInLark(p))
                    _state.Remove(p);
                continue;
            }

            // ---------------- files
            if (R != null && L == null)
            {
                if (B == null && MovedInLarkNotYetHere(R, baseByToken)) continue;   // moved in Lark; the PC copy follows next pass (don't download a second one)
                if (B == null) placeholders.Add((R, false));
                else if (R.Token != B.Token || ((!_opt.DeleteWins || EditedElsewhere(p, B)) && RemoteChanged(R, B))) { _state.Remove(p); placeholders.Add((R, false)); }   // a different Lark item now (or edited, with delete_wins off): bring it down
                else deleteInLark.Add(B);                                 // (an edited version stays in Lark's trash)
            }
            else if (R == null && L != null)
            {
                if (B == null)
                {
                    // A file linked to Lark (has a Lark ID) that Lark no longer has was deleted in Lark – even if
                    // this PC's sync records don't know it (e.g. after they were reset). Never re-upload it as "new".
                    if (L.IsPlaceholder && L.Identity != null && !SpecialTokens.IsCopy(L.Identity) && KnownMissingInLark(p)
                        && !_remoteTokens.Contains(L.Identity) && !_putBack.Contains(p))
                    {
                        deleteHere.Add(p);
                        continue;
                    }
                    // A cloud-only placeholder of an item Lark still has under another name (e.g. the "name (2)" shown for a duplicate
                    // name that has become unique) holds no PC data: never upload it as new; the item shows up under its real name.
                    if (L.IsPlaceholder && !L.OnDisk && L.Identity != null && !SpecialTokens.IsCopy(L.Identity) && _remoteTokens.Contains(L.Identity))
                    {
                        deleteHere.Add(p);
                        continue;
                    }
                    if (Busy(L, now)) continue;
                    if (IsShortcutOfGoneDoc(L)) { deleteHere.Add(p); continue; }   // a moved shortcut of a doc deleted in Lark: not a file to upload
                    uploads.Add(new Upload(p, null));
                }
                else if (!KnownMissingInLark(p)) continue;
                else if (B.Token != null && _remoteTokens.Contains(B.Token) && (MoveCanFollow(B.Token) || !(L.IsPlaceholder && L.Identity == B.Token) || (LocalChanged(L, B) && !(L.IsPlaceholder && !L.OnDisk))))
                {
                    // Not deleted: Lark moved or renamed it and the PC couldn't follow yet (see MoveLikeLark).
                    if (L.IsPlaceholder && L.Identity == B.Token && MoveCanFollow(B.Token)
                        && !(LocalChanged(L, B) && !(L.IsPlaceholder && !L.OnDisk) && UnderAny(_rTok[B.Token].Path, goneHere))) continue;   // the PC copy follows next pass (not into a folder this PC deleted: it is deleted in Lark now, so an edit is kept as a new file)
                    _state.Remove(p);                                     // what is here now is a different, new file that took the old name
                    if (!Busy(L, now)) uploads.Add(new Upload(p, null));
                }
                else if (!L.IsPlaceholder && B.Token != null && _l.Values.Any(o => o.Identity == B.Token && !string.Equals(o.Path, p, StringComparison.OrdinalIgnoreCase)))
                {
                    _state.Remove(p);                                     // the old file was moved away on the PC: this is a new, unlinked file that took its name
                    if (!Busy(L, now)) uploads.Add(new Upload(p, null));
                }
                else if (_opt.DeleteWins && LocalChanged(L, B) && Busy(L, now)) continue;   // still being written: wait until it settles
                else if (!LocalChanged(L, B) || (L.IsPlaceholder && !L.OnDisk) || _opt.DeleteWins) deleteHere.Add(p);   // edited here: to the Recycle Bin
                else
                {
                    _state.Remove(p);                                     // deleted in Lark but edited here: keep it, upload again
                    if (!Busy(L, now)) uploads.Add(new Upload(p, null));
                }
            }
            else if (R != null && L != null)
            {
                if (B == null)
                {
                    if (L.IsPlaceholder && L.Identity == R.Token) Adopt(p, R, L);
                    else bothNew.Add((R, L));
                    continue;
                }
                var lc = LocalChanged(L, B);
                var rc = RemoteChanged(R, B);
                if (!lc && !L.IsPlaceholder && B.Token == R.Token)
                {
                    relink.Add((p, R.Token));                              // uploaded, just not marked as synced yet
                    if (!rc) continue;
                }
                if (lc && Busy(L, now)) continue;
                if (!lc && !rc && _remote is RemoteFeatures rf && SpecialTokens.IsCopy(R.Token)
                    && rf.FreshSizes.TryRemove(R.Token, out var fresh) && fresh != B.Size)
                {
                    _sizes[R.Token + "|" + R.MTime] = fresh;               // the doc was exported again with a new size
                    placeholders.Add((R, true));
                    continue;
                }
                // A cloud-only placeholder of this very item holds no PC data: it can't be an edit, so Lark's version just refreshes it
                // (e.g. we were stopped right after refreshing it, before the state was written).
                if (lc && rc && L.IsPlaceholder && !L.OnDisk && L.Identity == R.Token) placeholders.Add((R, true));
                else if (lc && rc) conflicts.Add((R, L));
                else if (rc) placeholders.Add((R, true));
                else if (lc) uploads.Add(new Upload(p, B.Token));
                else if (B.RMTimePending)
                {                                                          // (only when Lark's date isn't much newer than our upload, see RemoteChanged)
                    B.RMTime = R.MTime;                                    // our upload, now confirmed by Lark
                    _state.Put(B);
                }
            }
            else if (B != null && KnownMissingInLark(p))
                _state.Remove(p);                                          // gone on both sides
        }

        deleteHere.RemoveAll(keep.Contains);

        // ---- the deletion guard: a big batch is held back until someone confirms it.
        // (Offline copies of Lark docs never count: removing one never deletes anything real.)
        bool IsCopyPath(string x) => SpecialTokens.IsCopy(_b.GetValueOrDefault(x)?.Token);
        var copiesHere = deleteHere.Where(IsCopyPath).ToList();
        var copiesLark = deleteInLark.Where(b => SpecialTokens.IsCopy(b.Token)).ToList();
        deleteHere.RemoveAll(IsCopyPath);
        deleteInLark.RemoveAll(b => SpecialTokens.IsCopy(b.Token));
        // Silent safety net: if one check would delete more than half of a big synced folder (more than 200
        // files), it's only done when the next check sees exactly the same – a glitch (half-loaded Lark listing,
        // unplugged drive) never wipes things out. No prompt; it just takes one more check.
        // A deleted folder counts the real files inside it, synced or just added in Lark (not twice if they're
        // listed as well). Offline copies never count.
        // In Lark a deleted shortcut trashes the real doc, and a deleted folder takes its online docs along, so
        // docs count there (lark = true); removing a shortcut on the PC deletes nothing real, so they don't.
        int Items(IEnumerable<string> ps, bool lark)
        {
            bool Real(BaseEntry b) => !SpecialTokens.IsCopy(b.Token) && (b.Kind == Kinds.File || (lark && b.Kind == Kinds.Link));
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in ps)
            {
                var bx = _b.GetValueOrDefault(x);
                if (bx?.Kind == Kinds.Folder) dirs.Add(x);
                else if (bx == null || Real(bx)) set.Add(x);
            }
            if (dirs.Count > 0)
            {
                foreach (var b in _b.Values)
                    if (Real(b) && UnderAny(b.Path, dirs)) set.Add(b.Path);
                // (folders deleted in Lark are gone from _r, so only a folder deleted here has anything to add)
                if (lark)
                    foreach (var r in _r.Values)
                        if ((r.IsFile || r.IsOnlineDoc) && !SpecialTokens.IsCopy(r.Token) && UnderAny(r.Path, dirs)) set.Add(r.Path);
            }
            return set.Count;
        }
        var nHere = Items(deleteHere, false);
        var nLark = Items(deleteInLark.Select(b => b.Path), true);
        if (!_res.Full && deleteInLark.Count > 0)
        {
            // A targeted pass cannot establish which items a remote folder deletion would affect.
            deleteInLark.Clear();
            ConfirmDeletesSoon = true;
        }
        // A folder delete that was approved and only half finished carries on without a new review: what's left of it
        // is a subset of what was approved (the review rules still apply to every item).
        var resumed = deleteInLark.Where(b => b.Kind == Kinds.Folder && b.Token != null && _resumeDeletes.ContainsKey(b.Token)).ToList();
        deleteInLark.RemoveAll(resumed.Contains);
        var files = Math.Max(1, _b.Values.Count(b => b.Kind == Kinds.File));
        string? big = null;
        if (nHere > 200 && nHere > files / 2) big = "here:" + Fingerprint(deleteHere);
        if (nLark > 200 && nLark > files / 2) big = (big ?? "") + "lark:" + Fingerprint(deleteInLark.Select(b => b.Path));
        var review = _reviewRequired || approvedDeletes != null
            || (_opt.ConfirmMassDeletes && (nHere > _opt.MaxDeletes || nLark > _opt.MaxDeletes));
        Dictionary<string, RemoteEntry>? reviewedEntries = null;
        if (review)
        {
            var affectedFolders = deleteInLark.Where(b => b.Kind == Kinds.Folder)
                .Select(b => SpecialTokens.Raw(b.Token!)).ToHashSet();
            // Which Lark items sit inside a folder being deleted: each token is resolved once (memoised along its
            // parent chain), so this is linear however big the drive is. Without folders there is nothing to look at.
            var affectedRemote = affectedFolders.Count == 0 ? new Dictionary<string, RemoteEntry>()
                : _index.Entries().GroupBy(e => e.Token).ToDictionary(g => g.Key, g => g.First());
            var inside = new Dictionary<string, bool>();
            bool Affected(RemoteEntry entry)
            {
                var chain = new List<string>();
                var verdict = false;
                for (var token = entry.Token; token != null;)
                {
                    if (inside.TryGetValue(token, out verdict)) break;
                    if (affectedFolders.Contains(token)) { verdict = true; chain.Add(token); break; }
                    if (chain.Contains(token)) break;                                  // a loop in Lark's data: not inside anything
                    chain.Add(token);
                    token = affectedRemote.GetValueOrDefault(token)?.ParentToken;
                }
                foreach (var t in chain) inside[t] = verdict;
                return verdict;
            }
            var rows = deleteHere.Select(p => new PendingDelete { Path = p, InLark = false })
                .Concat(deleteInLark.Select(b => new PendingDelete { Path = b.Path, InLark = true })).ToList();
            // Folder dates are left out on purpose: Lark updates a folder's modified_time lazily and unpredictably (a live
            // probe showed the folder holding a new file keeping its date while its parent and grandparent changed 20 s and
            // 2 min later), so they would change between the review and the approval pass. Items (token, path, type, parent)
            // and FILE versions still count: a new or edited file inside needs a new review.
            var lines = deleteHere.Select(p =>
            {
                var l = _l[p];
                return (p, System.Text.Json.JsonSerializer.Serialize(new { Side = "PC", l.Path, l.Identity, l.Size, l.MTime, l.InSync, l.OnDisk }));
            }).Concat(deleteInLark.Select(b =>
                (b.Path, System.Text.Json.JsonSerializer.Serialize(new { Side = "Lark", b.Path, b.Token, Version = b.Kind == Kinds.Folder ? null : _r.GetValueOrDefault(b.Path)?.MTime }))))
              .Concat(affectedRemote.Values.Where(Affected).Select(e =>
                (e.Path, System.Text.Json.JsonSerializer.Serialize(new { Side = "Contents", e.Path, e.Type, e.Token, MTime = e.IsFolder ? null : e.MTime, e.ParentToken }))))
              .ToList();
            var signature = Fingerprint(lines.Select(x => x.Item2), preserveCase: true);
            if (_res.Full && rows.Count > 0)
            {
                if (_reviewed.Count > 8) _reviewed.Clear();
                _reviewed[signature] = (lines.Select(x => x.Item2).ToHashSet(), lines.Select(x => x.Item1).ToHashSet());
            }
            // Items that are gone by now (an earlier partial run deleted them) leave a subset of what was approved: still fine.
            var approved = approvedDeletes == signature
                || (approvedDeletes != null && _reviewed.TryGetValue(approvedDeletes, out var was) && lines.All(x => was.Lines.Contains(x.Item2)));
            if (rows.Count > 0 && (!_res.Full || !approved))
            {
                if (_res.Full && approvedDeletes != null)
                {
                    var names = _reviewed.TryGetValue(approvedDeletes, out var old)
                        ? lines.Where(x => !old.Lines.Contains(x.Item2)).Select(x => $"{x.Item1} ({(old.Paths.Contains(x.Item1) ? "changed" : "new")})").Distinct().ToList()
                        : new List<string>();
                    Log.Warn($"The approved deletions were not applied: {(names.Count > 0 ? $"{names.Count:N0} item{(names.Count == 1 ? "" : "s")} in Lark changed since the review: {string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", …" : "")}" : "they changed since the review")}");
                    _res.Notices.Add(ChangedSinceReview);
                }
                _reviewRequired = true;
                foreach (var row in rows)
                    _res.PendingDeletes.Add(new PendingDelete { Path = row.Path, InLark = row.InLark, Approval = signature });
                deleteHere.Clear();
                deleteInLark.Clear();
                if (!_res.Full) ConfirmDeletesSoon = true;
            }
            else if (_res.Full)
            {
                _reviewRequired = rows.Count > 0;
                if (rows.Count > 0) reviewedEntries = affectedRemote.Values.Where(Affected).ToDictionary(e => e.Token);
            }
        }
        else if (big != null && big != _bigDeleteSeen)
        {
            _bigDeleteSeen = big;
            Log.Warn($"A big batch of deletions ({nHere:N0} here, {nLark:N0} in Lark): confirming on the next check before applying");
            deleteHere.Clear();
            deleteInLark.Clear();
            ConfirmDeletesSoon = true;                                     // the next full check comes in seconds
        }
        else if (big == _bigDeleteSeen || (big == null && _res.Full)) _bigDeleteSeen = null;

        if (resumed.Count > 0)
        {
            reviewedEntries ??= new();
            foreach (var r in resumed)
                foreach (var kv in _resumeDeletes[r.Token!]) reviewedEntries[kv.Key] = kv.Value;
            deleteInLark.AddRange(resumed);
        }
        deleteHere.AddRange(copiesHere);
        deleteInLark.AddRange(copiesLark);

        var work = localFolders.Count + placeholders.Count + links.Count + remoteFolders.Count + uploads.Count +
                   bothNew.Count + conflicts.Count + deleteHere.Count + deleteInLark.Count;
        if (work > 0) Progress($"Syncing {work:N0} change{(work == 1 ? "" : "s")}…");

        foreach (var (p, tok) in relink) Relink(p, tok);
        CreateLocalFolders(localFolders);
        ResolveSizes(placeholders.Select(x => x.R).Concat(conflicts.Select(c => c.R)).ToList());
        ApplyPlaceholders(placeholders);
        foreach (var r in links) WriteLink(r);
        CreateRemoteFolders(remoteFolders);
        foreach (var (r, l) in bothNew) BothNew(r, l, uploads);
        foreach (var (r, l) in conflicts) { if (!SameAsLark(r, l)) Conflict(r, l, uploads); }
        // Quick work first: deletions apply to paths no upload touches, so a long upload batch never holds them back.
        DeleteHere(deleteHere);
        DeleteInLark(deleteInLark, reviewedEntries);
        RunUploads(uploads);
    }

    /// <summary>Lark has moved/renamed this item and nothing on the PC blocks following: the place it goes to is free, or is held
    /// by another Lark item that is moving away itself (a rename chain).</summary>
    Dictionary<string, RemoteEntry> _rTok = new();                  // this pass's Lark items by ID
    bool MoveCanFollow(string token)
    {
        if (!_rTok.TryGetValue(token, out var r) || r.IsOnlineDoc) return false;
        if (!_l.TryGetValue(r.Path, out var o)) return true;
        return o.IsPlaceholder && o.Identity != null && o.Identity != token
            && _rTok.TryGetValue(o.Identity, out var other) && !string.Equals(other.Path, r.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>This Lark item is one the PC already has under another name (moved or renamed in Lark) and MoveLikeLark
    /// will move it when its target folder exists / its name is free.</summary>
    bool MovedInLarkNotYetHere(RemoteEntry r, Dictionary<string, BaseEntry> baseByToken) =>
        !r.IsOnlineDoc && baseByToken.TryGetValue(r.Token, out var bt) && bt.Kind != Kinds.Link
        && !_r.ContainsKey(bt.Path) && _l.TryGetValue(bt.Path, out var old) && old.Identity == r.Token;

    /// <summary>A new small .url file on the PC that points at a doc we had a shortcut for, and that doc is now gone from Lark
    /// (the shortcut was moved on the PC while Lark deleted the doc).</summary>
    bool IsShortcutOfGoneDoc(LocalItem l)
    {
        if (l.IsDir || l.IsPlaceholder || l.Size >= 8192 || !l.Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return false;
        if (!_b.Values.Any(b => b.Kind == Kinds.Link)) return false;
        var url = ReadShortcut(l.Path);
        if (url == null) return false;
        var b = _b.Values.FirstOrDefault(b => b.Kind == Kinds.Link && b.Token != null && LinkUrl(b.Url, b.RType, b.Token) == url);
        return b != null && !_remoteTokens.Contains(b.Token!) && KnownMissingInLark(b.Path);
    }

    /// <summary>The PC copy of this file was renamed/moved away and edited (a conflict copy), while Lark has a newer version under the old name.</summary>
    bool EditedElsewhere(string p, BaseEntry b) =>
        b.Kind == Kinds.File && _l.Values.Any(l => !l.IsDir && l.Identity == b.Token && !string.Equals(l.Path, p, StringComparison.OrdinalIgnoreCase)
                                                   && LocalChanged(l, b) && !(l.IsPlaceholder && !l.OnDisk));

    /// <summary>A file at or under this folder was added or edited on the PC since the last sync (a cloud-only
    /// placeholder never is).</summary>
    bool EditedInside(string folder) =>
        _l.Values.Any(l => !l.IsDir && PathUtil.IsUnder(l.Path, folder)
                           && (_b.TryGetValue(l.Path, out var b) ? LocalChanged(l, b) && !(l.IsPlaceholder && !l.OnDisk)
                                                                  : !(l.IsPlaceholder && l.Identity != null)));

    /// <summary>A folder deleted on the PC is deleted in Lark only if nothing inside it changed in Lark since.</summary>
    bool FolderDeletableInLark(string folder)
    {
        foreach (var r in _r.Values.Where(e => PathUtil.IsUnder(e.Path, folder)))
        {
            if (!_b.TryGetValue(r.Path, out var b)) return false;
            if (r.IsFile && RemoteChanged(r, b)) return false;
            if (_l.ContainsKey(r.Path)) return false;
        }
        return true;
    }

    static string Fingerprint(IEnumerable<string> paths, bool preserveCase = false)
    {
        var all = string.Join("\n", paths.Select(p => preserveCase ? p : p.ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all)));
    }

    bool Busy(LocalItem l, DateTime now)
    {
        if (l.IsDir) return false;
        var age = now - new DateTime(l.MTime, DateTimeKind.Utc);
        if (age > TimeSpan.FromSeconds(-2) && age < _opt.Settle)
        {
            BusyFilesWaiting = true;
            return true;
        }
        return false;
    }

    // ================================================================ failures
    static string Sig(RemoteEntry? r, LocalItem? l) => $"{r?.Token}|{r?.MTime}|{l?.Size}|{l?.MTime}|{l?.InSync}";

    /// <summary>A path that failed recently and hasn't changed since waits RetryFailedAfter before trying again.</summary>
    bool Waiting(string p, RemoteEntry? r, LocalItem? l)
    {
        if (!_failed.TryGetValue(p, out var f)) return false;
        if (f.Sig.EndsWith(Sig(r, l)) && f.Until > _opt.Now()) return true;
        _failed.TryRemove(p, out _);
        _uploadHere.TryRemove(p, out _);
        _openLinks.TryRemove(p, out _);
        _info.TryRemove(p, out _);
        return false;
    }

    void Fail(string path, Exception? ex, string what, string sigExtra = "", string? plain = null)
    {
        // Lark unreachable (network, DNS, Lark busy) isn't a problem with this file: stop this check and let
        // the whole thing be tried again shortly, instead of putting the file on a 30-minute wait.
        if (ex is LarkUnreachableException or HttpRequestException or NeedLoginException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex);   // keeps the original stack trace
        if (ex is LarkApiException { Code: 429 or 99991400 } busy) throw new LarkUnreachableException(busy.Message, busy);   // 99991400 = Lark's "too many requests"
        _r.TryGetValue(path, out var r);
        _l.TryGetValue(path, out var l);
        string? msg = plain;
        if (msg == null && ex is LarkApiException le)
        {
            if (le.EmptyRefused)
            {
                msg = null;                                                // not a problem: shown as "good to know"
                _info[path] = "Kept on this PC only: Lark doesn't store empty (0-byte) files. It's uploaded automatically once it has something in it.";
            }
            else if (le.IsBlocked)
            {
                msg = $"Can't {what}: Lark's security filter blocks this file when it is sent by this app, although Lark's own app " +
                      "accepts it. Fix: click “Upload with Lark…” and drag the file into the Lark folder that opens. " +
                      "After that it is matched up and stays in sync.";
                _uploadHere[path] = LarkFolderUrl(PathUtil.Parent(path));
            }
            else if (le.IsPermission && _rescued.TryGetValue(path + "|" + Sig(null, l), out var copyName))
            {
                var token = _b.GetValueOrDefault(path)?.Token;
                msg = SpecialTokens.IsCopy(token)
                    ? $"“{PathUtil.Name(path)}” is a read-only copy of a Lark doc, so your changes can't go back into the doc. Your edited version was saved to " +
                      $"My Folders › {RescueFolderName} › “{copyName}”. To change the doc itself, open its shortcut (.url) and edit it in Lark."
                    : $"You can only view this Lark folder, so your changes couldn't be saved there. A copy was saved to My Folders › {RescueFolderName} › " +
                      $"“{copyName}”. To save changes here, ask the folder's owner for edit access (click “Open in Lark”).";
                _openLinks[path] = LarkFolderUrl(PathUtil.Parent(path));
            }
            else if (le.IsPermission)
            {
                msg = $"Can't {what}: your Lark account isn't allowed to change this item (ask its owner for edit access). ({le.Message})";
                _openLinks[path] = LarkFolderUrl(PathUtil.Parent(path));
            }
            else msg = $"Can't {what}: {le.Message}";
        }
        else if (msg == null && ex is DeleteIncompleteException) msg = ex.Message;
        else if (msg == null && ex is IOException { InnerException: LarkApiException inner })
            msg = $"Can't {what}: Lark said: {inner.LarkMessage}. It will be tried again.";
        else if (msg == null && ex is IOException io)
            msg = $"Can't {what}: the file is open in another app or Windows refused ({io.Message}). It will be tried again.";
        else if (msg == null && ex is UnauthorizedAccessException ua)
            msg = $"Can't {what}: Windows denied access ({ua.Message}).";
        else msg ??= $"Can't {what}: {ex?.Message}";
        var retryIn = ex is IOException or DeleteIncompleteException ? TimeSpan.FromMinutes(2) : _opt.RetryFailedAfter;
        _failed[path] = (sigExtra + "|" + Sig(r, l), _opt.Now() + retryIn, msg);
        if (msg != null)
        {
            lock (_resLock) _res.Failed[path] = msg;
            Log.Error($"{path}: {msg}");
        }
        else Log.Info($"{path}: {ex?.Message}");
        if (ex is NeedLoginException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex);   // keeps the original stack trace
    }

    string? LarkFolderUrl(string rel)
    {
        if (string.IsNullOrEmpty(_opt.TenantHost)) return null;
        if (_remote.RootToken.StartsWith("wf:")) return null;
        var tok = rel.Length == 0 ? _remote.RootToken
            : _newFolders.GetValueOrDefault(rel) ?? _r.GetValueOrDefault(rel)?.Token ?? _b.GetValueOrDefault(rel)?.Token ?? _state.Get(rel)?.Token;
        return tok == null ? null : $"https://{_opt.TenantHost}/drive/folder/{tok}";
    }

    // ================================================================ Lark → PC
    void Adopt(string p, RemoteEntry r, LocalItem l)
    {
        _state.Put(new BaseEntry
        {
            Path = p, Kind = r.IsFolder ? Kinds.Folder : Kinds.File, Token = r.Token, RType = r.Type, RMTime = r.MTime,
            Size = l.Size, LMTime = l.MTime,
        });
    }

    void Relink(string p, string token)
    {
        try
        {
            var l = _local.MarkSynced(p, token);
            var r = _r.GetValueOrDefault(p);
            var b = _state.Get(p);
            _state.Put(new BaseEntry
            {
                Path = p, Kind = l.IsDir ? Kinds.Folder : Kinds.File, Token = token, RType = r?.Type ?? b?.RType ?? "file",
                RMTime = b?.Token == token ? b.RMTime : r?.MTime, Size = l.Size, LMTime = l.MTime,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"{p}: will finish linking it to Lark when it's closed ({ex.Message})");
        }
    }

    void CreateLocalFolders(List<RemoteEntry> folders)
    {
        foreach (var r in folders.OrderBy(f => PathUtil.Depth(f.Path)))
        {
            try
            {
                if (!LocalExists(PathUtil.Parent(r.Path)) && !_newLocal.Contains(PathUtil.Parent(r.Path))) continue;
                _local.CreateFolderPlaceholder(r.Path, r.Token);
                _newLocal.Add(r.Path);
                _state.Put(new BaseEntry { Path = r.Path, Kind = Kinds.Folder, Token = r.Token, RType = "folder", RMTime = r.MTime });
                _res.Created++;
            }
            catch (Exception ex) { Fail(r.Path, ex, "show this Lark folder on the PC"); }
        }
    }

    readonly HashSet<string> _newLocal = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lark's listing has no file sizes, and a placeholder needs one: ask Lark for each new or changed file.</summary>
    void ResolveSizes(List<RemoteEntry> entries)
    {
        var need = entries.Where(e => e.Size == null && !_sizes.ContainsKey(e.Token + "|" + e.MTime)).ToList();
        if (need.Count == 0) return;
        var done = 0;
        Parallel.ForEach(need, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _opt.MaxTransfers) }, e =>
        {
            try { _sizes[e.Token + "|" + e.MTime] = _remote.ProbeSize(e.Token); }
            catch (Exception ex) { Fail(e.Path, ex, "get this file's details from Lark"); }
            var n = Interlocked.Increment(ref done);
            if (need.Count > 20 && n % 25 == 0) Progress($"Getting file details from Lark: {n:N0} of {need.Count:N0}…");
        });
    }

    long? SizeOf(RemoteEntry r) => r.Size ?? (_sizes.TryGetValue(r.Token + "|" + r.MTime, out var s) ? s : null);

    void ApplyPlaceholders(List<(RemoteEntry R, bool Update)> items)
    {
        var n = 0;
        foreach (var (r, update) in items.OrderBy(x => PathUtil.Depth(x.R.Path)))
        {
            if (++n % 200 == 0) Progress($"Adding files from Lark: {n:N0} of {items.Count:N0}…");
            if (SizeOf(r) is not long size) continue;                     // failed above
            try
            {
                var parent = PathUtil.Parent(r.Path);
                if (!LocalExists(parent) && !_newLocal.Contains(parent)) continue;
                if (update)
                {
                    var wasPinned = _l.GetValueOrDefault(r.Path)?.Pinned == true;
                    _local.UpdateFilePlaceholder(r.Path, r.Token, size, r.MTimeUtc);
                    if (wasPinned) ToHydrate.Add(r.Path);
                    _res.Updated++;
                    Log.Info($"Updated from Lark: {r.Path}");
                }
                else
                {
                    _local.CreateFilePlaceholder(r.Path, r.Token, size, r.MTimeUtc);
                    _res.Created++;
                }
                var l = _local.Stat(r.Path);
                _state.Put(new BaseEntry
                {
                    Path = r.Path, Kind = Kinds.File, Token = r.Token, RType = r.Type, RMTime = r.MTime,
                    Size = l?.Size ?? size, LMTime = l?.MTime ?? r.MTimeUtc.Ticks,
                });
            }
            catch (Exception ex) { Fail(r.Path, ex, update ? "get the new version from Lark" : "show this Lark file on the PC"); }
        }
    }

    /// <summary>The address a shortcut points to. B keeps Lark's own url (may be null), so rebuild it the same way here.</summary>
    string? LinkUrl(string? url, string? type, string? token) =>
        url ?? (_opt.TenantHost != null && token != null ? $"https://{_opt.TenantHost}/{type}/{token}" : null);

    void WriteLink(RemoteEntry r)
    {
        var url = LinkUrl(r.Url, r.Type, r.Token);
        if (url == null) return;
        try
        {
            var parent = PathUtil.Parent(r.Path);
            if (!LocalExists(parent) && !_newLocal.Contains(parent)) return;
            _local.WriteShortcut(r.Path, url);
            var l = _local.Stat(r.Path);
            _state.Put(new BaseEntry
            {
                Path = r.Path, Kind = Kinds.Link, Token = r.Token, RType = r.Type, RMTime = r.MTime, Url = r.Url,
                Size = l?.Size ?? 0, LMTime = l?.MTime ?? 0,
            });
        }
        catch (Exception ex) { Fail(r.Path, ex, "create the shortcut to this Lark document"); }
    }

    // ================================================================ PC → Lark
    string FolderToken(string rel)
    {
        if (rel.Length == 0) return _remote.RootToken;
        if (_newFolders.TryGetValue(rel, out var t)) return t;
        if (_r.TryGetValue(rel, out var r) && r.IsFolder) return r.Token;
        if (_b.TryGetValue(rel, out var b) && b.Kind == Kinds.Folder && b.Token != null) return b.Token;
        var s = _state.Get(rel);
        if (s?.Kind == Kinds.Folder && s.Token != null) return s.Token;
        throw new InvalidOperationException($"its folder “{rel}” isn't in Lark yet");
    }

    string EnsureRemoteFolder(string rel)
    {
        try { return FolderToken(rel); }
        catch (InvalidOperationException)
        {
            var parent = EnsureRemoteFolder(PathUtil.Parent(rel));
            return CreateRemoteFolder(rel, parent);
        }
    }

    string CreateRemoteFolder(string rel, string parentToken)
    {
        // Lark may already have it (another PC made it since this PC last looked): use that one rather than
        // making a second folder of the same name, which would show up as "Name (2)" everywhere.
        var name = PathUtil.Name(rel);
        var tok = _remote.ListFolder(parentToken)
                      .FirstOrDefault(e => e.IsFolder && e.Name == name && !_remoteTokens.Contains(e.Token))?.Token
                  ?? _remote.CreateFolder(parentToken, name);
        _newFolders[rel] = tok;
        _dirtyRemote.Add(parentToken);
        _index.Put(new RemoteEntry { Path = rel, Token = tok, Type = Kinds.Folder }, parentToken);
        var b = new BaseEntry { Path = rel, Kind = Kinds.Folder, Token = tok, RType = "folder" };
        try
        {
            var l = _local.MarkSynced(rel, tok);
            b.LMTime = l.MTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _state.Put(b);
        lock (_resLock) _putBack.Remove(rel);                              // "put back" done: it's in Lark again
        Log.Info($"Created folder in Lark: {rel}");
        return tok;
    }

    /// <summary>New PC folders, created in Lark level by level (several at a time).</summary>
    void CreateRemoteFolders(List<string> folders)
    {
        if (folders.Count == 0) return;
        var done = 0;
        foreach (var level in folders.GroupBy(PathUtil.Depth).OrderBy(g => g.Key))
        {
            Parallel.ForEach(level, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_opt.MaxTransfers, 1, 8) }, p =>
            {
                try
                {
                    string parent;
                    try { parent = FolderToken(PathUtil.Parent(p)); }
                    catch (InvalidOperationException) { return; }          // its parent failed: it waits for the next pass
                    CreateRemoteFolder(p, parent);
                }
                catch (Exception ex) { Fail(p, ex, "create this folder in Lark"); }
                var n = Interlocked.Increment(ref done);
                if (folders.Count > 10 && n % 10 == 0) Progress($"Creating folders in Lark: {n:N0} of {folders.Count:N0}…");
            });
        }
    }

    /// <summary>The same name appeared on both sides since the last sync.</summary>
    void BothNew(RemoteEntry r, LocalItem l, List<Upload> uploads)
    {
        try
        {
            var size = SizeOf(r) ?? _remote.ProbeSize(r.Token);
            if (SameContent(r, l, size))
            {
                Relink(r.Path, r.Token);
                var b = _state.Get(r.Path);
                if (b != null) { b.RMTime = r.MTime; _state.Put(b); }
                Log.Info($"Matched up (same file here and in Lark): {r.Path}");
                return;
            }
            _sizes[r.Token + "|" + r.MTime] = size;
            Conflict(r, l, uploads);
        }
        catch (Exception ex) { Fail(r.Path, ex, "compare this file with the one in Lark"); }
    }

    const long MoveCheckMax = 16L << 20;          // after a move in Lark, bigger files are refreshed instead of downloaded to compare

    /// <summary>Same bytes here and in Lark? Different sizes answer "no" without downloading anything; so does a file
    /// above `maxBytes` (callers treat "no" as the safe outcome).</summary>
    bool SameContent(RemoteEntry r, LocalItem l, long remoteSize, long maxBytes = long.MaxValue)
    {
        if (remoteSize != l.Size || remoteSize > maxBytes) return false;
        using var a = _remote.OpenRead(r.Token, 0, _opt.Cancellation);
        using var b = _local.OpenForUpload(l.Path);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }

    /// <summary>"Changed on both sides" but the bytes are identical (e.g. we uploaded an edit and stopped before recording it):
    /// just record it as synced. Only a real content comparison counts; a different size, a file over the cap, an
    /// offline placeholder or a file that changes while it is compared is "no" (both versions are kept).</summary>
    bool SameAsLark(RemoteEntry r, LocalItem l)
    {
        try
        {
            if (l.IsPlaceholder && !l.OnDisk) return false;
            if (!SameContent(r, l, SizeOf(r) ?? _remote.ProbeSize(r.Token), MoveCheckMax)) return false;
            if (_local.Stat(l.Path) is not { } now || now.Size != l.Size || now.MTime != l.MTime) return false;
            Relink(r.Path, r.Token);
            var b = _state.Get(r.Path);
            if (b != null) { b.RMTime = r.MTime; _state.Put(b); }
            Log.Info($"Matched up (same content here and in Lark): {r.Path}");
            return true;
        }
        catch (Exception ex) when (ex is not NeedLoginException && !_opt.Cancellation.IsCancellationRequested)
        {
            Log.Info($"{r.Path}: couldn't compare it with Lark ({ex.Message}); keeping both versions");
            return false;
        }
    }

    /// <summary>Changed on both sides: keep both. The PC version is renamed and uploaded as a new file;
    /// the Lark version takes the original name.</summary>
    void Conflict(RemoteEntry r, LocalItem l, List<Upload> uploads)
    {
        try
        {
            var now = DateTime.Now;
            var copy = PathUtil.ConflictName(r.Path, _opt.MachineName, now);
            for (var n = 2; _local.Stat(copy) != null || _r.ContainsKey(copy); n++)          // a second conflict within the same minute
                copy = PathUtil.ConflictName(r.Path, _opt.MachineName, now, n);
            _local.Move(r.Path, copy);
            _state.Remove(r.Path);
            _res.Conflicts.Add(r.Path);
            Log.Warn($"Conflict: {r.Path} changed here and in Lark; kept this PC's version as “{PathUtil.Name(copy)}”");
            var size = SizeOf(r) ?? _remote.ProbeSize(r.Token);
            _local.CreateFilePlaceholder(r.Path, r.Token, size, r.MTimeUtc);
            var st = _local.Stat(r.Path);
            _state.Put(new BaseEntry
            {
                Path = r.Path, Kind = Kinds.File, Token = r.Token, RType = r.Type, RMTime = r.MTime,
                Size = st?.Size ?? size, LMTime = st?.MTime ?? r.MTimeUtc.Ticks,
            });
            var moved = _local.Stat(copy);
            if (moved != null) _l[copy] = moved;
            uploads.Add(new Upload(copy, null, true));
        }
        catch (Exception ex) { Fail(r.Path, ex, "keep both versions of this file"); }
    }

    const long SnapshotMargin = 256L << 20;                            // kept free on the drive on top of the snapshots
    long _snapReserved;                                                // bytes of snapshots being written right now
    bool _snapsCleaned;

    /// <summary>Small files first and at most max(1, MaxTransfers/4) large ones at a time, so a few multi-GB files can't
    /// keep every slot while hundreds of small edits wait. Total never exceeds MaxTransfers. Stops starting new uploads
    /// after UploadBudget (they're simply still "changed" at the next pass).</summary>
    void RunUploads(List<Upload> uploads)
    {
        if (uploads.Count == 0) return;
        if (!_snapsCleaned) { _snapsCleaned = true; try { _local.CleanSnapshots(); } catch (Exception ex) { Log.Info($"Couldn't clean old upload copies: {ex.Message}"); } }
        var sized = uploads.Select(u => (U: u, Size: _l.TryGetValue(u.Path, out var li) ? li.Size : 0L)).OrderBy(x => x.Size).ToList();
        var small = new Queue<Upload>(sized.Where(x => x.Size < _opt.LargeUpload).Select(x => x.U));
        var large = new Queue<Upload>(sized.Where(x => x.Size >= _opt.LargeUpload).Select(x => x.U));
        var workers = Math.Max(1, _opt.MaxTransfers);
        var largeCap = Math.Max(1, workers / 4);
        int largeRunning = 0, done = 0, left = uploads.Count;
        var gate = new object();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, Math.Min(workers, uploads.Count), new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            while (true)
            {
                Upload u;
                bool big;
                lock (gate)
                {
                    _opt.Cancellation.ThrowIfCancellationRequested();
                    if (clock.Elapsed > _opt.UploadBudget) return;
                    if (small.Count > 0) { u = small.Dequeue(); big = false; }
                    else if (large.Count > 0 && largeRunning < largeCap) { u = large.Dequeue(); largeRunning++; big = true; }
                    else return;                                       // the large ones left are picked up by those already running
                    left--;
                }
                try { UploadOne(u); }
                catch (NeedLoginException) { throw; }
                catch (Exception ex) { Fail(u.Path, ex, u.Existing == null ? $"upload new “{PathUtil.Name(u.Path)}”" : $"upload “{PathUtil.Name(u.Path)}”"); }
                finally { if (big) lock (gate) largeRunning--; }
                var n = Interlocked.Increment(ref done);
                Progress($"Uploading {n:N0} of {uploads.Count:N0} files…");
                if (n % 100 == 0) _state.Commit();                     // an interruption mid-way keeps what's already uploaded
            }
        });
        if (left > 0)
        {
            BusyFilesWaiting = true;                                   // check again soon
            Log.Info($"{left:N0} upload(s) continue in the next check (this one ran out of time)");
        }
    }

    void UploadOne(Upload u)
    {
        _opt.Cancellation.ThrowIfCancellationRequested();
        var before = _local.Stat(u.Path) ?? throw new FileNotFoundException("it was removed before it could be uploaded");
        var parent = FolderToken(PathUtil.Parent(u.Path));
        var dir = _local.SnapshotDir;
        // The snapshot needs room for the whole file (plus the others being copied right now): checked up front so a
        // lack of space skips this upload instead of failing half-way and filling the drive.
        var reserved = Interlocked.Add(ref _snapReserved, before.Size) - before.Size;
        try
        {
            if (_opt.FreeSpace(dir) - reserved < before.Size + SnapshotMargin)
            {
                var msg = $"Not enough free space on the drive of “{dir}” to upload “{PathUtil.Name(u.Path)}” ({before.Size / 1048576:N0} MB). It will be tried again; free some space.";
                lock (_resLock) { _res.Info[u.Path] = msg; if (_noted.Add(msg)) Log.Warn(msg); }
                return;
            }
            UploadSnapshot(u, before, parent, dir);
        }
        finally { Interlocked.Add(ref _snapReserved, -before.Size); }
    }

    void UploadSnapshot(Upload u, LocalItem before, string parent, string dir)
    {
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $".lcs-{Guid.NewGuid():N}");
        try
        {
            // Upload a snapshot, so an app that keeps the file open can't change it half-way. It's hashed while it is
            // copied, so it is never read again except by the upload itself; the PC file is checked against this hash.
            long size = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var src = _local.OpenForUpload(u.Path))
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 20];
                for (int n; (n = src.Read(buf, 0, buf.Length)) > 0;)
                {
                    _opt.Cancellation.ThrowIfCancellationRequested();
                    hasher.AppendData(buf, 0, n);
                    dst.Write(buf, 0, n);
                    size += n;
                }
            }
            var hash = Convert.ToHexString(hasher.GetHashAndReset());
            _opt.Cancellation.ThrowIfCancellationRequested();
            if (u.Existing != null && _pendingMarks.TryGetValue(u.Path, out var pending)
                && pending.Token == u.Existing && pending.Hash == hash
                && _state.Get(u.Path) is { LMTime: BaseEntry.LocalUploadPending } baseline)
            {
                var marked = MarkUploaded(u.Path, u.Existing, size, hash);
                if (marked != null)
                {
                    baseline.LMTime = marked.MTime;
                    _state.Put(baseline);
                    _pendingMarks.TryRemove(u.Path, out _);
                }
                else BusyFilesWaiting = true;
                return;
            }
            // An existing file keeps its real Lark name (the PC name may be a Windows-safe look-alike or have a number added);
            // a zipped one is stored with the suffix, which the remote adds again itself.
            var upName = PathUtil.Name(u.Path);
            if (u.Existing != null && _r.TryGetValue(u.Path, out var cur) && !string.IsNullOrEmpty(cur.LarkName))
                upName = SpecialTokens.IsZip(u.Existing) && cur.LarkName.EndsWith(SpecialTokens.ZipSuffix, StringComparison.OrdinalIgnoreCase)
                    ? cur.LarkName[..^SpecialTokens.ZipSuffix.Length] : cur.LarkName;
            string tok;
            try { tok = _remote.Upload(tmp, parent, upName, u.Existing); }
            catch (LarkApiException ex) when (ex.IsBlocked && _opt.ZipBlocked && _remote is RemoteFeatures rf)
            {
                RemoteFeatures.LastZipProtected = false;
                tok = rf.UploadZipped(tmp, parent, upName, u.Existing);
                var pw = RemoteFeatures.LastZipProtected ? $" (if Windows asks for a password, it's “{ProtectedZip.Password}”)" : "";
                lock (_resLock)
                    _res.Info[u.Path] = $"Lark's security filter blocks this file, so it's stored in Lark as “{upName}{SpecialTokens.ZipSuffix}”. " +
                                        $"On PCs with Lark Cloud Drive it shows as the normal file; in the browser, download and unzip it{pw}.";
                Log.Info($"{u.Path}: blocked by Lark's filter; stored zipped");
            }
            catch (LarkApiException ex) when (ex.IsPermission)
            {
                SaveCopyOfChanges(u.Path, tmp, before);
                throw;
            }
            _dirtyRemote.Add(parent);
            _index.Put(new RemoteEntry { Path = u.Path, Token = tok, Type = Kinds.File }, parent);
            var after = MarkUploaded(u.Path, tok, size, hash);
            _state.Put(new BaseEntry
            {
                Path = u.Path, Kind = Kinds.File, Token = tok, RType = "file", RMTime = BaseEntry.PendingMark(_opt.Now()),
                Size = size, LMTime = after?.MTime ?? BaseEntry.LocalUploadPending,
            });
            if (after == null)
            {
                _pendingMarks[u.Path] = (tok, hash);
                BusyFilesWaiting = true;
            }
            else _pendingMarks.TryRemove(u.Path, out _);
            lock (_resLock) { _res.Uploaded++; _putBack.Remove(u.Path); }
            Log.Info($"Uploaded{(u.Existing == null ? " new" : "")}: {u.Path}");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    LocalItem? MarkUploaded(string path, string token, long size, string hash)
    {
        try { return _local.MarkUploaded(path, token, size, hash); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Will recheck {path} after upload: {ex.Message}");
            return null;
        }
    }

    // ================================================================ view-only folders
    const string RescueFolderName = "Lark Cloud – changes not saved";

    /// <summary>Changes that can't be saved where they belong (view-only folder, read-only copy) are uploaded to
    /// the person's own My Folders, so no work is lost.</summary>
    void SaveCopyOfChanges(string path, string snapshot, LocalItem before)
    {
        var key = path + "|" + Sig(null, before);
        if (_rescued.ContainsKey(key)) return;
        try
        {
            var folder = RescueFolder();
            var name = PathUtil.Name(path);
            var ext = Path.GetExtension(name);
            var copy = $"{name[..^ext.Length]} (from {_opt.Label} {DateTime.Now:yyyy-MM-dd HHmm}){ext}";
            try { _remote.Upload(snapshot, folder, copy, null); }
            catch (LarkApiException ex) when (ex.IsGone)
            {
                _state.SetMeta("rescue_folder", "");
                _remote.Upload(snapshot, RescueFolder(), copy, null);
            }
            _rescued[key] = copy;
            if (_b.GetValueOrDefault(PathUtil.Parent(path))?.Token is { } ft && !SpecialTokens.IsCopy(_b.GetValueOrDefault(path)?.Token))
                _roFolders[ft] = 1;                                         // learned: this folder is view-only
            Log.Warn($"{path}: can't be saved in Lark (view only); saved a copy to My Folders/{RescueFolderName}/{copy}");
        }
        catch (Exception ex) { Log.Warn($"{path}: couldn't save a copy of the changes to My Folders ({ex.Message})"); }
    }

    string RescueFolder()
    {
        if (_state.GetMeta("rescue_folder") is { Length: > 0 } t) return t;
        var root = _remote.MyFoldersRoot();
        var tok = _remote.ListFolder(root).FirstOrDefault(e => e.IsFolder && e.Name == RescueFolderName)?.Token
                  ?? _remote.CreateFolder(root, RescueFolderName);
        _state.SetMeta("rescue_folder", tok);
        return tok;
    }

    void CheckEditable(DateTime now)
    {
        if (!_res.Full || now - _roCheckedAt < _opt.PermissionCheckEvery) return;
        _roCheckedAt = now;
        try
        {
            var was = _rootEditable;
            _rootEditable = _remote.CanEdit(_remote.RootToken, "folder");
            if (_rootEditable == true && was == false) _roFolders.Clear();
            if (_rootEditable == false && was != false) Log.Info("This Lark folder is view-only for you: its files are read-only on this PC");
        }
        catch (Exception ex) { Log.Info($"Couldn't check edit permission: {ex.Message}"); }
    }

    bool ShouldBeReadOnly(string path, string? token)
    {
        if (SpecialTokens.IsCopy(token)) return true;
        if (_rootEditable == false) return true;
        if (_roFolders.IsEmpty) return false;
        for (var p = PathUtil.Parent(path); p.Length > 0; p = PathUtil.Parent(p))
            if (_state.Get(p)?.Token is { } t && _roFolders.ContainsKey(t)) return true;
        return false;
    }

    /// <summary>Files in view-only folders (and offline doc copies) are read-only on the PC, so apps say so up front
    /// instead of the save failing later. Files are made writable again if the folder becomes editable.</summary>
    void ApplyReadOnly()
    {
        _roApplied ??= (_state.GetMeta("ro_paths") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anyRo = _rootEditable == false || !_roFolders.IsEmpty || _opt.DocCopies || _roApplied.Count > 0;
        if (!anyRo) return;
        var changed = false;
        // The pass's own PC list, plus files it put on the PC (a second full scan per pass is slow on big folders).
        var items = _l.Values.Where(x => !x.IsDir && x.IsPlaceholder).ToList();
        foreach (var b in _state.All())
            if (b.Kind == Kinds.File && !_l.ContainsKey(b.Path) && _local.Stat(b.Path) is { IsDir: false, IsPlaceholder: true } s) items.Add(s);
        foreach (var l in items)
        {
            var want = ShouldBeReadOnly(l.Path, l.Identity);
            try
            {
                if (want && !l.ReadOnly) { _local.SetReadOnly(l.Path, true); changed |= _roApplied.Add(l.Path); }
                else if (!want && l.ReadOnly && _roApplied.Contains(l.Path)) { _local.SetReadOnly(l.Path, false); changed |= _roApplied.Remove(l.Path); }
            }
            catch (Exception ex) { Log.Info($"{l.Path}: couldn't change read-only ({ex.Message})"); }
        }
        _roApplied.RemoveWhere(p => _state.Get(p) == null);
        if (changed) _state.SetMeta("ro_paths", string.Join("\n", _roApplied));
    }

    // ================================================================ deletions
    void DeleteHere(List<string> paths)
    {
        // Deepest level first (a folder is only removed once what's in it is gone); the items of one level are
        // independent, so they go several at a time.
        var n = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_opt.MaxTransfers, 1, 8), CancellationToken = _opt.Cancellation };
        foreach (var level in paths.GroupBy(PathUtil.Depth).OrderByDescending(g => g.Key))
            Parallel.ForEach(level, options, p =>
            {
                _opt.Cancellation.ThrowIfCancellationRequested();
                if (paths.Count > 20 && Interlocked.Increment(ref n) % 50 == 0) Progress($"Removing items deleted in Lark: {n:N0} of {paths.Count:N0}…");
                try
                {
                    _l.TryGetValue(p, out var observed);
                    var l = _local.Stat(p);
                    if (l == null || observed == null || l.IsDir != observed.IsDir || l.Identity != observed.Identity
                        || (!l.IsDir && (l.Size != observed.Size || l.MTime != observed.MTime || l.InSync != observed.InSync
                                        || l.OnDisk != observed.OnDisk)))
                    {
                        BusyFilesWaiting = true;
                        return;
                    }
                    if (l.IsDir)
                    {
                        var full = _local.FullPath(p);
                        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                        {
                            _state.Remove(p);                                // something inside is kept: the folder stays
                            return;
                        }
                        _local.Delete(p, recycle: false);
                    }
                    else
                    {
                        // Lark's trash already has it. Only a copy with changes Lark never got goes to the Recycle Bin
                        // (that's slow, so unchanged copies are simply removed).
                        var b = _b.GetValueOrDefault(p);
                        var kept = l.OnDisk && (b == null || LocalChanged(l, b));
                        _local.Delete(p, recycle: kept);
                        if (kept) Log.Info($"{p} was deleted in Lark; this PC's copy is in the Recycle Bin");
                    }
                    _state.RemoveTree(p);
                    lock (_resLock) _res.DeletedLocal++;
                    Log.Info($"Deleted here (deleted in Lark): {p}");
                }
                catch (Exception ex) { Fail(p, ex, "delete this item (it was deleted in Lark)"); }
            });
    }

    void DeleteInLark(List<BaseEntry> items, Dictionary<string, RemoteEntry>? reviewedEntries)
    {
        // Only the topmost items are deleted (a folder takes everything inside it along), several at a time.
        var folders = items.Where(b => b.Kind == Kinds.Folder).Select(b => b.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool InsideDeletedFolder(string path)
        {
            for (var p = PathUtil.Parent(path); p.Length > 0; p = PathUtil.Parent(p))
                if (folders.Contains(p)) return true;
            return false;
        }
        var top = new List<BaseEntry>();
        foreach (var b in items)
        {
            if (!InsideDeletedFolder(b.Path)) top.Add(b);
        }
        if (top.Count == 0) return;
        var done = 0;
        Parallel.ForEach(top, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_opt.MaxTransfers, 1, 8) }, b =>
        {
            DeleteOneInLark(b, reviewedEntries);
            var n = Interlocked.Increment(ref done);
            if (top.Count > 10 && n % 10 == 0) Progress($"Deleting in Lark: {n:N0} of {top.Count:N0}…");
        });
    }

    // Lark struggles (HTTP 500, "resource contention") when many deletes hit one folder at once, so only a few
    // delete requests run at the same time, however deep the recursion goes.
    const int DeleteParallel = 3;
    readonly SemaphoreSlim _deleteGate = new(DeleteParallel);
    const int DeleteTries = 3;                                       // per item, for temporary errors
    const int DeleteStreakLimit = 10;                                // this many failures in a row: Lark is not answering, stop and try later
    const int DeleteNoticeFrom = 20;                                 // smaller folder deletes finish without a tray message
    // An approved folder delete that didn't finish: folder token -> what was approved. The rest of it carries on
    // next pass without a new review (what's left is a subset of what was approved).
    readonly ConcurrentDictionary<string, Dictionary<string, RemoteEntry>> _resumeDeletes = new();
    // What each recent review looked like (its signature -> detail lines and paths), so an approval can be checked
    // against it and a mismatch can say what changed.
    readonly Dictionary<string, (HashSet<string> Lines, HashSet<string> Paths)> _reviewed = new();
    public const string ChangedSinceReview = "Lark changed since you reviewed these deletions – please review again.";
    readonly ConcurrentDictionary<string, int> _deleteTotal = new();   // folder token -> items when first tried
    readonly ConcurrentDictionary<string, bool> _deleteFailNotified = new(StringComparer.OrdinalIgnoreCase);

    sealed class DeleteRun
    {
        public string Name = "";
        public int Total, Done, Failures, Streak;
        public volatile bool Abort;
        public Exception? Last;
        public string? LastName;
    }

    sealed class DeleteIncompleteException : Exception
    {
        public DeleteIncompleteException(string message) : base(message) { }
    }

    DeleteRun StartRun(BaseEntry b)
    {
        var total = 1 + _b.Values.Count(x => x.Token != null && _rTok.ContainsKey(x.Token)
                                            && x.Path.StartsWith(b.Path + "/", StringComparison.OrdinalIgnoreCase));
        var first = _deleteTotal.GetOrAdd(b.Token!, total);               // a carried-on delete counts what was deleted before
        return new DeleteRun { Name = PathUtil.Name(b.Path), Total = Math.Max(first, total), Done = Math.Max(0, first - total) };
    }

    bool MarkDone(DeleteRun run)
    {
        var n = Interlocked.Increment(ref run.Done);
        Volatile.Write(ref run.Streak, 0);
        if (n % 25 == 0) Progress($"Deleting “{run.Name}” in Lark: {n:N0} of {Math.Max(n, run.Total):N0}…");
        return true;
    }

    void Note(DeleteRun run, Exception ex, string? name = null)
    {
        run.Last = ex;
        run.LastName = name;
        Interlocked.Increment(ref run.Failures);
        if (Interlocked.Increment(ref run.Streak) >= DeleteStreakLimit) run.Abort = true;
    }

    /// <summary>Deletes one item in Lark, trying a few times when Lark says "busy". An item that can't be deleted is
    /// noted on the run (the rest carries on) instead of stopping everything. `top`: a refusal because of permissions on
    /// the folder itself is thrown, so the caller can put it back on this PC.</summary>
    bool TryDelete(DeleteRun run, string token, string type, string? name = null, bool top = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            _opt.Cancellation.ThrowIfCancellationRequested();
            _deleteGate.Wait(_opt.Cancellation);
            try
            {
                _remote.Delete(token, type);
                return MarkDone(run);
            }
            catch (LarkApiException ex) when (ex.IsGone) { return MarkDone(run); }
            catch (LarkApiException ex) when (top && ex.IsPermission) { throw; }
            catch (Exception ex) when ((ex is LarkApiException { IsTransient: true } or LarkUnreachableException or HttpRequestException) && attempt < DeleteTries) { }
            catch (Exception ex) when (ex is not (OperationCanceledException or NeedLoginException))
            {
                Note(run, ex, name);
                return false;
            }
            finally { _deleteGate.Release(); }
        }
    }

    void DeleteOneInLark(BaseEntry b, Dictionary<string, RemoteEntry>? reviewedEntries)
    {
        {
            try
            {
                _opt.Cancellation.ThrowIfCancellationRequested();
                if (_local.CheckPresence(b.Path) != LocalPresence.Missing) { BusyFilesWaiting = true; return; }
                DeleteRun? run = null;
                if (b.Kind == Kinds.Folder)
                {
                    run = StartRun(b);
                    if (reviewedEntries != null && reviewedEntries.TryGetValue(b.Token!, out var snapshot))
                    {
                        _resumeDeletes[b.Token!] = reviewedEntries;
                        if (DeleteReviewedFolder(snapshot, reviewedEntries, b.Path, run, top: true) == null)
                        {
                            _resumeDeletes.TryRemove(b.Token!, out _);       // a change since the review: it needs a new one
                            ConfirmDeletesSoon = true;
                            Log.Warn($"“{b.Path}” was not deleted: Lark changed since the review");
                            lock (_resLock) _res.Notices.Add(ChangedSinceReview);
                            return;
                        }
                    }
                    else DeleteFolderInLark(b.Token!, b.Path, run, top: true);
                    if (run.Failures > 0 || run.Abort)
                    {
                        var said = run.Last is LarkApiException la ? la.LarkMessage : run.Last?.Message;
                        throw new DeleteIncompleteException($"Couldn't finish deleting “{run.Name}” in Lark: {Math.Min(run.Done, run.Total):N0} of {run.Total:N0} items deleted. " +
                                                            $"Lark said: {said}{(run.LastName != null ? $" (on “{run.LastName}”)" : "")}. It will be tried again in a few minutes.");
                    }
                }
                else _remote.Delete(b.Token!, b.RType ?? "file");
                _index.Remove(b.Token!);
                _state.RemoveTree(b.Path);
                lock (_resLock)
                {
                    _res.DeletedRemote++;
                    if (run != null && (run.Total > DeleteNoticeFrom || _deleteFailNotified.ContainsKey(b.Path)))
                        _res.Notices.Add($"Deleted “{run.Name}” in Lark ({run.Total:N0} items are in Lark's trash).");
                }
                if (b.Kind == Kinds.Folder) { _resumeDeletes.TryRemove(b.Token!, out _); _deleteTotal.TryRemove(b.Token!, out _); _deleteFailNotified.TryRemove(b.Path, out _); }
                Log.Info($"Deleted in Lark (moved to Lark's trash): {b.Path}");
            }
            catch (LarkApiException ex) when (ex.IsGone) { _state.RemoveTree(b.Path); }
            catch (LarkApiException ex) when (ex.IsPermission)
            {
                // View only: it can't be deleted in Lark, so it's put back on this PC.
                _state.RemoveTree(b.Path);
                lock (_resLock)
                    _res.Info[b.Path] = "You can only view this in Lark, so it can't be deleted there. It has been put back on this PC.";
                Log.Info($"{b.Path}: view only in Lark ({ex.Message}); putting it back");
            }
            catch (Exception ex)
            {
                // Shown once as a tray message; the retries after that only update the Problems list.
                if (ex is DeleteIncompleteException && _deleteFailNotified.TryAdd(b.Path, true))
                    lock (_resLock) _res.Notices.Add(ex.Message);
                else if (ex is not DeleteIncompleteException) _resumeDeletes.TryRemove(b.Token ?? "", out _);
                Fail(b.Path, ex, "delete this item in Lark");
            }
        }
    }

    // Work from the approved snapshot. A direct delete of a populated folder would also
    // trash children added after review. Only send a folder delete after an empty listing.
    // Returns null when something changed since the review (the rest is left alone for a new review), false when
    // something inside couldn't be deleted (noted on the run; the folder stays), true when the folder is gone.
    bool? DeleteReviewedFolder(RemoteEntry folder, Dictionary<string, RemoteEntry> reviewed, string localRoot, DeleteRun run, bool top = false)
    {
        var children = _remote.ListFolder(folder.Token);
        foreach (var child in children)
        {
            // Folder dates are ignored (Lark updates them lazily; see Decide); files must still be the reviewed version.
            if (!reviewed.TryGetValue(child.Token, out var expected) || expected.ParentToken != folder.Token
                || expected.Type != child.Type || (!child.IsFolder && expected.MTime != child.MTime)
                || expected.Path != PathUtil.Join(folder.Path, child.Path))
            {
                Log.Warn($"“{PathUtil.Join(folder.Path, child.Path)}” in Lark {(reviewed.ContainsKey(child.Token) ? "changed" : "is new")} since the review");
                return null;
            }
        }
        if (children is RemoteListing { Incomplete: true }) return null;       // there may be more in it than we can see
        // The children go a few at a time. Any "no" (the PC copy is back, something changed) stops the rest from
        // starting; items already sent to Lark's trash stay there, and the folder itself is then left alone.
        // An item Lark won't delete (after a few tries) doesn't stop the others; it's counted and tried again next pass.
        bool stopped = false, incomplete = false;
        var options = new ParallelOptions { MaxDegreeOfParallelism = DeleteParallel, CancellationToken = _opt.Cancellation };
        Parallel.ForEach(children, options, (child, state) =>
        {
            if (Volatile.Read(ref stopped) || run.Abort) return;
            try
            {
                _opt.Cancellation.ThrowIfCancellationRequested();
                var outcome = _local.CheckPresence(localRoot) != LocalPresence.Missing ? (bool?)null
                    : child.IsFolder ? DeleteReviewedFolder(reviewed[child.Token], reviewed, localRoot, run)
                    : TryDelete(run, child.Token, child.Type, reviewed[child.Token].Path);
                if (outcome == null)
                {
                    Volatile.Write(ref stopped, true);
                    state.Stop();
                }
                else if (outcome == false) Volatile.Write(ref incomplete, true);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or NeedLoginException)) { Note(run, ex); Volatile.Write(ref incomplete, true); }
        });
        if (stopped) return null;
        if (incomplete || run.Abort) return false;                             // the folder stays until its contents are gone
        if (children.Count > 0 && _remote.ListFolder(folder.Token).Count > 0) return null;
        if (_local.CheckPresence(localRoot) != LocalPresence.Missing) return null;
        return TryDelete(run, folder.Token, Kinds.Folder, folder.Path, top);
    }

    /// <summary>Delete a Lark folder. Lark refuses very big folders in one request (server error 500), so then
    /// what's inside is deleted first – a few items at a time, big sub-folders again in parts – and the
    /// empty folder last. Everything still goes to Lark's trash. Items Lark won't delete are counted on the run.</summary>
    /// <returns>true when the folder is gone; false when something inside couldn't be deleted (noted on the run).</returns>
    bool DeleteFolderInLark(string token, string path, DeleteRun run, bool top = false)
    {
        try
        {
            _deleteGate.Wait(_opt.Cancellation);
            try { _remote.Delete(token, "folder"); }
            finally { _deleteGate.Release(); }
            return MarkDone(run);
        }
        catch (LarkApiException ex) when ((ex.Code >= 500 && ex.Code < 600) || ex.Code == LarkApiException.ContentionCode)
        {
            Log.Info($"Lark couldn't delete “{path}” in one go ({ex.Message}); deleting what's inside it first");
        }
        var kids = _remote.ListFolder(token);
        if (kids is RemoteListing { Incomplete: true })
            throw new DeleteIncompleteException($"Couldn't finish deleting “{PathUtil.Name(path)}” in Lark: it has items that couldn't be listed yet. It will be tried again in a few minutes.");
        var incomplete = false;                       // this folder's own contents (not other folders of the same run)
        Parallel.ForEach(kids, new ParallelOptions { MaxDegreeOfParallelism = DeleteParallel, CancellationToken = _opt.Cancellation }, k =>
        {
            if (run.Abort) return;
            try
            {
                var gone = k.IsFolder ? DeleteFolderInLark(k.Token, PathUtil.Join(path, k.Name), run)
                                      : TryDelete(run, k.Token, k.Type, PathUtil.Join(path, k.Name));
                if (!gone) Volatile.Write(ref incomplete, true);
            }
            catch (LarkApiException ex) when (ex.IsGone) { }
            // A refusal on ONE item inside must not make the caller treat the whole folder as view only and put it
            // back: it is noted, the rest carries on and the next check retries.
            catch (Exception ex) when (ex is not (OperationCanceledException or NeedLoginException)) { Note(run, ex); Volatile.Write(ref incomplete, true); }
        });
        if (incomplete || run.Abort) return false;
        return TryDelete(run, token, "folder", path, top);
    }

    /// <summary>"Put them back" for deletions held for review. Deleted on the PC: shown again from Lark.
    /// Deleted in Lark: copies downloaded to this PC are uploaded again; cloud-only ones have no data
    /// here, so they're removed (they can be restored from Lark's trash).</summary>
    public void RestorePending(IEnumerable<PendingDelete> items)
    {
        foreach (var d in items)
        {
            if (d.InLark) { _state.RemoveTree(d.Path); continue; }
            var l = _local.Stat(d.Path);
            if (l == null) _state.RemoveTree(d.Path);
            else if (l.IsDir || l.OnDisk || !l.IsPlaceholder) { _state.Remove(d.Path); _putBack.Add(d.Path); }   // uploaded again
            else
            {
                try { _local.Delete(d.Path, recycle: false); _state.Remove(d.Path); }
                catch (Exception ex) { Log.Warn($"{d.Path}: {ex.Message}"); }
            }
        }
        _state.Commit();
    }

    // ================================================================ after uploads
    void ConfirmUploads()
    {
        var folders = _dirtyRemote.Distinct().ToList();
        if (folders.Count > 1) Progress($"Confirming uploads in Lark: 0 of {folders.Count:N0} folders…");
        try
        {
            _index.Refresh(folders, folders.Count > 1 ? (d, t) => Progress($"Confirming uploads in Lark: {d:N0} of {t:N0} folders…") : null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't re-check Lark after uploading ({ex.Message}); the next check looks at everything");
            _index.Invalidate();
            return;
        }
        var byToken = _index.Entries().GroupBy(e => e.Token).ToDictionary(g => g.Key, g => g.First());
        // What we did in this very pass is adopted as it is. A marker left by an earlier pass (its own re-check
        // failed) is adopted only if Lark's date isn't much newer than the marker: otherwise someone else edited it.
        foreach (var b in _state.All().Where(b => b.RMTimePending && b.Token != null))
            if (byToken.TryGetValue(b.Token!, out var r)
                && (b.PendingSince is not { } since || since >= _passStart.AddSeconds(-1) || r.MTimeUtc <= since + PendingSlack))
            {
                b.RMTime = r.MTime;
                _state.Put(b);
            }
    }
}
