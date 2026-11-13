using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LarkCloud.Core;

/// <summary>Where things live on the PC.
///   config.json next to LarkCloud.exe          - written by IT, same for everyone
///   %APPDATA%\LarkCloud\config.json            - per person: their synced folders ("pairs")
///   %LOCALAPPDATA%\LarkCloud\                  - token.dat, logs\, pairs\&lt;id&gt;\state.db
/// </summary>
public static class AppPaths
{
    public const string AppName = "LarkCloud";
    public const string AppTitle = "Lark Cloud Drive";
    public const string Version = "0.1.0";

    public static string? Override;   // tests

    public static string AppDir => AppContext.BaseDirectory;

    public static string UserConfigDir =>
        Ensure(Path.Combine(Override ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));

    public static string DataDir =>
        Ensure(Path.Combine(Override ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));

    public static string LogDir => Ensure(Path.Combine(DataDir, "logs"));
    public static string PairDir(string id) => Ensure(Path.Combine(DataDir, "pairs", id));

    static string Ensure(string d) { Directory.CreateDirectory(d); return d; }
}

public sealed class Pair
{
    public string Id { get; set; } = NewId();
    public string FolderUrl { get; set; } = "";       // Lark folder link, a bare token, or "" for My Folders
    public string FolderName { get; set; } = "";
    public string LocalFolder { get; set; } = "";
    public bool Paused { get; set; }
    public bool DocCopies { get; set; }               // show read-only offline copies of Lark docs
    public bool FromIt { get; set; }                  // set by IT; can't be removed by the person

    public static string NewId() => "c" + Guid.NewGuid().ToString("N")[..8];
    public string FolderToken => IsWiki ? $"wiki:{WikiSpace}/{WikiNode}" : Config.TokenFromLink(FolderUrl);
    public string? LarkUrl => FolderUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? FolderUrl : null;
    public string? TenantHost => Uri.TryCreate(FolderUrl, UriKind.Absolute, out var u) ? u.Host : null;
    public string Label => string.IsNullOrWhiteSpace(FolderName) ? "Lark folder" : FolderName;

    // ---- wiki spaces: "https://host/wiki/space/<space id>" or "https://host/wiki/<node>?space=<space id>"
    public bool IsWiki => WikiSpace != null;

    public string? WikiSpace
    {
        get
        {
            var m = Regex.Match(FolderUrl ?? "", @"/wiki/space/([A-Za-z0-9_-]+)|[?&]space=([A-Za-z0-9_-]+)");
            return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
        }
    }

    public string? WikiNode
    {
        get
        {
            var m = Regex.Match(FolderUrl ?? "", @"/wiki/(?!space/)([A-Za-z0-9_-]+)");
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    public static string WikiUrl(string? host, string space, string? node) =>
        $"https://{host ?? "larksuite.com"}/wiki/" + (node == null ? $"space/{space}" : $"{node}?space={space}");

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["folder_url"] = FolderUrl, ["folder_name"] = FolderName,
        ["local_folder"] = LocalFolder, ["paused"] = Paused, ["doc_copies"] = DocCopies,
    };

    public static Pair FromJson(JsonNode n, bool fromIt = false) => new()
    {
        Id = (string?)n["id"] ?? NewId(),
        FolderUrl = (string?)n["folder_url"] ?? "",
        FolderName = (string?)n["folder_name"] ?? "",
        LocalFolder = Config.Expand((string?)n["local_folder"] ?? ""),
        Paused = (bool?)n["paused"] ?? false,
        DocCopies = (bool?)n["doc_copies"] ?? false,
        FromIt = fromIt,
    };
}

public sealed class Config
{
    // ---- from IT's config.json
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string Brand { get; set; } = "lark";
    public bool LockFolders { get; set; }
    public List<Pair> DefaultPairs { get; } = new();
    public bool DeleteWins { get; set; } = true;                  // a delete on either side always wins
    public int SettleSeconds { get; set; } = 3;
    public string? Scopes { get; set; }
    public List<string> ExtraScopes { get; } = new();             // optional features enabled in the developer console
    public bool ZipBlockedFiles { get; set; } = true;

    // ---- settings staff can change in the app. The setters take IT's value (config.json); the getters give what
    // applies to this person: IT's value if that setting is locked, else their own choice, else IT's value.
    public const string KeyTheme = "theme", KeyNotifications = "notifications", KeyFreeUp = "free_up_after_days",
        KeyDocFormat = "doc_copies_format", KeyConfirmDeletes = "confirm_mass_deletes", KeyMaxDeletes = "max_deletes",
        KeyTransfers = "max_transfers", KeyIgnore = "extra_ignore";

    string _itTheme = "system", _itDocFormat = "office";
    bool _itNotifications = true, _itConfirmDeletes, _lockAll;
    int _itMaxDeletes = 20, _itTransfers = 8, _itFreeUp = 30;
    readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);
    readonly JsonObject _mine = new();                            // this person's choices: the "settings" object of the user config

    public string Theme { get => Pick(Eff(KeyTheme, _itTheme)?.ToLowerInvariant(), "system", "light", "dark"); set => _itTheme = value; }
    public bool Notifications { get => Eff(KeyNotifications, _itNotifications); set => _itNotifications = value; }
    public int MaxDeletes { get => Math.Clamp(Eff(KeyMaxDeletes, _itMaxDeletes), 1, 100000); set => _itMaxDeletes = value; }
    public bool ConfirmMassDeletes { get => Eff(KeyConfirmDeletes, _itConfirmDeletes); set => _itConfirmDeletes = value; }   // off: deletions run in the background
    public int MaxTransfers { get => Math.Clamp(Eff(KeyTransfers, _itTransfers), 1, 16); set => _itTransfers = value; }
    public int FreeUpAfterDays { get => Math.Max(0, Eff(KeyFreeUp, _itFreeUp)); set => _itFreeUp = value; }       // 0 = never free up space automatically
    public string DocCopyFormat { get => Pick(Eff(KeyDocFormat, _itDocFormat)?.ToLowerInvariant(), "office", "pdf"); set => _itDocFormat = value; }

    /// <summary>Name patterns IT never wants synced. People can't remove these.</summary>
    public List<string> ItExtraIgnore { get; } = new();

    /// <summary>The patterns this person added.</summary>
    public List<string> MyExtraIgnore
    {
        get
        {
            lock (_lock)
                return _mine[KeyIgnore] is JsonArray a ? a.Select(x => (string?)x).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() : new();
        }
    }

    /// <summary>Everything that applies: IT's patterns, plus the person's own unless IT locked this setting.</summary>
    public List<string> ExtraIgnore =>
        (IsLocked(KeyIgnore) ? ItExtraIgnore : ItExtraIgnore.Concat(MyExtraIgnore)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsLocked(string key) => _lockAll || _locked.Contains(key);

    T Eff<T>(string key, T it)
    {
        if (IsLocked(key)) return it;
        lock (_lock) return _mine[key] is JsonValue v && v.TryGetValue<T>(out var mine) ? mine : it;
    }

    static string Pick(string? v, string first, params string[] others) => v == first || others.Contains(v) ? v! : first;

    /// <summary>Save this person's choice (ignored if IT locked the setting). A null value goes back to IT's value.</summary>
    public void SetMine(string key, JsonNode? value)
    {
        lock (_lock)
        {
            if (IsLocked(key)) return;
            if (value == null) _mine.Remove(key); else _mine[key] = value;
            Save();
        }
    }

    public void SetMine(string key, bool v) => SetMine(key, JsonValue.Create(v));
    public void SetMine(string key, int v) => SetMine(key, JsonValue.Create(v));
    public void SetMine(string key, string v) => SetMine(key, JsonValue.Create(v));
    public void SetMyExtraIgnore(IEnumerable<string> patterns) =>
        SetMine(KeyIgnore, new JsonArray(patterns.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()));

    /// <summary>Plain-language problem with a "never sync" pattern, or null if it is fine.</summary>
    public static string? IgnoreProblem(string pattern)
    {
        var p = pattern?.Trim() ?? "";
        if (p.Length == 0) return "Type a file name or pattern, like *.tmp.";
        if (p.IndexOfAny(new[] { '\\', '/' }) >= 0) return "Use just a name, without folders. For example *.tmp or Thumbs.db.";
        return null;
    }

    // ---- per person
    readonly List<Pair> _pairs = new();
    readonly HashSet<string> _defaultsApplied = new();
    readonly object _lock = new();
    public string ItConfigPath { get; private set; } = "";
    public string UserConfigPath { get; private set; } = "";

    public bool IsConfigured => AppId.Length > 0 && AppSecret.Length > 0 && !AppSecret.StartsWith("PASTE");

    public static Config Load(string? itPath = null, string? userPath = null)
    {
        var c = new Config
        {
            ItConfigPath = itPath ?? Path.Combine(AppPaths.AppDir, "config.json"),
            UserConfigPath = userPath ?? Path.Combine(AppPaths.UserConfigDir, "config.json"),
        };
        var it = ReadJson(c.ItConfigPath);
        if (it != null)
        {
            c.AppId = (string?)it["app_id"] ?? "";
            c.AppSecret = (string?)it["app_secret"] ?? "";
            c.Brand = (string?)it["brand"] ?? "lark";
            c.LockFolders = (bool?)it["lock_folders"] ?? false;
            c.MaxDeletes = (int?)it["max_deletes"] ?? 20;
            c.ConfirmMassDeletes = (bool?)it["confirm_mass_deletes"] ?? false;
            c.DeleteWins = (bool?)it["delete_wins"] ?? true;
            c.MaxTransfers = (int?)it["max_transfers"] ?? 8;
            c.FreeUpAfterDays = (int?)it["free_up_after_days"] ?? 30;
            c.Theme = (string?)it["theme"] ?? "system";
            c.Notifications = (bool?)it["notifications"] ?? true;
            if (it["locked_settings"] is JsonArray ls)
                foreach (var x in ls) { if ((string?)x is { Length: > 0 } k) c._locked.Add(k.Trim()); }
            else if ((it["locked_settings"] as JsonValue)?.TryGetValue<string>(out var one) == true) c._locked.Add(one.Trim());
            c._lockAll = c._locked.Contains("all");
            c.SettleSeconds = Math.Max(1, (int?)it["settle_seconds"] ?? 3);
            c.Scopes = (string?)it["scopes"];
            c.ZipBlockedFiles = (bool?)it["zip_blocked_files"] ?? true;
            c.DocCopyFormat = (string?)it["doc_copies_format"] ?? "office";
            if (it["extra_scopes"] is JsonArray xs)
                c.ExtraScopes.AddRange(xs.Select(x => (string?)x).Where(x => !string.IsNullOrEmpty(x))!);
            if (it["extra_ignore"] is JsonArray ig)
                c.ItExtraIgnore.AddRange(ig.Select(x => (string?)x).Where(x => !string.IsNullOrEmpty(x))!);
            if (it["default_pairs"] is JsonArray dp)
                foreach (var p in dp)
                    if (p != null)
                    {
                        var pair = Pair.FromJson(p, fromIt: true);
                        if (string.IsNullOrEmpty((string?)p["id"])) pair.Id = "it-" + StableId(pair.FolderUrl);
                        if (string.IsNullOrEmpty(pair.LocalFolder)) pair.LocalFolder = DefaultLocalFor(pair.Label);
                        c.DefaultPairs.Add(pair);
                    }
        }
        var user = ReadJson(c.UserConfigPath);
        if (user?["pairs"] is JsonArray arr)
            foreach (var p in arr)
                if (p != null) c._pairs.Add(Pair.FromJson(p));
        if (user?["defaults_applied"] is JsonArray da)
            foreach (var x in da) if ((string?)x is { } s) c._defaultsApplied.Add(s);
        if (user?["settings"] is JsonObject mine)
            foreach (var kv in mine) if (kv.Value != null) c._mine[kv.Key] = kv.Value.DeepClone();
        c.ApplyDefaultPairs();
        return c;
    }

    static string StableId(string s)
    {
        var h = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h)[..8].ToLowerInvariant();
    }

    /// <summary>IT's default folders are added once each; afterwards the person may remove them
    /// (unless lock_folders is on, in which case only IT's folders are used).</summary>
    public void ApplyDefaultPairs()
    {
        lock (_lock)
        {
            if (LockFolders) return;
            var changed = false;
            foreach (var d in DefaultPairs)
            {
                if (_defaultsApplied.Contains(d.Id)) continue;
                _defaultsApplied.Add(d.Id);
                changed = true;
                if (_pairs.Any(p => p.FolderToken == d.FolderToken) || LocalOverlap(d.LocalFolder) != null) continue;
                _pairs.Add(new Pair { Id = d.Id, FolderUrl = d.FolderUrl, FolderName = d.FolderName, LocalFolder = d.LocalFolder });
            }
            if (changed) Save();
        }
    }

    public List<Pair> GetPairs()
    {
        lock (_lock) return LockFolders ? DefaultPairs.ToList() : _pairs.ToList();
    }

    public void AddPair(Pair p) { lock (_lock) { _pairs.Add(p); Save(); } }

    public void RemovePair(string id) { lock (_lock) { _pairs.RemoveAll(p => p.Id == id); Save(); } }

    public void SetPaused(string id, bool paused)
    {
        lock (_lock)
        {
            foreach (var p in GetPairs().Where(p => p.Id == id)) p.Paused = paused;
            Save();
        }
    }

    public void SetDocCopies(string id, bool on)
    {
        lock (_lock)
        {
            foreach (var p in GetPairs().Where(p => p.Id == id)) p.DocCopies = on;
            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            var root = new JsonObject
            {
                ["pairs"] = new JsonArray(_pairs.Select(p => (JsonNode)p.ToJson()).ToArray()),
                ["defaults_applied"] = new JsonArray(_defaultsApplied.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["settings"] = _mine.DeepClone(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(UserConfigPath)!);
            var tmp = UserConfigPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, UserConfigPath, overwrite: true);
        }
    }

    /// <summary>Plain-language reason why `local` can't be used next to the existing folders, or null.</summary>
    public string? LocalOverlap(string local, string? excludeId = null)
    {
        foreach (var p in GetPairs())
        {
            if (p.Id == excludeId || string.IsNullOrEmpty(p.LocalFolder)) continue;
            if (SameOrInside(local, p.LocalFolder))
                return $"This folder is inside (or the same as) “{p.LocalFolder}”, which already shows “{p.Label}”. Choose a folder outside it.";
            if (SameOrInside(p.LocalFolder, local))
                return $"This folder contains “{p.LocalFolder}”, which already shows “{p.Label}”. Choose a different folder.";
        }
        return null;
    }

    public static bool SameOrInside(string a, string b)
    {
        string N(string x) => Path.GetFullPath(x).TrimEnd('\\', '/').ToLowerInvariant();
        var na = N(a);
        var nb = N(b);
        return na == nb || na.StartsWith(nb + Path.DirectorySeparatorChar) || na.StartsWith(nb + "/");
    }

    public static string TokenFromLink(string text)
    {
        var m = Regex.Match(text ?? "", @"/drive/folder/([A-Za-z0-9_-]+)");
        return m.Success ? m.Groups[1].Value : (text ?? "").Trim();
    }

    public static string Expand(string path) =>
        string.IsNullOrEmpty(path) ? path : Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            path.StartsWith("~") ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path));

    public static string DefaultLocalFor(string name)
    {
        var safe = new string((name ?? "").Select(c => "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (safe.Length == 0) safe = "Lark folder";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Lark Cloud", safe);
    }

    static JsonNode? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) { Log.Warn($"Could not read {path}: {ex.Message}"); return null; }
    }
}
