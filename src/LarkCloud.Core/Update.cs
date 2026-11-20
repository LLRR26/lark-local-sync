using System.Text.Json;

namespace LarkCloud.Core;

/// <summary>Reads GitHub's releases/latest JSON and says whether it holds a newer LarkCloud.exe.</summary>
public static class Update
{
    public const string AssetName = "LarkCloud.exe";

    public static (string version, string url, long size)? Newer(string releaseJson, string currentVersion)
    {
        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            var r = doc.RootElement;
            if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean()) return null;
            var tag = r.GetProperty("tag_name").GetString()!.Trim();
            if (tag.StartsWith('v') || tag.StartsWith('V')) tag = tag[1..];
            if (!Version.TryParse(tag, out var latest) || !Version.TryParse(currentVersion, out var cur) || latest <= cur) return null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    return (tag, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64());
            return null;
        }
        catch (Exception) { return null; }   // bad JSON, missing fields: no update
    }
}
