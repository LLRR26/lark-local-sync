namespace LarkCloud.Core;

/// <summary>A cached copy of the synced Lark folder's tree. A full refresh lists every folder
/// (several at a time, level by level); a targeted refresh re-lists just a few folders.</summary>
public sealed class RemoteIndex
{
    sealed class Node
    {
        public required RemoteEntry Entry;     // Path = name only
        public string Parent = "";
    }

    readonly IRemoteDrive _remote;
    readonly int _workers;
    readonly object _lock = new();
    readonly Dictionary<string, Node> _nodes = new();
    readonly Dictionary<string, HashSet<string>> _kids = new();
    HashSet<string> _incomplete = new();       // folders whose listing had to skip an entry (see RemoteListing)
    public bool Complete { get; private set; }
    public int LastListCalls { get; private set; }

    public RemoteIndex(IRemoteDrive remote, int workers = 8)
    {
        _remote = remote;
        _workers = Math.Max(1, workers);
    }

    string Root => _remote.RootToken;

    /// <summary>Folders (tokens) whose last listing left an entry out: nothing under them may be treated as gone from Lark.</summary>
    public HashSet<string> IncompleteFolders() { lock (_lock) return new HashSet<string>(_incomplete); }

    public int FolderCount { get { lock (_lock) return _kids.Count; } }

    public void Invalidate() { lock (_lock) { Complete = false; _p = null; } }

    List<RemoteEntry>? ListOrGone(string folder)
    {
        try { return _remote.ListFolder(folder); }
        catch (LarkApiException ex) when (ex.IsGone && folder != Root) { return null; }
    }

    /// <summary>List folders in parallel; returns (folder, entries or null if it no longer exists).</summary>
    List<(string Folder, List<RemoteEntry>? Entries)> ListMany(List<string> folders)
    {
        var results = new (string, List<RemoteEntry>?)[folders.Count];
        Parallel.For(0, folders.Count, new ParallelOptions { MaxDegreeOfParallelism = _workers },
            i => results[i] = (folders[i], ListOrGone(folders[i])));
        return results.ToList();
    }

    /// <summary>A full refresh in progress that was paused: what is listed so far and what is still to do.</summary>
    sealed class Partial
    {
        public readonly Dictionary<string, Node> Nodes = new();
        public readonly Dictionary<string, HashSet<string>> Kids = new();
        public List<string> Level = new(), Next = new();
        public int Calls, Seen;
        public readonly HashSet<string> Incomplete = new();
        public readonly HashSet<string> Touched = new();   // folders our own changes (Refresh/Put/Remove) touched meanwhile
    }

    Partial? _p;
    /// <summary>Folders listed per step of a full refresh; a pause can happen between two steps.</summary>
    public int ChunkSize { get; init; } = 40;

    void Touch(string? folder) { if (_p != null && folder != null) _p.Touched.Add(folder); }   // call under _lock

    /// <summary>Lists every folder. If <paramref name="shouldYield"/> says so (checked between steps, never before
    /// the first complete listing) it stops and returns false; the next call carries on from there.</summary>
    public bool FullRefresh(Action<int, int>? progress = null, Func<bool>? shouldYield = null)
    {
        Partial p;
        lock (_lock) p = _p ??= new Partial { Level = new List<string> { Root } };
        try
        {
            var steps = 0;
            while (true)
            {
                if (p.Level.Count == 0)
                {
                    if (p.Next.Count == 0) break;
                    p.Level = p.Next.Distinct().ToList();
                    p.Next = new();
                }
                // (never before the first step of a call, so a resumed refresh always makes progress)
                if (steps > 0 && shouldYield != null && Complete && shouldYield()) return false;
                var chunk = p.Level.Take(Math.Max(1, ChunkSize)).ToList();
                foreach (var (folder, entries) in ListMany(chunk))
                {
                    p.Calls++;
                    p.Kids[folder] = new HashSet<string>();
                    if (entries is RemoteListing { Incomplete: true }) p.Incomplete.Add(folder);
                    foreach (var e in entries ?? new())
                    {
                        p.Kids[folder].Add(e.Token);
                        p.Nodes[e.Token] = new Node { Entry = e, Parent = folder };
                        if (e.IsFolder && !p.Kids.ContainsKey(e.Token)) p.Next.Add(e.Token);
                    }
                }
                p.Level.RemoveRange(0, chunk.Count);
                p.Seen += chunk.Count;
                progress?.Invoke(p.Seen, p.Seen + p.Level.Count + p.Next.Count);
                steps++;
            }
            List<string> touched;
            lock (_lock)
            {
                _nodes.Clear();
                _kids.Clear();
                foreach (var kv in p.Nodes) _nodes[kv.Key] = kv.Value;
                foreach (var kv in p.Kids) _kids[kv.Key] = kv.Value;
                _incomplete = p.Incomplete;
                LastListCalls = p.Calls;
                touched = p.Touched.ToList();
                _p = null;
                Complete = touched.Count == 0;
            }
            if (touched.Count > 0)
            {
                // Folders we changed while paused may be out of date in the snapshot (e.g. a file we just uploaded
                // is missing): list them again before anything may rely on this index. Complete stays false until done.
                var calls = p.Calls;
                Refresh(touched);
                lock (_lock) { LastListCalls += calls; Complete = true; }
            }
            return true;
        }
        catch
        {
            lock (_lock) { if (_p == p) _p = null; }
            throw;
        }
    }

    /// <summary>Re-list just these folders (and new sub-folders that appear in them).</summary>
    public void Refresh(IEnumerable<string> folderTokens, Action<int, int>? progress = null)
    {
        List<string> batch;
        lock (_lock)
        {
            batch = folderTokens.Distinct().Where(t => t == Root || _kids.ContainsKey(t)).ToList();
            foreach (var t in batch) Touch(t);
        }
        var listed = new HashSet<string>();
        var calls = 0;
        var total = batch.Count;
        while (batch.Count > 0)
        {
            batch = batch.Where(listed.Add).ToList();
            var results = ListMany(batch);
            var next = new List<string>();
            lock (_lock)
            {
                var removed = new List<(string Token, string Folder)>();       // dropped after all listings: another listing of this batch may show them moved
                foreach (var (folder, entries) in results)
                {
                    calls++;
                    Touch(folder);
                    _incomplete.Remove(folder);
                    if (entries is RemoteListing { Incomplete: true }) _incomplete.Add(folder);
                    if (entries == null)
                    {
                        if (_nodes.TryGetValue(folder, out var gone))
                        {
                            Touch(gone.Parent);
                            if (_kids.TryGetValue(gone.Parent, out var sib)) sib.Remove(folder);
                        }
                        Drop(folder);
                        continue;
                    }
                    var now = entries.Select(e => e.Token).ToHashSet();
                    foreach (var g in (_kids.GetValueOrDefault(folder) ?? new()).Except(now)) removed.Add((g, folder));
                    _kids[folder] = now;
                    foreach (var e in entries)
                    {
                        var movedHere = false;
                        if (_nodes.TryGetValue(e.Token, out var old) && old.Parent != folder && _kids.TryGetValue(old.Parent, out var oldSib))
                        {
                            Touch(old.Parent);
                            oldSib.Remove(e.Token);                  // moved here from another folder
                            movedHere = true;
                        }
                        _nodes[e.Token] = new Node { Entry = e, Parent = folder };
                        if (e.IsFolder && (!_kids.ContainsKey(e.Token) || movedHere) && !listed.Contains(e.Token)) next.Add(e.Token);   // (a moved folder is listed again: what we know of it may be out of date)
                    }
                }
                foreach (var (g, folder) in removed)
                    if (!_nodes.TryGetValue(g, out var n) || n.Parent == folder) Drop(g);       // (not if it now sits in another folder)
            }
            total += next.Distinct().Count();
            progress?.Invoke(calls, total);
            batch = next.Distinct().ToList();
        }
        LastListCalls = calls;
    }

    void Drop(string token)
    {
        if (_kids.Remove(token, out var children))
            foreach (var c in children) Drop(c);
        _nodes.Remove(token);
        _incomplete.Remove(token);
    }

    /// <summary>Every entry with its full relative path.</summary>
    public List<RemoteEntry> Entries()
    {
        lock (_lock)
        {
            var outList = new List<RemoteEntry>();
            void Walk(string folder, string prefix)
            {
                if (!_kids.TryGetValue(folder, out var children)) return;
                foreach (var t in children)
                {
                    if (!_nodes.TryGetValue(t, out var n)) continue;
                    var e = n.Entry.Clone();
                    e.Path = PathUtil.Join(prefix, n.Entry.Path);
                    e.ParentToken = folder;
                    outList.Add(e);
                    if (e.IsFolder) Walk(t, e.Path);
                }
            }
            Walk(Root, "");
            return outList;
        }
    }

    public bool HasFolder(string token) { lock (_lock) return _kids.ContainsKey(token); }

    /// <summary>Record our own change straight away (until the next listing confirms it).</summary>
    public void Put(RemoteEntry e, string parentToken)
    {
        lock (_lock)
        {
            var copy = e.Clone();
            copy.Path = e.Name;
            Touch(parentToken);
            if (_nodes.TryGetValue(e.Token, out var old)) Touch(old.Parent);
            if (e.IsFolder) Touch(e.Token);
            if (old != null && _kids.TryGetValue(old.Parent, out var sib)) sib.Remove(e.Token);
            _nodes[e.Token] = new Node { Entry = copy, Parent = parentToken };
            if (!_kids.TryGetValue(parentToken, out var k)) _kids[parentToken] = k = new HashSet<string>();
            k.Add(e.Token);
            if (e.IsFolder && !_kids.ContainsKey(e.Token)) _kids[e.Token] = new HashSet<string>();
        }
    }

    public void Remove(string token)
    {
        lock (_lock)
        {
            Touch(token);
            if (_nodes.TryGetValue(token, out var n))
            {
                Touch(n.Parent);
                if (_kids.TryGetValue(n.Parent, out var sib)) sib.Remove(token);
            }
            Drop(token);
        }
    }
}
