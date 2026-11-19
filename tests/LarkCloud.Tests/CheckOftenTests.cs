using System.Text;
using LarkCloud.Core;

namespace LarkCloud.Tests;

/// <summary>"Check often": a quick pass over chosen folders picks up Lark-side changes there; the choice is saved.</summary>
public class CheckOftenTests
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void A_quick_pass_over_a_chosen_folder_and_its_subfolders_picks_up_a_file_added_in_lark()
    {
        using var e = new Env();
        e.Remote.AddFile("Projects/2026/Design/old.txt", B("old"));
        e.Remote.AddFile("Other/keep.txt", B("keep"));
        e.Sync();

        e.Remote.AddFile("Projects/2026/Design/new.txt", B("new"));
        e.Remote.AddFile("Other/later.txt", B("later"));             // outside the chosen folder: waits for the full pass
        var folders = e.State.All().Where(b => b.Kind == Kinds.Folder && b.Path.StartsWith("Projects/2026")).Select(b => b.Path).ToList();
        Assert.Contains("Projects/2026/Design", folders);

        var res = e.Sync(changed: folders);

        Assert.False(res.Full);
        Assert.NotNull(e.Local.Stat("Projects/2026/Design/new.txt"));
        Assert.Null(e.Local.Stat("Other/later.txt"));
        e.Sync();
        Assert.NotNull(e.Local.Stat("Other/later.txt"));
    }

    [Fact]
    public void Check_often_folders_are_saved_and_come_back_for_the_pair()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lcd-co-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var it = Path.Combine(dir, "it.json");
            var user = Path.Combine(dir, "user.json");
            File.WriteAllText(it, "{}");
            var c = Config.Load(it, user);
            c.AddPair(new Pair { Id = "p1", FolderUrl = "tok", LocalFolder = Path.Combine(dir, "L") });
            c.SetCheckOften("p1", new[] { "Projects/2026", "Shared/Design" });

            var again = Config.Load(it, user).GetPairs().Single();
            Assert.Equal(new[] { "Projects/2026", "Shared/Design" }, again.CheckOften);
            Assert.Empty(new Pair().CheckOften);                     // default: nothing chosen
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
