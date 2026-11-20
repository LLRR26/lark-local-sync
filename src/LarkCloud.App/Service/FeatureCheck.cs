using LarkCloud.Core;

namespace LarkCloud.App.Service;

internal enum CheckStatus { Ok, Note, NeedsPermission, Failed }

/// <summary>One line of the "Check Lark features" result. <paramref name="Fix"/> is plain words for the person;
/// <paramref name="ForIt"/> names the permission, for the IT team.</summary>
internal sealed record CheckItem(CheckStatus Status, string Name, string Detail, string? Fix = null, string? ForIt = null);

/// <summary>"Check Lark features": tries each feature against the person's own Lark (in a temporary folder
/// in My Folders, removed afterwards) and reports what works and what to enable for the rest. Blocking: run it on a background thread.</summary>
internal static class FeatureCheck
{
    const string NoteMark = "\u0001";
    static string Note(string text) => NoteMark + text;

    static bool Missing(Exception ex) => ex is LarkApiException { Code: 99991679 };

    static string Why(Exception ex) => Missing(ex) ? "Lark says this app is missing a permission for it." : ex.Message;

    public static void Run(SyncService svc, Action<CheckItem> report)
    {
        void Say(CheckItem i)
        {
            Log.Info($"Check: {i.Status} · {i.Name}: {i.Detail}");
            report(i);
        }
        void Fail(string name, Exception ex, string? fix = null, string? forIt = null) =>
            Say(new(Missing(ex) ? CheckStatus.NeedsPermission : CheckStatus.Failed, name, Why(ex), fix, forIt));
        void Check(string name, Func<string> test, string? fix = null, string? forIt = null)
        {
            try
            {
                var r = test();
                Say(r.StartsWith(NoteMark) ? new(CheckStatus.Note, name, r[1..]) : new(CheckStatus.Ok, name, r));
            }
            catch (Exception ex) { Fail(name, ex, fix, forIt); }
        }

        var api = svc.CreateApi();
        string root;
        try { root = api.MyFoldersRoot(); Say(new(CheckStatus.Ok, "Sign-in and My Folders", "OK")); }
        catch (Exception ex) { Fail("Sign-in and My Folders", ex); return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), ".lcs-check-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmpDir);
        string? folder = null;
        try
        {
            folder = api.CreateFolder(root, "Lark Cloud check (safe to delete)");
            Say(new(CheckStatus.Ok, "Create a folder", "OK"));

            var normal = Path.Combine(tmpDir, "hello.txt");
            File.WriteAllText(normal, "Hello from Lark Cloud Drive");
            string? helloTok = null;
            Check("Upload a file", () => { helloTok = api.Upload(normal, folder, "hello.txt", null); return "OK"; });
            if (helloTok != null)
            {
                Check("File size lookup", () => $"OK ({api.ProbeSize(helloTok)} bytes)");
                Check("Overwrite in place (keeps version history)", () =>
                {
                    File.WriteAllText(normal, "Hello again");
                    var t = api.Upload(normal, folder, "hello.txt", helloTok);
                    return t == helloTok ? "OK" : Note("Not available on this Lark – the app uploads a new copy and replaces the old one instead (works, but the Lark link changes).");
                });
                Check("Rename", () => { api.Rename(helloTok, "file", "hello renamed.txt"); return "OK"; });
                Check("Edit permission check", () => api.CanEdit(folder, "folder") switch
                {
                    true => "OK (view-only folders will be read-only on the PC)",
                    false => Note("Lark answered “no edit permission” for your own folder – unexpected."),
                    null => Note("Not available – view-only folders are still detected when a save is refused."),
                });
            }

            var empty = Path.Combine(tmpDir, "empty.txt");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            try
            {
                var t = api.Upload(empty, folder, "empty.txt", null);
                Say(new(CheckStatus.Ok, "Empty (0-byte) files", $"Lark accepts them (size reads as {api.ProbeSize(t)})"));
            }
            catch (LarkApiException ex) when (ex.EmptyRefused)
            {
                Say(new(CheckStatus.Note, "Empty (0-byte) files", "Lark doesn't accept them. They stay on the PC and upload once they have content (shown as a note)."));
            }
            catch (Exception ex) { Fail("Empty (0-byte) files", ex); }

            // A small script with the kind of content Lark's filter has blocked before
            var script = Path.Combine(tmpDir, "check.js");
            File.WriteAllText(script, "<script>document.write(`${location.href}`); eval('1');</script>");
            try
            {
                api.Upload(script, folder, "check.js", null);
                Say(new(CheckStatus.Ok, "Code and script files", "This test file wasn't blocked (some real files still may be)."));
            }
            catch (LarkApiException ex) when (ex.IsBlocked)
            {
                var features = new RemoteFeatures(api, api, _ => null, (_, _) => { });
                try
                {
                    features.UploadZipped(script, folder, "check.js", null);
                    Say(new(CheckStatus.Ok, "Code and script files", "Lark's filter blocked the test file, and storing it zipped worked – blocked files will sync as “name (zipped).zip”."));
                }
                catch (Exception ex2)
                {
                    Say(new(CheckStatus.Failed, "Code and script files", "Blocked, and the zipped copy was blocked too: " + Why(ex2),
                        "For these files use “Upload with Lark…” in Files that can't sync."));
                }
            }
            catch (Exception ex) { Fail("Code and script files", ex); }
        }
        catch (Exception ex) { Fail("Create a folder", ex); }

        Check("Find folders shared with you (search)", () => $"OK ({api.SearchFolders("Lark").Count} folder(s) found for “Lark”)",
            "Ask your IT team to turn on searching for the Lark app. Until then, paste the folder's link when you add a folder.",
            "Enable “search:docs:read” in the Lark developer console and add it to extra_scopes in config.json (README → Optional features).");
        Check("Wiki spaces", () =>
        {
            var spaces = api.WikiSpaces();
            if (spaces.Count == 0) return "OK (no wiki spaces you can open)";
            var nodes = api.WikiNodes(spaces[0].Token, null);
            var file = nodes.FirstOrDefault(n => n.ObjType == "file");
            var extra = "";
            if (file != null)
            {
                try { extra = $"; opening a file attached in the wiki works ({api.ProbeSize(file.ObjToken)} bytes)"; }
                catch (Exception ex) { extra = $"; opening a file attached in the wiki failed: {Why(ex)}"; }
            }
            return $"OK ({spaces.Count} space(s), {nodes.Count} top-level page(s) in “{spaces[0].Name}”{extra})";
        }, "Ask your IT team to turn on wiki access for the Lark app.",
            "Enable “wiki:wiki:readonly” in the Lark developer console and add it to extra_scopes in config.json.");
        Check("Offline copies of Lark docs (export)", () =>
        {
            var doc = api.ListFolder(api.MyFoldersRoot()).FirstOrDefault(e => SpecialTokens.CopyExt(e.Type, "office") != null);
            if (doc == null) return Note("Skipped – there is no Lark doc or sheet directly in My Folders to test with.");
            var ext = SpecialTokens.CopyExt(doc.Type, svc.Config.DocCopyFormat)!;
            var (ft, size) = api.Export(doc.Token, doc.Type, ext, CancellationToken.None);
            using var s = api.OpenExport(ft, CancellationToken.None);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return $"OK (exported “{doc.Name}” as .{ext}, {ms.Length:N0} bytes)";
        }, "Ask your IT team to turn on document export for the Lark app.",
            "Enable “drive:export:readonly” (or “docs:document:export”) in the developer console and add it to extra_scopes in config.json.");

        if (folder != null)
        {
            try { api.Delete(folder, "folder"); Say(new(CheckStatus.Ok, "Clean-up", "The check folder was moved to Lark's trash.")); }
            catch (Exception ex) { Say(new(CheckStatus.Failed, "Clean-up", Why(ex), "Please delete “Lark Cloud check (safe to delete)” in My Folders yourself.")); }
        }
        try { Directory.Delete(tmpDir, true); } catch { }
    }
}
