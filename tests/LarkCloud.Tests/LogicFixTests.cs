using System.Text;
using LarkCloud.Core;

namespace LarkCloud.Tests;

public class LogicFixTests
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Put_back_for_a_folder_deleted_in_Lark_uploads_the_downloaded_copies_again()
    {
        using var e = new Env();
        e.Options.ConfirmMassDeletes = true;
        for (var i = 0; i < 22; i++) e.Remote.AddFile($"D/f{i}.txt", B($"x{i}"));
        e.Remote.AddFile("D/sub/g.txt", B("g"));
        e.Remote.AddFile("D/sub/h.txt", B("h"));
        e.Sync();
        e.Local.UserOpen("D/f0.txt");                                 // these have a copy on the PC
        e.Local.UserOpen("D/sub/g.txt");
        e.Remote.RemovePath("D");
        var res = e.Sync();
        Assert.NotEmpty(res.PendingDeletes);
        e.Engine.RestorePending(res.PendingDeletes);
        var after = e.Sync();
        Assert.Empty(after.PendingDeletes);
        Assert.Equal(new[] { "D", "D/f0.txt", "D/sub", "D/sub/g.txt" }, e.Remote.Paths());
        Assert.Equal(new[] { "D", "D/f0.txt", "D/sub", "D/sub/g.txt" }, e.Local.Paths());
        Assert.Empty(e.Sync().PendingDeletes);
        e.AssertSettled();
    }

    [Fact]
    public void An_edit_wins_over_a_folder_deleted_in_Lark_when_delete_wins_is_off()
    {
        using var e = new Env();
        e.Options.DeleteWins = false;
        e.Remote.AddFile("D/a.txt", B("a"));
        e.Remote.AddFile("D/b.txt", B("b"));
        e.Remote.AddFile("D/sub/c.txt", B("c"));
        e.Sync();
        e.Local.UserOpen("D/a.txt");
        e.Local.UserWrite("D/a.txt", B("edited"));
        e.Local.UserWrite("D/new.txt", B("brand new"));
        e.Remote.RemovePath("D");
        e.Sync();
        e.Sync();
        Assert.Equal("edited", Encoding.UTF8.GetString(e.Remote.Content("D/a.txt")!));
        Assert.Equal("brand new", Encoding.UTF8.GetString(e.Remote.Content("D/new.txt")!));
        Assert.Null(e.Remote.Find("D/b.txt"));
        Assert.Null(e.Local.Stat("D/b.txt"));
        Assert.Null(e.Local.Stat("D/sub"));                           // nothing edited in there: deleted as before
        Assert.DoesNotContain(e.Local.Deleted, d => d.Path is "D/a.txt" or "D/new.txt");
        e.AssertSettled();
    }

    [Fact]
    public void A_folder_deleted_in_Lark_still_goes_when_delete_wins_is_on()
    {
        using var e = new Env();
        e.Remote.AddFile("D/a.txt", B("a"));
        e.Sync();
        e.Local.UserOpen("D/a.txt");
        e.Local.UserWrite("D/a.txt", B("edited"));
        e.Remote.RemovePath("D");
        e.Sync();
        Assert.Null(e.Local.Stat("D"));
        Assert.Null(e.Remote.Find("D"));
        e.AssertSettled();
    }

    static long Later(int minutes) => DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();

    /// <summary>Upload an edit whose confirmation re-list fails, so the entry stays "pending".</summary>
    static Env UploadWithFailedConfirmation()
    {
        var e = new Env();
        e.Remote.AddFile("a.txt", B("v1"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.UserWrite("a.txt", B("mine"));
        var armed = false;
        e.Remote.UploadFilter = (_, _) => { armed = true; return null; };
        var folder = e.Remote.RootToken;
        e.Remote.ListFault = t => armed && t == folder ? new IOException("network down") : null;
        e.Sync();
        e.Remote.UploadFilter = null;
        e.Remote.ListFault = null;
        return e;
    }

    [Fact]
    public void A_colleagues_edit_after_a_failed_upload_confirmation_is_not_lost()
    {
        using var e = UploadWithFailedConfirmation();
        Assert.Equal("mine", Encoding.UTF8.GetString(e.Remote.Content("a.txt")!));
        var n = e.Remote.Find("a.txt")!;
        n.Content = B("colleague");
        n.MTime = Later(30);
        e.Sync();
        Assert.Equal("colleague", Encoding.UTF8.GetString(e.Remote.Content("a.txt")!));
        Assert.Equal("colleague", Encoding.UTF8.GetString(e.Local.UserOpen("a.txt")));
        e.AssertSettled();
    }

    [Fact]
    public void Late_adoption_of_our_own_upload_changes_nothing()
    {
        using var e = UploadWithFailedConfirmation();
        var uploads = e.Count("upload");
        var r = e.Sync();
        Assert.Equal(0, r.Updated + r.Uploaded + r.Conflicts.Count);
        Assert.Equal(uploads, e.Count("upload"));
        Assert.Equal("mine", Encoding.UTF8.GetString(e.Remote.Content("a.txt")!));
        e.AssertSettled();
    }

    [Fact]
    public void A_moved_shortcut_still_moves_the_doc_when_Lark_gave_no_link()
    {
        using var e = new Env();
        var doc = e.Remote.AddDoc("Plans/Roadmap");
        e.Remote.Nodes[doc].Url = null;                               // the link is built from the tenant host instead
        e.Remote.AddFolder("Archive");
        e.Sync();
        e.Local.UserMove("Plans/Roadmap.url", "Archive/Roadmap 2027.url");
        var res = e.Sync();
        Assert.Equal(1, res.Moved);
        Assert.Equal("Archive/Roadmap 2027", e.Remote.PathOf(doc));
        Assert.Equal(0, e.Count("upload"));                           // not uploaded as an ordinary file
        e.AssertSettled();
    }

    [Fact]
    public void Lark_too_many_requests_retries_soon_instead_of_waiting_30_minutes()
    {
        using var e = new Env();
        e.Sync();
        e.Local.UserWrite("a.txt", B("hello"));
        e.Remote.UploadFilter = (_, _) => new LarkApiException(99991400, "request trigger frequency limit");
        Assert.ThrowsAny<Exception>(() => e.Sync());                  // the pass stops, like for HTTP 429
        e.Remote.UploadFilter = null;
        e.Sync();
        Assert.NotNull(e.Remote.PathOf(e.Remote.Nodes.Values.Single(n => n.Name == "a.txt").Token));
        e.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_zipped_files_size_is_read_from_the_zip_header_without_downloading_it(bool protectedZip)
    {
        using var e = new Env();
        var data = new byte[5000];
        Random.Shared.NextBytes(data);
        var src = Path.Combine(Path.GetTempPath(), $"lcs-test-{Guid.NewGuid():N}.bin");
        var zip = src + ".zip";
        try
        {
            File.WriteAllBytes(src, data);
            string tok;
            if (protectedZip)
            {
                ProtectedZip.Write(src, zip, "a.bin");
                tok = e.Remote.Upload(zip, e.Remote.RootToken, "a.bin" + SpecialTokens.ZipSuffix, null);
            }
            else tok = SpecialTokens.Raw(e.Features.UploadZipped(src, e.Remote.RootToken, "a.bin", null));
            var node = e.Remote.Nodes[tok];
            node.Content = node.Content[..40];                        // only the header: reading the whole zip would fail
            Assert.Equal(data.Length, e.Features.ProbeSize(SpecialTokens.ZipPrefix + tok));
        }
        finally { File.Delete(src); File.Delete(zip); }
    }

    [Fact]
    public void Two_PC_renames_in_a_chain_are_moves_in_Lark_not_uploads()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("A"));
        e.Remote.AddFile("b.txt", B("B"));
        e.Sync();
        e.Local.UserMove("a.txt", "c.txt");
        e.Local.UserMove("b.txt", "a.txt");
        e.Sync();
        e.Sync();
        Assert.Equal(new[] { "a.txt", "c.txt" }, e.Remote.Paths());
        Assert.Equal("B", Encoding.UTF8.GetString(e.Remote.Content("a.txt")!));
        Assert.Equal("A", Encoding.UTF8.GetString(e.Remote.Content("c.txt")!));
        e.AssertSettled();
    }

    [Fact]
    public void A_PC_edit_survives_when_Lark_moves_the_file_into_a_new_folder()
    {
        using var e = new Env();
        e.Remote.AddFile("a/x.txt", B("old"));
        e.Sync();
        e.Local.UserOpen("a/x.txt");
        e.Local.UserWrite("a/x.txt", B("edited on the PC"));
        e.Remote.AddFolder("a/n");
        e.Remote.MovePath("a/x.txt", "a/n");
        e.Sync();
        e.Sync();
        e.Sync();
        // The move changed Lark's date, so the edit may be kept as a conflict copy, but it must be in Lark and not in the Recycle Bin.
        Assert.Contains(e.Remote.Paths(), p => p.StartsWith("a/n/") && Encoding.UTF8.GetString(e.Remote.Content(p)!) == "edited on the PC");
        Assert.Empty(e.Local.Deleted.Where(d => d.Path == "a/x.txt"));
        e.AssertSettled();
    }

    [Fact]
    public void A_same_size_Lark_edit_plus_a_Lark_rename_is_not_overwritten_by_a_PC_edit()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("old"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.UserWrite("a.txt", B("edited on the PC"));
        e.Remote.Edit("a.txt", B("OLD"));                             // same size as before
        e.Remote.RenamePath("a.txt", "b.txt");
        e.Sync();
        e.Sync();
        var contents = e.Remote.Paths().Select(p => Encoding.UTF8.GetString(e.Remote.Content(p)!)).ToList();
        Assert.Contains("OLD", contents);                             // the Lark edit survives
        Assert.Contains("edited on the PC", contents);                // and so does the PC edit
        e.AssertSettled();
    }

    [Fact]
    public void Both_sides_edited_to_identical_bytes_is_not_a_conflict()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("old"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.UserWrite("a.txt", B("same"));
        e.Remote.Edit("a.txt", B("same"));
        var res = e.Sync();
        Assert.Empty(res.Conflicts);
        Assert.Equal(new[] { "a.txt" }, e.Remote.Paths());
        e.AssertSettled();
    }

    [Fact]
    public void Both_sides_edited_to_same_size_different_bytes_is_still_a_conflict()
    {
        using var e = new Env();
        e.Remote.AddFile("a.txt", B("old"));
        e.Sync();
        e.Local.UserOpen("a.txt");
        e.Local.UserWrite("a.txt", B("mine"));
        e.Remote.Edit("a.txt", B("MINE"));
        var res = e.Sync();
        Assert.Single(res.Conflicts);
        var contents = e.Remote.Paths().Select(p => Encoding.UTF8.GetString(e.Remote.Content(p)!)).ToList();
        Assert.Contains("mine", contents);
        Assert.Contains("MINE", contents);
        e.AssertSettled();
    }
}
