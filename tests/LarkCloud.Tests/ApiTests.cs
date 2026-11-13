using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LarkCloud.Core;

namespace LarkCloud.Tests;

/// <summary>Answers HTTP requests the way open.larksuite.com / accounts.larksuite.com would, backed by a FakeRemote.</summary>
public sealed class FakeLarkHttp : HttpMessageHandler
{
    public readonly FakeRemote R = new();
    public string ValidToken = "uat-1";
    public bool ExpireNext;
    public int RateLimitNext;
    public int PageSize = 2;
    public Func<string, bool>? Waf;                       // content -> blocked?  (bare 403, like the real filter)
    public bool WafBlocksBinary = true, WafBlocksParts = true;
    public readonly List<string> Log = new();
    readonly Dictionary<string, (JsonObject Meta, SortedDictionary<int, byte[]> Parts)> _uploads = new();
    public int DevicePolls;
    public Func<HttpResponseMessage?>? TokenHook;          // e.g. simulate DNS failure / 429 on sign-in renewal
    public int TokenCalls;
    public readonly List<(string Space, string Parent, WikiNode Node)> Wiki = new();
    public bool PermissionAuth = true;                    // members/auth answers "can edit"
    public readonly List<FoundFolder> SearchResults = new();

    static HttpResponseMessage Json(JsonNode body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Ok(JsonNode data) => Json(new JsonObject { ["code"] = 0, ["msg"] = "success", ["data"] = data });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => Task.FromResult(Handle(req));
    protected override HttpResponseMessage Send(HttpRequestMessage req, CancellationToken ct) => Handle(req);

    HttpResponseMessage Handle(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
        lock (Log) Log.Add($"{req.Method} {path}");

        // ---------------- accounts
        if (path == "/oauth/v1/device_authorization")
            return Json(new JsonObject { ["device_code"] = "dev1", ["user_code"] = "ABCD-EFGH", ["verification_uri_complete"] = "https://x/verify", ["interval"] = 1, ["expires_in"] = 60 });
        if (path == "/oauth/v3/token")
        {
            TokenCalls++;
            if (TokenHook?.Invoke() is { } hooked) return hooked;
            var body = req.Content!.ReadAsStringAsync().Result;
            if (body.Contains("device_code") && ++DevicePolls < 2) return Json(new JsonObject { ["error"] = "authorization_pending" }, HttpStatusCode.BadRequest);
            return Json(new JsonObject { ["access_token"] = ValidToken, ["expires_in"] = 7200, ["refresh_token"] = "r-1", ["refresh_token_expires_in"] = 604800 });
        }

        // ---------------- open api
        if (req.Headers.Authorization?.Parameter != ValidToken) return Json(new JsonObject { ["code"] = 99991677, ["msg"] = "token expired" }, HttpStatusCode.BadRequest);
        if (ExpireNext) { ExpireNext = false; ValidToken = "uat-2"; return Json(new JsonObject { ["code"] = 99991677, ["msg"] = "token expired" }, HttpStatusCode.BadRequest); }
        if (RateLimitNext > 0) { RateLimitNext--; return Json(new JsonObject { ["code"] = 99991400, ["msg"] = "rate limited" }, HttpStatusCode.BadRequest); }

        if (path == "/open-apis/authen/v1/user_info") return Ok(new JsonObject { ["name"] = "Rennon" });
        if (path == "/open-apis/wiki/v2/spaces")
            return Ok(new JsonObject { ["items"] = new JsonArray(Wiki.Select(w => w.Space).Distinct().Select(s => (JsonNode)new JsonObject { ["space_id"] = s, ["name"] = "Space " + s }).ToArray()), ["has_more"] = false });
        if (path.StartsWith("/open-apis/wiki/v2/spaces/") && path.EndsWith("/nodes"))
        {
            var space = path.Split('/')[^2];
            var parent = q["parent_node_token"] ?? "";
            return Ok(new JsonObject
            {
                ["items"] = new JsonArray(Wiki.Where(w => w.Space == space && w.Parent == parent).Select(w => (JsonNode)new JsonObject
                {
                    ["node_token"] = w.Node.NodeToken, ["obj_token"] = w.Node.ObjToken, ["obj_type"] = w.Node.ObjType, ["title"] = w.Node.Title,
                    ["has_child"] = Wiki.Any(c => c.Parent == w.Node.NodeToken), ["obj_edit_time"] = w.Node.EditTime,
                }).ToArray()),
                ["has_more"] = false,
            });
        }
        if (path.EndsWith("/members/auth")) return Ok(new JsonObject { ["auth_result"] = PermissionAuth });
        if (path == "/open-apis/search/v2/doc_wiki/search")
            return Ok(new JsonObject
            {
                ["res_units"] = new JsonArray(SearchResults.Select(f => (JsonNode)new JsonObject
                {
                    ["title_highlighted"] = $"<h>{f.Name}</h>", ["result_meta"] = new JsonObject { ["token"] = f.Token, ["url"] = f.Url },
                }).ToArray()),
            });
        if (path == "/open-apis/drive/explorer/v2/root_folder/meta") return Ok(new JsonObject { ["token"] = R.RootToken });
        if (path == "/open-apis/drive/v1/files" && req.Method == HttpMethod.Get)
        {
            var folder = q["folder_token"]!;
            if (folder == "") folder = R.RootToken;
            List<RemoteEntry> kids;
            try { kids = R.ListFolder(folder); }
            catch (LarkApiException) { return Json(new JsonObject { ["code"] = 1061007, ["msg"] = "file has been delete." }, HttpStatusCode.BadRequest); }
            var start = int.Parse(q["page_token"] ?? "0");
            var page = kids.Skip(start).Take(PageSize).ToList();
            var more = start + PageSize < kids.Count;
            return Ok(new JsonObject
            {
                ["files"] = new JsonArray(page.Select(k => (JsonNode)new JsonObject { ["token"] = k.Token, ["name"] = k.Path, ["type"] = k.Type, ["modified_time"] = k.MTime, ["url"] = k.Url }).ToArray()),
                ["has_more"] = more, ["next_page_token"] = more ? (start + PageSize).ToString() : "",
            });
        }
        if (path.EndsWith("/download"))
        {
            var tok = path.Split('/')[^2];
            var data = R.Nodes[tok].Content;
            if (req.Headers.Range?.Ranges.FirstOrDefault() is { } rg0 && (rg0.From ?? 0) >= data.Length)
            {
                var r416 = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new StringContent("") };
                r416.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(data.Length);
                return r416;
            }
            if (req.Headers.Range?.Ranges.FirstOrDefault() is { } rg)
            {
                var from = rg.From ?? 0;
                var to = Math.Min(rg.To ?? data.Length - 1, data.Length - 1);
                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[(int)from..(int)(to + 1)]) };
                resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, data.Length);
                resp.Content.Headers.ContentType = new("application/octet-stream");
                return resp;
            }
            var full = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            full.Content.Headers.ContentType = new("application/octet-stream");
            return full;
        }
        if (path is "/open-apis/drive/v1/files/upload_all" or "/open-apis/drive/v1/files/upload_part")
        {
            var form = (MultipartFormDataContent)req.Content!;
            var fields = new Dictionary<string, string>();
            byte[] file = Array.Empty<byte>();
            var binary = false;
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "file") { file = part.ReadAsByteArrayAsync().Result; binary = part.Headers.ContentType?.MediaType == "application/octet-stream"; }
                else fields[name] = part.ReadAsStringAsync().Result;
            }
            var isPart = path.EndsWith("upload_part");
            if (Waf?.Invoke(Encoding.UTF8.GetString(file)) == true && (!binary || WafBlocksBinary) && (!isPart || WafBlocksParts))
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>Forbidden</html>", Encoding.UTF8, "text/html") };
            if (isPart)
            {
                _uploads[fields["upload_id"]].Parts[int.Parse(fields["seq"])] = file;
                return Ok(new JsonObject());
            }
            if (long.Parse(fields["size"]) != file.Length) return Json(new JsonObject { ["code"] = 1061002, ["msg"] = "params error" }, HttpStatusCode.BadRequest);
            if (file.Length == 0) return Json(new JsonObject { ["code"] = 1061002, ["msg"] = "params error" }, HttpStatusCode.BadRequest);
            return Ok(new JsonObject { ["file_token"] = Store(fields["parent_node"], fields["file_name"], fields.GetValueOrDefault("file_token"), file) });
        }
        if (path == "/open-apis/drive/v1/files/upload_prepare")
        {
            var meta = JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!.AsObject();
            var id = "up" + _uploads.Count;
            _uploads[id] = (meta, new());
            var size = (long)meta["size"]!;
            return Ok(new JsonObject { ["upload_id"] = id, ["block_size"] = 4, ["block_num"] = (int)Math.Max(1, (size + 3) / 4) });
        }
        if (path == "/open-apis/drive/v1/files/upload_finish")
        {
            var b = JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!;
            var u = _uploads[(string)b["upload_id"]!];
            var content = u.Parts.Values.SelectMany(x => x).ToArray();
            return Ok(new JsonObject { ["file_token"] = Store((string)u.Meta["parent_node"]!, (string)u.Meta["file_name"]!, (string?)u.Meta["file_token"], content) });
        }
        if (path == "/open-apis/drive/v1/files/create_folder")
        {
            var b = JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!;
            return Ok(new JsonObject { ["token"] = R.CreateFolder((string)b["folder_token"]!, (string)b["name"]!) });
        }
        if (req.Method == HttpMethod.Delete)
        {
            R.Delete(path.Split('/')[^1], q["type"]!);
            return Ok(q["type"] == "folder" ? new JsonObject { ["task_id"] = "t1" } : new JsonObject());
        }
        if (path == "/open-apis/drive/v1/files/task_check") return Ok(new JsonObject { ["status"] = "success" });
        if (path.EndsWith("/move"))
        {
            var b = JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!;
            R.Move(path.Split('/')[^2], (string)b["type"]!, (string)b["folder_token"]!);
            return Ok(new JsonObject());
        }
        if (req.Method == HttpMethod.Patch)
        {
            var b = JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!;
            R.Rename(path.Split('/')[^1], q["type"]!, (string)b["new_title"]!);
            return Ok(new JsonObject());
        }
        return Json(new JsonObject { ["code"] = 404, ["msg"] = "not found " + path }, HttpStatusCode.NotFound);
    }

    string Store(string parent, string name, string? existing, byte[] content)
    {
        if (existing != null && R.Nodes.TryGetValue(existing, out var n)) { n.Content = content; n.MTime = R.Tick(); return existing; }
        var tok = R.AddFile(PathUtil.Join(R.PathOf(parent), name), content);
        return tok;
    }
}

public sealed class ApiFixture : IDisposable
{
    public readonly FakeLarkHttp Http = new();
    public readonly LarkAuth Auth;
    public readonly LarkApi Api;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lcs-api-" + Guid.NewGuid().ToString("N"));

    public ApiFixture(bool signIn = true)
    {
        Directory.CreateDirectory(_dir);
        var cfg = new Config { AppId = "cli_x", AppSecret = "secret" };
        var client = new HttpClient(Http);
        Auth = new LarkAuth(cfg, new TokenStore(new PlainProtector(), Path.Combine(_dir, "token.dat")), client);
        if (signIn)
        {
            Task.Run(async () =>
            {
                var flow = await Auth.StartDeviceFlowAsync();
                await Auth.PollDeviceFlowAsync(flow with { Interval = 0 }, CancellationToken.None);
            }).GetAwaiter().GetResult();
        }
        Api = new LarkApi(Auth, client, "", 0) { Sleep = _ => { } };
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}

public class ApiTests
{
    [Fact]
    public void Device_sign_in_saves_a_refreshable_session_with_the_name()
    {
        using var f = new ApiFixture();
        Assert.True(f.Auth.SignedIn);
        Assert.Equal("Rennon", f.Auth.UserName);
        Assert.Equal("uat-1", f.Auth.AccessToken());
    }

    [Fact]
    public void Expired_token_is_refreshed_once_and_the_call_succeeds()
    {
        using var f = new ApiFixture();
        f.Http.R.AddFile("a.txt", new byte[] { 1 });
        f.Http.ExpireNext = true;
        f.Http.ValidToken = "uat-1";
        // after the refresh the fake hands out "uat-1" again, which it now accepts
        var list = f.Api.ListFolder(f.Api.RootToken);
        Assert.Single(list);
    }

    [Fact]
    public void Listing_follows_every_page_and_my_folders_root_is_looked_up()
    {
        using var f = new ApiFixture();
        for (var i = 0; i < 7; i++) f.Http.R.AddFile($"f{i}.txt", new byte[] { 1 });
        Assert.Equal("fldROOT", f.Api.RootToken);
        Assert.Equal(7, f.Api.ListFolder(f.Api.RootToken).Count);
    }

    [Fact]
    public void Rate_limits_are_retried()
    {
        using var f = new ApiFixture();
        f.Http.R.AddFile("a.txt", new byte[] { 1 });
        f.Http.RateLimitNext = 2;
        Assert.Single(f.Api.ListFolder("fldROOT"));
        Assert.True(f.Api.RateLimitHits >= 2);
    }

    [Fact]
    public void Size_comes_from_a_one_byte_range_request_and_reads_can_start_mid_file()
    {
        using var f = new ApiFixture();
        var tok = f.Http.R.AddFile("big.bin", Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        Assert.Equal(1000, f.Api.ProbeSize(tok));
        using var s = f.Api.OpenRead(tok, 600, CancellationToken.None);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(400, ms.Length);
        Assert.Equal((byte)(600 % 256), ms.ToArray()[0]);
    }

    [Fact]
    public void Empty_files_in_Lark_have_size_zero_not_an_error()
    {
        using var f = new ApiFixture();
        var tok = f.Http.R.AddFile("List.txt", Array.Empty<byte>());
        Assert.Equal(0, f.Api.ProbeSize(tok));
    }

    [Fact]
    public void Uploads_new_overwrite_and_multipart()
    {
        using var f = new ApiFixture();
        var dir = Path.Combine(Path.GetTempPath(), "lcs-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "a.txt");
        File.WriteAllText(src, "hello");
        var tok = f.Api.Upload(src, "fldROOT", "a.txt", null);
        File.WriteAllText(src, "hello again");
        Assert.Equal(tok, f.Api.Upload(src, "fldROOT", "a.txt", tok));
        Assert.Equal("hello again", Encoding.UTF8.GetString(f.Http.R.Content("a.txt")!));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Blocked_upload_gets_through_another_route_unchanged()
    {
        using var f = new ApiFixture();
        f.Http.Waf = c => c.Contains("${");
        f.Http.WafBlocksParts = false;
        var dir = Path.Combine(Path.GetTempPath(), "lcs-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "Forum.kt");
        var text = "Text(\"by ${author}\")";
        File.WriteAllText(src, text);
        var tok = f.Api.Upload(src, "fldROOT", "Forum.kt", null);
        Assert.Equal(text, Encoding.UTF8.GetString(f.Http.R.Nodes[tok].Content));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Blocked_on_every_route_is_reported_as_blocked_not_permission()
    {
        using var f = new ApiFixture();
        f.Http.Waf = c => c.Contains("${");
        var dir = Path.Combine(Path.GetTempPath(), "lcs-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "typekit.js");
        File.WriteAllText(src, "${}${}${}");
        var ex = Assert.Throws<LarkApiException>(() => f.Api.Upload(src, "fldROOT", "typekit.js", null));
        Assert.True(ex.IsBlocked);
        Assert.False(ex.IsPermission);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Folders_create_move_rename_delete()
    {
        using var f = new ApiFixture();
        var a = f.Api.CreateFolder("fldROOT", "A");
        var b = f.Api.CreateFolder("fldROOT", "B");
        var tok = f.Http.R.AddFile("A/x.txt", new byte[] { 1 });
        f.Api.Move(tok, "file", b);
        f.Api.Rename(tok, "file", "y.txt");
        Assert.NotNull(f.Http.R.Find("B/y.txt"));
        f.Api.Delete(a, "folder");
        Assert.Null(f.Http.R.Find("A"));
    }

    [Fact]
    public void Full_sync_through_the_real_client()
    {
        using var f = new ApiFixture();
        f.Http.R.AddFile("Docs/report.pdf", Encoding.UTF8.GetBytes("pdf"));
        var local = new FakeLocal(f.Http.R);
        var dir = Path.Combine(Path.GetTempPath(), "lcs-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using (var state = new StateDb(Path.Combine(dir, "s.db")))
        {
            var eng = new CloudEngine(f.Api, local, state, new RemoteIndex(f.Api, 4), new EngineOptions());
            eng.Run();
            Assert.Equal(3, local.Stat("Docs/report.pdf")!.Size);
            local.UserWrite("Docs/new.txt", Encoding.UTF8.GetBytes("new"));
            Assert.Equal(1, eng.Run().Uploaded);
            Assert.False(eng.Run().Changed);
        }
        Directory.Delete(dir, true);
    }
}


public class WikiAndSearchTests
{
    [Fact]
    public void Wiki_space_shows_as_read_only_folders_with_shortcuts_and_files()
    {
        using var f = new ApiFixture();
        var fileTok = f.Http.R.AddFile("(wiki attachments)/drawing.pdf", Encoding.UTF8.GetBytes("pdf bytes"));
        f.Http.Wiki.Add(("sp1", "", new WikiNode("nA", "docA", "docx", "Handbook", true, "1700000001")));
        f.Http.Wiki.Add(("sp1", "nA", new WikiNode("nB", "docB", "sheet", "Leave: rules?", false, "1700000002")));
        f.Http.Wiki.Add(("sp1", "nA", new WikiNode("nC", fileTok, "file", "drawing.pdf", false, "1700000003")));
        var wiki = new WikiRemote(f.Api, "sp1", null, "x.larksuite.com");
        using var e = new Env(inner: wiki);
        var res = e.Sync();
        Assert.True(res.ViewOnly);
        Assert.Equal(new[] { "Handbook", "Handbook.url", "Handbook/Leave： rules？.url", "Handbook/drawing.pdf" },
            e.Local.Paths().OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains("https://x.larksuite.com/wiki/nA", Encoding.UTF8.GetString(e.Local.Data("Handbook.url")!));
        Assert.True(e.Local.Stat("Handbook/drawing.pdf")!.ReadOnly);
        Assert.Equal("pdf bytes", Encoding.UTF8.GetString(e.Local.UserOpen("Handbook/drawing.pdf")));
        e.AssertSettled();
        // deleting it here can't delete it in the wiki: it's put back
        e.Local.UserDelete("Handbook/drawing.pdf");
        res = e.Sync();
        Assert.Contains("Handbook/drawing.pdf", res.Info.Keys);
        e.Sync();
        Assert.Contains("Handbook/drawing.pdf", e.Local.Paths());
    }

    [Fact]
    public void Wiki_spaces_and_shared_folders_can_be_found()
    {
        using var f = new ApiFixture();
        f.Http.Wiki.Add(("sp9", "", new WikiNode("n1", "d1", "docx", "Home", false, "1")));
        Assert.Equal("sp9", f.Api.WikiSpaces().Single().Token);
        f.Http.SearchResults.Add(new FoundFolder("fldSHARED", "Marketing 2026", "https://x.larksuite.com/drive/folder/fldSHARED"));
        var found = f.Api.SearchFolders("Marketing");
        Assert.Equal("Marketing 2026", found.Single().Name);
        Assert.Equal("fldSHARED", found.Single().Token);
    }

    [Fact]
    public void Edit_permission_comes_from_Lark()
    {
        using var f = new ApiFixture();
        Assert.True(f.Api.CanEdit("fldSHARED", "folder"));
        f.Http.PermissionAuth = false;
        Assert.False(f.Api.CanEdit("fldSHARED", "folder"));
        Assert.True(f.Api.CanEdit("fldROOT", "folder"));            // your own My Folders: not asked (Lark rejects it)
    }

    [Fact]
    public void Wiki_pair_links_are_understood()
    {
        var p = new Pair { FolderUrl = Pair.WikiUrl("x.larksuite.com", "sp1", "nA") };
        Assert.True(p.IsWiki);
        Assert.Equal("sp1", p.WikiSpace);
        Assert.Equal("nA", p.WikiNode);
        var whole = new Pair { FolderUrl = Pair.WikiUrl("x.larksuite.com", "sp1", null) };
        Assert.Null(whole.WikiNode);
        Assert.False(new Pair { FolderUrl = "https://x.larksuite.com/drive/folder/abc" }.IsWiki);
    }
}

public class StateDbTests
{
    [Fact]
    public void Many_threads_can_use_the_sync_records_at_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lcs-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using (var db = new StateDb(Path.Combine(dir, "s.db")))
        {
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                db.SetMeta("export:" + i, $"ft{i}|{i}");
                Assert.Equal($"ft{i}|{i}", db.GetMeta("export:" + i));
                db.Put(new BaseEntry { Path = $"f{i}", Token = $"t{i}" });
                if (i % 50 == 0) db.Commit();
            });
        }
        using (var db = new StateDb(Path.Combine(dir, "s.db")))
            Assert.Equal(400, db.Count);
        Directory.Delete(dir, true);
    }
}


public class SignInRenewalTests
{
    [Fact]
    public void A_failed_renewal_keeps_using_the_current_sign_in_and_doesnt_hammer_Lark()
    {
        using var f = new ApiFixture();
        f.Auth.Sleep = _ => { };
        var now = DateTime.UtcNow;
        f.Auth.Now = () => now.AddSeconds(7200 - 200);             // inside the "renew 5 minutes early" window
        f.Http.TokenHook = () => throw new HttpRequestException("No such host is known. (accounts.larksuite.com:443)");
        f.Http.TokenCalls = 0;
        Assert.Equal("uat-1", f.Auth.AccessToken());                // still valid: keep working
        var calls = f.Http.TokenCalls;
        for (var i = 0; i < 20; i++) f.Auth.AccessToken();          // 20 more requests (8 threads in real life)
        Assert.Equal(calls, f.Http.TokenCalls);                     // no renewal storm (that's what caused the 429s)
        f.Http.TokenHook = () => new HttpResponseMessage((HttpStatusCode)429);
        f.Auth.Now = () => now.AddSeconds(7200 + 60);               // token really expired, Lark busy
        Assert.Throws<LarkUnreachableException>(() => f.Auth.AccessToken());
        f.Http.TokenHook = null;
        f.Auth.Now = () => now.AddSeconds(7200 + 120);
        Assert.Equal("uat-1", f.Auth.AccessToken());                // renewed once Lark answers again
    }
}


public class ProtectedZipTests
{
    [Fact]
    public void Protected_zip_round_trips_and_is_a_standard_zip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lcs-pz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "sbom.cdx.json");
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"bom-ref\":\"pkg:pypi/onnx\"}", 300)));
        File.WriteAllBytes(src, data);
        var zip = Path.Combine(dir, "out.zip");
        ProtectedZip.Write(src, zip);
        var bytes = File.ReadAllBytes(zip);
        Assert.True(ProtectedZip.IsProtected(bytes));
        Assert.Equal(data, ProtectedZip.Read(bytes));
        Assert.DoesNotContain("bom-ref", Encoding.Latin1.GetString(bytes));     // nothing readable inside
        Environment.SetEnvironmentVariable("LCS_PZ", zip);
        Directory.Delete(dir, true);
    }
}
