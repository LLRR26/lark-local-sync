using LarkCloud.Core;

namespace LarkCloud.Tests;

/// <summary>Which value of a setting applies: IT's, or the person's own, unless IT locked it.</summary>
public class ConfigSettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lcd-cfg-" + Guid.NewGuid().ToString("N"));
    string It => Path.Combine(_dir, "it.json");
    string User => Path.Combine(_dir, "user.json");

    public ConfigSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    Config Load(string itJson = "{}")
    {
        File.WriteAllText(It, itJson);
        return Config.Load(It, User);
    }

    [Fact]
    public void It_values_and_defaults_apply_when_the_person_chose_nothing()
    {
        var c = Load("""{"free_up_after_days": 14, "max_transfers": 4}""");
        Assert.Equal(14, c.FreeUpAfterDays);
        Assert.Equal(4, c.MaxTransfers);
        Assert.Equal(20, c.MaxDeletes);
        Assert.Equal("system", c.Theme);
        Assert.True(c.Notifications);
        Assert.Equal("office", c.DocCopyFormat);
        Assert.False(c.ConfirmMassDeletes);
    }

    [Fact]
    public void A_person_s_choice_beats_it_s_value()
    {
        var c = Load("""{"free_up_after_days": 14}""");
        c.SetMine(Config.KeyFreeUp, 60);
        c.SetMine(Config.KeyTheme, "dark");
        c.SetMine(Config.KeyNotifications, false);
        c.SetMine(Config.KeyDocFormat, "pdf");
        Assert.Equal(60, c.FreeUpAfterDays);
        Assert.Equal("dark", c.Theme);
        Assert.False(c.Notifications);
        Assert.Equal("pdf", c.DocCopyFormat);
        c.SetMine(Config.KeyFreeUp, 0);                      // 0 = off is a real choice, not "unset"
        Assert.Equal(0, c.FreeUpAfterDays);
        c.SetMine(Config.KeyFreeUp, (System.Text.Json.Nodes.JsonNode?)null);                   // back to IT's value
        Assert.Equal(14, c.FreeUpAfterDays);
    }

    [Fact]
    public void A_locked_setting_ignores_the_person_s_choice_even_one_already_saved()
    {
        File.WriteAllText(User, """{"settings": {"max_transfers": 16, "theme": "dark"}}""");
        var c = Load("""{"max_transfers": 2, "locked_settings": ["max_transfers"]}""");
        Assert.True(c.IsLocked(Config.KeyTransfers));
        Assert.False(c.IsLocked(Config.KeyTheme));
        Assert.Equal(2, c.MaxTransfers);
        Assert.Equal("dark", c.Theme);
        c.SetMine(Config.KeyTransfers, 8);                   // refused
        Assert.Equal(2, c.MaxTransfers);
    }

    [Theory]
    [InlineData("""{"locked_settings": ["all"], "max_deletes": 50}""")]
    [InlineData("""{"locked_settings": "all", "max_deletes": 50}""")]
    public void All_locks_every_setting(string itJson)
    {
        var c = Load(itJson);
        c.SetMine(Config.KeyMaxDeletes, 200);
        c.SetMine(Config.KeyTheme, "light");
        c.SetMyExtraIgnore(new[] { "*.tmp" });
        Assert.Equal(50, c.MaxDeletes);
        Assert.Equal("system", c.Theme);
        Assert.Empty(c.ExtraIgnore);
        Assert.All(new[] { Config.KeyTheme, Config.KeyNotifications, Config.KeyFreeUp, Config.KeyDocFormat, Config.KeyConfirmDeletes, Config.KeyMaxDeletes, Config.KeyTransfers, Config.KeyIgnore },
            k => Assert.True(c.IsLocked(k)));
    }

    [Fact]
    public void Ignore_patterns_are_it_s_plus_the_person_s_unless_locked()
    {
        var c = Load("""{"extra_ignore": ["*.bak", "Thumbs.db"]}""");
        c.SetMyExtraIgnore(new[] { "*.tmp", "*.BAK" });
        Assert.Equal(new[] { "*.bak", "Thumbs.db", "*.tmp" }, c.ExtraIgnore);     // no duplicates, case-insensitive
        Assert.Equal(new[] { "*.tmp", "*.BAK" }, c.MyExtraIgnore);
        Assert.Equal(new[] { "*.bak", "Thumbs.db" }, c.ItExtraIgnore);            // the person can't remove IT's

        var locked = Load("""{"extra_ignore": ["*.bak"], "locked_settings": ["extra_ignore"]}""");
        locked.SetMyExtraIgnore(new[] { "*.tmp" });
        Assert.Equal(new[] { "*.bak" }, locked.ExtraIgnore);
    }

    [Fact]
    public void Values_are_kept_in_range()
    {
        var c = Load("""{"max_transfers": 99, "free_up_after_days": -5, "doc_copies_format": "html", "theme": "neon"}""");
        Assert.Equal(16, c.MaxTransfers);
        Assert.Equal(0, c.FreeUpAfterDays);
        Assert.Equal("office", c.DocCopyFormat);
        Assert.Equal("system", c.Theme);
    }

    [Fact]
    public void The_settings_object_survives_save_and_load_and_leaves_folders_alone()
    {
        var c = Load();
        c.AddPair(new Pair { FolderUrl = "https://x/drive/folder/abc", LocalFolder = Path.Combine(_dir, "f") });
        c.SetMine(Config.KeyTheme, "light");
        c.SetMine(Config.KeyConfirmDeletes, true);
        c.SetMine(Config.KeyMaxDeletes, 50);
        c.SetMine(Config.KeyTransfers, 2);
        c.SetMine(Config.KeyFreeUp, 7);
        c.SetMine(Config.KeyNotifications, false);
        c.SetMine(Config.KeyDocFormat, "pdf");
        c.SetMyExtraIgnore(new[] { "*.tmp" });

        var back = Config.Load(It, User);
        Assert.Equal("light", back.Theme);
        Assert.True(back.ConfirmMassDeletes);
        Assert.Equal(50, back.MaxDeletes);
        Assert.Equal(2, back.MaxTransfers);
        Assert.Equal(7, back.FreeUpAfterDays);
        Assert.False(back.Notifications);
        Assert.Equal("pdf", back.DocCopyFormat);
        Assert.Equal(new[] { "*.tmp" }, back.ExtraIgnore);
        Assert.Single(back.GetPairs());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("*.tmp", true)]
    [InlineData("Thumbs.db", true)]
    public void Ignore_patterns_are_checked(string pattern, bool ok) => Assert.Equal(ok, Config.IgnoreProblem(pattern) == null);
}
