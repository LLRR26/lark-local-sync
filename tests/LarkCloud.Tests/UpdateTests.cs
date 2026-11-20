using LarkCloud.Core;

namespace LarkCloud.Tests;

public class UpdateTests
{
    static string Json(string tag, string asset = "LarkCloud.exe", bool pre = false) =>
        $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":{{(pre ? "true" : "false")}},"assets":[{"name":"{{asset}}","browser_download_url":"https://x/y","size":42}]}""";

    [Fact] public void Newer_is_returned() => Assert.Equal(("0.2.0", "https://x/y", 42L), Update.Newer(Json("0.2.0"), "0.1.0"));
    [Fact] public void V_prefix_and_asset_case() => Assert.Equal("1.0.0", Update.Newer(Json("v1.0.0", "larkcloud.EXE"), "0.9.9")?.version);
    [Fact] public void Same_or_older_is_null() { Assert.Null(Update.Newer(Json("v0.1.0"), "0.1.0")); Assert.Null(Update.Newer(Json("v0.0.9"), "0.1.0")); }
    [Fact] public void Missing_asset_is_null() => Assert.Null(Update.Newer(Json("v0.2.0", "other.zip"), "0.1.0"));
    [Fact] public void Prerelease_is_null() => Assert.Null(Update.Newer(Json("v0.2.0", pre: true), "0.1.0"));
    [Fact] public void Bad_json_is_null() { Assert.Null(Update.Newer("not json", "0.1.0")); Assert.Null(Update.Newer("{}", "0.1.0")); }
}
