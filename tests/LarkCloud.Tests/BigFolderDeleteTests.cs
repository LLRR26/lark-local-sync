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
}
