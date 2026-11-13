using LarkCloud.Core;

namespace LarkCloud.App.UI;

/// <summary>What the "Add a folder" dialog needs from Lark. The real one wraps <see cref="LarkApi"/>; the debug preview fakes it.
/// Every call may take a while: the dialog calls them from a background task.</summary>
internal interface IFolderSource
{
    string RootToken { get; }
    List<RemoteEntry> ChildFolders(string token);
    List<FoundFolder> SearchFolders(string query);
    List<FoundFolder> WikiSpaces();
    List<WikiNode> WikiNodes(string space, string? parent);
    string FolderName(string token);
    string CreateFolder(string parent, string name);
}

internal sealed class LarkFolderSource : IFolderSource
{
    readonly LarkApi _api;
    public LarkFolderSource(LarkApi api) => _api = api;
    public string RootToken => _api.RootToken;
    public List<RemoteEntry> ChildFolders(string token) => _api.ChildFolders(token);
    public List<FoundFolder> SearchFolders(string query) => _api.SearchFolders(query);
    public List<FoundFolder> WikiSpaces() => _api.WikiSpaces();
    public List<WikiNode> WikiNodes(string space, string? parent) => _api.WikiNodes(space, parent);
    public string FolderName(string token) => _api.FolderName(token);
    public string CreateFolder(string parent, string name) => _api.CreateFolder(parent, name);
}
