using System.Text;
using LarkCloud.Core;

namespace LarkCloud.Tests;

public class PauseTests
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    static FakeRemote Tree()
    {
        var r = new FakeRemote();
        r.AddFile("a/b/c/deep.txt", B("1"));
        r.AddFile("a/b/x.txt", B("2"));
        r.AddFile("a/y.txt", B("3"));
        r.AddFile("d/z.txt", B("4"));
        r.AddFile("top.txt", B("5"));
        return r;
    }

    static List<string> Paths(RemoteIndex i) => i.Entries().Select(e => e.Path).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void A_paused_full_refresh_resumes_to_the_same_index_without_relisting()
    {
        var remote = Tree();
        var whole = new RemoteIndex(remote, 2);
        Assert.True(whole.FullRefresh());
        var folders = remote.ListCalls;                              // one listing per folder

        var paused = new RemoteIndex(remote, 2) { ChunkSize = 1 };
        Assert.True(paused.FullRefresh());                           // first listing: complete
        remote.ListCalls = 0;
        remote.AddFile("d/new.txt", B("6"));
        var pauses = 0;
        var done = false;
        while (!done)
        {
            done = paused.FullRefresh(null, () => true);
            if (!done) pauses++;
        }
        Assert.True(pauses >= 3);
        Assert.Equal(folders, remote.ListCalls);                     // nothing listed twice
        Assert.True(paused.Complete);
        var expected = new RemoteIndex(remote, 2);
        expected.FullRefresh();
        Assert.Equal(Paths(expected), Paths(paused));
        Assert.Contains("d/new.txt", Paths(paused));
    }

    [Fact]
    public void No_pause_before_the_first_complete_listing()
    {
        var idx = new RemoteIndex(Tree(), 2) { ChunkSize = 1 };
        Assert.False(idx.Complete);
        Assert.True(idx.FullRefresh(null, () => true));              // asked to yield, but must finish
        Assert.True(idx.Complete);
    }

    [Fact]
    public void A_failed_or_invalidated_partial_refresh_starts_over()
    {
        var remote = Tree();
        var idx = new RemoteIndex(remote, 2) { ChunkSize = 1 };
        idx.FullRefresh();
        Assert.False(idx.FullRefresh(null, () => true));
        idx.Invalidate();
        remote.ListCalls = 0;
        Assert.True(idx.FullRefresh(null, () => true));              // not complete any more: runs to the end
        Assert.Equal(5, remote.ListCalls);                           // root, a, a/b, a/b/c, d: all again from scratch
    }

    [Fact]
    public void An_interrupted_run_decides_and_changes_nothing()
    {
        using var e = new Env();
        e.Remote.AddFile("a/b/x.txt", B("remote"));
        e.Sync();
        e.Local.UserWrite("new.txt", B("local"));
        e.Remote.AddFile("later.txt", B("later"));
        var local = e.Local.Paths();
        var remote = e.Remote.Paths();
        var state = e.State.All().Select(b => b.Path).OrderBy(x => x).ToList();

        var res = e.Engine.Run(shouldYield: () => true);

        Assert.True(res.Interrupted);
        Assert.False(res.Full);
        Assert.False(res.Changed);
        Assert.Empty(res.Failed);
        Assert.Equal(0, e.Count("upload"));
        Assert.Equal(0, e.Count("delete"));
        Assert.Empty(e.Local.Deleted);
        Assert.Equal(local, e.Local.Paths());
        Assert.Equal(remote, e.Remote.Paths());
        Assert.Equal(state, e.State.All().Select(b => b.Path).OrderBy(x => x).ToList());
    }

    [Fact]
    public void A_file_uploaded_while_the_full_check_is_paused_is_not_treated_as_deleted()
    {
        using var e = new Env();
        e.Remote.AddFile("a/b/x.txt", B("remote"));
        e.Sync();

        e.Local.UserWrite("new.txt", B("saved during the full check"));
        Assert.True(e.Engine.Run(shouldYield: () => true).Interrupted);   // root was listed before the file existed

        var quick = e.Engine.Run(changed: new[] { "new.txt" });
        Assert.False(quick.Full);
        Assert.Equal(1, quick.Uploaded);
        Assert.NotNull(e.Remote.Find("new.txt"));

        var full = e.Sync();                                         // resumes and completes
        Assert.True(full.Full);
        Assert.False(full.Interrupted);
        Assert.Empty(e.Local.Deleted);
        Assert.NotNull(e.Local.Data("new.txt"));
        Assert.NotNull(e.Remote.Find("new.txt"));
        Assert.Equal(0, e.Count("delete"));
        e.AssertSettled();
    }
}
