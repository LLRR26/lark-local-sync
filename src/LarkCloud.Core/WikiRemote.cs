namespace LarkCloud.Core;

/// <summary>A Lark wiki space (or part of one) shown like a folder:
///   pages with sub-pages → folders, pages → shortcuts (and optional offline copies), attached files → files.
/// Read-only for now: Lark's API for adding files to a wiki isn't something this app relies on yet, so the
/// files are read-only on the PC and changes are saved to My Folders instead (see the engine).</summary>
public sealed class WikiRemote : IRemoteDrive
{
    public const string FolderPrefix = "wf:";
    readonly LarkApi _api;
    readonly string _space;
    readonly string? _node;
    readonly string? _host;

    public WikiRemote(LarkApi api, string spaceId, string? rootNode, string? tenantHost)
    {
        _api = api;
        _space = spaceId;
        _node = string.IsNullOrEmpty(rootNode) ? null : rootNode;
        _host = tenantHost;
    }

    public string RootToken => FolderPrefix + (_node ?? "");

    static bool IsWiki(string token) => token.StartsWith(FolderPrefix, StringComparison.Ordinal);

    static LarkApiException ReadOnly() => new(-4, "no permission to change wiki spaces from this app yet");

    public List<RemoteEntry> ListFolder(string folderToken)
    {
        if (!IsWiki(folderToken)) return _api.ListFolder(folderToken);       // Drive folders (e.g. My Folders)
        var node = folderToken[FolderPrefix.Length..];
        var outList = new RemoteListing();
        var nodes = _api.WikiNodes(_space, node.Length == 0 ? null : node);
        outList.Incomplete = (nodes as WikiNodeList)?.Incomplete == true;
        foreach (var n in nodes)
        {
            if (n.ObjType == "file")
                outList.Add(new RemoteEntry { Path = n.Title, Token = n.ObjToken, Type = Kinds.File, MTime = n.EditTime, ParentToken = folderToken });
            else if (n.ObjToken.Length > 0)
                outList.Add(new RemoteEntry
                {
                    Path = n.Title, Token = n.ObjToken, Type = n.ObjType, MTime = n.EditTime, ParentToken = folderToken,
                    Url = _host != null ? $"https://{_host}/wiki/{n.NodeToken}" : null,
                });
            if (n.HasChild)
                outList.Add(new RemoteEntry { Path = n.Title, Token = FolderPrefix + n.NodeToken, Type = Kinds.Folder, MTime = "", ParentToken = folderToken });
        }
        return outList;
    }

    public long ProbeSize(string token) => _api.ProbeSize(token);
    public Stream OpenRead(string token, long offset, CancellationToken ct) => _api.OpenRead(token, offset, ct);

    public string Upload(string srcPath, string parentToken, string name, string? existingToken) =>
        IsWiki(parentToken) ? throw ReadOnly() : _api.Upload(srcPath, parentToken, name, existingToken);

    public string CreateFolder(string parentToken, string name) => IsWiki(parentToken) ? throw ReadOnly() : _api.CreateFolder(parentToken, name);
    public void Delete(string token, string type) => throw ReadOnly();
    public void Move(string token, string type, string destFolderToken) => throw ReadOnly();
    public void Rename(string token, string type, string newName) => throw ReadOnly();
    public bool? CanEdit(string token, string type) => IsWiki(token) ? false : _api.CanEdit(token, type);
    public string MyFoldersRoot() => _api.MyFoldersRoot();
}
