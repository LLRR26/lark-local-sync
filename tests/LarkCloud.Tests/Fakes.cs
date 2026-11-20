using System.Collections.Concurrent;
using LarkCloud.Core;

namespace LarkCloud.Tests;

/// <summary>In-memory Lark Drive.</summary>
public sealed class FakeRemote : IRemoteDrive
{
    public sealed class Node
    {
        public string Token = "", Name = "", Type = Kinds.File, Parent = "";
        public byte[] Content = Array.Empty<byte>();
        public long MTime;
        public string? Url;
    }

    readonly object _lock = new();
    public readonly Dictionary<string, Node> Nodes = new();
    public readonly ConcurrentQueue<(string Op, string Arg)> Calls = new();
    public string RootToken { get; } = "fldROOT";
    long _clock = 1_700_000_000;
    int _seq;
    public Func<string, string, Exception?>? UploadFilter;     // (name, content) -> exception to throw
    public readonly HashSet<string> ReadOnly = new();          // folder tokens the person can only view
    public string MyRoot = "fldMINE";
    public bool? CanEdit(string token, string type) => !ReadOnly.Contains(token);
    public string MyFoldersRoot() { lock (_lock) if (!Nodes.ContainsKey(MyRoot)) Nodes[MyRoot] = new Node { Token = MyRoot, Name = "(My Folders)", Parent = "", Type = Kinds.Folder }; return MyRoot; }
    public int ListCalls;

    string Tok(string prefix) => $"{prefix}{Interlocked.Increment(ref _seq):D5}";
    public long Tick() => Interlocked.Increment(ref _clock);

    public string PathOf(string token)
    {
        lock (_lock)
        {
            var parts = new List<string>();
            while (token != RootToken && Nodes.TryGetValue(token, out var n)) { parts.Insert(0, n.Name); token = n.Parent; }
            return string.Join("/", parts);
        }
    }

    readonly Dictionary<string, string> _byPath = new();          // path -> token, checked before use (moves/renames make entries stale)

    public Node? Find(string path)
    {
        lock (_lock)
        {
            if (_byPath.TryGetValue(path, out var t) && Nodes.TryGetValue(t, out var hit) && PathOf(t) == path) return hit;
            var n = Nodes.Values.FirstOrDefault(n => PathOf(n.Token) == path);
            if (n != null) _byPath[path] = n.Token;
            return n;
        }
    }

    public string FolderFor(string path) => path.Length == 0 ? RootToken : Find(path)?.Token ?? AddFolder(path);

    public string AddFolder(string path)
    {
        var parent = FolderFor(PathUtil.Parent(path));
        return CreateFolder(parent, PathUtil.Name(path));
    }

    public string AddFile(string path, byte[] content)
    {
        var parent = FolderFor(PathUtil.Parent(path));
        var n = new Node { Token = Tok("box"), Name = PathUtil.Name(path), Parent = parent, Content = content, MTime = Tick() };
        lock (_lock) { Nodes[n.Token] = n; _byPath[PathUtil.Join(PathOf(parent), n.Name)] = n.Token; }
        return n.Token;
    }

    public string AddDoc(string path, string type = "docx")
    {
        var parent = FolderFor(PathUtil.Parent(path));
        var tok = Tok("dox");
        lock (_lock) Nodes[tok] = new Node { Token = tok, Name = PathUtil.Name(path), Parent = parent, Type = type, MTime = Tick(), Url = $"https://x.larksuite.com/{type}/{tok}" };
        return tok;
    }

    public void Edit(string path, byte[] content)
    {
        var n = Find(path)!;
        n.Content = content;
        n.MTime = Tick();
    }

    public void RemovePath(string path) => Delete(Find(path)!.Token, "file");
    public void MovePath(string path, string destFolder) => Move(Find(path)!.Token, "file", FolderFor(destFolder));
    public void RenamePath(string path, string name) => Rename(Find(path)!.Token, "file", name);

    public List<string> Paths()
    {
        lock (_lock) return Nodes.Keys.Select(PathOf).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public byte[]? Content(string path) => Find(path)?.Content;

    // ---------------------------------------------------------------- IRemoteDrive
    public Func<string, Exception?>? ListFault;                // by folder token: an error to throw instead of listing
    public Action? MutationHook;                               // runs after a move or rename

    public List<RemoteEntry> ListFolder(string folder)
    {
        Interlocked.Increment(ref ListCalls);
        if (ListFault?.Invoke(folder) is { } fault) throw fault;
        lock (_lock)
        {
            if (folder != RootToken && !Nodes.ContainsKey(folder)) throw new LarkApiException(1061007, "file has been delete.");
            return Nodes.Values.Where(n => n.Parent == folder).OrderBy(n => n.Token)
                .Select(n => new RemoteEntry { Path = n.Name, Token = n.Token, Type = n.Type, MTime = n.MTime.ToString(), Url = n.Url, ParentToken = folder })
                .ToList();
        }
    }

    public Action? SizeHook;

    public long ProbeSize(string token)
    {
        SizeHook?.Invoke();
        Calls.Enqueue(("size", token));
        lock (_lock) return Nodes[token].Content.Length;
    }

    public Stream OpenRead(string token, long offset, CancellationToken ct)
    {
        Calls.Enqueue(("read", token));
        lock (_lock) return new MemoryStream(Nodes[token].Content[(int)offset..]);
    }

    public string Upload(string src, string parent, string name, string? existing)
    {
        var content = File.ReadAllBytes(src);
        if (UploadFilter?.Invoke(name, name.EndsWith(".zip") ? System.Text.Encoding.Latin1.GetString(content) : System.Text.Encoding.UTF8.GetString(content)) is { } ex) throw ex;
        Calls.Enqueue(("upload", name));
        lock (_lock)
        {
            var target = existing != null && Nodes.TryGetValue(existing, out var ex0) ? ex0.Parent : parent;
            for (var t = target; t != RootToken && Nodes.TryGetValue(t, out var nn); t = nn.Parent)
                if (ReadOnly.Contains(t)) throw new LarkApiException(1061004, "forbidden");
            if (ReadOnly.Contains(RootToken) && target != MyRoot && !PathOf(target).StartsWith("(My Folders)")) throw new LarkApiException(1061004, "forbidden");
            if (existing != null && Nodes.TryGetValue(existing, out var n))
            {
                n.Content = content;
                n.MTime = Tick();
                return existing;
            }
            var tok = Tok("box");
            Nodes[tok] = new Node { Token = tok, Name = name, Parent = parent, Content = content, MTime = Tick() };
            _byPath[PathUtil.Join(PathOf(parent), name)] = tok;
            return tok;
        }
    }

    public string CreateFolder(string parent, string name)
    {
        if (parent == RootToken && ReadOnly.Contains(RootToken)) throw new LarkApiException(1061004, "forbidden");
        Calls.Enqueue(("mkdir", name));
        var tok = Tok("fld");
        lock (_lock) { Nodes[tok] = new Node { Token = tok, Name = name, Parent = parent, Type = Kinds.Folder, MTime = Tick() }; _byPath[PathUtil.Join(PathOf(parent), name)] = tok; }
        return tok;
    }

    public Action? DeleteDelay;
    public Func<string, Exception?>? DeleteFault;              // by token: an error to throw instead of deleting
    public int MaxFolderDelete = int.MaxValue;                 // like Lark: bigger folders fail with HTTP 500

    public void Delete(string token, string type)
    {
        DeleteDelay?.Invoke();
        if (DeleteFault?.Invoke(token) is { } fault) throw fault;
        Calls.Enqueue(("delete", token));
        lock (_lock)
        {
            if (!Nodes.ContainsKey(token)) throw new LarkApiException(1061007, "file has been delete.");
            int Count(string t) => Nodes.Values.Where(n => n.Parent == t).Sum(n => 1 + Count(n.Token));
            if (Nodes[token].Type == Kinds.Folder && Count(token) > MaxFolderDelete) throw new LarkApiException(500, "Internal Server Error");
            void Del(string t) { foreach (var c in Nodes.Values.Where(n => n.Parent == t).Select(n => n.Token).ToList()) Del(c); Nodes.Remove(t); }
            Del(token);
        }
    }

    public void Move(string token, string type, string dest)
    {
        Calls.Enqueue(("move", token));
        lock (_lock) { Nodes[token].Parent = dest; Nodes[token].MTime = Tick(); }
        MutationHook?.Invoke();
    }

    public void Rename(string token, string type, string newName)
    {
        Calls.Enqueue(("rename", token));
        lock (_lock) { Nodes[token].Name = newName; Nodes[token].MTime = Tick(); }
        MutationHook?.Invoke();
    }
}

/// <summary>In-memory "files on demand" folder that behaves like the Windows Cloud Files API.</summary>
public sealed class FakeLocal : ICloudLocal
{
    public sealed class Item
    {
        public bool IsDir;
        public byte[]? Data;                // null = placeholder without data (cloud-only)
        public long Size;
        public long MTime;
        public string? Identity;
        public bool InSync = true;
        public bool Pinned;
        public bool IsPlaceholder;
        public bool ReadOnly;
    }

    readonly object _lock = new();
    public readonly Dictionary<string, Item> Items = new(StringComparer.OrdinalIgnoreCase);
    readonly IRemoteDrive _remote;
    public Func<DateTime> Now = () => DateTime.UtcNow;
    public HashSet<string> OpenInApp = new(StringComparer.OrdinalIgnoreCase);
    public int Hydrations;

    public FakeLocal(IRemoteDrive remote) { _remote = remote; }
    public List<string> Unreadable = new();
    public IReadOnlyCollection<string> UnreadablePaths => Unreadable;
    public string Root => "C:\\Fake";
    public string FullPath(string rel) => Root + "\\" + rel.Replace('/', '\\');

    // ---------------------------------------------------------------- what a person does
    public void UserWrite(string rel, byte[] data, DateTime? when = null)
    {
        lock (_lock)
        {
            EnsureParents(rel);
            var t = (when ?? Now().AddMinutes(-1)).Ticks;
            if (Items.TryGetValue(rel, out var it) && !it.IsDir)
            {
                it.Data = data; it.Size = data.Length; it.MTime = t;
                if (it.IsPlaceholder) it.InSync = false;
            }
            else Items[rel] = new Item { Data = data, Size = data.Length, MTime = t };
        }
    }

    public void UserMkdir(string rel) { lock (_lock) { EnsureParents(rel); if (!Items.ContainsKey(rel)) Items[rel] = new Item { IsDir = true, MTime = Now().Ticks }; } }

    public void UserDelete(string rel)
    {
        lock (_lock) foreach (var k in Items.Keys.Where(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)).ToList()) Items.Remove(k);
    }

    public void UserMove(string from, string to) => Move(from, to);

    /// <summary>Open a file in an app: fetches the data from Lark if it's cloud-only.</summary>
    public byte[] UserOpen(string rel)
    {
        lock (_lock)
        {
            var it = Items[rel];
            if (it.Data == null)
            {
                using var s = _remote.OpenRead(it.Identity!, 0, CancellationToken.None);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                it.Data = ms.ToArray();
                Hydrations++;
            }
            return it.Data;
        }
    }

    public byte[]? Data(string rel) { lock (_lock) return Items.TryGetValue(rel, out var i) ? i.Data : null; }
    public List<string> Paths() { lock (_lock) return Items.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(); }

    void EnsureParents(string rel)
    {
        var p = PathUtil.Parent(rel);
        while (p.Length > 0)
        {
            if (!Items.ContainsKey(p)) Items[p] = new Item { IsDir = true, MTime = Now().Ticks };
            p = PathUtil.Parent(p);
        }
    }

    void RequireParent(string rel)
    {
        var p = PathUtil.Parent(rel);
        if (p.Length > 0 && !Items.ContainsKey(p)) throw new DirectoryNotFoundException(p);
    }

    // ---------------------------------------------------------------- ICloudLocal
    public List<LocalItem> Scan() { lock (_lock) return Items.Select(kv => Describe(kv.Key, kv.Value)).ToList(); }

    static LocalItem Describe(string key, Item i) => new()
    {
        Path = key, IsDir = i.IsDir, Size = i.IsDir ? 0 : i.Size, MTime = i.MTime, IsPlaceholder = i.IsPlaceholder,
        Identity = i.Identity, InSync = i.InSync, OnDisk = i.Data != null, Pinned = i.Pinned, LastUse = i.MTime, ReadOnly = i.ReadOnly,
    };

    public Func<string, LocalPresence>? PresenceOverride;
    public LocalPresence CheckPresence(string rel) =>
        PresenceOverride?.Invoke(rel) ?? (Stat(rel) == null ? LocalPresence.Missing : LocalPresence.Present);

    public LocalItem? Stat(string rel)
    {
        lock (_lock)
        {
            if (!Items.TryGetValue(rel, out var i)) return null;
            var key = Items.Keys.First(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase));
            return Describe(key, i);
        }
    }

    public void CreateFolderPlaceholder(string rel, string token)
    {
        lock (_lock)
        {
            RequireParent(rel);
            if (Items.ContainsKey(rel)) throw new IOException("exists");
            Items[rel] = new Item { IsDir = true, Identity = token, IsPlaceholder = true, MTime = Now().Ticks };
        }
    }

    public void CreateFilePlaceholder(string rel, string token, long size, DateTime mtimeUtc)
    {
        lock (_lock)
        {
            RequireParent(rel);
            if (Items.ContainsKey(rel)) throw new IOException("exists");
            Items[rel] = new Item { Size = size, MTime = mtimeUtc.Ticks, Identity = token, IsPlaceholder = true };
        }
    }

    public void UpdateFilePlaceholder(string rel, string token, long size, DateTime mtimeUtc)
    {
        lock (_lock)
        {
            if (OpenInApp.Contains(rel)) throw new IOException("The process cannot access the file because it is being used by another process.");
            var i = Items[rel];
            i.Size = size; i.MTime = mtimeUtc.Ticks; i.Identity = token; i.Data = null; i.InSync = true; i.IsPlaceholder = true;
        }
    }

    public LocalItem MarkSynced(string rel, string token)
    {
        lock (_lock)
        {
            if (OpenInApp.Contains(rel)) throw new IOException("in use");
            var i = Items[rel];
            i.IsPlaceholder = true; i.Identity = token; i.InSync = true;
            return Stat(rel)!;
        }
    }

    public void Move(string from, string to)
    {
        lock (_lock)
        {
            RequireParent(to);
            foreach (var k in Items.Keys.Where(k => k.Equals(from, StringComparison.OrdinalIgnoreCase) || k.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var v = Items[k];
                Items.Remove(k);
                Items[to + k[from.Length..]] = v;
            }
        }
    }

    public readonly List<(string Path, bool Recycled)> Deleted = new();

    public void Delete(string rel, bool recycle)
    {
        lock (_lock)
        {
            Deleted.Add((rel, recycle));
            if (Items.TryGetValue(rel, out var i) && i.IsDir && Items.Keys.Any(k => k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("not empty");
            Items.Remove(rel);
        }
    }

    public void Dehydrate(string rel) { lock (_lock) Items[rel].Data = null; }

    public void Hydrate(string rel, CancellationToken ct) => UserOpen(rel);

    public void WriteShortcut(string rel, string url)
    {
        lock (_lock)
        {
            RequireParent(rel);
            var d = System.Text.Encoding.UTF8.GetBytes($"[InternetShortcut]\r\nURL={url}\r\n");
            Items[rel] = new Item { Data = d, Size = d.Length, MTime = Now().Ticks };
        }
    }

    public void SetReadOnly(string rel, bool readOnly) { lock (_lock) Items[rel].ReadOnly = readOnly; }

    public Func<string, Exception?>? OpenFault;                // an error to throw when a file is opened for reading

    public readonly ConcurrentDictionary<string, int> Opens = new(StringComparer.OrdinalIgnoreCase);       // reads of a file's data for upload/verify
    public readonly ConcurrentDictionary<string, (long Size, string Hash)> MarkedWith = new(StringComparer.OrdinalIgnoreCase);

    public LocalItem? MarkUploaded(string rel, string token, long size, string hash)
    {
        MarkedWith[rel] = (size, hash);
        using var current = OpenForUpload(rel);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(current)) == hash ? MarkSynced(rel, token) : null;
    }

    public Stream OpenForUpload(string rel)
    {
        Opens.AddOrUpdate(rel, 1, (_, n) => n + 1);
        if (OpenFault?.Invoke(rel) is { } fault) throw fault;
        lock (_lock)
        {
            var i = Items[rel];
            return new MemoryStream(i.Data ?? throw new IOException("cloud-only file can't be read for upload"));
        }
    }
}

/// <summary>Exports a Lark doc to a "file" whose bytes say which doc and version it came from.</summary>
public sealed class FakeExtras : ILarkExtras
{
    readonly FakeRemote _r;
    readonly Dictionary<string, byte[]> _files = new();
    public int Exports;
    public FakeExtras(FakeRemote r) { _r = r; }

    public (string FileToken, long Size) Export(string docToken, string docType, string ext, CancellationToken ct)
    {
        Interlocked.Increment(ref Exports);
        var n = _r.Nodes[docToken];
        var data = System.Text.Encoding.UTF8.GetBytes($"{ext} export of {n.Name} @{n.MTime}");
        var ft = "exp" + Guid.NewGuid().ToString("N")[..6];
        lock (_files) _files[ft] = data;
        return (ft, data.Length);
    }

    public Stream OpenExport(string fileToken, CancellationToken ct)
    {
        lock (_files) return new MemoryStream(_files.TryGetValue(fileToken, out var d) ? d : throw new IOException("expired"));
    }

    public void ExpireAll() { lock (_files) _files.Clear(); }
}

public sealed class Env : IDisposable
{
    public readonly FakeRemote Remote = new();
    public readonly FakeExtras Extras;
    public readonly RemoteFeatures Features;
    public readonly FakeLocal Local;
    public readonly StateDb State;
    public readonly CloudEngine Engine;
    public readonly EngineOptions Options;
    public readonly List<string> Progress = new();
    readonly string _dir;

    public Env(int maxTransfers = 4, IRemoteDrive? inner = null)
    {
        _dir = Path.Combine(Path.GetTempPath(), "lcs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        State = new StateDb(Path.Combine(_dir, "state.db"));
        Extras = new FakeExtras(Remote);
        Features = new RemoteFeatures(inner ?? Remote, Extras, State.GetMeta, State.SetMeta);
        Local = new FakeLocal(Features);
        Options = new EngineOptions { MaxTransfers = maxTransfers, TenantHost = "x.larksuite.com", MachineName = "PC1", Label = "Shared" };
        Engine = new CloudEngine(Features, Local, State, new RemoteIndex(Features, 4), Options);
        Engine.Progress = m => { lock (Progress) Progress.Add(m); };
    }

    public SyncResult Sync(string? approvedDeletes = null, IReadOnlyCollection<string>? changed = null) => Engine.Run(approvedDeletes, changed);

    /// <summary>Another pass changes nothing.</summary>
    public void AssertSettled()
    {
        var r = Sync();
        Assert.False(r.Changed, "expected no changes, got: " + r.Summary());
        Assert.Empty(r.Failed);
    }

    public int Count(string op) => Remote.Calls.Count(c => c.Op == op);

    public void Dispose()
    {
        State.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
