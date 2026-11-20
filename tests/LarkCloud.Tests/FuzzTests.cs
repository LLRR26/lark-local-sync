using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LarkCloud.Core;

namespace LarkCloud.Tests;

// Randomised ("model-based") and crash-mid-sync tests. They only run with the environment variable LARK_FUZZ=1,
// so scripts\build.bat (which runs every test) is not affected.
//
//   $env:LARK_FUZZ='1'; dotnet test tests\LarkCloud.Tests -c Release --filter FullyQualifiedName~FuzzTests
//
// Optional knobs: LARK_FUZZ_SEED=<n> (replay one seed, prints the whole action log), LARK_FUZZ_SEEDS=<count>,
// LARK_FUZZ_ROUNDS=<n>, LARK_FUZZ_CRASH_SEEDS=<count>, LARK_FUZZ_SHOW=<n> (reduced examples per problem kind),
// LARK_FUZZ_TRACE=<file> (+ LARK_FUZZ_CALM=1): prints both sides after every pass of a recorded script.
//
// How it is built (what the numbers do and do not cover):
//  * One Sim = FakeRemote + FakeLocal + the real CloudEngine + a StateDb file; the Tests' fakes are reused, wrapped by FuzzRemote/FuzzLocal.
//  * A "user" applies random steps to the PC and to Lark (create/edit/delete/move/rename of files, folders and Lark docs, and "twin" steps that
//    change the same item on both sides). Every step is recorded with concrete paths, so a failing run is replayed and reduced (ddmin).
//  * Invariants after the engine is quiet (two passes in a row change and fail nothing): tree equality by Lark ID (names compare as
//    PathUtil.SafeName(larkName) or a numbered variant, a .url shortcut matches by the doc token in its URL), no lost version (a version
//    must be live, in Lark's trash, or in the Recycle Bin when somebody deleted it in Lark), no duplicates, no redundant uploads, identity
//    of items nobody deleted, deletions that stick, bounded passes.
//  * Things the harness changes on purpose, so results are not about them: the engine clock runs 1 hour ahead (files the engine writes itself
//    are never "still being written"), every pass gets its own machine name (the minute-resolution conflict-copy name would otherwise
//    collide everywhere; Bug_second_conflict_of_the_same_file_in_the_same_minute_cannot_be_kept covers that), MaxTransfers = 1 (no parallelism),
//    failed paths are retried at once (Engine.RetryFailedNow), reading a cloud-only placeholder fetches its data from Lark (but fails when the
//    placeholder's size is out of date), FullPath() is a real folder with an empty file per item so the engine's "is the folder empty" check works.
//  * "Without the known move conflicts" runs the same generator but refuses steps that combine into the bugs listed below (Sim.Avoid), so a
//    clean run only says there is nothing else in the part of the space it still reaches.

/// <summary>[Fact] that is skipped unless LARK_FUZZ=1.</summary>
public sealed class FuzzFactAttribute : FactAttribute
{
    public FuzzFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LARK_FUZZ") != "1") Skip = "fuzz tests only run with LARK_FUZZ=1";
    }
}

// ==================================================================== harness
/// <summary>Kills the "process": the Nth mutating remote/local call throws (before or after it takes effect), and every call after that throws too.
/// It throws NeedLoginException because the engine rethrows that one everywhere instead of turning it into a per-file failure.</summary>
public sealed class CrashSwitch
{
    public bool Counting, Dead, After;
    public int Ops, CrashAt = -1;
    public string? Hit;
    public readonly List<string> Seen = new();
    static NeedLoginException Crash() => new("simulated crash");
    public void Check() { if (Dead) throw Crash(); }

    public T Op<T>(string what, Func<T> f)
    {
        Check();
        var mine = false;
        if (Counting)
        {
            Ops++;
            Seen.Add($"#{Ops} {what}");
            if (Ops == CrashAt)
            {
                mine = true;
                Hit = $"#{Ops} {what} ({(After ? "after" : "before")} it took effect)";
                if (!After) { Dead = true; throw Crash(); }
            }
        }
        var r = f();
        if (mine) { Dead = true; throw Crash(); }
        return r;
    }

    public void OpV(string what, Action f) => Op<int>(what, () => { f(); return 0; });
}

sealed class FuzzRemote : IRemoteDrive
{
    readonly FakeRemote _r;
    readonly Sim _s;
    public FuzzRemote(FakeRemote r, Sim s) { _r = r; _s = s; }
    public string RootToken => _r.RootToken;
    public List<RemoteEntry> ListFolder(string folderToken) { _s.C.Check(); return _r.ListFolder(folderToken); }
    public long ProbeSize(string token) { _s.C.Check(); return _r.ProbeSize(token); }
    public Stream OpenRead(string token, long offset, CancellationToken ct) { _s.C.Check(); return _r.OpenRead(token, offset, ct); }
    public string Upload(string srcPath, string parentToken, string name, string? existingToken) =>
        _s.C.Op($"upload {name}{(existingToken != null ? " (overwrite)" : " (new)")}", () => _r.Upload(srcPath, parentToken, name, existingToken));
    public string CreateFolder(string parentToken, string name) => _s.C.Op($"remote mkdir {name}", () => _r.CreateFolder(parentToken, name));
    public void Delete(string token, string type) => _s.C.OpV($"remote delete {_r.PathOf(token)}", () => { _s.CheckRemoteDelete(token); _s.RecordTrash(token); _r.Delete(token, type); });
    public void Move(string token, string type, string destFolderToken) => _s.C.OpV($"remote move {_r.PathOf(token)}", () =>
    {
        // like Lark: a folder can't go into itself
        for (var t = destFolderToken; t != _r.RootToken && _r.Nodes.TryGetValue(t, out var n); t = n.Parent)
            if (t == token) throw new LarkApiException(1061045, "can't move a folder into itself");
        _r.Move(token, type, destFolderToken);
    });
    public void Rename(string token, string type, string newName) => _s.C.OpV($"remote rename {_r.PathOf(token)}", () => _r.Rename(token, type, newName));
    public bool? CanEdit(string token, string type) => _r.CanEdit(token, type);
    public string MyFoldersRoot() => _r.MyFoldersRoot();
}

sealed class FuzzLocal : ICloudLocal
{
    readonly FakeLocal _f;
    readonly Sim _s;
    public FuzzLocal(FakeLocal f, Sim s) { _f = f; _s = s; }
    CrashSwitch C => _s.C;
    public string Root => _f.Root;
    // The engine asks "does this folder still have anything in it" with Directory.Exists/EnumerateFileSystemEntries on this path; the fake
    // lives in memory, so the folder (and an empty file for everything under it) is made on a real disk first, like the real app's folder.
    public string FullPath(string rel)
    {
        var real = Path.Combine(_s.RealRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        try { if (Directory.Exists(real)) Directory.Delete(real, true); } catch { }
        if (_f.Items.TryGetValue(rel, out var it) && it.IsDir)
        {
            Directory.CreateDirectory(real);
            foreach (var k in _f.Items.Keys.Where(k => k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).ToList())
            {
                var kp = Path.Combine(_s.RealRoot, k.Replace('/', Path.DirectorySeparatorChar));
                if (_f.Items[k].IsDir) Directory.CreateDirectory(kp); else { Directory.CreateDirectory(Path.GetDirectoryName(kp)!); File.WriteAllBytes(kp, Array.Empty<byte>()); }
            }
        }
        return real;
    }
    public List<LocalItem> Scan() { C.Check(); return _f.Scan(); }
    public LocalItem? Stat(string rel) { C.Check(); return _f.Stat(rel); }
    public LocalPresence CheckPresence(string rel) { C.Check(); return _f.CheckPresence(rel); }
    public void CreateFolderPlaceholder(string rel, string token) => C.OpV($"local create folder {rel}", () => _f.CreateFolderPlaceholder(rel, token));
    public void CreateFilePlaceholder(string rel, string token, long size, DateTime m) => C.OpV($"local create placeholder {rel}", () => _f.CreateFilePlaceholder(rel, token, size, m));
    public void UpdateFilePlaceholder(string rel, string token, long size, DateTime m) => C.OpV($"local update placeholder {rel}", () => _f.UpdateFilePlaceholder(rel, token, size, m));
    public LocalItem MarkSynced(string rel, string token) => C.Op($"local mark synced {rel}", () => _f.MarkSynced(rel, token));
    public LocalItem? MarkUploaded(string rel, string token, long size, string hash) => C.Op($"local mark uploaded {rel}", () => _f.MarkUploaded(rel, token, size, hash));
    public void Move(string from, string to) => C.OpV($"local move {from} -> {to}", () =>
    {
        // Windows refuses to move onto an existing name (the conflict copy's name only has minute resolution: see CONFLICT-NAME-COLLISION)
        if (!from.Equals(to, StringComparison.OrdinalIgnoreCase) && _f.Items.ContainsKey(to)) throw new IOException($"“{to}” already exists");
        _f.Move(from, to);
    });
    public void Delete(string rel, bool recycle) => C.OpV($"local delete {rel}{(recycle ? " (recycle bin)" : "")}", () =>
    {
        _s.LocalDelete(rel, recycle);
        _f.Delete(rel, recycle);
    });
    public void Dehydrate(string rel) => _f.Dehydrate(rel);
    public void Hydrate(string rel, CancellationToken ct) => _f.Hydrate(rel, ct);
    public void WriteShortcut(string rel, string url) => C.OpV($"local write shortcut {rel}", () => _f.WriteShortcut(rel, url));
    public Stream OpenForUpload(string rel)
    {
        C.Check();
        // Windows fetches the data of a cloud-only placeholder when something reads it (the app's own provider serves it from Lark)
        if (_f.Items.TryGetValue(rel, out var it) && !it.IsDir && it.Data == null && it.Identity != null)
        {
            // (a placeholder whose size no longer matches Lark's data can't be served: the provider refuses, as KnownLengthStream does in the real app)
            if (_s.Remote.Nodes.TryGetValue(it.Identity, out var node) && node.Content.Length != it.Size)
                throw new IOException($"the placeholder says {it.Size} bytes but Lark has {node.Content.Length}: it must be refreshed first");
            try { _f.Hydrate(rel, CancellationToken.None); }
            catch (Exception ex) when (ex is not NeedLoginException) { throw new IOException("couldn't fetch the file's data from Lark: " + ex.Message); }
        }
        return _f.OpenForUpload(rel);
    }
    public void SetReadOnly(string rel, bool readOnly) => _f.SetReadOnly(rel, readOnly);
    public IReadOnlyCollection<string> UnreadablePaths => _f.UnreadablePaths;
}

/// <summary>One synced folder: Lark + PC + engine + state db, plus a "user" that edits both sides and a ledger of every file version written.</summary>
public sealed class Sim : IDisposable
{
    public readonly FakeRemote Remote = new();
    public readonly CrashSwitch C = new();
    public readonly FakeLocal Local;
    readonly FuzzRemote _rw;
    readonly FuzzLocal _lw;
    readonly FakeExtras _extras;
    readonly string _dir;
    public string RealRoot => Path.Combine(_dir, "pc");
    readonly EngineOptions _opt = new() { MaxTransfers = 1, TenantHost = "x.larksuite.com", MachineName = "PC1", Label = "Shared",
        Now = () => DateTime.UtcNow.AddHours(1) };      // files the engine writes itself (shortcuts) are never "still being written"
    StateDb _state = null!;
    public CloudEngine Engine = null!;
    public readonly bool Calm;
    public readonly Random Rng;

    // ledger of file versions: "V12|xxxx…" (the length is unique per version, so a size match means the same version)
    public readonly Dictionary<int, char> Writer = new();
    public readonly HashSet<int> Superseded = new(), Exempt = new(), Trash = new(), Recycled = new();
    public readonly HashSet<string> Killed = new(), Moved = new(), TokensBefore = new();
    public readonly List<string> Log = new();
    public int NextV, NextName, NextTok, PcWrites, Conflicts, UploadsMark;
    public int PassNo;
    public bool UniqueConflictNames = true;
    /// <summary>Generator/replay refuses steps that combine with an earlier step (since the last check) into one of the known bugs
    /// (same item moved on both sides; item moved on the PC and edited in Lark), so the run can look for other problems.</summary>
    public bool Avoid;
    public readonly Dictionary<string, string> KilledAt = new();
    public readonly HashSet<string> PcMovedDirect = new(), LarkMovedDirect = new(), LarkEditedTok = new();
    public readonly HashSet<string> PcVacated = new(StringComparer.OrdinalIgnoreCase);     // paths a PC move left
    public readonly Dictionary<string, int> KilledVerAt = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> PcMoveTargets = new(StringComparer.OrdinalIgnoreCase);    // normalised, see Norm
    public readonly HashSet<string> LarkVacated = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> PcDirty = new();          // tokens of PC items (and folders above them) with PC-written content in this interval
    public readonly List<string> PcRelated = new();          // folders touched by PC moves (see RemoteIndex refresh bug)
    public readonly HashSet<string> KilledByLark = new(), KilledByPc = new();
    public readonly List<string> Violations = new();         // things the engine did that nobody asked for
    public bool InEngine;
    public int PcFolderMoves, LarkFolderMoves;           // folder moves on both sides can make a cycle
    public SyncResult? Last;
    public string Tag = "";

    public Sim(int seed, bool calm, bool avoid = false)
    {
        Calm = calm;
        Avoid = avoid;
        Rng = new Random(seed);
        _dir = Path.Combine(Path.GetTempPath(), "lcs-fuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(RealRoot);
        _extras = new FakeExtras(Remote);
        _rw = new FuzzRemote(Remote, this);
        Local = new FakeLocal(_rw);
        _lw = new FuzzLocal(Local, this);
        Boot();
    }

    void Boot()
    {
        _state = new StateDb(Path.Combine(_dir, "state.db"));
        var feat = new RemoteFeatures(_rw, _extras, _state.GetMeta, _state.SetMeta);
        Engine = new CloudEngine(feat, _lw, _state, new RemoteIndex(feat, 4), _opt);
    }

    /// <summary>The process dies: the in-memory sync records are lost (anything not committed to the db), the db connection is just dropped.</summary>
    public void Kill()
    {
        ((IDisposable)typeof(StateDb).GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_state)!).Dispose();
        C.Dead = false; C.Counting = false; C.CrashAt = -1;
        Boot();
    }

    public void Dispose()
    {
        try { _state.Dispose(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    public void Note(string s) => Log.Add(s);

    // ---------------------------------------------------------------- passes
    public SyncResult Pass()
    {
        // conflict copies are named "(conflict from <machine> <minute>)": a new "machine" name per pass keeps two conflicts of one file
        // from colliding within the same minute (that bug has its own test)
        if (UniqueConflictNames) _opt.MachineName = "PC" + (++PassNo);
        SyncResult r;
        InEngine = true;
        try { r = Engine.Run(); } finally { InEngine = false; }
        Conflicts += r.Conflicts.Count;
        Last = r;
        return r;
    }

    /// <summary>Passes until two in a row change nothing and fail nothing (bounded).</summary>
    public List<string> Quiesce(int max = 15)
    {
        var errs = new List<string>();
        var quiet = 0;
        var n = 0;
        while (n < max && quiet < 2)
        {
            n++;
            SyncResult r;
            Engine.RetryFailedNow();          // the engine parks a failed path for 2-30 minutes; a pass that really runs later would try it again
            try { r = Pass(); }
            catch (Exception ex) { errs.Add($"[EXC] pass threw {ex.GetType().Name}: {ex.Message}"); return errs; }
            if (!r.Changed && r.Failed.Count == 0 && r.PendingDeletes.Count == 0) quiet++; else quiet = 0;
        }
        if (quiet < 2)
        {
            var last = Last!;
            if (last.Failed.Count > 0)
                errs.Add("[STUCK] still failing after " + n + " passes: " + string.Join(" | ", last.Failed.Take(3).Select(kv => kv.Key + ": " + kv.Value)));
            else
                errs.Add($"[NONTERM] still changing after {n} passes: {last.Summary()}");
        }
        return errs;
    }

    // ---------------------------------------------------------------- version ledger
    public static int Vid(byte[]? b)
    {
        if (b == null || b.Length < 3 || b[0] != (byte)'V') return -1;
        var s = Encoding.ASCII.GetString(b, 1, Math.Min(10, b.Length - 1));
        var i = s.IndexOf('|');
        return i > 0 && int.TryParse(s[..i], out var v) ? v : -1;
    }

    byte[] NewVer(char writer, int prior)
    {
        var id = ++NextV;
        Writer[id] = writer;
        if (prior > 0) Superseded.Add(prior);
        return Encoding.UTF8.GetBytes($"V{id}|" + new string('x', id));
    }

    // ---------------------------------------------------------------- trash / recycle bin ledger (what the engine deleted)
    public List<FakeRemote.Node> Subtree(string tok)
    {
        var res = new List<FakeRemote.Node>();
        var guard = 0;
        void Rec(string t)
        {
            if (++guard > 10000) return;
            foreach (var c in Remote.Nodes.Values.Where(n => n.Parent == t).OrderBy(n => n.Token, StringComparer.Ordinal).ToList()) { res.Add(c); Rec(c.Token); }
        }
        if (Remote.Nodes.TryGetValue(tok, out var me)) res.Add(me);
        Rec(tok);
        return res;
    }

    /// <summary>The engine is deleting this PC item. The Recycle Bin only counts as a safe place for its content when somebody deleted the
    /// item (or a folder above it) in Lark; a copy the sync recycles for any other reason must still be somewhere else.</summary>
    public void LocalDelete(string rel, bool recycle)
    {
        var justified = !InEngine;
        for (var p = rel; !justified && p.Length > 0; p = PathUtil.Parent(p))
            if ((PcToken(p) is { } t && KilledByLark.Contains(t)) || (KilledAt.TryGetValue(p, out var t2) && KilledByLark.Contains(t2))) justified = true;   // (deleted in Lark, and a new file now has the old one's name: delete wins)
        if (recycle && justified) RecordRecycled(rel);
    }

    /// <summary>The engine is deleting this Lark item: somebody must have deleted it (or a folder above it) on the PC.</summary>
    public void CheckRemoteDelete(string token)
    {
        if (!InEngine) return;
        if (LarkChain(token).Any(KilledByPc.Contains)) return;
        Violations.Add($"[BAD-DELETE-LARK] the sync deleted {Remote.PathOf(token)} in Lark, but nobody deleted it on the PC");
    }

    public void RecordTrash(string token)
    {
        foreach (var n in Subtree(token))
            if (n.Type == Kinds.File && Vid(n.Content) is var v and > 0) Trash.Add(v);
    }

    public void RecordRecycled(string rel)
    {
        foreach (var (k, it) in Local.Items.ToList())
            if ((k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)) && Vid(it.Data) is var v and > 0)
                Recycled.Add(v);
    }

    // ---------------------------------------------------------------- tree helpers
    public bool UnderRoot(FakeRemote.Node n)
    {
        var t = n.Parent;
        for (var g = 0; g < 100; g++)
        {
            if (t == Remote.RootToken) return true;
            if (!Remote.Nodes.TryGetValue(t, out var p)) return false;
            t = p.Parent;
        }
        return false;
    }

    public Dictionary<string, FakeRemote.Node> LarkNodes() => Remote.Nodes.Values.Where(UnderRoot).ToDictionary(n => n.Token);

    int LarkDepth(FakeRemote.Node n)
    {
        var d = 1;
        for (var t = n.Parent; t != Remote.RootToken && Remote.Nodes.TryGetValue(t, out var p) && d < 50; t = p.Parent) d++;
        return d;
    }

    bool LarkInside(string tok, string folderTok)          // is `tok` the folder or inside it?
    {
        for (var g = 0; g < 100 && tok != Remote.RootToken; g++)
        {
            if (tok == folderTok) return true;
            if (!Remote.Nodes.TryGetValue(tok, out var n)) return false;
            tok = n.Parent;
        }
        return false;
    }

    string? PcToken(string path)
    {
        if (!Local.Items.TryGetValue(path, out var it)) return null;
        if (it.Identity != null) return it.Identity;
        if (!it.IsDir && it.Data != null && path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(Encoding.UTF8.GetString(it.Data), @"URL=.*/([^/\s]+)\s");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    IEnumerable<string> PcSubtree(string path) =>
        Local.Items.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList();

    public void Checkpoint()
    {
        TokensBefore.Clear();
        foreach (var t in LarkNodes().Keys) TokensBefore.Add(t);
        Killed.Clear(); Moved.Clear(); KilledAt.Clear(); PcVacated.Clear(); KilledVerAt.Clear(); PcMoveTargets.Clear(); PcRelated.Clear(); PcDirty.Clear(); LarkVacated.Clear(); KilledByLark.Clear(); KilledByPc.Clear(); Violations.Clear();
        PcMovedDirect.Clear(); LarkMovedDirect.Clear(); LarkEditedTok.Clear(); PcFolderMoves = LarkFolderMoves = 0;
        PcWrites = 0; Conflicts = 0;
        UploadsMark = Remote.Calls.Count(c => c.Op == "upload");
    }

    // ---------------------------------------------------------------- script: every user step is recorded with concrete paths, so a
    // failing run can be replayed and reduced (ddmin) without the random generator.
    public readonly List<string[]> Script = new();
    void Rec(params string[] step) => Script.Add(step);

    string? FolderTok(string path)
    {
        if (path.Length == 0) return Remote.RootToken;
        var n = Remote.Find(path);
        return n is { Type: Kinds.Folder } && UnderRoot(n) ? n.Token : null;
    }

    FakeRemote.Node? LarkNode(string path)
    {
        var n = path.Length == 0 ? null : Remote.Find(path);
        return n != null && UnderRoot(n) ? n : null;
    }

    /// <summary>Run one recorded step. "q"/"q0" return the invariant violations (quiesce, verify, checkpoint).</summary>
    public List<string> Exec(string[] st)
    {
        switch (st[0])
        {
            case "lfolder": LarkFolder(st[1], st[2]); break;
            case "lfile": LarkFile(st[1], st[2]); break;
            case "ldoc": LarkDoc(st[1], st[2]); break;
            case "ledit": LarkEdit(st[1]); break;
            case "ldel": LarkDelete(st[1]); break;
            case "lmove": LarkMove(st[1], st[2], st[3]); break;
            case "pcreate": PcCreate(st[1]); break;
            case "pmkdir": PcMkdir(st[1]); break;
            case "pedit": PcEdit(st[1]); break;
            case "pdel": PcDelete(st[1]); break;
            case "pmove": PcMove(st[1], st[2]); break;
            case "pass":
                Rec("pass"); Note("-- pass");
                try { Pass(); } catch (Exception ex) { return new List<string> { $"[EXC] pass threw {ex.GetType().Name}: {ex.Message}" }; }
                break;
            case "q": case "q0": return QuiesceVerify(st[0] == "q");
            case "crash":
                if (!CrashPass(int.Parse(st[1]), st[2] == "1")) return new List<string> { "[NOCRASH] the pass ended before that call" };
                return QuiesceVerify(false);
        }
        return new List<string>();
    }

    /// <summary>Kills the engine caught (and then went on to finish the pass): those points are not a real kill any more.</summary>
    public static readonly List<string> SwallowedKills = new();
    public int LastOps;
    public string? LastHit;
    public List<string> LastSeen = new();

    /// <summary>One pass during which the process dies at the n-th mutating call (n &lt;= 0: no crash, just count the calls).
    /// After a crash the engine is rebuilt over the same state db, like a restart. Returns true if it crashed.</summary>
    public bool CrashPass(int n, bool after)
    {
        C.Counting = true; C.Ops = 0; C.CrashAt = n; C.After = after; C.Hit = null; C.Dead = false; C.Seen.Clear();
        var crashed = false;
        var returned = false;
        try { Note($"-- pass (crash at call #{n}, {(after ? "after" : "before")} it takes effect)"); Pass(); returned = true; }
        catch (Exception) { if (!C.Dead) { C.Counting = false; throw; } }
        crashed = C.Dead;
        if (crashed && returned) lock (SwallowedKills) SwallowedKills.Add(C.Hit ?? "?");     // the engine caught the kill and finished the pass, including its end-of-pass commit
        LastOps = C.Ops; LastHit = C.Hit; LastSeen = C.Seen.ToList();
        C.Counting = false;
        if (crashed) { Note($"-- killed at {LastHit}; restart over the same db"); Kill(); }
        return crashed;
    }

    public List<string> QuiesceVerify(bool checkUploads)
    {
        Rec(checkUploads ? "q" : "q0"); Note("-- quiesce + check");
        var errs = Quiesce();
        if (errs.Count == 0) errs = FuzzCheck.Verify(this, checkUploads);
        if (errs.Count == 0) Checkpoint();
        return errs;
    }

    public static string Show(string[] st) => st[0] switch
    {
        "lfolder" => $"Lark mkdir  {Join(st[1], st[2])}",
        "lfile" => $"Lark create {Join(st[1], st[2])}",
        "ldoc" => $"Lark create doc {Join(st[1], st[2])}",
        "ledit" => $"Lark edit   {st[1]}",
        "ldel" => $"Lark delete {st[1]}",
        "lmove" => $"Lark move   {st[1]} -> {Join(st[2], st[3])}",
        "pcreate" => $"PC create   {st[1]}",
        "pmkdir" => $"PC mkdir    {st[1]}",
        "pedit" => $"PC edit     {st[1]}",
        "pdel" => $"PC delete   {st[1]}",
        "pmove" => $"PC move     {st[1]} -> {st[2]}",
        "pass" => "sync pass",
        "q0" => "sync until quiet, then check",
        "crash" => $"sync pass that is killed at mutating call #{st[1]} ({(st[2] == "1" ? "after" : "before")} it takes effect), restart, sync until quiet, then check",
        _ => "sync until quiet, then check (incl. upload count)",
    };
    static string Join(string a, string b) => PathUtil.Join(a, b);

    // ---------------------------------------------------------------- the user: Lark side (all by path)
    public void LarkFolder(string parentPath, string name)
    {
        if (FolderTok(parentPath) is not { } p) return;
        if (Avoid && (PcMoveTargets.Contains(Norm(Join(parentPath, name))) || LarkVacated.Contains(Join(parentPath, name)))) return;
        Rec("lfolder", parentPath, name);
        Remote.CreateFolder(p, name);
        Note($"Lark mkdir {Join(parentPath, name)}");
    }

    public void LarkFile(string parentPath, string name)
    {
        if (FolderTok(parentPath) is not { } p) return;
        if (Avoid && (PcMoveTargets.Contains(Norm(Join(parentPath, name))) || LarkVacated.Contains(Join(parentPath, name)))) return;
        Rec("lfile", parentPath, name);
        var tok = "zz" + (++NextTok).ToString("D5");
        var data = NewVer('L', -1);
        Remote.Nodes[tok] = new FakeRemote.Node { Token = tok, Name = name, Parent = p, Content = data, MTime = Remote.Tick() };
        Note($"Lark create {Join(parentPath, name)} V{Vid(data)}");
    }

    public void LarkDoc(string parentPath, string name)
    {
        if (FolderTok(parentPath) is not { } p) return;
        if (Avoid && (PcMoveTargets.Contains(Norm(Join(parentPath, name))) || LarkVacated.Contains(Join(parentPath, name)))) return;
        Rec("ldoc", parentPath, name);
        var tok = "zd" + (++NextTok).ToString("D5");
        Remote.Nodes[tok] = new FakeRemote.Node { Token = tok, Name = name, Parent = p, Type = "docx", MTime = Remote.Tick(), Url = $"https://x.larksuite.com/docx/{tok}" };
        Note($"Lark create doc {Join(parentPath, name)}");
    }

    public void LarkEdit(string path)
    {
        if (LarkNode(path) is not { Type: Kinds.File } n) return;
        if (Avoid && PcMovedDirect.Contains(n.Token)) return;
        Rec("ledit", path);
        LarkEditedTok.Add(n.Token);
        n.Content = NewVer('L', Vid(n.Content));
        n.MTime = Remote.Tick();
        Note($"Lark edit {path} -> V{Vid(n.Content)}");
    }

    public void LarkDelete(string path)
    {
        if (LarkNode(path) is not { } n) return;
        if (Avoid && n.Type != Kinds.File && n.Type != Kinds.Folder && PcMovedDirect.Contains(n.Token)) return;     // (shortcut moved on the PC, doc deleted in Lark)
        Rec("ldel", path);
        foreach (var x in Subtree(n.Token))
        {
            Killed.Add(x.Token);
            KilledByLark.Add(x.Token);
            if (x.Type == Kinds.File && Vid(x.Content) is var v and > 0 && Writer.GetValueOrDefault(v) == 'L') Exempt.Add(v);
        }
        Note($"Lark delete {path}");
        _rw.Delete(n.Token, n.Type);
    }

    public void LarkMove(string path, string destParentPath, string newName)
    {
        if (LarkNode(path) is not { } n || FolderTok(destParentPath) is not { } dest || LarkInside(dest, n.Token)) return;
        if (dest == n.Parent && newName == n.Name) return;
        if (Avoid && (Subtree(n.Token).Any(x => PcMovedDirect.Contains(x.Token) || PcDirty.Contains(x.Token)) || (n.Type == Kinds.Folder && PcFolderMoves > 0) || PcMoveTargets.Contains(Norm(Join(destParentPath, newName))) || LarkVacated.Contains(Join(destParentPath, newName)) || (dest != Remote.RootToken && !TokensBefore.Contains(dest)) || LarkChain(dest).Any(PcMovedDirect.Contains))) return;
        Rec("lmove", path, destParentPath, newName);
        LarkMovedDirect.Add(n.Token);
        LarkVacated.Add(path);
        if (n.Type == Kinds.Folder) LarkFolderMoves++;
        foreach (var x in Subtree(n.Token)) Moved.Add(x.Token);
        if (dest != n.Parent) _rw.Move(n.Token, n.Type, dest);
        if (newName != n.Name) _rw.Rename(n.Token, n.Type, newName);
        Note($"Lark move {path} -> {Join(destParentPath, newName)}");
    }

    // ---------------------------------------------------------------- the user: PC side (all by path)
    /// <summary>The version of a file the PC user is looking at (its data, or the Lark version the placeholder stands for).</summary>
    int PriorVersion(FakeLocal.Item it) =>
        it.IsDir ? -1 : it.Data != null ? Vid(it.Data)
        : it.Identity != null && Remote.Nodes.TryGetValue(it.Identity, out var n) && n.Content.Length == it.Size ? Vid(n.Content) : -1;

    /// <summary>A path whose item was deleted on the PC is used again: the new item replaces the old one for good.</summary>
    void Recreated(string path)
    {
        if (KilledAt.TryGetValue(path, out var back)) Moved.Add(back);
        if (KilledVerAt.TryGetValue(path, out var v) && v > 0) Superseded.Add(v);
    }

    IEnumerable<string> LarkChain(string tok)         // the item and the folders above it
    {
        for (var g = 0; g < 100 && tok != Remote.RootToken && Remote.Nodes.TryGetValue(tok, out var n); g++) { yield return tok; tok = n.Parent; }
    }

    /// <summary>"d2 (2)/f.txt" -> "d2/f.txt": the PC's number for a duplicate name in Lark is not part of the name.</summary>
    static string Norm(string path) => Regex.Replace(path, @" \(\d+\)(?=/|\.|$)", "");

    IEnumerable<string> PcChainTokens(string path)     // tokens of the item and of the folders above it
    {
        for (var p = path; p.Length > 0; p = PathUtil.Parent(p))
            if (PcToken(p) is { } t) yield return t;
    }

    bool LarkHasName(string path) => Remote.Find(Norm(path)) is { } n && UnderRoot(n);

    bool IntoLarkDeleted(string folderPath)
    {
        for (var p = folderPath; p.Length > 0; p = PathUtil.Parent(p))
            if (PcToken(p) is { } t && Killed.Contains(t)) return true;
        return false;
    }

    public void PcEdit(string path)
    {
        if (!Local.Items.TryGetValue(path, out var it) || it.IsDir || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return;
        if (Avoid && PcChainTokens(path).Any(LarkMovedDirect.Contains)) return;
        Rec("pedit", path);
        foreach (var t in PcChainTokens(path)) PcDirty.Add(t);
        var data = NewVer('P', PriorVersion(it));
        Local.UserWrite(path, data);
        PcWrites++;
        Note($"PC edit {path} -> V{Vid(data)}");
    }

    public void PcCreate(string path)
    {
        var parent = PathUtil.Parent(path);
        if (Local.Items.ContainsKey(path) || (parent.Length > 0 && !(Local.Items.TryGetValue(parent, out var pi) && pi.IsDir))) return;
        if (Avoid && (PcVacated.Contains(path) || LarkHasName(path) || PcChainTokens(parent).Any(LarkMovedDirect.Contains))) return;
        Rec("pcreate", path);
        foreach (var t in PcChainTokens(parent)) PcDirty.Add(t);
        Recreated(path);
        var data = NewVer('P', -1);
        Local.UserWrite(path, data);
        PcWrites++;
        Note($"PC create {path} V{Vid(data)}");
    }

    public void PcMkdir(string path)
    {
        var parent = PathUtil.Parent(path);
        if (Local.Items.ContainsKey(path) || (parent.Length > 0 && !(Local.Items.TryGetValue(parent, out var pi) && pi.IsDir))) return;
        if (Avoid && PcVacated.Contains(path)) return;
        Rec("pmkdir", path);
        Recreated(path);
        Local.UserMkdir(path);
        Note($"PC mkdir {path}");
    }

    public void PcDelete(string path)
    {
        if (!Local.Items.ContainsKey(path)) return;
        Rec("pdel", path);
        foreach (var k in PcSubtree(path))
        {
            if (PcToken(k) is { } t) { Killed.Add(t); KilledByPc.Add(t); KilledAt[k] = t; }
            KilledVerAt[k] = PriorVersion(Local.Items[k]);
            PcVacated.Add(k);
            if (Vid(Local.Items[k].Data) is var v and > 0 && Writer.GetValueOrDefault(v) == 'P') Exempt.Add(v);
        }
        Local.UserDelete(path);
        Note($"PC delete {path}");
    }

    public void PcMove(string from, string to)
    {
        if (!Local.Items.ContainsKey(from) || from.Equals(to, StringComparison.Ordinal) || Local.Items.ContainsKey(to)
            || to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)) return;
        var parent = PathUtil.Parent(to);
        if (parent.Length > 0 && !(Local.Items.TryGetValue(parent, out var pi) && pi.IsDir)) return;
        var direct = PcToken(from);
        var movingDir = Local.Items[from].IsDir;
        if (Avoid && (PcVacated.Contains(to) || LarkHasName(to) || IntoLarkDeleted(parent) || PcChainTokens(parent).Any(LarkMovedDirect.Contains) || (from.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && PcToken(from) is { } ut && Killed.Contains(ut)))) return;
        if (Avoid && ((direct != null && (LarkEditedTok.Contains(direct) || LarkChain(direct).Any(LarkMovedDirect.Contains))) || (movingDir && LarkFolderMoves > 0))) return;
        var touched = new[] { PathUtil.Parent(from), PathUtil.Parent(to) }.Concat(movingDir ? new[] { from, to } : Array.Empty<string>()).Where(x => x.Length > 0).ToList();
        if (Avoid && touched.Any(x => PcRelated.Any(y => x.Equals(y, StringComparison.OrdinalIgnoreCase) || x.StartsWith(y + "/", StringComparison.OrdinalIgnoreCase) || y.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase)))) return;
        PcRelated.AddRange(touched);
        Rec("pmove", from, to);
        if (movingDir) PcFolderMoves++;
        PcMoveTargets.Add(Norm(to));
        if (direct != null) PcMovedDirect.Add(direct);
        Recreated(to);
        foreach (var d in new[] { KilledAt, (Dictionary<string, string>)null! }.Where(x => x != null))
            foreach (var k in d.Keys.Where(k => k.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList()) { d[to + k[from.Length..]] = d[k]; d.Remove(k); }
        foreach (var k in KilledVerAt.Keys.Where(k => k.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList()) { KilledVerAt[to + k[from.Length..]] = KilledVerAt[k]; KilledVerAt.Remove(k); }
        foreach (var k in PcSubtree(from)) { PcVacated.Add(k); if (PcToken(k) is { } t) Moved.Add(t); }
        Local.UserMove(from, to);
        Note($"PC move {from} -> {to}");
    }

    // ---------------------------------------------------------------- random generator (chooses concrete steps, then performs them)
    public void BuildInitial()
    {
        var r = Rng;
        var folders = new List<string> { "" };
        for (var i = r.Next(1, 4); i > 0; i--)
        {
            var parent = folders[r.Next(folders.Count)];
            var name = NewName(false, true);
            LarkFolder(parent, name);
            if (!folders.Contains(Join(parent, name))) folders.Add(Join(parent, name));
        }
        for (var i = r.Next(4, 9); i > 0; i--) LarkFile(folders[r.Next(folders.Count)], NewName(false, false));
        if (r.Next(3) == 0) LarkDoc(folders[r.Next(folders.Count)], NewName(false, false).Replace(".txt", ""));
    }

    string NewName(bool pc, bool dir)
    {
        if (Calm) return $"{(pc ? "p" : "l")}{(dir ? "d" : "")}{++NextName}{(dir ? "" : ".txt")}";
        return dir ? $"d{Rng.Next(1, 4)}" : $"f{Rng.Next(1, 6)}.txt";
    }

    string Pick(List<string> l) => l[Rng.Next(l.Count)];

    static readonly string[] Ops = { "edit", "edit", "delete", "rename", "move", "move", "create", "mkdir" };

    /// <summary>One random user action on the PC (on `target` with `op` if given).</summary>
    public void PcAction(string? target = null, string? op = null)
    {
        var r = Rng;
        var keys = Local.Items.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var parents = new List<string> { "" };
        parents.AddRange(keys.Where(k => Local.Items[k].IsDir && PathUtil.Depth(k) < 3));
        if (target == null && keys.Count > 0 && r.Next(5) != 0) target = Pick(keys);
        op ??= Ops[r.Next(Ops.Length)];
        if (target == null || !Local.Items.TryGetValue(target, out var it))
        {
            if (op == "mkdir") PcMkdir(Join(Pick(parents), NewName(true, true)));
            else PcCreate(Join(Pick(parents), NewName(true, false)));
            return;
        }
        var isUrl = target.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
        switch (op)
        {
            case "edit": if (it.IsDir) PcCreate(Join(target, NewName(true, false))); else PcEdit(target); break;
            case "create": PcCreate(Join(it.IsDir ? target : PathUtil.Parent(target), NewName(true, false))); break;
            case "mkdir": PcMkdir(Join(it.IsDir ? target : PathUtil.Parent(target), NewName(true, true))); break;
            case "delete": PcDelete(target); break;
            case "rename": PcMove(target, Join(PathUtil.Parent(target), isUrl ? PathUtil.Name(target) : NewName(true, it.IsDir))); break;
            case "move":
                var dest = parents.Where(p => !(p.Equals(target, StringComparison.OrdinalIgnoreCase) || p.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase))).ToList();
                if (dest.Count == 0) break;
                PcMove(target, Join(Pick(dest), isUrl || r.Next(2) == 0 ? PathUtil.Name(target) : NewName(true, it.IsDir)));
                break;
        }
    }

    string LarkFileName() => !Calm && Rng.Next(10) == 0 ? $"q?{Rng.Next(1, 3)}.txt" : Calm && Rng.Next(10) == 0 ? $"l?{++NextName}.txt" : NewName(false, false);

    public void LarkAction(string? targetPath = null, string? op = null)
    {
        var r = Rng;
        var under = Remote.Nodes.Values.Where(UnderRoot).Select(n => Remote.PathOf(n.Token)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var parents = new List<string> { "" };
        parents.AddRange(under.Where(p => LarkNode(p)?.Type == Kinds.Folder && PathUtil.Depth(p) < 3));
        if (targetPath == null && under.Count > 0 && r.Next(5) != 0) targetPath = Pick(under);
        op ??= Ops[r.Next(Ops.Length)];
        var t = targetPath == null ? null : LarkNode(targetPath);
        if (t == null)
        {
            if (op == "mkdir") LarkFolder(Pick(parents), NewName(false, true));
            else LarkFile(Pick(parents), LarkFileName());
            return;
        }
        var isDoc = t.Type != Kinds.File && t.Type != Kinds.Folder;
        var folder = t.Type == Kinds.Folder;
        var parentPath = PathUtil.Parent(targetPath!);
        string NewLarkName() => isDoc ? NewName(false, false).Replace(".txt", "") : NewName(false, folder);
        switch (op)
        {
            case "edit": if (folder) LarkFile(targetPath!, LarkFileName()); else LarkEdit(targetPath!); break;
            case "create":
                if (r.Next(12) == 0) LarkDoc(folder ? targetPath! : parentPath, NewName(false, false).Replace(".txt", ""));
                else LarkFile(folder ? targetPath! : parentPath, LarkFileName());
                break;
            case "mkdir": LarkFolder(folder ? targetPath! : parentPath, NewName(false, true)); break;
            case "delete": LarkDelete(targetPath!); break;
            case "rename": LarkMove(targetPath!, parentPath, NewLarkName()); break;
            case "move":
                var dest = parents.Where(p => !(p.Equals(targetPath, StringComparison.OrdinalIgnoreCase) || p.StartsWith(targetPath + "/", StringComparison.OrdinalIgnoreCase))).ToList();
                if (dest.Count == 0) break;
                LarkMove(targetPath!, Pick(dest), r.Next(2) == 0 ? t.Name : NewLarkName());
                break;
        }
    }

    /// <summary>The same item changed on both sides before any pass.</summary>
    public void TwinAction()
    {
        var pairs = Local.Items.Where(kv => kv.Value.Identity != null && Remote.Nodes.TryGetValue(kv.Value.Identity, out var n) && UnderRoot(n))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (pairs.Count == 0) return;
        var (path, it) = pairs[Rng.Next(pairs.Count)];
        var larkPath = Remote.PathOf(it.Identity!);
        var ops = it.IsDir ? new[] { "create", "delete", "rename", "move", "edit", "mkdir" } : new[] { "edit", "delete", "rename", "move" };
        var pcOp = ops[Rng.Next(ops.Length)];
        var larkOp = ops[Rng.Next(ops.Length)];
        Note($"-- twin on {path}: PC {pcOp} / Lark {larkOp}");
        var tok = it.Identity!;
        PcAction(path, pcOp);
        if (Remote.Nodes.ContainsKey(tok)) LarkAction(Remote.PathOf(tok) == larkPath ? larkPath : larkPath, larkOp);
    }

    public void RandomAction()
    {
        var k = Rng.Next(10);
        if (k < 4) PcAction();
        else if (k < 8) LarkAction();
        else TwinAction();
    }
}

// ==================================================================== checks
public static class FuzzCheck
{
    /// <summary>The invariants after a quiet pass. Names: "[TREE]" the two sides disagree, "[LOSS]" a written version is nowhere,
    /// "[DUP]" identical content twice, "[REUPLOAD]" more uploads than edits, "[TOKEN-LOST]"/"[RESURRECT]" an item deleted when nobody
    /// deleted it / kept after a delete.</summary>
    public static List<string> Verify(Sim s, bool checkUploads = true)
    {
        var e = new List<string>(s.Violations);
        var R = s.Remote;
        var L = s.Local;
        var nodes = s.LarkNodes();
        var seen = new Dictionary<string, int>();

        // ---- (a) PC tree == Lark tree. Normalisation: a PC item is matched to its Lark item by Lark ID (placeholders carry
        // it; a .url shortcut carries the doc's token in its URL). Names compare as PathUtil.SafeName(larkName) (+ ".url" for
        // docs) or a numbered variant "name (2).ext". The parent must be the folder with the Lark parent's token.
        foreach (var (path, it) in L.Items.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var tok = it.Identity;
            var shortcut = false;
            if (tok == null && !it.IsDir && it.Data != null && path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(Encoding.UTF8.GetString(it.Data), @"URL=.*/([^/\s]+)\s");
                if (m.Success && nodes.ContainsKey(m.Groups[1].Value)) { tok = m.Groups[1].Value; shortcut = true; }
            }
            if (tok == null) { e.Add($"[TREE] PC item is not linked to Lark: {path}"); continue; }
            if (!nodes.TryGetValue(tok, out var n)) { e.Add($"[TREE] PC item {path} points at a Lark item that no longer exists"); continue; }
            seen[tok] = seen.GetValueOrDefault(tok) + 1;
            if (it.IsDir != (n.Type == Kinds.Folder)) e.Add($"[TREE] {path}: folder on one side, file on the other");
            var expect = PathUtil.SafeName(shortcut ? n.Name + ".url" : n.Name);
            if (!PathUtil.IsVariant(PathUtil.Name(path), expect)) e.Add($"[TREE] {path}: Lark calls it “{n.Name}” ({R.PathOf(tok)})");
            var pp = PathUtil.Parent(path);
            var ptok = pp.Length == 0 ? R.RootToken : L.Items.GetValueOrDefault(pp)?.Identity;
            if (ptok != n.Parent) e.Add($"[TREE] {path}: in a different folder than in Lark ({R.PathOf(tok)})");
            if (!it.IsDir && !shortcut)
            {
                if (it.Data != null) { if (!it.Data.SequenceEqual(n.Content)) e.Add($"[TREE] {path}: PC has V{Sim.Vid(it.Data)}, Lark has V{Sim.Vid(n.Content)}"); }
                else if (it.Size != n.Content.Length) e.Add($"[TREE] {path}: PC placeholder is for a different version than Lark's (size {it.Size} vs {n.Content.Length}, Lark V{Sim.Vid(n.Content)})");
            }
        }
        foreach (var n in nodes.Values)
        {
            var c = seen.GetValueOrDefault(n.Token);
            if (c == 0) e.Add($"[TREE] Lark item {R.PathOf(n.Token)} is missing on the PC");
            else if (c > 1) e.Add($"[TREE] Lark item {R.PathOf(n.Token)} shows {c} times on the PC");
        }

        // ---- (b) no data loss: every version that was not overwritten by a later edit and not deleted by its own author
        // must still exist: in Lark, on the PC, in Lark's trash (anything the engine or a Lark user deleted) or the Recycle Bin.
        var live = new HashSet<int>();
        foreach (var n in R.Nodes.Values) if (n.Type == Kinds.File && Sim.Vid(n.Content) is var v and > 0) live.Add(v);
        foreach (var it in L.Items.Values) if (Sim.Vid(it.Data) is var v and > 0) live.Add(v);
        foreach (var (v, w) in s.Writer.OrderBy(k => k.Key))
        {
            if (s.Superseded.Contains(v) || s.Exempt.Contains(v) || live.Contains(v) || s.Trash.Contains(v) || s.Recycled.Contains(v)) continue;
            e.Add($"[LOSS] V{v} (written by {(w == 'P' ? "the PC" : "Lark")}) exists nowhere: not in Lark, not on the PC, not in Lark's trash, not in the Recycle Bin");
        }

        // ---- (c) duplicates and redundant uploads
        foreach (var g in nodes.Values.Where(n => n.Type == Kinds.File && Sim.Vid(n.Content) > 0).GroupBy(n => Sim.Vid(n.Content)).Where(g => g.Count() > 1))
            e.Add($"[DUP] Lark has V{g.Key} {g.Count()} times: " + string.Join(", ", g.Select(n => R.PathOf(n.Token))));
        foreach (var g in L.Items.Where(k => Sim.Vid(k.Value.Data) > 0).GroupBy(k => Sim.Vid(k.Value.Data)).Where(g => g.Count() > 1))
            e.Add($"[DUP] the PC has V{g.Key} {g.Count()} times: " + string.Join(", ", g.Select(k => k.Key)));
        if (s.Calm)
            foreach (var g in nodes.Values.GroupBy(n => (n.Parent, n.Name.ToLowerInvariant())).Where(g => g.Count() > 1))
                e.Add($"[NAMEDUP] Lark has {g.Count()} items called “{g.First().Name}” in {R.PathOf(g.First().Parent)}");
        if (checkUploads)
        {
            var uploads = R.Calls.Count(c => c.Op == "upload") - s.UploadsMark;
            if (uploads > s.PcWrites + s.Conflicts)
                e.Add($"[REUPLOAD] {uploads} uploads for {s.PcWrites} PC edits/creations and {s.Conflicts} conflicts (moves/renames/deletes must not upload)");
        }

        // ---- identity: nothing nobody deleted may have been deleted (and re-created); nothing deleted may stay
        foreach (var t in s.TokensBefore.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!s.Killed.Contains(t) && !s.Moved.Contains(t) && !nodes.ContainsKey(t))
                e.Add($"[TOKEN-LOST] a Lark item nobody deleted is gone (token {t}); it was removed or replaced by the sync");
        }
        foreach (var t in s.Killed.OrderBy(x => x, StringComparer.Ordinal))
            if (!s.Moved.Contains(t) && nodes.ContainsKey(t))
                e.Add($"[RESURRECT] {R.PathOf(t)} was deleted on one side but is still in Lark (delete should win)");
        return e;
    }
}


// ==================================================================== tests
public class FuzzTests
{
    static int EnvInt(string name, int def) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : def;

    static T Guarded<T>(Func<T> f, TimeSpan limit, Func<T> onHang)
    {
        var t = Task.Run(f);
        return t.Wait(limit) ? t.Result : onHang();
    }

    internal static string Tag(string e) => e.Contains(']') ? e[e.IndexOf('[')..(e.IndexOf(']') + 1)] : "[?]";

    /// <summary>Run recorded steps on a fresh sim until the first check fails. Returns the problems and the steps that really ran.</summary>
    internal static (List<string> Errors, List<string[]> Effective) Replay(bool calm, IEnumerable<string[]> steps, bool avoid = false, bool uniqueConflictNames = true)
    {
        using var s = new Sim(1, calm, avoid) { UniqueConflictNames = uniqueConflictNames };
        var errs = new List<string>();
        try
        {
            foreach (var st in steps)
            {
                errs = s.Exec(st);
                if (errs.Count > 0) break;
            }
        }
        catch (Exception ex) { errs = new List<string> { $"[EXC] {ex.GetType().Name}: {ex.Message}" }; }
        return (errs, s.Script.ToList());
    }

    /// <summary>Delta debugging: drop chunks of steps while the same kind of problem still shows up.</summary>
    internal static List<string[]> Minimize(bool calm, List<string[]> steps, string tag, bool avoid = false)
    {
        bool Fails(List<string[]> c)
        {
            var (e, _) = Guarded(() => Replay(calm, c, avoid), TimeSpan.FromSeconds(20), () => (new List<string>(), new List<string[]>()));
            return e.Count > 0 && Tag(e[0]) == tag;
        }
        var n = 2;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (steps.Count >= 2 && clock.Elapsed < TimeSpan.FromSeconds(40))
        {
            var chunk = Math.Max(1, steps.Count / n);
            var reduced = false;
            for (var i = 0; i < steps.Count; i += chunk)
            {
                var cand = steps.Take(i).Concat(steps.Skip(i + chunk)).ToList();
                if (cand.Count > 0 && Fails(cand)) { steps = cand; n = Math.Max(n - 1, 2); reduced = true; break; }
            }
            if (!reduced) { if (chunk == 1) break; n = Math.Min(steps.Count, n * 2); }
        }
        return steps;
    }

    /// <summary>One seed of the random test: returns the problems found (empty = fine) and the recorded steps.</summary>
    internal static (List<string> Errors, List<string[]> Script) RunSeed(int seed, int rounds, bool avoid = false)
    {
        var errors = new List<string>();
        using var s = new Sim(seed, calm: seed % 2 == 0, avoid);
        try
        {
            s.BuildInitial();
            errors = s.QuiesceVerify(false);
            var r = s.Rng;
            for (var round = 1; round <= rounds && errors.Count == 0; round++)
            {
                s.Note($"=== round {round}");
                var waves = r.Next(4) == 0 ? 2 : 1;
                for (var w = 0; w < waves && errors.Count == 0; w++)
                {
                    for (var i = r.Next(1, 7); i > 0; i--) s.RandomAction();
                    if (w + 1 < waves || r.Next(3) == 0)
                        for (var p = r.Next(1, 3); p > 0 && errors.Count == 0; p--) errors = s.Exec(new[] { "pass" });
                }
                if (errors.Count > 0) break;
                if (round == rounds || r.Next(3) == 0) errors = s.QuiesceVerify(true);
                else if (r.Next(2) == 0) errors = s.Exec(new[] { "pass" });
            }
        }
        catch (Exception ex) { errors = new List<string> { $"[EXC] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" }; }
        return (errors, s.Script.ToList());
    }

    static void Describe(StringBuilder sb, bool calm, List<string> errors, List<string[]> script, bool avoid = false, string indent = "      ")
    {
        foreach (var x in errors.Take(4)) sb.AppendLine(indent + x);
        var min = Minimize(calm, script, Tag(errors[0]), avoid);
        var (e2, eff) = Replay(calm, min, avoid);
        sb.AppendLine($"{indent}reduced to {eff.Count} step(s) ({(calm ? "calm: every new name is unique" : "chaos: small shared name pool")} mode); outcome: {(e2.Count > 0 ? e2[0] : "(did not reproduce)")}");
        foreach (var st in eff) sb.AppendLine(indent + "  " + Sim.Show(st));
    }

    static void RandomRun(bool avoid)
    {
        var only = EnvInt("LARK_FUZZ_SEED", -1);
        var seeds = only >= 0 ? new[] { only } : Enumerable.Range(1, EnvInt("LARK_FUZZ_SEEDS", 200)).ToArray();
        var rounds = EnvInt("LARK_FUZZ_ROUNDS", 30);
        var failed = new List<(int Seed, List<string> Errors, List<string[]> Script)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var seed in seeds)
        {
            var (errs, script) = Guarded(() => RunSeed(seed, rounds, avoid), TimeSpan.FromSeconds(60),
                () => (new List<string> { "[HANG] seed did not finish in 60 s" }, new List<string[]>()));
            if (errs.Count > 0) failed.Add((seed, errs, script));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"{seeds.Length} seeds x {rounds} rounds in {clock.Elapsed.TotalSeconds:F0}s; {failed.Count} seed(s) failed. Replay: $env:LARK_FUZZ='1'; $env:LARK_FUZZ_SEED=<n>; dotnet test tests/LarkCloud.Tests -c Release --filter FullyQualifiedName~Random_syncs");
        foreach (var g in failed.GroupBy(f => Tag(f.Errors[0])).OrderBy(g => g.Key))
        {
            sb.AppendLine($"--- {g.Key}: {g.Count()} seed(s): {string.Join(",", g.Select(f => f.Seed).Take(40))}");
            foreach (var f in g.OrderBy(f => f.Script.Count).Take(EnvInt("LARK_FUZZ_SHOW", 3)))
            {
                sb.AppendLine($"    seed {f.Seed}:");
                Describe(sb, f.Seed % 2 == 0, f.Errors, f.Script, avoid);
            }
        }
        Assert.True(failed.Count == 0, sb.ToString());
    }

    /// <summary>Everything: includes the known bugs (same item moved on both sides, move on the PC + edit in Lark).</summary>
    [FuzzFact]
    public void Random_syncs_converge_keep_every_edit_and_make_no_duplicates() => RandomRun(avoid: false);

    /// <summary>The same random runs, minus the combinations behind the known bugs: anything this reports is another problem.</summary>
    [FuzzFact]
    public void Random_syncs_without_the_known_move_conflicts_converge_keep_every_edit_and_make_no_duplicates() => RandomRun(avoid: true);


    // ================================================================ crash tests
    internal sealed record Crashed(List<string> Errors, string Snapshot, int Uploads, int Ops, string? Hit, bool Died, List<string> Seen);

    static readonly Regex ConflictStamp = new(@"\(conflict from PC\d+ \d{4}-\d{2}-\d{2} \d{4}\)");

    /// <summary>Lark's final tree, names of conflict copies normalised.</summary>
    internal static string Snapshot(Sim s) => string.Join("\n", s.LarkNodes().Values
        .Select(n => ConflictStamp.Replace(s.Remote.PathOf(n.Token), "(conflict)") + " = " + (n.Type == Kinds.File ? "V" + Sim.Vid(n.Content) : n.Type))
        .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>prep + actions, then one pass that is killed at call n (0 = never), restart, run to quiet, check.</summary>
    internal static Crashed RunCrash(bool calm, IEnumerable<string[]> steps, int n, bool after)
    {
        using var s = new Sim(1, calm);
        foreach (var st in steps)
        {
            var e = s.Exec(st);
            if (e.Count > 0) return new Crashed(e.Select(x => "(before the crash) " + x).ToList(), "", 0, 0, null, false, new());
        }
        var uploadsBefore = s.Remote.Calls.Count(c => c.Op == "upload");
        bool died;
        try { died = s.CrashPass(n, after); }
        catch (Exception ex) { return new Crashed(new() { $"[EXC] {ex.GetType().Name}: {ex.Message}" }, "", 0, s.LastOps, null, false, new()); }
        var ops = s.LastOps; var hit = s.LastHit; var seen = s.LastSeen;
        if (n > 0 && !died) return new Crashed(new(), "", 0, ops, hit, false, seen);
        var errs = s.QuiesceVerify(false);
        return new Crashed(errs, Snapshot(s), s.Remote.Calls.Count(c => c.Op == "upload") - uploadsBefore, ops, hit, died, seen);
    }

    internal sealed record BadPoint(int N, bool After, string Hit, List<string> Problems);

    /// <summary>Kill the pass at every mutating call (before and after it takes effect). Compares each outcome with the same run without a crash.</summary>
    internal static (int Points, List<BadPoint> Bad) Sweep(bool calm, List<string[]> steps)
    {
        var bad = new List<BadPoint>();
        var baseline = RunCrash(calm, steps, 0, false);
        if (baseline.Errors.Count > 0) { bad.Add(new BadPoint(0, false, "(no crash at all)", baseline.Errors)); return (0, bad); }
        var points = 0;
        for (var n = 1; n <= baseline.Ops; n++)
            foreach (var after in new[] { false, true })
            {
                var r = RunCrash(calm, steps, n, after);
                if (!r.Died) continue;
                points++;
                var problems = new List<string>(r.Errors);
                if (r.Errors.Count == 0)
                {
                    if (r.Snapshot != baseline.Snapshot)
                    {
                        var a = baseline.Snapshot.Split('\n').ToHashSet(); var b = r.Snapshot.Split('\n').ToHashSet();
                        problems.Add("[CRASH-DIFF] result differs from the run without a crash. Only without crash: " + string.Join("; ", a.Except(b)) + "  |  only with crash: " + string.Join("; ", b.Except(a)));
                    }
                    else if (r.Uploads > baseline.Uploads)
                        problems.Add($"[CRASH-REUPLOAD] {r.Uploads} uploads instead of {baseline.Uploads}, same final tree");
                }
                if (problems.Count > 0) bad.Add(new BadPoint(n, after, r.Hit ?? "?", problems));
            }
        return (points, bad);
    }

    static string StepsText(IEnumerable<string[]> steps, string indent = "        ") => string.Concat(steps.Select(st => indent + Sim.Show(st) + "\n"));

    static string[] St(params string[] a) => a;

    /// <summary>Every crash point of one hand-written scenario; fails with the points that went wrong.</summary>
    static void SweepScenario(string title, bool calm, string[][] prep, string[][] actions)
    {
        var steps = prep.Append(St("q0")).Concat(actions).ToList();
        var (points, bad) = Sweep(calm, steps);
        var sb = new StringBuilder($"{title}: {points} crash points tried, {bad.Count} went wrong.\n");
        sb.Append(StepsText(steps));
        foreach (var g in bad.GroupBy(b => b.Problems[0].Split(' ')[0]).OrderBy(g => g.Key))
        {
            sb.AppendLine($"  --- {g.Key}: {g.Count()} crash point(s)");
            foreach (var b in g.Take(4)) sb.AppendLine($"    killed at {b.Hit}: {string.Join(" || ", b.Problems.Take(2))}");
        }
        Assert.True(bad.Count == 0, sb.ToString());
    }

    [FuzzFact]
    public void Crash_sweep_of_random_workloads_ends_in_the_same_state_as_without_a_crash()
    {
        var seeds = EnvInt("LARK_FUZZ_CRASH_SEEDS", 40);
        var rows = new List<(int Seed, int Points, List<BadPoint> Bad, List<string[]> Steps)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var seed = 1; seed <= seeds; seed++)
        {
            var calm = seed % 2 == 0;
            // a random workload: a tree, then one batch of changes on both sides (the next pass is the one that gets killed)
            List<string[]> steps;
            using (var g = new Sim(seed, calm, avoid: true))
            {
                g.BuildInitial();
                g.QuiesceVerify(false);
                for (var i = g.Rng.Next(3, 9); i > 0; i--) g.RandomAction();
                steps = g.Script.ToList();
            }
            var (points, bad) = Sweep(calm, steps);
            rows.Add((seed, points, bad, steps));
        }
        var sb = new StringBuilder($"{seeds} random workloads, {rows.Sum(r => r.Points)} crash points in {clock.Elapsed.TotalSeconds:F0}s; {rows.Count(r => r.Bad.Count > 0)} workload(s) with at least one bad crash point ({rows.Sum(r => r.Bad.Count)} bad points).\n");
        var all = rows.SelectMany(r => r.Bad.Select(b => (r.Seed, r.Steps, Bad: b))).ToList();
        foreach (var g in all.GroupBy(x => x.Bad.Problems[0].Split(' ')[0]).OrderBy(g => g.Key))
        {
            sb.AppendLine($"--- {g.Key}: {g.Count()} crash point(s) in {g.Select(x => x.Seed).Distinct().Count()} workload(s): seeds {string.Join(",", g.Select(x => x.Seed).Distinct().Take(20))}");
            foreach (var kind in g.GroupBy(x => Regex.Replace(x.Bad.Hit, @"#\d+ ", "").Replace(".txt", "").Split(' ')[0] + " " + (x.Bad.After ? "after" : "before")).OrderByDescending(k => k.Count()).Take(6))
                sb.AppendLine($"    killed at '{kind.Key}': {kind.Count()}x, e.g. seed {kind.First().Seed}: {kind.First().Bad.Hit}: {kind.First().Bad.Problems[0]}");
            var ex = g.OrderBy(x => x.Steps.Count).First();
            sb.AppendLine($"    smallest example: seed {ex.Seed}, killed at {ex.Bad.Hit}");
            sb.Append(StepsText(ex.Steps, "      "));
        }
        lock (Sim.SwallowedKills) sb.AppendLine($"Kills the engine swallowed (pass finished and committed anyway): {Sim.SwallowedKills.Count}" + (Sim.SwallowedKills.Count > 0 ? ": " + string.Join("; ", Sim.SwallowedKills.Distinct().Take(8)) : ""));
        Assert.True(all.Count == 0, sb.ToString());
    }


    // ---------------------------------------------------------------- hand-written crash scenarios (every crash point of one small change)
    // A scenario is "tree synced, then these changes, then the next pass dies at call N (before/after it takes effect), restart, run to quiet".
    [FuzzFact]
    public void Crash_while_uploading_a_new_PC_file() =>
        SweepScenario("new file on the PC", true, new string[0][], new[] { St("pcreate", "a.txt") });

    [FuzzFact]
    public void Crash_while_uploading_a_new_PC_folder_with_files() =>
        SweepScenario("new folder with two files on the PC", true, new string[0][],
            new[] { St("pmkdir", "d"), St("pcreate", "d/a.txt"), St("pcreate", "d/b.txt") });

    [FuzzFact]
    public void Crash_while_uploading_an_edit_of_a_synced_file() =>
        SweepScenario("edit of a synced file on the PC", true, new[] { St("lfile", "", "a.txt") }, new[] { St("pedit", "a.txt") });

    [FuzzFact]
    public void Crash_while_moving_and_renaming_a_synced_file_on_the_PC() =>
        SweepScenario("PC moves + renames a file", true, new[] { St("lfolder", "", "d"), St("lfile", "", "a.txt") }, new[] { St("pmove", "a.txt", "d/b.txt") });

    [FuzzFact]
    public void Crash_while_moving_a_synced_folder_on_the_PC() =>
        SweepScenario("PC moves a folder with a file in it", true,
            new[] { St("lfolder", "", "d"), St("lfile", "d", "a.txt"), St("lfolder", "", "e") }, new[] { St("pmove", "d", "e/d2") });

    [FuzzFact]
    public void Crash_while_deleting_a_synced_file_on_the_PC() =>
        SweepScenario("PC deletes a file", true, new[] { St("lfile", "", "a.txt") }, new[] { St("pdel", "a.txt") });

    [FuzzFact]
    public void Crash_while_deleting_a_synced_folder_on_the_PC() =>
        SweepScenario("PC deletes a folder with two files", true,
            new[] { St("lfolder", "", "d"), St("lfile", "d", "a.txt"), St("lfile", "d", "b.txt") }, new[] { St("pdel", "d") });

    [FuzzFact]
    public void Crash_while_showing_new_Lark_files_and_folders_on_the_PC() =>
        SweepScenario("Lark gets a folder with two files and a file", true, new string[0][],
            new[] { St("lfolder", "", "d"), St("lfile", "d", "a.txt"), St("lfile", "d", "b.txt"), St("lfile", "", "c.txt") });

    [FuzzFact]
    public void Crash_while_applying_a_Lark_edit_a_Lark_rename_and_a_Lark_move() =>
        SweepScenario("Lark edits one file, renames one, moves one", true,
            new[] { St("lfile", "", "a.txt"), St("lfile", "", "b.txt"), St("lfolder", "", "d"), St("lfile", "", "c.txt") },
            new[] { St("ledit", "a.txt"), St("lmove", "b.txt", "", "b2.txt"), St("lmove", "c.txt", "d", "c.txt") });

    [FuzzFact]
    public void Crash_while_applying_a_Lark_folder_rename_with_a_PC_deletion_inside() =>
        SweepScenario("Lark renames a folder, the PC deleted a file in it", true,
            new[] { St("lfolder", "", "d"), St("lfile", "d", "a.txt"), St("lfile", "d", "b.txt") },
            new[] { St("lmove", "d", "", "d2"), St("pdel", "d/a.txt") });

    [FuzzFact]
    public void Crash_while_applying_a_Lark_folder_rename_with_a_PC_edit_inside() =>
        SweepScenario("Lark renames a folder, the PC edited a file in it", true,
            new[] { St("lfolder", "", "d"), St("lfile", "d", "a.txt"), St("lfile", "d", "b.txt") },
            new[] { St("lmove", "d", "", "d2"), St("pedit", "d/a.txt") });

    [FuzzFact]
    public void Crash_while_applying_a_Lark_deletion() =>
        SweepScenario("Lark deletes a file and a folder with a file", true,
            new[] { St("lfile", "", "a.txt"), St("lfolder", "", "d"), St("lfile", "d", "b.txt") }, new[] { St("ldel", "a.txt"), St("ldel", "d") });

    [FuzzFact]
    public void Crash_while_keeping_both_versions_of_a_conflict() =>
        SweepScenario("both sides edit the same file", true, new[] { St("lfile", "", "a.txt") }, new[] { St("pedit", "a.txt"), St("ledit", "a.txt") });

    [FuzzFact]
    public void Crash_while_resolving_PC_edit_against_Lark_delete() =>
        SweepScenario("PC edits, Lark deletes", true, new[] { St("lfile", "", "a.txt") }, new[] { St("pedit", "a.txt"), St("ldel", "a.txt") });

    [FuzzFact]
    public void Crash_while_resolving_Lark_edit_against_PC_delete() =>
        SweepScenario("Lark edits, PC deletes", true, new[] { St("lfile", "", "a.txt") }, new[] { St("ledit", "a.txt"), St("pdel", "a.txt") });

    [FuzzFact]
    public void Crash_while_the_same_new_name_appears_on_both_sides() =>
        SweepScenario("same new file name on both sides", true, new string[0][], new[] { St("pcreate", "a.txt"), St("lfile", "", "a.txt") });

    [FuzzFact]
    public void Crash_while_deleting_a_doc_shortcut_on_the_PC() =>
        SweepScenario("PC deletes the shortcut of a Lark doc", true, new[] { St("ldoc", "", "plan") }, new[] { St("pdel", "plan.url") });


    // ================================================================ one named test per distinct bug found (each fails until the bug is fixed)
    /// <summary>Runs the steps (the last "q" is the check) and fails with the problem and the steps if anything is wrong.</summary>
    static void MustSync(string what, bool calm, string[][] steps, bool uniqueConflictNames = true)
    {
        var (errs, _) = Replay(calm, steps, false, uniqueConflictNames);
        Assert.True(errs.Count == 0, $"{what}\n" + StepsText(steps) + "\n  => " + string.Join("\n     ", errs));
    }

    /// <summary>Same, for a pass that is killed at call n (before/after it takes effect) and then restarted.</summary>
    static void MustSurviveCrash(string what, string[][] steps, int n, bool after)
    {
        var baseline = RunCrash(true, steps, 0, false);
        var r = RunCrash(true, steps, n, after);
        var problems = new List<string>(baseline.Errors.Select(x => "(without a crash) " + x));
        problems.AddRange(r.Errors);
        if (r.Errors.Count == 0 && r.Snapshot != baseline.Snapshot) problems.Add($"final Lark tree differs from the run without a crash:\n       without: {baseline.Snapshot.Replace("\n", "; ")}\n       with:    {r.Snapshot.Replace("\n", "; ")}");
        if (r.Errors.Count == 0 && r.Snapshot == baseline.Snapshot && r.Uploads > baseline.Uploads) problems.Add($"{r.Uploads} uploads instead of {baseline.Uploads}");
        Assert.True(problems.Count == 0, $"{what}; killed at call #{n} ({(after ? "after" : "before")} it takes effect: {string.Join(" / ", r.Seen)})\n" + StepsText(steps) + "\n  => " + string.Join("\n     ", problems));
    }

    // BUG-1: the same item moved or renamed on both sides before a pass. With the file on the PC cloud-only, the PC copy can never be
    // uploaded ("cloud-only file can't be read for upload") and nothing recovers; with data on the PC it is uploaded as a duplicate.
    [FuzzFact]
    public void Bug_file_moved_on_both_sides_is_uploaded_again_as_a_duplicate() => MustSync(
        "Lark moves a file to d3, the PC renames it to f1.txt before the next pass (CloudEngine.MoveLikePc, 'changed in Lark too: let phase C decide')", true,
        new[] { St("lfolder", "", "d3"), St("lfile", "", "f5.txt"), St("q0"), St("lmove", "f5.txt", "d3", "f5.txt"), St("pmove", "f5.txt", "f1.txt"), St("q") });

    [FuzzFact]
    public void Bug_file_moved_out_of_a_folder_on_the_PC_while_Lark_renames_the_folder_is_uploaded_again_as_a_new_file() => MustSync(
        "the PC moves d3/f5.txt (data on the PC) to d2/f2.txt and deletes d3 while Lark renames d3 to d1: Lark ends with d1/f5.txt AND d2/f2.txt",
        true, new[] { St("lfolder", "", "d3"), St("q0"), St("pcreate", "d3/f5.txt"), St("pmkdir", "d2"), St("pass"),
                      St("pmove", "d3/f5.txt", "d2/f2.txt"), St("pdel", "d3"), St("lmove", "d3", "", "d1"), St("q") });

    // BUG-2: A->C and B->A on the PC before a pass (a rename chain / swap). MoveLikePc skips C because A's old name is taken again
    // ('a copy, not a move'); B->A is then applied in Lark as a second item called A.
    [FuzzFact]
    public void Bug_two_PC_renames_in_a_chain_duplicate_a_file_and_leave_two_files_with_one_name_in_Lark() => MustSync(
        "PC renames a.txt to c.txt and then b.txt to a.txt before the pass", true,
        new[] { St("lfile", "", "a.txt"), St("lfile", "", "b.txt"), St("q0"), St("pmove", "a.txt", "c.txt"), St("pmove", "b.txt", "a.txt"), St("q") });

    [FuzzFact]
    public void Bug_two_PC_renames_in_a_chain_upload_a_duplicate_when_the_files_have_data() => MustSync(
        "same, both files edited on the PC first", true,
        new[] { St("lfile", "", "a.txt"), St("lfile", "", "b.txt"), St("q0"), St("pedit", "a.txt"), St("pedit", "b.txt"), St("q0"), St("pmove", "a.txt", "c.txt"), St("pmove", "b.txt", "a.txt"), St("q") });

    // BUG-3: rename a file on the PC and save a new file under the old name before the pass. The new file is uploaded over the renamed
    // file's Lark item (same Lark ID) and the renamed copy is fetched and uploaded as a new file. The content survives only because the
    // smaller upload goes first; a bigger new file, or parallel uploads, can overwrite the Lark original before it is read.
    [FuzzFact]
    public void Bug_renaming_a_file_and_then_creating_a_new_one_with_the_old_name_uploads_the_renamed_file_again_and_overwrites_its_Lark_item() => MustSync(
        "PC: f5.txt -> f3.txt, then a new f5.txt, before the pass. The rename is not seen as a move; the new file is uploaded over the old Lark item and the renamed copy is uploaded as a new file", true,
        new[] { St("lfile", "", "f5.txt"), St("q0"), St("pmove", "f5.txt", "f3.txt"), St("pcreate", "f5.txt"), St("q") });

    // BUG-4: a PC rename/move (which makes the engine adopt Lark's new date, MoveLikePc 'mb.RMTime = null') swallows an edit made in Lark
    // before the move was noticed: the PC keeps showing the old version (wrong size), and the next PC edit overwrites Lark's edit silently.
    [FuzzFact]
    public void Bug_Lark_edit_is_not_downloaded_when_the_file_was_renamed_on_the_PC() => MustSync(
        "PC renames a file while Lark edits it", true,
        new[] { St("lfolder", "", "ld1"), St("lfile", "ld1", "l6.txt"), St("q0"), St("pmove", "ld1/l6.txt", "p11.txt"), St("ledit", "ld1/l6.txt"), St("q") });

    [FuzzFact]
    public void Bug_PC_edit_after_a_missed_Lark_edit_overwrites_it_without_a_conflict()
    {
        using var s = new Sim(1, true);
        foreach (var st in new[] { St("lfile", "", "a.txt"), St("q0"), St("pmove", "a.txt", "b.txt"), St("ledit", "a.txt"), St("pass"), St("pass"), St("pedit", "b.txt") })
            s.Exec(st);
        var errs = s.QuiesceVerify(true);
        Assert.True(errs.Count == 0, "the PC renamed a.txt while Lark edited it, the sync ran, then the PC edited b.txt:\n" + string.Join("\n", s.Script.Select(x => "  " + Sim.Show(x))) + "\n  => " + string.Join("\n     ", errs));
    }

    // BUG-5: RemoteIndex.Refresh drops a folder's children when that folder was moved out of another re-listed folder in the same batch
    // (the re-listed folder 'a' is also dropped via its old parent). The engine then thinks everything inside is gone from Lark.
    [FuzzFact]
    public void Bug_PC_folder_deletion_is_forgotten_when_a_file_is_moved_out_and_the_parent_moved_in_the_same_pass() => MustSync(
        "PC: delete a/b, move a/f.txt out, move a to c/a2 (all before one pass)", true,
        new[] { St("lfolder", "", "a"), St("lfile", "a", "f.txt"), St("lfolder", "", "c"), St("q0"), St("lfolder", "a", "b"), St("pass"),
                St("pdel", "a/b"), St("pmove", "a/f.txt", "f.txt"), St("pmove", "a", "c/a2"), St("q") });

    [FuzzFact]
    public void Bug_PC_edit_goes_to_the_Recycle_Bin_when_its_folder_and_a_sibling_are_moved_in_the_same_pass() => MustSync(
        "PC: edit a/x.txt, move a/f.txt out, move a to c/a2 (the index loses a's children for one pass, so x.txt looks deleted in Lark)", true,
        new[] { St("lfolder", "", "a"), St("lfile", "a", "f.txt"), St("lfile", "a", "x.txt"), St("lfolder", "", "c"), St("q0"),
                St("pedit", "a/x.txt"), St("pmove", "a/f.txt", "f.txt"), St("pmove", "a", "c/a2"), St("q") });

    // BUG-6: Lark moves/renames a file but the engine cannot mirror it on the PC in that pass (the target folder is new, was moved on the PC, or the
    // target name is still used). Decide() then reads 'gone from this path' as 'deleted' (CloudEngine.cs ~656-663, no check that the Lark item still
    // exists elsewhere): the PC copy, with the user's edit, goes to the Recycle Bin and Lark's old version is downloaded in its place.
    [FuzzFact]
    public void Bug_PC_edit_goes_to_the_Recycle_Bin_when_Lark_moves_the_file_into_a_new_folder() => MustSync(
        "PC edits a/x.txt, Lark moves it into the brand-new folder a/n", true,
        new[] { St("lfolder", "", "a"), St("lfile", "a", "x.txt"), St("q0"), St("pedit", "a/x.txt"), St("lfolder", "a", "n"), St("lmove", "a/x.txt", "a/n", "x.txt"), St("q") });

    [FuzzFact]
    public void Bug_PC_edit_goes_to_the_Recycle_Bin_when_Lark_renames_the_file_to_a_name_that_a_Lark_rename_chain_has_not_freed_yet() => MustSync(
        "Lark: f3 -> f4 and f5 -> f3 in one go; the PC edited f5", true,
        new[] { St("lfile", "", "f3.txt"), St("lfile", "", "f5.txt"), St("q0"), St("lmove", "f3.txt", "", "f4.txt"), St("pedit", "f5.txt"), St("lmove", "f5.txt", "", "f3.txt"), St("q") });

    [FuzzFact]
    public void Bug_new_PC_file_goes_to_the_Recycle_Bin_when_it_takes_the_name_of_a_file_that_Lark_renamed_and_the_PC_deleted() => MustSync(
        "PC deletes f2 and creates a new f2 while Lark renamed f2 to f4 (f4 was free after a Lark delete)", true,
        new[] { St("lfile", "", "f4.txt"), St("pcreate", "f2.txt"), St("q0"), St("ldel", "f4.txt"), St("pdel", "f2.txt"), St("lmove", "f2.txt", "", "f4.txt"), St("pcreate", "f2.txt"), St("q") });

    // BUG-7: PC and Lark each move a folder into the other: a cycle. Lark refuses the PC's move; the engine retries forever with a message that blames permissions.
    [FuzzFact]
    public void Bug_folders_moved_into_each_other_on_the_two_sides_are_deleted_in_Lark() => MustSync(
        "PC moves ld2 into ld15/ld33, Lark moves ld15 into ld2", true,
        new[] { St("lfolder", "", "ld1"), St("lfolder", "ld1", "ld2"), St("lfolder", "", "ld15"), St("lfolder", "ld15", "ld33"), St("q0"),
                St("pmove", "ld1/ld2", "ld15/ld33/ld2"), St("lmove", "ld15", "ld1/ld2", "ld15"), St("q") });

    // BUG-8: the PC moves something into a folder Lark has deleted: the move fails in Lark ('file has been delete'), the deletion of the PC folder
    // fails ('not empty'): both retry forever.
    [FuzzFact]
    public void Bug_PC_move_into_a_folder_deleted_in_Lark_deletes_the_moved_folder_in_Lark() => MustSync(
        "Lark deletes ld2, the PC made ld2/pd13 and moved ld1 into it", true,
        new[] { St("lfolder", "", "ld1"), St("lfolder", "", "ld2"), St("q0"), St("pmkdir", "ld2/pd13"), St("ldel", "ld2"), St("pmove", "ld1", "ld2/pd13/pd14"), St("q") });

    // BUG-9: conflict copies are named only to the minute, so a second conflict of one file within the same minute cannot be kept
    // ('already exists'), and the file is stuck until a later minute.
    [FuzzFact]
    public void Bug_second_conflict_of_the_same_file_in_the_same_minute_cannot_be_kept() => MustSync(
        "two conflicts of a.txt within one minute", true,
        new[] { St("lfile", "", "a.txt"), St("q0"), St("pedit", "a.txt"), St("ledit", "a.txt"), St("q0"), St("pedit", "a.txt"), St("ledit", "a.txt"), St("q") },
        uniqueConflictNames: false);

    // BUG-11 (small): the shortcut of a doc that Lark deleted is moved on the PC: it is uploaded to Lark as an ordinary file.
    [FuzzFact]
    public void Bug_moved_shortcut_of_a_deleted_Lark_doc_is_uploaded_as_a_file() => MustSync(
        "Lark deletes the folder with a doc, the PC moved the doc's .url shortcut out", true,
        new[] { St("lfolder", "", "ld1"), St("ldoc", "ld1", "plan"), St("lfolder", "", "ld42"), St("q0"), St("ldel", "ld1"), St("pmove", "ld1/plan.url", "ld42/plan.url"), St("q") });

    // BUG-12 (small): a rename in Lark changes the file's date in Lark, and a PC edit of that file is then compared as 'changed in Lark too':
    // the PC edit is kept as a conflict copy although Lark's content never changed.
    [FuzzFact]
    public void Bug_PC_edit_plus_Lark_rename_makes_a_conflict_copy_although_Lark_did_not_change_the_content()
    {
        using var s = new Sim(1, true);
        foreach (var st in new[] { St("lfile", "", "a.txt"), St("q0"), St("pedit", "a.txt"), St("lmove", "a.txt", "", "b.txt") }) s.Exec(st);
        var errs = s.QuiesceVerify(true);
        var files = s.LarkNodes().Values.Where(n => n.Type == Kinds.File).Select(n => n.Name).OrderBy(x => x).ToList();
        Assert.True(errs.Count == 0 && files.Count == 1, "PC edits a.txt, Lark renames it to b.txt. Expected one file (b.txt with the PC's edit); Lark has: " + string.Join(", ", files) + (errs.Count > 0 ? "\n  => " + string.Join("\n     ", errs) : ""));
    }

    // ---------------------------------------------------------------- crash points
    // CRASH-1: killed after the upload of an EDIT, before the state db knows: on restart Lark's new date looks like a change made in Lark,
    // so the (identical) PC file is kept as a conflict copy and uploaded again: two copies in Lark.
    [FuzzFact]
    public void Crash_after_uploading_an_edit_leaves_a_conflict_copy_with_the_same_content() =>
        MustSurviveCrash("edit of a synced file", new[] { St("lfile", "", "a.txt"), St("q0"), St("pedit", "a.txt") }, 1, after: true);

    // CRASH-2: killed between the two remote calls of a PC move (Move, then Rename): the half-moved item looks like 'moved in Lark too',
    // the PC copy is then uploaded as a new file: stuck for a cloud-only file, a duplicate for one with data.
    [FuzzFact]
    public void Crash_between_the_remote_move_and_the_remote_rename_of_a_PC_move_uploads_a_duplicate() =>
        MustSurviveCrash("PC moves a.txt into d and renames it to b.txt", new[] { St("lfolder", "", "d"), St("lfile", "", "a.txt"), St("q0"), St("pmove", "a.txt", "d/b.txt") }, 1, after: true);

    // CRASH-3: killed after the placeholder was refreshed from Lark but before the state db was written: the refreshed placeholder looks edited,
    // so Lark's own new version is treated as a PC edit -> conflict -> the cloud-only 'PC version' can't be uploaded, stuck.
    [FuzzFact]
    public void Crash_after_refreshing_a_placeholder_from_Lark_makes_a_conflict_copy_with_the_same_content() =>
        MustSurviveCrash("Lark edited a.txt", new[] { St("lfile", "", "a.txt"), St("q0"), St("ledit", "a.txt") }, 1, after: true);

    // CRASH-4 (data loss): killed in the middle of keeping both versions of a conflict, right after the PC file was renamed to the conflict copy.
    // On restart the renamed file still carries a.txt's Lark ID and is treated as 'a.txt moved here': it is renamed in Lark (to the conflict name)
    // and uploaded over it, so Lark's own edit is overwritten.
    [FuzzFact]
    public void Crash_in_the_middle_of_a_conflict_overwrites_the_Lark_edit() =>
        MustSurviveCrash("both sides edit a.txt", new[] { St("lfile", "", "a.txt"), St("q0"), St("pedit", "a.txt"), St("ledit", "a.txt") }, 1, after: true);

    // CRASH-5: killed after Lark's folder rename was applied on the PC but before the state db was written: a file the PC deleted meanwhile
    // comes back (the deletion is forgotten).
    [FuzzFact]
    public void Crash_after_applying_a_Lark_folder_rename_brings_back_a_file_the_PC_had_deleted() =>
        MustSurviveCrash("Lark renames folder d to d2, the PC deleted d/a.txt", new[] { St("lfolder", "", "d"), St("lfile", "d", "a.txt"), St("lfile", "d", "b.txt"), St("q0"), St("lmove", "d", "", "d2"), St("pdel", "d/a.txt") }, 1, after: true);

    /// <summary>Both sides and the sync records, as text (used by the tracing helper).</summary>
    public static string Dump(Sim s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("    Lark:");
        foreach (var n in s.Remote.Nodes.Values.Where(s.UnderRoot).OrderBy(n => s.Remote.PathOf(n.Token), StringComparer.Ordinal))
            sb.AppendLine($"      {s.Remote.PathOf(n.Token)}  [{n.Token}] {(n.Type == Kinds.File ? "V" + Sim.Vid(n.Content) : n.Type)}");
        sb.AppendLine("    engine's view of Lark: " + string.Join(", ", s.Engine.Index.Entries().Select(e => e.Path).OrderBy(x => x, StringComparer.Ordinal)));
        sb.AppendLine("    PC:");
        foreach (var (k, it) in s.Local.Items.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.AppendLine($"      {k}  [{it.Identity}] {(it.IsDir ? "dir" : it.Data != null ? "V" + Sim.Vid(it.Data) : "cloud-only size " + it.Size)}{(it.InSync ? "" : " (edited)")}");
        return sb.ToString();
    }

    /// <summary>Debug helper: LARK_FUZZ_TRACE=<file with one step per line, fields separated by |>; fails with a before/after dump of every step.</summary>
    [FuzzFact]
    public void Trace_script_from_file()
    {
        var file = Environment.GetEnvironmentVariable("LARK_FUZZ_TRACE");
        if (string.IsNullOrEmpty(file)) return;
        using var s = new Sim(1, calm: Environment.GetEnvironmentVariable("LARK_FUZZ_CALM") == "1");
        var sb = new StringBuilder();
        foreach (var line in File.ReadAllLines(file).Where(l => l.Trim().Length > 0))
        {
            var st = line.Split('|');
            sb.AppendLine("> " + line);
            var errs = s.Exec(st);
            if (st[0] is "pass" or "q" or "q0") { sb.Append(Dump(s)); if (s.Last != null) sb.AppendLine("    result: " + s.Last.Summary() + string.Concat(s.Last.Failed.Select(f => $"{Environment.NewLine}    FAILED {f.Key}: {f.Value}"))); }
            foreach (var e in errs) sb.AppendLine("    !! " + e);
        }
        Assert.Fail(sb.ToString());
    }
}
