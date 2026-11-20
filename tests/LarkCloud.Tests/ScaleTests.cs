using System.Diagnostics;
using System.Text;
using LarkCloud.Core;
using Xunit.Abstractions;

namespace LarkCloud.Tests;

/// <summary>Tens of thousands of files per pair, size-aware upload scheduling, hash-once snapshots.</summary>
public class ScaleTests(ITestOutputHelper output)
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    static IEnumerable<string> Paths(string prefix, int n) =>
        Enumerable.Range(0, n).Select(i => $"{prefix}{i % 50:D2}/sub{i / 50 % 10}/deep{i / 500 % 4}/f{i}.dat");

    [Fact]
    public void Twenty_thousand_files_sync_and_a_pass_of_two_thousand_mixed_changes_ends_correct()
    {
        using var env = new Env(maxTransfers: 8);
        var sw = Stopwatch.StartNew();
        var pcFiles = Paths("pc", 10_000).ToList();
        var larkFiles = Paths("lark", 10_000).ToList();
        foreach (var p in pcFiles) env.Local.UserWrite(p, B("pc:" + p));
        foreach (var p in larkFiles) env.Remote.AddFile(p, B("lark:" + p));
        output.WriteLine($"seed: {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var first = env.Sync();
        var initial = sw.ElapsedMilliseconds;
        output.WriteLine($"initial sync: {initial} ms");
        Assert.Empty(first.Failed);
        Assert.Equal(10_000, first.Uploaded);
        Assert.Equal(20_000, env.Remote.Nodes.Values.Count(n => n.Type == Kinds.File));
        Assert.Equal(20_000, env.Local.Items.Values.Count(i => !i.IsDir));

        // ~2,000 changes of every kind
        var expectLocal = new Dictionary<string, string>();                       // path -> content, for files that must exist on both sides
        foreach (var p in pcFiles) expectLocal[p] = "pc:" + p;
        foreach (var p in larkFiles) expectLocal[p] = "lark:" + p;
        var gone = new HashSet<string>();
        for (var i = 0; i < 400; i++) { var p = pcFiles[i * 7]; env.Local.UserWrite(p, B("pc-edit:" + p)); expectLocal[p] = "pc-edit:" + p; }          // uploads
        for (var i = 0; i < 300; i++) { var p = pcFiles[i * 7 + 1]; env.Local.UserDelete(p); expectLocal.Remove(p); gone.Add(p); }                   // deleted here
        for (var i = 0; i < 300; i++) { var p = larkFiles[i * 7 + 1]; env.Remote.RemovePath(p); expectLocal.Remove(p); gone.Add(p); }                // deleted in Lark
        for (var i = 0; i < 400; i++) { var p = larkFiles[i * 7]; env.Remote.Edit(p, B("lark-edit:" + p)); expectLocal[p] = "lark-edit:" + p; }       // modified in Lark
        for (var i = 0; i < 300; i++) { var p = $"new{i % 20:D2}/n{i}.dat"; env.Local.UserWrite(p, B("new:" + p)); expectLocal[p] = "new:" + p; }    // new here
        for (var i = 0; i < 150; i++)                                                                                                                 // renamed in Lark
        {
            var p = larkFiles[i * 7 + 2];
            var to = PathUtil.Join(PathUtil.Parent(p), $"renL{i}.dat");
            env.Remote.RenamePath(p, $"renL{i}.dat");
            expectLocal[to] = expectLocal[p]; expectLocal.Remove(p); gone.Add(p);
        }
        for (var i = 0; i < 150; i++)                                                                                                                 // moved on the PC
        {
            var p = pcFiles[i * 7 + 2];
            var to = $"moved/renP{i}.dat";
            env.Local.UserMkdir("moved");
            env.Local.UserMove(p, to);
            expectLocal[to] = expectLocal[p]; expectLocal.Remove(p); gone.Add(p);
        }

        sw.Restart();
        var res = env.Sync();
        var changed = sw.ElapsedMilliseconds;
        output.WriteLine($"2,000-change pass: {changed} ms");
        Assert.Empty(res.Failed);

        sw.Restart();
        env.AssertSettled();
        output.WriteLine($"settled pass: {sw.ElapsedMilliseconds} ms");

        foreach (var (p, text) in expectLocal)
        {
            Assert.Equal(text, Encoding.UTF8.GetString(env.Local.UserOpen(p)));
            Assert.Equal(text, Encoding.UTF8.GetString(env.Remote.Content(p)!));
        }
        foreach (var p in gone.Where(p => !expectLocal.ContainsKey(p)))
        {
            Assert.Null(env.Local.Stat(p));
            Assert.Null(env.Remote.Find(p));
        }
        Assert.Equal(expectLocal.Count, env.Remote.Nodes.Values.Count(n => n.Type == Kinds.File));
        Assert.Equal(expectLocal.Count, env.Local.Items.Values.Count(i => !i.IsDir));
        Assert.True(initial < 60_000 && changed < 60_000, $"too slow: initial {initial} ms, changes {changed} ms");
    }

    // ---------------------------------------------------------------- scheduling
    [Theory]
    [InlineData(8, 2)]
    [InlineData(2, 1)]
    public void Large_uploads_are_capped_and_small_files_keep_flowing(int maxTransfers, int largeCap)
    {
        using var env = new Env(maxTransfers);
        env.Options.LargeUpload = 1000;
        for (var i = 0; i < 6; i++) env.Local.UserWrite($"big{i}.bin", new byte[2000]);
        for (var i = 0; i < 30; i++) env.Local.UserWrite($"small{i}.txt", B("s" + i));
        int total = 0, big = 0, maxTotal = 0, maxBig = 0;
        var started = new List<string>();
        env.Remote.UploadFilter = (name, _) =>
        {
            var isBig = name.StartsWith("big");
            lock (started)
            {
                started.Add(name);
                maxTotal = Math.Max(maxTotal, ++total);
                if (isBig) maxBig = Math.Max(maxBig, ++big);
            }
            Thread.Sleep(isBig ? 120 : 5);
            lock (started) { total--; if (isBig) big--; }
            return null;
        };

        var res = env.Sync();

        Assert.Empty(res.Failed);
        Assert.Equal(36, res.Uploaded);
        Assert.True(maxTotal <= maxTransfers, $"{maxTotal} uploads at once");
        Assert.Equal(largeCap, maxBig);                                           // the cap is reached but never passed
        var firstBig = started.FindIndex(n => n.StartsWith("big"));
        Assert.True(firstBig >= 30 - maxTransfers, $"a large upload started before the small ones were under way ({firstBig})");
    }

    [Fact]
    public void Deletions_from_Lark_are_applied_before_a_long_upload_batch_starts()
    {
        using var env = new Env();
        env.Remote.AddFile("gone.txt", B("x"));
        env.Sync();
        env.Remote.RemovePath("gone.txt");
        env.Local.UserWrite("new.txt", B("n"));
        bool? goneWhenUploadStarted = null;
        env.Remote.UploadFilter = (_, _) => { goneWhenUploadStarted ??= env.Local.Stat("gone.txt") == null; return null; };

        env.Sync();

        Assert.True(goneWhenUploadStarted);
        env.AssertSettled();
    }

    [Fact]
    public void A_pass_stops_starting_uploads_after_its_time_budget_and_the_next_pass_finishes_them()
    {
        using var env = new Env(maxTransfers: 1);
        env.Options.UploadBudget = TimeSpan.FromMilliseconds(40);
        for (var i = 0; i < 10; i++) env.Local.UserWrite($"f{i}.txt", B("v" + i));
        env.Remote.UploadFilter = (_, _) => { Thread.Sleep(30); return null; };

        var first = env.Sync();

        Assert.InRange(first.Uploaded, 1, 9);
        Assert.True(env.Engine.BusyFilesWaiting);                                 // asks for another check soon
        env.Options.UploadBudget = TimeSpan.FromMinutes(20);
        env.Sync();
        Assert.Equal(10, env.Remote.Nodes.Values.Count(n => n.Type == Kinds.File));
        env.AssertSettled();
    }

    // ---------------------------------------------------------------- snapshots
    [Fact]
    public void The_snapshot_is_hashed_once_while_it_is_copied_and_that_hash_reaches_MarkUploaded()
    {
        using var env = new Env();
        var data = B("some file contents " + new string('x', 5000));
        env.Local.UserWrite("a.txt", data);

        env.Sync();

        Assert.Equal(2, env.Local.Opens["a.txt"]);                                // the copy, and the live file's check; no third read
        var (size, hash) = env.Local.MarkedWith["a.txt"];
        Assert.Equal(data.Length, size);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)), hash);
        env.AssertSettled();
    }

    [Fact]
    public void A_file_changed_during_the_upload_is_not_marked_synced()
    {
        using var env = new Env();
        env.Local.UserWrite("a.txt", B("first"));
        env.Remote.UploadFilter = (_, _) => { env.Local.UserWrite("a.txt", B("second, written while uploading")); return null; };

        env.Sync();

        Assert.False(env.Local.Items["a.txt"].IsPlaceholder);                    // hash differs: never linked as if it matched
        env.Remote.UploadFilter = null;
        env.Sync();
        Assert.Equal("second, written while uploading", Encoding.UTF8.GetString(env.Remote.Content("a.txt")!));
    }

    [Fact]
    public void Without_room_for_the_snapshot_the_upload_is_skipped_with_a_message_and_retried_later()
    {
        using var env = new Env();
        env.Options.FreeSpace = _ => 10L << 20;                                   // less than the safety margin
        env.Local.UserWrite("a.txt", B("data"));

        var res = env.Sync();

        Assert.Equal(0, res.Uploaded);
        Assert.Empty(res.Failed);                                                 // not a failure: nothing was started
        Assert.Contains("free space", res.Info["a.txt"]);
        Assert.Null(env.Remote.Find("a.txt"));
        env.Options.FreeSpace = _ => long.MaxValue;
        Assert.Equal(1, env.Sync().Uploaded);
    }

    // ---------------------------------------------------------------- chunked upload
    [Fact]
    public void A_part_that_fails_transiently_is_retried_on_its_own()
    {
        using var f = new ApiFixture();
        f.Api.SingleUploadLimit = 4;                                              // the fake Lark cuts parts at 4 bytes
        f.Http.PartFault = (part, attempt) => part == 1 && attempt == 1 ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) : null;
        var dir = Path.Combine(Path.GetTempPath(), "lcs-part-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "big.bin");
            File.WriteAllText(src, "0123456789");                                  // 3 parts

            var tok = f.Api.Upload(src, "fldROOT", "big.bin", null);

            Assert.Equal("0123456789", Encoding.UTF8.GetString(f.Http.R.Nodes[tok].Content));
            Assert.Equal(1, f.Http.Prepares);                                     // the upload wasn't started over
            Assert.Equal(new[] { 1, 2, 1 }, f.Http.PartAttempts.OrderBy(kv => kv.Key).Select(kv => kv.Value));
        }
        finally { Directory.Delete(dir, true); }
    }
}
