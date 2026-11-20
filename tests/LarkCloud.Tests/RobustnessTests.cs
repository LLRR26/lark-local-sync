using System.Text;
using LarkCloud.Core;

namespace LarkCloud.Tests;

/// <summary>Request timeouts vs. queueing, half-empty listing entries, comparing after a move in Lark, parallel reviewed deletes.</summary>
public class RobustnessTests
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    // ---------------------------------------------------------------- request timeout
    [Fact]
    public void Time_queued_behind_a_rate_limit_cooldown_does_not_count_against_the_request_timeout()
    {
        using var f = new ApiFixture();
        f.Http.R.AddFile("a.txt", B("a"));
        f.Api.RequestTimeout = TimeSpan.FromMilliseconds(50);
        var advance = f.Api.Sleep!;
        var slept = 0;
        f.Api.Sleep = d => { Thread.Sleep(200); slept++; advance(d); };     // really waits longer than the timeout
        f.Http.RateLimitNext = 1;                                             // first request: "slow down", then the cooldown wait

        var list = f.Api.ListFolder(f.Api.RootToken);

        Assert.True(slept > 0);
        Assert.Equal("a.txt", Assert.Single(list).Path);
        Assert.Equal(1, f.Api.RateLimitHits);
    }

    [Fact]
    public void A_request_that_never_answers_ends_as_unreachable_not_as_a_cancellation()
    {
        using var f = new ApiFixture();
        f.Api.RequestTimeout = TimeSpan.FromMilliseconds(30);
        f.Http.HangListing = true;
        Assert.Throws<LarkUnreachableException>(() => f.Api.ListFolder(f.Api.RootToken));
    }

    // ---------------------------------------------------------------- half-empty listing entries
    [Fact]
    public void A_half_empty_entry_is_skipped_and_the_listing_says_it_is_incomplete()
    {
        using var f = new ApiFixture();
        f.Http.R.AddFile("good.txt", B("g"));
        f.Http.BadTokens.Add(f.Http.R.AddFile("odd.txt", B("o")));
        var list = Assert.IsType<RemoteListing>(f.Api.ListFolder(f.Api.RootToken));
        Assert.Equal("good.txt", Assert.Single(list).Path);
        Assert.True(list.Incomplete);
    }

    [Fact]
    public void A_skipped_file_is_not_deleted_on_the_PC_and_the_rest_keeps_syncing()
    {
        using var f = new ApiFixture();
        using var env = new Env(inner: f.Api);
        for (var i = 0; i < 3; i++) f.Http.R.AddFile($"file{i}.txt", B("x" + i));
        env.Sync();
        f.Http.BadTokens.Add(f.Http.R.Find("file1.txt")!.Token);
        f.Http.R.AddFile("fresh.txt", B("new"));

        var res = env.Sync();

        Assert.Equal(0, res.DeletedLocal);
        Assert.Empty(env.Local.Deleted);
        Assert.NotNull(env.Local.Stat("file1.txt"));
        Assert.NotNull(env.Local.Stat("fresh.txt"));                         // others still sync
        f.Http.BadTokens.Clear();
        env.Sync();
        Assert.NotNull(env.Local.Stat("file1.txt"));
        env.AssertSettled();
    }

    [Fact]
    public void A_skipped_folder_does_not_look_deleted_in_Lark()
    {
        using var f = new ApiFixture();
        using var env = new Env(inner: f.Api);
        f.Http.R.AddFile("dir/a.txt", B("a"));
        f.Http.R.AddFile("dir/sub/b.txt", B("b"));
        f.Http.R.AddFile("keep.txt", B("k"));
        env.Sync();
        f.Http.BadTokens.Add(f.Http.R.Find("dir")!.Token);

        env.Sync();

        Assert.Empty(env.Local.Deleted);
        Assert.NotNull(env.Local.Stat("dir/sub/b.txt"));
        Assert.NotNull(env.State.Get("dir/a.txt"));
    }

    [Fact]
    public void A_folder_with_an_unlisted_item_is_not_deleted_in_Lark_when_removed_on_the_PC()
    {
        using var f = new ApiFixture();
        using var env = new Env(inner: f.Api);
        f.Http.R.AddFile("dir/a.txt", B("a"));
        var odd = f.Http.R.AddFile("dir/odd.txt", B("o"));
        env.Sync();
        f.Http.BadTokens.Add(odd);
        env.Local.UserDelete("dir");

        env.Sync();

        Assert.NotNull(f.Http.R.Find("dir/odd.txt"));                        // would have gone to Lark's trash with the folder
        Assert.NotNull(f.Http.R.Find("dir"));
    }

    // ---------------------------------------------------------------- comparing after a move in Lark
    [Fact]
    public void A_move_in_Lark_with_a_different_size_is_not_downloaded_to_compare()
    {
        using var e = new Env();
        e.Remote.AddFile("old.txt", B("first"));
        e.Sync();
        e.Local.UserOpen("old.txt");
        var reads = e.Count("read");

        e.Remote.RenamePath("old.txt", "new.txt");
        e.Remote.Edit("new.txt", B("a much longer text"));
        e.Sync();

        Assert.Equal(reads, e.Count("read"));
        Assert.Equal(B("a much longer text"), e.Local.UserOpen("new.txt"));
    }

    [Fact]
    public void A_move_in_Lark_of_a_big_file_is_refreshed_instead_of_downloaded_to_compare()
    {
        using var e = new Env();
        var data = new byte[17 << 20];
        e.Remote.AddFile("old.bin", data);
        e.Sync();
        e.Local.UserOpen("old.bin");
        var reads = e.Count("read");

        e.Remote.RenamePath("old.bin", "new.bin");
        var edited = new byte[data.Length];
        edited[0] = 1;
        e.Remote.Edit("new.bin", edited);
        e.Sync();

        Assert.Equal(reads, e.Count("read"));
        Assert.Equal(edited, e.Local.UserOpen("new.bin"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_file_that_cannot_be_read_after_a_move_does_not_abort_the_pass(bool unauthorized)
    {
        using var e = new Env();
        e.Remote.AddFile("old.txt", B("first"));
        e.Sync();
        e.Local.UserOpen("old.txt");
        e.Local.OpenFault = _ => unauthorized ? new UnauthorizedAccessException("denied") : new OperationCanceledException();

        e.Remote.RenamePath("old.txt", "new.txt");
        e.Remote.Edit("new.txt", B("later"));                                // same size, so the content has to be compared
        e.Remote.AddFile("other.txt", B("other"));
        var res = e.Sync();

        Assert.NotNull(e.Local.Stat("other.txt"));                           // the rest of the pass still ran
        Assert.Null(e.Local.Stat("old.txt"));
        Assert.Equal(1, res.Moved);
        e.Local.OpenFault = null;
        Assert.Equal(B("later"), e.Local.UserOpen("new.txt"));               // unverified, so refreshed from Lark
    }

    // ---------------------------------------------------------------- reviewed folder deletion
    [Fact]
    public void Approved_folder_children_are_deleted_several_at_a_time()
    {
        using var e = new Env(maxTransfers: 4);
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 1;
        for (var i = 0; i < 24; i++) e.Remote.AddFile($"folder/f{i}.txt", B("x"));
        e.Sync();
        e.Local.UserDelete("folder");
        var review = Assert.Single(e.Sync().PendingDeletes.GroupBy(d => d.Approval).Select(g => g.Key));
        int running = 0, max = 0;
        e.Remote.DeleteDelay = () =>
        {
            var n = Interlocked.Increment(ref running);
            lock (e.Remote) max = Math.Max(max, n);
            Thread.Sleep(15);
            Interlocked.Decrement(ref running);
        };

        var res = e.Sync(approvedDeletes: review);

        Assert.Equal(1, res.DeletedRemote);
        Assert.Empty(e.Remote.Paths());
        Assert.InRange(max, 2, 4);
    }

    [Fact]
    public void Parallel_reviewed_delete_stops_when_something_new_arrives()
    {
        using var e = new Env(maxTransfers: 4);
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 1;
        string? first = null;
        for (var i = 0; i < 12; i++)
        {
            var t = e.Remote.AddFile($"folder/f{i}.txt", B("x"));
            first ??= t;
        }
        e.Sync();
        e.Local.UserDelete("folder");
        var review = Assert.Single(e.Sync().PendingDeletes.GroupBy(d => d.Approval).Select(g => g.Key));
        e.Remote.DeleteFault = token =>
        {
            if (token == first) e.Remote.AddFile("folder/new.txt", B("new"));
            return null;
        };

        var res = e.Sync(approvedDeletes: review);

        Assert.Equal(0, res.DeletedRemote);
        Assert.True(e.Engine.ConfirmDeletesSoon);
        Assert.NotNull(e.Remote.Find("folder"));                             // never sent a delete for the folder itself
        Assert.NotNull(e.Remote.Find("folder/new.txt"));
        Assert.DoesNotContain(e.Remote.Calls, c => c.Op == "delete" && c.Arg == e.Remote.Find("folder")!.Token);
    }

    [Fact]
    public void Reviewed_delete_does_not_start_when_the_PC_copy_is_back()
    {
        using var e = new Env(maxTransfers: 4);
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 1;
        for (var i = 0; i < 6; i++) e.Remote.AddFile($"folder/f{i}.txt", B("x"));
        e.Sync();
        e.Local.UserDelete("folder");
        var review = Assert.Single(e.Sync().PendingDeletes.GroupBy(d => d.Approval).Select(g => g.Key));
        e.Local.PresenceOverride = _ => LocalPresence.Present;

        var res = e.Sync(approvedDeletes: review);

        Assert.Equal(0, res.DeletedRemote);
        Assert.Equal(0, e.Count("delete"));
    }
}
