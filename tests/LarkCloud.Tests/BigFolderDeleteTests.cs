using System.Text;
using LarkCloud.Core;

namespace LarkCloud.Tests;

/// <summary>Deleting a big folder on the PC: Lark won't delete a folder with too many items in one go (HTTP 500)
/// and answers "resource contention" (1061045) to some deletes. It must still end up deleted, with feedback.</summary>
public class BigFolderDeleteTests
{
    static Env Setup(bool reviewed)
    {
        var e = new Env();
        if (reviewed) { e.Options.ConfirmMassDeletes = true; e.Options.MaxDeletes = 1; }
        for (var d = 0; d < 6; d++)
            for (var f = 0; f < 10; f++)
                e.Remote.AddFile($"big/sub{d}/f{f}.txt", Encoding.UTF8.GetBytes($"{d}-{f}"));
        e.Remote.MaxFolderDelete = 0;                        // like Lark: bigger folders fail with HTTP 500
        e.Sync();
        e.Local.UserDelete("big");
        return e;
    }

    static SyncResult Delete(Env e, bool reviewed)
    {
        if (!reviewed) return e.Sync();
        var review = Assert.Single(e.Sync().PendingDeletes);
        return e.Sync(approvedDeletes: review.Approval);
    }

    [Fact]
    public void Lark_error_1061045_is_a_temporary_error_not_a_permission_error()
    {
        var ex = new LarkApiException(1061045, "resource contention occurred, please retry");
        Assert.False(ex.IsPermission);
        Assert.True(ex.IsTransient);
        Assert.True(new LarkApiException(1061004, "forbidden").IsPermission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_big_folder_is_deleted_despite_the_limit_and_busy_answers(bool reviewed)
    {
        using var e = Setup(reviewed);
        var seen = new HashSet<string>();
        var n = 0;
        e.Remote.DeleteFault = token =>
        {
            lock (seen) if (seen.Add(token) && ++n % 4 == 0) return new LarkApiException(1061045, "resource contention occurred, please retry");
            return null;
        };
        var res = Delete(e, reviewed);
        Assert.Empty(res.Failed);
        Assert.Equal(1, res.DeletedRemote);
        Assert.Empty(e.Remote.Paths());
        Assert.Contains(e.Progress, m => m.StartsWith("Deleting “big” in Lark: 25 of"));
        Assert.Equal("Deleted “big” in Lark (67 items are in Lark's trash).", Assert.Single(res.Notices));
        e.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_delete_that_cannot_finish_is_reported_once_and_carries_on_next_pass(bool reviewed)
    {
        using var e = Setup(reviewed);
        var stuck = e.Remote.Find("big/sub3/f4.txt")!.Token;
        var broken = true;
        e.Remote.DeleteFault = token => broken && token == stuck ? new LarkApiException(1061045, "resource contention occurred, please retry") : null;

        var res = Delete(e, reviewed);
        var problem = Assert.Single(res.Failed);
        Assert.Equal("big", problem.Key);
        Assert.Equal("Couldn't finish deleting “big” in Lark: 64 of 67 items deleted. Lark said: resource contention occurred, please retry (on “big/sub3/f4.txt”). " +
                     "It will be tried again in a few minutes.", problem.Value);
        Assert.DoesNotContain("open in another app", problem.Value);
        Assert.Equal(0, res.DeletedRemote);
        Assert.Contains("Couldn't finish deleting", Assert.Single(res.Notices));
        Assert.Equal(new[] { "big", "big/sub3", "big/sub3/f4.txt" }, e.Remote.Paths());      // everything else is gone

        // still failing on the retry: the Problems list keeps it, but there is no second tray message
        e.Engine.RetryFailedNow();
        res = e.Sync();
        Assert.Single(res.Failed);
        Assert.Empty(res.Notices);

        // Lark recovers: it carries on without asking again
        broken = false;
        e.Engine.RetryFailedNow();
        res = e.Sync();
        Assert.Empty(res.PendingDeletes);
        Assert.Empty(res.Failed);
        Assert.Equal(1, res.DeletedRemote);
        Assert.Empty(e.Remote.Paths());
        Assert.Contains("Deleted “big” in Lark", Assert.Single(res.Notices));
    }

    // ---- the approval must survive Lark's lazily changing folder dates, but not real changes

    [Fact]
    public void Approval_survives_changed_folder_dates_and_items_already_gone()
    {
        using var e = Setup(true);
        var review = Assert.Single(e.Sync().PendingDeletes);
        foreach (var n in e.Remote.Nodes.Values.Where(n => n.Type == Kinds.Folder && n.Token != e.Remote.MyRoot)) n.MTime = e.Remote.Tick();
        e.Remote.RemovePath("big/sub0/f0.txt");                       // an earlier partial run took some away
        e.Remote.RemovePath("big/sub2/f3.txt");
        var res = e.Sync(approvedDeletes: review.Approval);
        Assert.Empty(res.Failed);
        Assert.Empty(res.PendingDeletes);
        Assert.Empty(e.Remote.Paths());
        Assert.Contains("Deleted “big” in Lark", Assert.Single(res.Notices));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_new_or_edited_file_after_the_review_is_not_deleted_and_says_so(bool edit)
    {
        using var e = Setup(true);
        var review = Assert.Single(e.Sync().PendingDeletes);
        var changed = false;
        e.Remote.DeleteFault = _ =>
        {
            // Lark changes while the approved delete runs (after the approval pass looked at everything)
            if (!changed) { changed = true; if (edit) e.Remote.Edit("big/sub5/f0.txt", new byte[] { 1 }); else e.Remote.AddFile("big/sub5/new.txt", new byte[] { 1 }); }
            return null;
        };
        var res = e.Sync(approvedDeletes: review.Approval);
        Assert.Contains(CloudEngine.ChangedSinceReview, res.Notices);
        Assert.Contains("big/sub5", e.Remote.Paths());
        Assert.Contains(edit ? "big/sub5/f0.txt" : "big/sub5/new.txt", e.Remote.Paths());
        Assert.Contains("big", e.Remote.Paths());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_changed_before_the_approval_pass_goes_back_to_review(bool edit)
    {
        using var e = Setup(true);
        var review = Assert.Single(e.Sync().PendingDeletes);
        if (edit) e.Remote.Edit("big/sub1/f1.txt", new byte[] { 9 }); else e.Remote.AddFile("big/sub1/new.txt", new byte[] { 9 });
        var res = e.Sync(approvedDeletes: review.Approval);
        Assert.Contains(edit ? "big/sub1/f1.txt" : "big/sub1/new.txt", e.Remote.Paths());      // never deleted
        Assert.Empty(res.Failed);
        Assert.Single(res.PendingDeletes);                                                     // back in Review
        Assert.Contains(CloudEngine.ChangedSinceReview, res.Notices);
    }
}
