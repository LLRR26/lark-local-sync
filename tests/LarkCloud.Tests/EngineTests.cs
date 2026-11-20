using System.Text;
using LarkCloud.Core;

namespace LarkCloud.Tests;

public class EngineTests
{
    [Fact]
    public void An_edit_during_upload_is_sent_in_the_next_pass()
    {
        using var e = new Env();
        e.Local.UserWrite("file.txt", B("first"));
        e.Remote.UploadFilter = (_, _) =>
        {
            e.Local.UserWrite("file.txt", B("later"));
            return null;
        };
        Assert.Equal(1, e.Sync().Uploaded);
        Assert.Equal(B("first"), e.Remote.Find("file.txt")!.Content);
        Assert.Equal(BaseEntry.LocalUploadPending, e.State.Get("file.txt")!.LMTime);

        e.Remote.UploadFilter = null;
        Assert.Equal(1, e.Sync().Uploaded);
        Assert.Equal(B("later"), e.Remote.Find("file.txt")!.Content);
        e.AssertSettled();
    }

    [Fact]
    public void A_remote_rename_with_a_same_size_edit_refreshes_the_downloaded_copy()
    {
        using var e = new Env();
        e.Remote.AddFile("old.txt", B("first"));
        e.Sync();
        e.Local.UserOpen("old.txt");

        e.Remote.RenamePath("old.txt", "new.txt");
        e.Remote.Edit("new.txt", B("later"));
        e.Sync();

        Assert.Null(e.Local.Stat("old.txt"));
        e.Local.UserOpen("new.txt");
        Assert.Equal(B("later"), e.Local.Data("new.txt"));
    }

    [Fact]
    public void Approved_folder_deletion_waits_for_review_when_its_contents_change()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 1;
        e.Remote.AddFile("folder/a.txt", B("a"));
        e.Remote.AddFile("folder/b.txt", B("b"));
        e.Sync();
        e.Local.UserDelete("folder");
        var original = Assert.Single(e.Sync().PendingDeletes);

        e.Remote.AddFile("folder/c.txt", B("c"));
        var revised = Assert.Single(e.Sync(approvedDeletes: original.Approval).PendingDeletes);
        Assert.NotEqual(original.Approval, revised.Approval);
        Assert.NotNull(e.Remote.Find("folder/c.txt"));
        Assert.Equal(0, e.Count("delete"));
        Assert.Equal(1, e.Sync(approvedDeletes: revised.Approval).DeletedRemote);
    }
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Lark_files_show_up_without_being_downloaded()
    {
        using var e = new Env();
        e.Remote.AddFile("Reports/Q1.xlsx", B("quarter one"));
        e.Remote.AddFile("big.psd", new byte[5000]);
        var res = e.Sync();
        Assert.Equal(new[] { "Reports", "Reports/Q1.xlsx", "big.psd" }, e.Local.Paths().OrderBy(x => x, StringComparer.Ordinal));
        Assert.Null(e.Local.Data("big.psd"));                        // cloud-only
        Assert.Equal(5000, e.Local.Stat("big.psd")!.Size);           // but shows its real size
        Assert.Equal(0, e.Count("read"));                            // nothing downloaded
        Assert.Equal(3, res.Created);
        e.AssertSettled();
    }

    [Fact]
    public void Opening_a_file_fetches_it_and_that_is_not_a_change()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("hello"));
        e.Sync();
        Assert.Equal("hello", Encoding.UTF8.GetString(e.Local.UserOpen("a.txt")));
        e.AssertSettled();
        Assert.Equal(0, e.Count("upload"));
    }

    [Fact]
    public void New_file_on_the_PC_is_uploaded_and_becomes_a_synced_placeholder()
    {
        using var e = new Env();
        e.Local.UserWrite("notes/todo.txt", B("buy milk"));
        var res = e.Sync();
        Assert.Equal(1, res.Uploaded);
        Assert.Equal("buy milk", Encoding.UTF8.GetString(e.Remote.Content("notes/todo.txt")!));
        var st = e.Local.Stat("notes/todo.txt")!;
        Assert.True(st.IsPlaceholder && st.InSync);
        Assert.Equal(e.Remote.Find("notes/todo.txt")!.Token, st.Identity);
        e.AssertSettled();
    }

    [Fact]
    public void Editing_a_file_uploads_it_in_place()
    {
        using var e = new Env();
        var tok = e.Remote.AddFile("a.txt", B("v1"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.UserWrite("a.txt", B("version two"));
        var res = e.Sync();
        Assert.Equal(1, res.Uploaded);
        Assert.Equal("version two", Encoding.UTF8.GetString(e.Remote.Content("a.txt")!));
        Assert.Equal(tok, e.Remote.Find("a.txt")!.Token);           // same Lark file, not a copy
        Assert.Single(e.Remote.Paths());
        e.AssertSettled();
    }

    [Fact]
    public void Lark_edit_updates_the_placeholder_and_drops_the_old_local_data()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("old"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Remote.Edit("a.txt", B("much newer text"));
        var res = e.Sync();
        Assert.Equal(1, res.Updated);
        Assert.Null(e.Local.Data("a.txt"));                           // old copy gone
        Assert.Equal(15, e.Local.Stat("a.txt")!.Size);
        Assert.Equal("much newer text", Encoding.UTF8.GetString(e.Local.UserOpen("a.txt")));
        e.AssertSettled();
    }

    [Fact]
    public void Pinned_files_are_queued_to_download_again_after_a_Lark_edit()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("old"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.Items["a.txt"].Pinned = true;
        e.Remote.Edit("a.txt", B("new"));
        e.Sync();
        Assert.Contains("a.txt", e.Engine.ToHydrate);
    }

    [Fact]
    public void Lark_edit_waits_while_the_file_is_open()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("old"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.OpenInApp.Add("a.txt");
        e.Remote.Edit("a.txt", B("new"));
        var res = e.Sync();
        Assert.Contains("a.txt", res.Failed.Keys);
        Assert.Equal("old", Encoding.UTF8.GetString(e.Local.Data("a.txt")!));
        e.Local.OpenInApp.Clear();
        e.Engine.RetryFailedNow();
        e.Sync();
        Assert.Equal("new", Encoding.UTF8.GetString(e.Local.UserOpen("a.txt")));
    }

    [Fact]
    public void Changed_on_both_sides_keeps_both()
    {
        using var e = new Env();
        e.Remote.AddFile("plan.docx", B("base"));
        e.Sync();
        e.Local.UserOpen("plan.docx");
        e.Local.UserWrite("plan.docx", B("mine"));
        e.Remote.Edit("plan.docx", B("theirs"));
        var res = e.Sync();
        Assert.Single(res.Conflicts);
        var copy = e.Remote.Paths().Single(p => p.Contains("conflict"));
        Assert.Equal("mine", Encoding.UTF8.GetString(e.Remote.Content(copy)!));
        Assert.Equal("theirs", Encoding.UTF8.GetString(e.Remote.Content("plan.docx")!));
        Assert.Equal("theirs", Encoding.UTF8.GetString(e.Local.UserOpen("plan.docx")));
        Assert.Equal("mine", Encoding.UTF8.GetString(e.Local.Data(copy)!));
        e.AssertSettled();
    }

    [Fact]
    public void Deleted_in_Lark_is_deleted_here_and_the_other_way_round()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("a"));
        e.Remote.AddFile("b.txt", B("b"));
        e.Sync();
        e.Remote.RemovePath("a.txt");
        e.Local.UserDelete("b.txt");
        var res = e.Sync();
        Assert.Equal(1, res.DeletedLocal);
        Assert.Equal(1, res.DeletedRemote);
        Assert.Empty(e.Local.Paths());
        Assert.Empty(e.Remote.Paths());
        e.AssertSettled();
    }

    [Fact]
    public void A_folder_this_PC_cant_read_is_never_treated_as_deleted()
    {
        using var e = new Env();
        e.Remote.AddFile("Locked/a.txt", B("a"));
        e.Remote.AddFile("Locked/b.txt", B("b"));
        e.Sync();
        foreach (var p in new[] { "Locked/a.txt", "Locked/b.txt" }) e.Local.Items.Remove(p);   // the scan can't see inside
        e.Local.Unreadable.Add("Locked");
        var res = e.Sync();
        Assert.Equal(0, res.DeletedRemote);
        Assert.NotNull(e.Remote.Find("Locked/a.txt"));
    }

    [Fact]
    public void Deleted_in_Lark_is_recognised_even_if_the_sync_records_were_lost()
    {
        using var e = new Env();
        e.Remote.AddFile("report.pdf", B("pdf"));
        e.Sync();
        foreach (var b in e.State.All()) e.State.Remove(b.Path);  // sync records gone (e.g. reset)
        e.State.Commit();
        e.Remote.RemovePath("report.pdf");
        e.Sync();
        Assert.Empty(e.Local.Paths());                                // deleted here too…
        Assert.Equal(0, e.Count("upload"));                           // …not uploaded again as if it were new
    }

    [Fact]
    public void A_rename_that_lost_its_Lark_link_is_still_a_rename_not_delete_plus_add()
    {
        using var e = new Env();
        e.Local.UserWrite("draft.docx", B("contract text"));
        e.Sync();
        var tok = e.Remote.Find("draft.docx")!.Token;
        var it = e.Local.Items["draft.docx"];
        it.IsPlaceholder = false;                                     // the app that saved it replaced the file
        it.Identity = null;
        e.Local.UserMove("draft.docx", "final.docx");
        var res = e.Sync();
        Assert.Equal(1, res.Moved);
        Assert.Equal(0, res.DeletedRemote);
        Assert.Equal(1, e.Count("upload"));                           // only the first upload
        Assert.Equal(tok, e.Remote.Find("final.docx")!.Token);        // same Lark file, renamed
        e.AssertSettled();
    }

    [Fact]
    public void A_huge_deletion_is_confirmed_by_a_second_check_before_it_happens()
    {
        using var e = new Env(maxTransfers: 8);
        for (var i = 0; i < 300; i++) e.Remote.AddFile($"f{i}.txt", B("x"));
        e.Sync();
        for (var i = 0; i < 300; i++) e.Remote.RemovePath($"f{i}.txt");
        var res = e.Sync();
        Assert.Equal(0, res.DeletedLocal);                            // first sight: wait
        Assert.True(e.Engine.ConfirmDeletesSoon);
        Assert.Empty(res.PendingDeletes);                             // no prompt
        res = e.Sync();
        Assert.Equal(300, res.DeletedLocal);                          // confirmed by the next check
        Assert.Empty(e.Local.Paths());
    }

    [Fact]
    public void A_delete_wins_even_if_the_other_side_edited_it()
    {
        using var e = new Env();
        e.Remote.AddFile("mine.txt", B("a"));
        e.Remote.AddFile("theirs.txt", B("b"));
        e.Sync();
        e.Local.UserOpen("mine.txt");
        e.Local.UserWrite("mine.txt", B("edited here"));
        e.Remote.RemovePath("mine.txt");                             // deleted in Lark
        e.Remote.Edit("theirs.txt", B("edited in Lark"));
        e.Local.UserDelete("theirs.txt");                            // deleted here
        e.Sync();
        Assert.Empty(e.Local.Paths());
        Assert.Empty(e.Remote.Paths());
        Assert.Contains(("mine.txt", true), e.Local.Deleted);         // the edited copy went to the Recycle Bin
        e.AssertSettled();
    }

    [Fact]
    public void A_folder_deleted_on_either_side_takes_everything_inside_with_it()
    {
        using var e = new Env();
        e.Remote.AddFile("Gone here/a.txt", B("a"));
        e.Remote.AddFile("Gone in Lark/b.txt", B("b"));
        e.Sync();
        e.Local.UserDelete("Gone here");
        e.Remote.AddFile("Gone here/colleague added.txt", B("c"));   // added in Lark meanwhile
        e.Remote.RemovePath("Gone in Lark");
        e.Local.UserWrite("Gone in Lark/new here.txt", B("n"));       // added here meanwhile
        var res = e.Sync();
        Assert.Empty(res.Failed);
        Assert.Empty(e.Remote.Paths());
        Assert.Empty(e.Local.Paths());
        Assert.Contains(("Gone in Lark/new here.txt", true), e.Local.Deleted);   // never in Lark: Recycle Bin
        Assert.Equal(0, e.Count("upload"));
        e.AssertSettled();
    }

    [Fact]
    public void Problems_with_files_deleted_since_disappear_straight_away()
    {
        using var e = new Env();
        e.Options.ZipBlocked = false;
        e.Remote.UploadFilter = (name, content) => content.Contains("${") ? new LarkApiException(403, "Forbidden") : null;
        e.Local.UserWrite("sbom.cdx (conflict from RENNON 2026-11-10 2146).json", B("{\"x\":\"${y}\"}"));
        Assert.Single(e.Sync().Failed);
        e.Local.UserDelete("sbom.cdx (conflict from RENNON 2026-11-10 2146).json");
        var res = e.Sync();
        Assert.Empty(res.Failed);                                     // not still listed for 30 minutes
        Assert.Empty(res.UploadHere);
    }

    [Fact]
    public void Deleted_in_Lark_but_edited_here_is_kept_and_uploaded_again()
    {
        using var e = new Env();
        e.Options.DeleteWins = false;                                // the older "an edit wins" setting
        e.Remote.AddFile("a.txt", B("a"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.UserWrite("a.txt", B("edited"));
        e.Remote.RemovePath("a.txt");
        e.Sync();
        Assert.Equal("edited", Encoding.UTF8.GetString(e.Remote.Content("a.txt")!));
        e.AssertSettled();
    }

    [Fact]
    public void Edited_in_Lark_but_deleted_here_comes_back()
    {
        using var e = new Env();
        e.Options.DeleteWins = false;                                // the older "an edit wins" setting
        e.Remote.AddFile("a.txt", B("a"));
        e.Sync();
        e.Local.UserDelete("a.txt");
        e.Remote.Edit("a.txt", B("newer"));
        e.Sync();
        Assert.Equal("newer", Encoding.UTF8.GetString(e.Local.UserOpen("a.txt")));
        e.AssertSettled();
    }

    [Fact]
    public void Many_deletions_run_in_the_background_by_default()
    {
        using var e = new Env();
        for (var i = 0; i < 30; i++) e.Remote.AddFile($"f{i}.txt", B("x"));
        for (var i = 0; i < 30; i++) e.Remote.AddFile($"g{i}.txt", B("y"));
        e.Sync();
        for (var i = 0; i < 30; i++) e.Remote.RemovePath($"f{i}.txt");
        for (var i = 0; i < 30; i++) e.Local.UserDelete($"g{i}.txt");
        var res = e.Sync();
        Assert.Empty(res.PendingDeletes);
        Assert.Equal(30, res.DeletedLocal);
        Assert.Equal(30, res.DeletedRemote);
        Assert.Empty(e.Local.Paths());
        Assert.Empty(e.Remote.Paths());
        e.AssertSettled();
    }

    [Fact]
    public void Deletions_in_Lark_run_several_at_a_time_and_folders_go_in_one_call()
    {
        using var e = new Env(maxTransfers: 8);
        for (var i = 0; i < 40; i++) e.Remote.AddFile($"loose{i}.txt", B("x"));
        for (var i = 0; i < 50; i++) e.Remote.AddFile($"Old project/sub/f{i}.txt", B("y"));
        e.Sync();
        var live = 0;
        var peak = 0;
        e.Remote.DeleteDelay = () =>
        {
            var n = Interlocked.Increment(ref live);
            lock (e) peak = Math.Max(peak, n);
            Thread.Sleep(20);
            Interlocked.Decrement(ref live);
        };
        for (var i = 0; i < 40; i++) e.Local.UserDelete($"loose{i}.txt");
        e.Local.UserDelete("Old project");
        var res = e.Sync();
        Assert.Equal(41, res.DeletedRemote);                         // 40 files + 1 folder (its 50 files go with it)
        Assert.Equal(41, e.Count("delete"));
        Assert.True(peak > 1, "deletes should run in parallel");
        Assert.Empty(e.Remote.Paths());
        e.AssertSettled();
    }

    [Fact]
    public void Folders_too_big_for_Lark_to_delete_at_once_are_deleted_in_parts()
    {
        using var e = new Env(maxTransfers: 8);
        for (var i = 0; i < 6; i++)
            for (var j = 0; j < 5; j++)
                for (var k = 0; k < 4; k++)
                    e.Remote.AddFile($"Adobe Illustrator CC 2019/P{i}/S{j}/f{k}.txt", B("x"));
        e.Remote.AddFile("Keep/k.txt", B("k"));
        e.Sync();
        e.Remote.MaxFolderDelete = 10;                               // Lark answers 500 for anything bigger
        e.Local.UserDelete("Adobe Illustrator CC 2019");
        var res = e.Sync();
        Assert.Empty(res.Failed);
        Assert.Equal(1, res.DeletedRemote);
        Assert.Equal(new[] { "Keep", "Keep/k.txt" }, e.Remote.Paths());
        e.AssertSettled();
    }

    [Fact]
    public void A_refusal_on_one_item_inside_a_big_folder_does_not_put_the_whole_folder_back()
    {
        using var e = new Env(maxTransfers: 8);
        for (var i = 0; i < 40; i++)
            for (var j = 0; j < 5; j++)
                for (var k = 0; k < 20; k++)
                    e.Remote.AddFile($"mall-crowd-analytics/.venv/Lib/site-packages/pkg{i}/sub{j}/f{k}.py", B("x"));
        for (var i = 0; i < 5; i++) e.Remote.AddFile($"mall-crowd-analytics/top{i}.py", B("t"));
        e.Remote.AddFile("Keep/k.txt", B("k"));
        var bad = e.Remote.AddFile("mall-crowd-analytics/.venv/Lib/site-packages/pkg7/sub3/odd.py", B("x"));
        e.Sync();
        e.Remote.MaxFolderDelete = 15;                               // 500 at every level, down to the sub folders (21 items)
        var armed = true;
        e.Remote.DeleteFault = t =>
        {
            if (t != bad || !armed) return null;
            armed = false;                                           // first attempt only
            return new LarkApiException(1061004, "forbidden");
        };
        e.Local.UserDelete("mall-crowd-analytics");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = e.Sync();                                          // big batch: waits for a second look
        res = e.Sync();                                              // deletes; one item refused
        Assert.DoesNotContain("mall-crowd-analytics", e.Local.Paths());
        Assert.Contains(res.Failed.Keys, k => k == "mall-crowd-analytics");
        Assert.Contains("odd.py", res.Failed["mall-crowd-analytics"]);
        Assert.DoesNotContain("mall-crowd-analytics", res.Info.Keys);
        Assert.Empty(e.Local.Paths().Where(p => p.StartsWith("mall-crowd-analytics")));
        e.Engine.RetryFailedNow();                                   // (the wait before retrying has passed)
        e.Sync();                                                    // the records still count as a big batch: confirm again
        Assert.DoesNotContain(e.Local.Paths(), p => p.StartsWith("mall-crowd-analytics"));
        e.Sync();                                                    // retry finishes it
        Assert.DoesNotContain(e.Local.Paths(), p => p.StartsWith("mall-crowd-analytics"));
        Assert.Equal(new[] { "Keep", "Keep/k.txt" }, e.Remote.Paths());
        Assert.DoesNotContain(e.Local.Paths(), p => p.StartsWith("mall-crowd-analytics"));
        e.AssertSettled();
        Console.WriteLine($"big-folder delete test: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_genuine_refusal_on_a_folder_itself_puts_it_back()
    {
        using var e = new Env();
        var f = e.Remote.AddFile("Shared/a.txt", B("a"));
        e.Remote.AddFile("Keep/k.txt", B("k"));
        e.Sync();
        var shared = e.Remote.Nodes[f].Parent;
        e.Remote.DeleteFault = t => t == shared ? new LarkApiException(1061004, "forbidden") : null;
        e.Local.UserDelete("Shared");
        var res = e.Sync();
        Assert.Contains("Shared", res.Info.Keys);
        e.Remote.DeleteFault = null;
        e.Sync();
        Assert.Contains("Shared/a.txt", e.Local.Paths());
        Assert.Contains("Shared/a.txt", e.Remote.Paths());
        e.AssertSettled();
    }

    [Fact]
    public void Connection_problems_pause_the_check_instead_of_blaming_files()
    {
        using var e = new Env();
        for (var i = 0; i < 12; i++) e.Remote.AddFile($"f{i}.txt", B("x"));
        var offline = true;
        e.Remote.SizeHook = () => { if (offline) throw new LarkUnreachableException("Can't reach Lark (No such host is known.)"); };
        Assert.ThrowsAny<Exception>(() => e.Sync());
        offline = false;
        var res = e.Sync();                                          // straight away, not 30 minutes later
        Assert.Empty(res.Failed);
        Assert.Equal(12, e.Local.Paths().Count);
        e.AssertSettled();
    }

    [Fact]
    public void Unchanged_downloaded_copies_are_removed_directly_changed_ones_kept_in_recycle_bin()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("a"));
        e.Remote.AddFile("b.txt", B("b"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.UserOpen("b.txt");
        e.Local.UserWrite("b.txt", B("my edit"));
        e.Remote.RemovePath("a.txt");
        e.Remote.RemovePath("b.txt");
        e.Sync();
        Assert.Equal(new[] { "a.txt" }, e.Local.Deleted.Where(d => !d.Recycled).Select(d => d.Path));
        Assert.Equal(new[] { "b.txt" }, e.Local.Deleted.Where(d => d.Recycled).Select(d => d.Path));   // edited here: Recycle Bin
    }

    [Fact]
    public void Many_deletions_are_held_back_until_confirmed()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        for (var i = 0; i < 30; i++) e.Remote.AddFile($"f{i}.txt", B("x"));
        e.Sync();
        for (var i = 0; i < 30; i++) e.Remote.RemovePath($"f{i}.txt");
        var res = e.Sync();
        Assert.Equal(30, res.PendingDeletes.Count);
        Assert.Equal(30, e.Local.Paths().Count);
        res = e.Sync(approvedDeletes: res.PendingDeletes[0].Approval);
        Assert.Equal(30, res.DeletedLocal);
        Assert.Empty(e.Local.Paths());
    }

    [Fact]
    public void Held_deletions_on_the_PC_can_be_undone()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        for (var i = 0; i < 25; i++) e.Remote.AddFile($"f{i}.txt", B("x"));
        e.Sync();
        for (var i = 0; i < 25; i++) e.Local.UserDelete($"f{i}.txt");
        var res = e.Sync();
        Assert.Equal(25, res.PendingDeletes.Count);
        Assert.Equal(25, e.Remote.Paths().Count);
        e.Engine.RestorePending(res.PendingDeletes);
        e.Sync();
        Assert.Equal(25, e.Local.Paths().Count);                     // shown again from Lark
        Assert.Equal(25, e.Remote.Paths().Count);
        e.AssertSettled();
    }

    [Fact]
    public void Moves_in_Lark_move_here_without_downloading()
    {
        using var e = new Env();
        e.Remote.AddFile("A/x.txt", B("x"));
        e.Remote.AddFolder("B");
        e.Sync();
        e.Local.UserOpen("A/x.txt");
        e.Remote.MovePath("A/x.txt", "B");
        var res = e.Sync();
        Assert.Equal(1, res.Moved);
        Assert.Equal("x", Encoding.UTF8.GetString(e.Local.Data("B/x.txt")!));    // the downloaded copy moved with it
        Assert.Null(e.Local.Stat("A/x.txt"));
        e.AssertSettled();
    }

    [Fact]
    public void Moves_and_renames_on_the_PC_move_in_Lark_without_uploading()
    {
        using var e = new Env();
        var tok = e.Remote.AddFile("A/x.txt", B("x"));
        var ftok = e.Remote.AddFolder("Old name");
        e.Remote.AddFile("Old name/inner.txt", B("i"));
        e.Sync();
        e.Local.UserMkdir("B");
        e.Sync();
        e.Local.UserMove("A/x.txt", "B/renamed.txt");
        e.Local.UserMove("Old name", "New name");
        var res = e.Sync();
        Assert.Equal(2, res.Moved);
        Assert.Equal(0, e.Count("upload"));
        Assert.Equal(tok, e.Remote.Find("B/renamed.txt")!.Token);
        Assert.Equal(ftok, e.Remote.Find("New name")!.Token);
        Assert.NotNull(e.Remote.Find("New name/inner.txt"));
        e.AssertSettled();
    }

    [Fact]
    public void Online_docs_become_shortcuts()
    {
        using var e = new Env();
        var tok = e.Remote.AddDoc("Plans/Roadmap");
        e.Sync();
        var data = Encoding.UTF8.GetString(e.Local.Data("Plans/Roadmap.url")!);
        Assert.Contains(tok, data);
        e.AssertSettled();
        e.Remote.RemovePath("Plans/Roadmap");
        e.Sync();
        Assert.Null(e.Local.Stat("Plans/Roadmap.url"));
        Assert.Equal(0, e.Count("upload"));
    }

    [Fact]
    public void Names_Windows_cannot_use_get_look_alike_characters_and_stay_linked()
    {
        using var e = new Env();
        var tok = e.Remote.AddFile("Q1: Plan?.txt", B("q"));
        e.Remote.AddFile("100PLUS Vendor Quotation .pdf ", B("p"));
        e.Remote.AddFile("CON.txt", B("c"));
        var res = e.Sync();
        Assert.Empty(res.Notes);
        Assert.Contains("Q1： Plan？.txt", e.Local.Paths());
        Assert.Contains("100PLUS Vendor Quotation .pdf", e.Local.Paths());
        Assert.Contains("CON (Lark).txt", e.Local.Paths());
        e.AssertSettled();
        // editing it updates the same Lark file, whose name stays as it was in Lark
        e.Local.UserOpen("Q1： Plan？.txt");
        e.Local.UserWrite("Q1： Plan？.txt", B("edited"));
        e.Sync();
        Assert.Equal("edited", Encoding.UTF8.GetString(e.Remote.Nodes[tok].Content));
        Assert.Equal("Q1: Plan?.txt", e.Remote.Nodes[tok].Name);
        e.AssertSettled();
    }

    [Fact]
    public void Duplicate_names_in_Lark_are_all_shown_with_numbers()
    {
        using var e = new Env();
        var a = e.Remote.AddFile("Report.xlsx", B("first"));
        var b = e.Remote.AddFile("Report.xlsx", B("second!"));
        var d1 = e.Remote.AddDoc("Test Doc");
        e.Remote.AddFile("Test Doc.url", B("[InternetShortcut]"));
        var res = e.Sync();
        Assert.Empty(res.Notes);
        Assert.Contains("Report.xlsx", e.Local.Paths());
        Assert.Contains("Report (2).xlsx", e.Local.Paths());
        Assert.Contains("Test Doc.url", e.Local.Paths());
        Assert.Contains("Test Doc (2).url", e.Local.Paths());
        e.AssertSettled();
        e.Local.UserOpen("Report (2).xlsx");
        e.Local.UserWrite("Report (2).xlsx", B("second, edited"));
        e.Sync();
        Assert.Equal("first", Encoding.UTF8.GetString(e.Remote.Nodes[a].Content));
        Assert.Equal("second, edited", Encoding.UTF8.GetString(e.Remote.Nodes[b].Content));
        // names stay put when a new duplicate turns up
        e.Remote.AddFile("Report.xlsx", B("third"));
        e.Sync();
        Assert.Equal("second, edited", Encoding.UTF8.GetString(e.Local.UserOpen("Report (2).xlsx")));
        Assert.Contains("Report (3).xlsx", e.Local.Paths());
        e.AssertSettled();
    }

    [Fact]
    public void Blocked_files_are_stored_zipped_and_show_as_the_normal_file()
    {
        using var e = new Env();
        // the simulated filter reads text files; a zip is compressed binary, which may contain "${" bytes by chance
        e.Remote.UploadFilter = (name, content) => !name.EndsWith(".zip") && content.Contains("${") ? new LarkApiException(403, "Forbidden") : null;
        e.Local.UserWrite("js/typekit.js", B("var x = `${y}`;"));
        var res = e.Sync();
        Assert.Empty(res.Failed);
        Assert.Contains("js/typekit.js", res.Info.Keys);
        Assert.NotNull(e.Remote.Find("js/typekit.js (zipped).zip"));
        e.AssertSettled();
        Assert.Equal(new[] { "js", "js/typekit.js" }, e.Local.Paths());
        // free up space and open again: the app unzips it
        e.Local.Dehydrate("js/typekit.js");
        Assert.Equal("var x = `${y}`;", Encoding.UTF8.GetString(e.Local.UserOpen("js/typekit.js")));
        // edit: the zip in Lark is updated, no second copy
        e.Local.UserWrite("js/typekit.js", B("var x = `${z}`; // v2"));
        e.Sync();
        Assert.Single(e.Remote.Paths().Where(p => p.StartsWith("js/typekit")));
        e.AssertSettled();
    }

    [Fact]
    public void When_Lark_blocks_the_zip_too_a_password_protected_zip_is_used()
    {
        using var e = new Env();
        // a filter that also looks inside normal zips (like the one that blocked sbom.cdx.json)
        e.Remote.UploadFilter = (name, content) =>
        {
            var bytes = Encoding.Latin1.GetBytes(content);
            var text = content;
            if (name.EndsWith(".zip") && !ProtectedZip.IsProtected(bytes))
            {
                using var z = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
                using var r = new StreamReader(z.Entries[0].Open());
                text = r.ReadToEnd();
            }
            return text.Contains("bom-ref") ? new LarkApiException(403, "Forbidden") : null;
        };
        e.Local.UserWrite("sboms/sbom.cdx.json", B("{\"bomFormat\":\"CycloneDX\",\"bom-ref\":\"x\"}"));
        var res = e.Sync();
        Assert.Empty(res.Failed);
        Assert.Contains("password", res.Info["sboms/sbom.cdx.json"]);
        var stored = e.Remote.Find("sboms/sbom.cdx.json (zipped).zip")!.Content;
        Assert.True(ProtectedZip.IsProtected(stored));
        e.Local.Dehydrate("sboms/sbom.cdx.json");
        Assert.Contains("CycloneDX", Encoding.UTF8.GetString(e.Local.UserOpen("sboms/sbom.cdx.json")));
        e.AssertSettled();
    }

    [Fact]
    public void Another_PC_shows_zipped_files_as_the_normal_file()
    {
        using var e = new Env();
        using (var ms = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            using (var w = new StreamWriter(z.CreateEntry("a.js").Open())) w.Write("hello ${x}");
            e.Remote.AddFile("a.js (zipped).zip", ms.ToArray());
        }
        e.Sync();
        Assert.Equal(new[] { "a.js" }, e.Local.Paths());
        Assert.Equal(10, e.Local.Stat("a.js")!.Size);
        Assert.Equal("hello ${x}", Encoding.UTF8.GetString(e.Local.UserOpen("a.js")));
        e.AssertSettled();
    }

    [Fact]
    public void Doc_shortcuts_rename_move_and_delete_the_doc()
    {
        using var e = new Env();
        var doc = e.Remote.AddDoc("Plans/Roadmap");
        var doc2 = e.Remote.AddDoc("Old notes", "sheet");
        e.Remote.AddFolder("Archive");
        e.Sync();
        e.Local.UserMove("Plans/Roadmap.url", "Archive/Roadmap 2027.url");
        e.Local.UserDelete("Old notes.url");
        var res = e.Sync();
        Assert.Equal(1, res.Moved);
        Assert.Equal("Archive/Roadmap 2027", e.Remote.PathOf(doc));
        Assert.False(e.Remote.Nodes.ContainsKey(doc2));               // to Lark's trash
        Assert.Equal(0, e.Count("upload"));
        e.AssertSettled();
    }

    [Fact]
    public void Offline_copies_of_docs_are_read_only_and_follow_the_doc()
    {
        using var e = new Env();
        e.Options.DocCopies = true;
        var doc = e.Remote.AddDoc("Plan");
        e.Remote.AddDoc("Budget", "sheet");
        e.Sync();
        Assert.Contains("Plan (Lark copy).docx", e.Local.Paths());
        Assert.Contains("Budget (Lark copy).xlsx", e.Local.Paths());
        Assert.True(e.Local.Stat("Plan (Lark copy).docx")!.ReadOnly);
        Assert.StartsWith("docx export of Plan", Encoding.UTF8.GetString(e.Local.UserOpen("Plan (Lark copy).docx")));
        e.AssertSettled();
        // the doc changes in Lark: the copy is refreshed
        e.Remote.Nodes[doc].MTime = e.Remote.Tick();
        e.Sync();
        Assert.Null(e.Local.Data("Plan (Lark copy).docx"));
        // deleting a copy never deletes the doc
        e.Local.UserDelete("Budget (Lark copy).xlsx");
        e.Sync();
        Assert.NotNull(e.Remote.Find("Budget"));
        e.Sync();
        Assert.Contains("Budget (Lark copy).xlsx", e.Local.Paths());
        // the export expired in Lark: opening exports again
        e.Extras.ExpireAll();
        e.Local.Dehydrate("Budget (Lark copy).xlsx");
        Assert.StartsWith("xlsx export", Encoding.UTF8.GetString(e.Local.UserOpen("Budget (Lark copy).xlsx")));
        // turning copies off removes them (and only them), without asking about deletions
        e.Options.DocCopies = false;
        var res = e.Sync();
        Assert.Empty(res.PendingDeletes);
        Assert.DoesNotContain(e.Local.Paths(), p => p.Contains("Lark copy"));
        Assert.Contains("Plan.url", e.Local.Paths());
    }

    [Fact]
    public void View_only_folders_are_read_only_and_changes_are_saved_to_my_folders()
    {
        using var e = new Env();
        e.Remote.AddFile("spec.docx", B("v1"));
        e.Remote.ReadOnly.Add(e.Remote.RootToken);
        var res = e.Sync();
        Assert.True(res.ViewOnly);
        Assert.True(e.Local.Stat("spec.docx")!.ReadOnly);
        // edited anyway (e.g. access removed while it was open)
        e.Local.UserOpen("spec.docx");
        e.Local.UserWrite("spec.docx", B("my changes"));
        res = e.Sync();
        Assert.Contains("spec.docx", res.Failed.Keys);
        Assert.Contains("My Folders", res.Failed["spec.docx"]);
        Assert.StartsWith("https://x.larksuite.com/drive/folder/", res.OpenLinks["spec.docx"]);
        var copy = e.Remote.Nodes.Values.Single(n => n.Name.StartsWith("spec (from Shared"));
        Assert.Equal("my changes", Encoding.UTF8.GetString(copy.Content));
        Assert.Equal("Lark Cloud – changes not saved", e.Remote.Nodes[copy.Parent].Name);
        Assert.Equal("v1", Encoding.UTF8.GetString(e.Remote.Content("spec.docx")!));
        // tried again later: no second copy of the same changes
        e.Engine.RetryFailedNow();
        e.Sync();
        Assert.Single(e.Remote.Nodes.Values, n => n.Name.StartsWith("spec (from Shared"));
        // edit access granted: files become writable again
        e.Remote.ReadOnly.Clear();
        e.Options.PermissionCheckEvery = TimeSpan.Zero;
        e.Engine.RetryFailedNow();
        e.Sync();
        Assert.False(e.Local.Stat("spec.docx")!.ReadOnly);
        Assert.Equal("my changes", Encoding.UTF8.GetString(e.Remote.Content("spec.docx")!));
    }

    [Fact]
    public void Empty_files_Lark_refuses_stay_here_and_are_listed_as_good_to_know()
    {
        using var e = new Env();
        e.Remote.UploadFilter = (name, content) => content.Length == 0 ? new LarkApiException(-1, "empty") { EmptyRefused = true } : null;
        e.Local.UserWrite("blank.txt", Array.Empty<byte>());
        var res = e.Sync();
        Assert.Empty(res.Failed);
        Assert.Contains("blank.txt", res.Info.Keys);
        Assert.Contains("blank.txt", e.Local.Paths());
        e.Local.UserWrite("blank.txt", B("now it has text"));
        res = e.Sync();
        Assert.Empty(res.Info);
        Assert.Equal("now it has text", Encoding.UTF8.GetString(e.Remote.Content("blank.txt")!));
    }

    [Fact]
    public void Empty_files_in_Lark_show_up_normally()
    {
        using var e = new Env();
        e.Remote.AddFile("gc.properties", Array.Empty<byte>());
        var res = e.Sync();
        Assert.Empty(res.Failed);
        Assert.Equal(0, e.Local.Stat("gc.properties")!.Size);
        e.AssertSettled();
    }

    [Fact]
    public void Same_file_added_on_both_sides_is_matched_not_duplicated()
    {
        using var e = new Env();
        e.Remote.AddFile("same.txt", B("identical"));
        e.Local.UserWrite("same.txt", B("identical"));
        var res = e.Sync();
        Assert.Empty(res.Conflicts);
        Assert.Equal(0, e.Count("upload"));
        Assert.True(e.Local.Stat("same.txt")!.IsPlaceholder);
        e.AssertSettled();
    }

    [Fact]
    public void Different_files_with_the_same_new_name_keep_both()
    {
        using var e = new Env();
        e.Remote.AddFile("x.txt", B("theirs"));
        e.Local.UserWrite("x.txt", B("mine!!"));
        var res = e.Sync();
        Assert.Single(res.Conflicts);
        Assert.Equal(2, e.Remote.Paths().Count);
        e.AssertSettled();
    }

    [Fact]
    public void Blocked_upload_offers_upload_with_Lark_then_matches_the_manual_upload()
    {
        using var e = new Env();
        e.Options.ZipBlocked = false;                                 // the manual route (when zipping is turned off or also blocked)
        e.Remote.UploadFilter = (name, content) => content.Contains("${") ? new LarkApiException(403, "Forbidden") : null;
        e.Local.UserWrite("src/typekit.js", B("var x = `${y}`;"));
        e.Local.UserWrite("src/ok.js", B("var ok = 1;"));
        var res = e.Sync();
        Assert.Contains("src/typekit.js", res.Failed.Keys);
        Assert.Contains("security filter", res.Failed["src/typekit.js"]);
        Assert.StartsWith("https://x.larksuite.com/drive/folder/", res.UploadHere["src/typekit.js"]);
        Assert.NotNull(e.Remote.Find("src/ok.js"));
        // it isn't retried straight away
        Assert.Contains("src/typekit.js", e.Sync().Failed.Keys);
        // the person drags it into Lark by hand: matched up, no duplicate, no conflict
        e.Remote.AddFile("src/typekit.js", B("var x = `${y}`;"));
        res = e.Sync();
        Assert.Empty(res.Failed);
        Assert.Empty(res.Conflicts);
        Assert.Equal(3, e.Remote.Paths().Count);
        e.AssertSettled();
    }

    [Fact]
    public void New_folder_tree_is_created_in_Lark_once_each_parents_first()
    {
        using var e = new Env(maxTransfers: 8);
        for (var i = 0; i < 6; i++)
            for (var j = 0; j < 5; j++)
                e.Local.UserWrite($"Adobe/P{i}/S{j}/f.txt", B($"{i}{j}"));
        var res = e.Sync();
        Assert.Equal(30, res.Uploaded);
        var mk = e.Remote.Calls.Where(c => c.Op == "mkdir").ToList();
        Assert.Equal(1 + 6 + 30, mk.Count);
        Assert.Equal(37, e.Remote.Paths().Count(p => e.Remote.Find(p)!.Type == "folder"));
        Assert.Contains(e.Progress, m => m.StartsWith("Creating folders in Lark"));
        Assert.Contains(e.Progress, m => m.StartsWith("Syncing"));
        e.AssertSettled();
    }

    [Fact]
    public void Files_still_being_written_wait()
    {
        using var e = new Env();
        e.Local.UserWrite("growing.zip", B("part"), when: DateTime.UtcNow);
        var res = e.Sync();
        Assert.Equal(0, res.Uploaded);
        Assert.True(e.Engine.BusyFilesWaiting);
        e.Local.Items["growing.zip"].MTime = DateTime.UtcNow.AddMinutes(-1).Ticks;
        Assert.Equal(1, e.Sync().Uploaded);
    }

    [Fact]
    public void Targeted_pass_never_deletes_for_Lark_items_it_did_not_relist()
    {
        using var e = new Env();
        e.Remote.AddFile("A/x.txt", B("x"));
        e.Remote.AddFile("B/y.txt", B("y"));
        e.Sync();
        e.Remote.MovePath("A/x.txt", "B");                 // B isn't re-listed below
        e.Local.UserWrite("A/new.txt", B("n"));
        var res = e.Sync(changed: new[] { "A/new.txt" });
        Assert.False(res.Full);
        Assert.Equal(0, res.DeletedLocal);
        res = e.Sync();
        Assert.Equal(1, res.Moved);
        Assert.NotNull(e.Local.Stat("B/x.txt"));
        e.AssertSettled();
    }

    [Fact]
    public void Folder_deleted_here_is_deleted_in_Lark_unless_something_new_is_inside()
    {
        using var e = new Env();
        e.Options.DeleteWins = false;                                // the older "an edit wins" setting
        e.Remote.AddFile("Old/a.txt", B("a"));
        e.Remote.AddFile("Keep/b.txt", B("b"));
        e.Sync();
        e.Local.UserDelete("Old");
        e.Local.UserDelete("Keep");
        e.Remote.AddFile("Keep/new from colleague.txt", B("c"));
        e.Sync();
        Assert.Null(e.Remote.Find("Old"));
        Assert.NotNull(e.Remote.Find("Keep/new from colleague.txt"));   // not thrown away
        Assert.NotNull(e.Local.Stat("Keep/new from colleague.txt"));    // shown again here
        e.AssertSettled();
    }

    [Fact]
    public void Held_deletions_from_Lark_put_back_reupload_downloaded_copies_only()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        for (var i = 0; i < 22; i++) e.Remote.AddFile($"f{i}.txt", B($"x{i}"));
        e.Sync();
        e.Local.UserOpen("f0.txt");                                   // this one has a copy on the PC
        for (var i = 0; i < 22; i++) e.Remote.RemovePath($"f{i}.txt");
        var res = e.Sync();
        Assert.Equal(22, res.PendingDeletes.Count);
        e.Engine.RestorePending(res.PendingDeletes);
        e.Sync();
        Assert.Equal(new[] { "f0.txt" }, e.Remote.Paths());             // uploaded again
        Assert.Equal(new[] { "f0.txt" }, e.Local.Paths());              // cloud-only copies removed
        e.AssertSettled();
    }

    [Fact]
    public void Ten_thousand_files_settle_quickly()
    {
        using var e = new Env(maxTransfers: 8);
        for (var i = 0; i < 100; i++)
            for (var j = 0; j < 100; j++)
                e.Remote.AddFile($"D{i}/f{j}.pdf", B("x"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        e.Sync();
        var first = sw.Elapsed;
        sw.Restart();
        Assert.False(e.Sync().Changed);
        var idle = sw.Elapsed;
        Assert.True(first < TimeSpan.FromSeconds(60) && idle < TimeSpan.FromSeconds(15), $"first {first}, idle {idle}");
    }

    [Fact]
    public void An_unreadable_folder_protects_its_files_whatever_the_letter_case()
    {
        using var e = new Env();
        e.Remote.AddFile("Locked/a.txt", B("a"));
        e.Sync();
        e.Local.Items.Remove("Locked/a.txt");
        e.Local.Unreadable.Add("locked");                            // reported with different case
        var res = e.Sync();
        Assert.Equal(0, res.DeletedRemote);
        Assert.NotNull(e.Remote.Find("Locked/a.txt"));
    }

    [Fact]
    public void Lark_delete_of_a_file_still_being_written_here_waits_until_it_settles()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("a"));
        e.Sync();
        e.Local.UserWrite("a.txt", B("being written"), when: DateTime.UtcNow);
        e.Remote.RemovePath("a.txt");
        e.Sync();
        Assert.NotNull(e.Local.Stat("a.txt"));                        // not this pass
        Assert.True(e.Engine.BusyFilesWaiting);
        e.Local.Items["a.txt"].MTime = DateTime.UtcNow.AddMinutes(-1).Ticks;
        e.Sync();
        Assert.Null(e.Local.Stat("a.txt"));
    }

    [Fact]
    public void A_deleted_folder_counts_the_files_inside_for_the_deletion_hold()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 3;
        for (var i = 0; i < 5; i++) e.Remote.AddFile($"D/f{i}.txt", B("x"));
        e.Sync();
        e.Local.UserDelete("D");
        var res = e.Sync();
        Assert.NotEmpty(res.PendingDeletes);
        Assert.Equal(0, res.DeletedRemote);
        Assert.NotNull(e.Remote.Find("D/f0.txt"));
    }

    [Fact]
    public void A_deleted_folder_counts_the_files_inside_for_the_big_batch_net()
    {
        using var e = new Env(maxTransfers: 8);
        for (var i = 0; i < 300; i++) e.Remote.AddFile($"D/f{i}.txt", B("x"));
        e.Sync();
        e.Local.UserDelete("D");
        var res = e.Sync();
        Assert.Equal(0, res.DeletedRemote);                           // first sight: wait
        Assert.True(e.Engine.ConfirmDeletesSoon);
        Assert.NotNull(e.Remote.Find("D"));
        e.Sync();
        Assert.Null(e.Remote.Find("D"));
    }

    [Fact]
    public void A_different_Lark_item_at_the_path_of_a_deleted_file_is_not_trashed()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("old"));
        e.Sync();
        e.Local.UserDelete("a.txt");
        e.Remote.RemovePath("a.txt");
        var other = e.Remote.AddFile("a.txt", B("someone else's"));
        var deletes = e.Count("delete");
        e.Sync();
        Assert.Equal(deletes, e.Count("delete"));
        Assert.Equal(other, e.Remote.Find("a.txt")!.Token);
        Assert.Equal("someone else's", Encoding.UTF8.GetString(e.Local.UserOpen("a.txt")));
    }

    [Fact]
    public void Deleting_a_shortcut_keeps_a_doc_that_was_edited_in_Lark_since()
    {
        using var e = new Env();
        e.Remote.AddDoc("Plan");
        e.Sync();
        e.Local.UserDelete("Plan.url");
        e.Remote.Edit("Plan", B("edited"));
        e.Sync();
        Assert.NotNull(e.Remote.Find("Plan"));
        Assert.Contains("Plan.url", e.Local.Paths());                 // the shortcut comes back
    }

    [Fact]
    public void Deleting_a_shortcut_trashes_the_doc_when_it_was_edited_before_a_pass_that_saw_the_shortcut()
    {
        using var e = new Env();
        e.Remote.AddDoc("Plan");
        e.Sync();
        e.Remote.Edit("Plan", B("edited"));
        e.Sync();                                                     // the shortcut is still there: it has seen the edit
        e.Local.UserDelete("Plan.url");
        e.Sync();
        Assert.Null(e.Remote.Find("Plan"));
        Assert.DoesNotContain("Plan.url", e.Local.Paths());
    }

    [Fact]
    public void Offline_copies_never_count_for_the_deletion_hold_on_either_side()
    {
        using var e = new Env();
        e.Options.DocCopies = true;
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 4;
        // PC side deleted in Lark: 3 docs (3 shortcuts + 3 copies) and 1 file; only the file counts (1, not over 1)
        e.Options.MaxDeletes = 1;
        for (var i = 0; i < 3; i++) e.Remote.AddDoc($"E/doc{i}");
        e.Remote.AddFile("E/real.txt", B("x"));
        e.Sync();
        e.Options.Now = () => DateTime.UtcNow.AddMinutes(10);        // the fresh shortcuts and copies are no longer "being written"
        e.Remote.RemovePath("E");
        var res = e.Sync();
        Assert.Empty(res.PendingDeletes);
        Assert.Null(e.Local.Stat("E"));

        // Lark side deleted on the PC: 3 docs (+3 copies) and 1 file; 4 real items, copies would make 7 (over 4)
        e.Options.MaxDeletes = 4;
        for (var i = 0; i < 3; i++) e.Remote.AddDoc($"D/doc{i}");
        e.Remote.AddFile("D/real.txt", B("x"));
        e.Sync();
        e.Local.UserDelete("D");
        res = e.Sync();
        Assert.Empty(res.PendingDeletes);
        Assert.Null(e.Remote.Find("D"));
    }

    [Fact]
    public void Deleting_many_shortcuts_here_is_held_because_it_would_trash_real_docs()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 2;
        for (var i = 0; i < 4; i++) e.Remote.AddDoc($"doc{i}");
        e.Sync();
        for (var i = 0; i < 4; i++) e.Local.UserDelete($"doc{i}.url");
        var res = e.Sync();
        Assert.NotEmpty(res.PendingDeletes);
        for (var i = 0; i < 4; i++) Assert.NotNull(e.Remote.Find($"doc{i}"));
    }

    [Fact]
    public void A_deleted_folder_counts_its_online_docs_for_the_deletion_hold()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 3;
        for (var i = 0; i < 4; i++) e.Remote.AddDoc($"D/doc{i}");
        e.Remote.AddFile("D/real.txt", B("x"));
        e.Sync();
        e.Local.UserDelete("D");
        var res = e.Sync();
        Assert.NotEmpty(res.PendingDeletes);
        Assert.NotNull(e.Remote.Find("D/doc0"));
        Assert.NotNull(e.Remote.Find("D"));
    }

    [Fact]
    public void Deleting_shortcuts_in_Lark_deleted_folder_does_not_count_for_the_hold_on_the_PC()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 2;
        for (var i = 0; i < 4; i++) e.Remote.AddDoc($"D/doc{i}");
        e.Remote.AddFile("D/real.txt", B("x"));
        e.Sync();
        e.Options.Now = () => DateTime.UtcNow.AddMinutes(10);        // the fresh shortcuts are no longer "being written"
        e.Remote.RemovePath("D");
        var res = e.Sync();
        Assert.Empty(res.PendingDeletes);                             // only real.txt is a real deletion here
        Assert.Null(e.Local.Stat("D"));
    }

    [Fact]
    public void An_edited_shortcut_does_not_take_the_Lark_date_so_deleting_it_keeps_the_doc()
    {
        using var e = new Env();
        e.Remote.AddDoc("Plan");
        e.Sync();
        e.Local.UserWrite("Plan.url", B("[InternetShortcut]\nURL=https://example.com/my-own-notes\n"));
        e.Remote.Edit("Plan", B("edited"));
        e.Sync();                                                     // the edited shortcut has not "seen" the doc's new date
        e.Local.UserDelete("Plan.url");
        e.Sync();
        Assert.NotNull(e.Remote.Find("Plan"));
    }

    [Fact]
    public void Files_added_in_Lark_inside_a_folder_deleted_here_count_for_the_deletion_hold()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        e.Options.MaxDeletes = 3;
        for (var i = 0; i < 2; i++) e.Remote.AddFile($"D/f{i}.txt", B("x"));
        e.Sync();
        for (var i = 0; i < 5; i++) e.Remote.AddFile($"D/new{i}.txt", B("n"));
        e.Local.UserDelete("D");
        var res = e.Sync();
        Assert.NotEmpty(res.PendingDeletes);
        Assert.NotNull(e.Remote.Find("D/new0.txt"));
    }

    [Fact]
    public void Folder_deleted_in_Lark_waits_while_a_file_inside_is_still_being_written_here()
    {
        using var e = new Env();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        e.Options.Now = () => now;                                    // fixed clock: "being written" doesn't depend on timing
        e.Remote.AddFile("D/a.txt", B("a"));
        e.Remote.AddFile("D/b.txt", B("b"));
        e.Sync();
        var uploads = e.Count("upload");
        e.Local.UserWrite("D/a.txt", B("being written"), when: now);
        e.Remote.RemovePath("D");
        e.Sync();
        Assert.NotNull(e.Local.Stat("D/a.txt"));
        Assert.Null(e.Local.Stat("D/b.txt"));                         // an idle sibling goes
        Assert.NotNull(e.Local.Stat("D"));                            // the folder waits with the busy file
        Assert.True(e.Engine.BusyFilesWaiting);
        Assert.Null(e.Remote.Find("D"));                              // not re-created in Lark
        Assert.Equal(uploads, e.Count("upload"));
        e.Local.Items["D/a.txt"].MTime = now.AddMinutes(-1).Ticks;
        e.Sync();
        Assert.Null(e.Local.Stat("D/a.txt"));
        Assert.Null(e.Local.Stat("D"));
        Assert.Null(e.Remote.Find("D"));
        Assert.Equal(uploads, e.Count("upload"));
    }
}
