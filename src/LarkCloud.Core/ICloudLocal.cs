using System.Text.RegularExpressions;

namespace LarkCloud.Core;

/// <summary>The PC side: a folder whose files are Windows cloud placeholders. The Windows app implements
/// it with the Cloud Files API; the tests use an in-memory fake. Paths are relative, "/"-separated.</summary>
public interface ICloudLocal
{
    string Root { get; }
    string FullPath(string rel);
    List<LocalItem> Scan();
    LocalItem? Stat(string rel);

    /// <summary>Folder that mirrors a Lark folder (in sync).</summary>
    void CreateFolderPlaceholder(string rel, string token);
    /// <summary>A file that shows on the PC but whose data stays in Lark until opened (in sync).</summary>
    void CreateFilePlaceholder(string rel, string token, long size, DateTime mtimeUtc);
    /// <summary>Lark has a newer version: new size/time, local data dropped (fetched again when opened).
    /// Throws IOException when the file is open in an app.</summary>
    void UpdateFilePlaceholder(string rel, string token, long size, DateTime mtimeUtc);
    /// <summary>After uploading a file or creating a folder in Lark: link it to `token` and mark it in sync.</summary>
    LocalItem MarkSynced(string rel, string token);
    LocalItem? MarkUploaded(string rel, string token, string snapshot)
    {
        using var copy = File.OpenRead(snapshot);
        using var current = OpenForUpload(rel);
        return System.Security.Cryptography.SHA256.HashData(copy).AsSpan()
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(current))
            ? MarkSynced(rel, token) : null;
    }
    void Move(string from, string to);
    /// <summary>Delete; `recycle` sends it to the Recycle Bin.</summary>
    void Delete(string rel, bool recycle);
    void Dehydrate(string rel);
    void Hydrate(string rel, CancellationToken ct);
    void WriteShortcut(string rel, string url);
    Stream OpenForUpload(string rel);
    /// <summary>Mark a file read-only (view-only Lark folders, offline copies of Lark docs).</summary>
    void SetReadOnly(string rel, bool readOnly);
    /// <summary>Folders/files the last Scan couldn't read (locked, access denied). Anything at or under these
    /// is "unknown" for that check – never treated as deleted.</summary>
    IReadOnlyCollection<string> UnreadablePaths => Array.Empty<string>();
}

/// <summary>Which names never sync: things that are never real documents.</summary>
public sealed class IgnoreRules
{
    public static readonly string[] Defaults =
    {
        "~$*",                      // Office "file is open" markers
        ".~lock.*#",                // LibreOffice "file is open" markers
        "*.crdownload",             // unfinished Chrome/Edge downloads
        ".lcs-*",                   // this app's own temporary files
        "$RECYCLE.BIN", "System Volume Information",
    };

    readonly List<Regex> _rx;

    public IgnoreRules(IEnumerable<string>? extra = null)
    {
        _rx = Defaults.Concat(extra ?? Array.Empty<string>())
            .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }

    public bool IsIgnoredName(string name) => _rx.Any(r => r.IsMatch(name));

    /// <summary>Ignored if the name or any parent folder's name is ignored.</summary>
    public bool IsIgnored(string rel) => rel.Split('/').Any(IsIgnoredName);
}
