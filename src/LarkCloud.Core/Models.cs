namespace LarkCloud.Core;

public static class Kinds
{
    public const string File = "file";
    public const string Folder = "folder";
    public const string Link = "link";        // a Lark online doc shown on the PC as a .url shortcut

    /// <summary>Lark online documents: not files, so they appear on the PC as shortcuts that open in Lark.</summary>
    public static readonly HashSet<string> OnlineDocTypes =
        new(StringComparer.OrdinalIgnoreCase) { "doc", "docx", "sheet", "bitable", "mindnote", "slides", "shortcut", "file_shortcut" };
}

/// <summary>One item in Lark Drive. Path is relative to the synced Lark folder, "/"-separated.</summary>
public sealed class RemoteEntry
{
    public string Path { get; set; } = "";
    public string Token { get; set; } = "";
    public string Type { get; set; } = Kinds.File;
    public string MTime { get; set; } = "";          // Lark modified_time (epoch seconds as text): compared for equality
    public long? Size { get; set; }
    public string? Url { get; set; }
    public string? ParentToken { get; set; }
    public string? LarkName { get; set; }            // the real name in Lark (the PC name may differ, see PathUtil.SafeName)

    public bool IsFolder => Type == Kinds.Folder;
    public bool IsFile => Type == Kinds.File;
    public bool IsOnlineDoc => Kinds.OnlineDocTypes.Contains(Type);
    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;

    public DateTime MTimeUtc
    {
        get
        {
            if (long.TryParse(MTime, out var s) && s > 0)
            {
                if (s > 100_000_000_000) s /= 1000;          // milliseconds
                return DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
            }
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }
    }

    public RemoteEntry Clone() => (RemoteEntry)MemberwiseClone();
}

/// <summary>One item in the synced folder on the PC, as the scan sees it.</summary>
public sealed class LocalItem
{
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public long MTime { get; set; }                  // last-write time, UTC ticks
    public bool IsPlaceholder { get; set; }          // managed by us (has a Lark identity)
    public string? Identity { get; set; }            // the Lark token stored in the placeholder
    public bool InSync { get; set; } = true;         // false once Windows sees the content changed
    public bool OnDisk { get; set; }                 // the file's data is on the PC (opened / "always keep")
    public bool Pinned { get; set; }                 // "Always keep on this device"
    public bool Unpinned { get; set; }               // "Free up space"
    public long LastUse { get; set; }                // max(last access, last write), UTC ticks
    public bool ReadOnly { get; set; }
}

/// <summary>What a path looked like on both sides right after it was last synced.</summary>
public sealed class BaseEntry
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = Kinds.File;
    public string? Token { get; set; }
    public string? RType { get; set; }
    public string? RMTime { get; set; }              // null = "we just uploaded it; adopt what Lark reports next"
    public long Size { get; set; }
    public long LMTime { get; set; }                 // local last-write ticks right after sync
    public string? Url { get; set; }                 // for links

    public BaseEntry Clone() => (BaseEntry)MemberwiseClone();
}

public sealed class PendingDelete
{
    public string Path { get; init; } = "";
    public bool InLark { get; init; }                // true: would be deleted in Lark (deleted on the PC); false: on the PC
}

public sealed class SyncResult
{
    public int Uploaded, Created, Updated, DeletedLocal, DeletedRemote, Moved, Downloaded;
    public List<string> Conflicts { get; } = new();
    public Dictionary<string, string> Failed { get; } = new();         // path -> plain-language problem
    public Dictionary<string, string?> UploadHere { get; } = new();    // blocked path -> its Lark folder link
    public List<string> Notes { get; } = new();                        // non-file problems (duplicate names, ...)
    public List<PendingDelete> PendingDeletes { get; } = new();
    public Dictionary<string, string> Info { get; } = new();             // path -> "good to know" (not a problem)
    public Dictionary<string, string?> OpenLinks { get; } = new();       // path -> Lark page to open (e.g. to ask for access)
    public bool ViewOnly { get; set; }                                   // the person can only view this Lark folder
    public bool Full { get; set; } = true;
    public bool Interrupted { get; set; }                                // a full check paused for quick work: nothing was decided or changed
    public bool Changed => Uploaded + Created + Updated + DeletedLocal + DeletedRemote + Moved + Downloaded + Conflicts.Count > 0;

    public string Summary()
    {
        var parts = new List<string>();
        void Add(int n, string label) { if (n > 0) parts.Add($"{n:N0} {label}"); }
        Add(Uploaded, "uploaded");
        Add(Created + Updated, "updated from Lark");
        Add(Moved, "moved");
        Add(DeletedLocal, "deleted here");
        Add(DeletedRemote, "deleted in Lark");
        if (Conflicts.Count > 0) parts.Add($"{Conflicts.Count} conflict(s)");
        if (Failed.Count > 0) parts.Add($"{Failed.Count} file(s) can't sync");
        return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
    }
}

public static class PathUtil
{
    public static string Parent(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
    public static string Name(string rel) => rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;
    public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    public static int Depth(string rel) => rel.Length == 0 ? 0 : rel.Count(c => c == '/') + 1;
    public static bool IsUnder(string rel, string folder) => folder.Length == 0 || rel.StartsWith(folder + "/", StringComparison.Ordinal);

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    static readonly Dictionary<char, char> LookAlike = new()
    {
        ['<'] = '＜', ['>'] = '＞', [':'] = '：', ['"'] = '＂', ['\\'] = '＼', ['|'] = '｜', ['?'] = '？', ['*'] = '＊', ['/'] = '／',
    };

    /// <summary>A Lark name made safe for Windows, the way OneDrive does it: characters Windows doesn't allow
    /// become look-alikes (Plan? → Plan？), a trailing dot/space is dropped, reserved names get " (Lark)".</summary>
    public static string SafeName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 32) continue;
            sb.Append(LookAlike.TryGetValue(c, out var r) ? r : c);
        }
        var s = sb.ToString().TrimEnd(' ', '.');
        if (s.Length == 0) s = "Untitled";
        var dot = s.IndexOf('.');
        var stem = dot > 0 ? s[..dot] : s;
        if (Reserved.Contains(stem)) s = stem + " (Lark)" + (dot > 0 ? s[dot..] : "");
        if (s.Length > 240)
        {
            var ext = System.IO.Path.GetExtension(s);
            s = s[..(240 - ext.Length)] + ext;
        }
        return s;
    }

    /// <summary>"Report.xlsx" if free, otherwise "Report (2).xlsx", "Report (3).xlsx", ...</summary>
    public static string FirstFree(string name, ISet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        var ext = name.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetExtension(name[..^4]) + ".url" : System.IO.Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var n = 2; ; n++)
        {
            var c = $"{stem} ({n}){ext}";
            if (!taken.Contains(c)) return c;
        }
    }

    /// <summary>Is `candidate` the name itself or one of its numbered variants ("Report (2).xlsx")?</summary>
    public static bool IsVariant(string candidate, string name)
    {
        if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;
        var ext = name.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetExtension(name[..^4]) + ".url" : System.IO.Path.GetExtension(name);
        var stem = name[..^ext.Length];
        return System.Text.RegularExpressions.Regex.IsMatch(candidate,
            "^" + System.Text.RegularExpressions.Regex.Escape(stem) + @" \(\d+\)" + System.Text.RegularExpressions.Regex.Escape(ext) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Why a Lark name can't exist on Windows, or null if it can.</summary>
    public static string? WindowsNameProblem(string name)
    {
        if (string.IsNullOrEmpty(name)) return "it has no name";
        if (name.IndexOfAny(new[] { '<', '>', ':', '"', '\\', '|', '?', '*' }) >= 0 || name.Any(c => c < 32))
            return "its name has a character Windows doesn't allow (< > : \" \\ | ? *)";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "its name ends with a space or a dot, which Windows doesn't allow";
        var stem = name.Split('.')[0];
        if (Reserved.Contains(stem)) return $"“{stem}” is a name Windows reserves";
        if (name.Length > 255) return "its name is too long for Windows";
        return null;
    }

    /// <summary>"Report.xlsx" -> "Report (conflict from PC-NAME 2026-11-06).xlsx"</summary>
    public static string ConflictName(string rel, string who, DateTime when)
    {
        var parent = Parent(rel);
        var name = Name(rel);
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        return Join(parent, $"{stem} (conflict from {who} {when:yyyy-MM-dd HHmm}){ext}");
    }
}
