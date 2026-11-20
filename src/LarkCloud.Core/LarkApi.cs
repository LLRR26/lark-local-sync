using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace LarkCloud.Core;

public sealed class LarkApiException : Exception
{
    static readonly HashSet<long> PermissionCodes = new() { 1061004, 1062501, 1061045, 1063001, 99991672, 99991679 };
    public static readonly HashSet<long> GoneCodes = new() { 1061007, 1061003 };

    public long Code { get; }
    public string LarkMessage { get; }
    public bool EmptyRefused { get; init; }

    public LarkApiException(long code, string msg, string path = "")
        : base($"Lark error {code}: {msg}" + (path.Length > 0 ? $" ({path})" : ""))
    {
        Code = code;
        LarkMessage = msg;
    }

    /// <summary>A bare HTTP 403 with no Lark error code: a security filter in front of Lark's API
    /// refused the request (usually because of what is written in the file), not a Drive permission.</summary>
    public bool IsBlocked => Code == 403;

    public bool IsGone => GoneCodes.Contains(Code);

    public bool IsPermission
    {
        get
        {
            if (IsBlocked) return false;
            var m = LarkMessage.ToLowerInvariant();
            return PermissionCodes.Contains(Code) || m.Contains("permission") || m.Contains("forbidden") || m.Contains("no auth");
        }
    }
}

/// <summary>A download stream that knows the file's full size in Lark.</summary>
public interface IKnownLength
{
    long? TotalLength { get; }
}

/// <summary>A folder listing that had to leave out entries Lark returned half-empty. Nothing under such a folder may be
/// treated as "gone from Lark" (see RemoteIndex / CloudEngine): the missing entry is unknown, not deleted.</summary>
public sealed class RemoteListing : List<RemoteEntry>
{
    public bool Incomplete { get; set; }
}

/// <summary>Everything the sync engine needs from Lark Drive. LarkApi is the real one; the tests use a fake.</summary>
public interface IRemoteDrive
{
    string RootToken { get; }
    List<RemoteEntry> ListFolder(string folderToken);
    /// <summary>Size in bytes of a file (Lark's listing doesn't include it).</summary>
    long ProbeSize(string token);
    /// <summary>The file's bytes from `offset` to the end.</summary>
    Stream OpenRead(string token, long offset, CancellationToken ct);
    string Upload(string srcPath, string parentToken, string name, string? existingToken);
    string CreateFolder(string parentToken, string name);
    void Delete(string token, string type);
    void Move(string token, string type, string destFolderToken);
    void Rename(string token, string type, string newName);
    /// <summary>Can the signed-in person edit this item? null = couldn't find out.</summary>
    bool? CanEdit(string token, string type) => null;
    /// <summary>The person's own My Folders (for copies of changes that couldn't be saved elsewhere).</summary>
    string MyFoldersRoot() => RootToken;
}

/// <summary>A folder found by search ("shared with me") or a wiki space/node, for the folder picker.</summary>
public sealed record FoundFolder(string Token, string Name, string? Url, string? Owner = null);

public sealed record WikiNode(string NodeToken, string ObjToken, string ObjType, string Title, bool HasChild, string EditTime);

public sealed class WikiNodeList : List<WikiNode>
{
    public bool Incomplete { get; set; }
}

public sealed class LarkApi : IRemoteDrive, ILarkExtras
{
    public long SingleUploadLimit { get; set; } = 20L * 1024 * 1024;       // bigger files go up in parts (settable for tests)
    static readonly HashSet<long> TokenErrors = new() { 99991661, 99991663, 99991668, 99991677 };
    const long RateLimit = 99991400;

    readonly LarkAuth _auth;
    readonly HttpClient _http;
    readonly string _base;
    readonly CancellationToken _lifetime;
    string _root;

    readonly LarkRequestLimiter _limiter;
    public int RateLimitHits;
    public bool OverwriteSupported = true;
    int _overwriteFailures;
    public Action<TimeSpan>? Sleep;
    /// <summary>Per HTTP request (send until the answer is read); time spent waiting for the limiter or backing off doesn't count.</summary>
    public TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _skippedLogged = new();

    public LarkApi(LarkAuth auth, HttpClient http, string rootToken, double minIntervalSeconds = 0.12,
        CancellationToken lifetime = default, LarkRequestLimiter? limiter = null)
    {
        _auth = auth;
        _http = http;
        _base = auth.OpenBase;
        _root = rootToken;
        _lifetime = lifetime;
        _limiter = limiter ?? new LarkRequestLimiter(minIntervalSeconds);
    }

    /// <summary>The synced folder's token. "" (My Folders) is looked up once.</summary>
    public string RootToken
    {
        get
        {
            if (_root.Length == 0) _root = RootMeta();
            return _root;
        }
    }

    // ------------------------------------------------------------ plumbing
    void Delay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Sleep != null) Sleep(delay);
        else Task.Delay(delay, ct).GetAwaiter().GetResult();
    }

    void SlowDown(HttpResponseMessage response, int attempt)
    {
        Interlocked.Increment(ref RateLimitHits);
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        if (delay == null || delay <= TimeSpan.Zero)
            delay = TimeSpan.FromSeconds(Math.Min(30.0, Math.Pow(2, attempt) + Random.Shared.NextDouble()));
        _limiter.Throttled(delay.Value);
        Log.Warn($"Lark API rate limited; pausing new Lark requests for {delay.Value.TotalSeconds:0.0}s");
    }

    void Backoff(int attempt, string why, CancellationToken ct)
    {
        var delay = Math.Min(30.0, Math.Pow(2, attempt) + Random.Shared.NextDouble());
        Log.Warn($"Lark API {why}; retrying in {delay:0.0}s");
        Delay(TimeSpan.FromSeconds(delay), ct);
    }

    /// <summary>Sends a request (rebuilt for every attempt) and returns the response.
    /// JSON answers with code != 0 become LarkApiException; non-JSON answers are returned when `raw`.</summary>
    HttpResponseMessage Send(Func<HttpRequestMessage> make, string path, bool raw = false, CancellationToken ct = default, int serverErrorTries = 6,
                             TimeSpan? timeout = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        ct = linked.Token;
        var refreshed = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var token = _auth.AccessToken(ct: ct);
            _limiter.Wait(Delay, ct);
            using var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage r;
            // The timeout covers just this send/receive; waiting for the limiter or backing off only answers to ct.
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is TimeSpan t) sendCts.CancelAfter(t);
            try
            {
                r = _http.Send(req, raw ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, sendCts.Token);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == 5) throw new LarkUnreachableException($"Can't reach Lark ({ex.Message})", ex);
                Backoff(attempt, $"network error: {ex.Message}", ct);
                continue;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt == 5) throw new LarkUnreachableException("Lark didn't answer in time", ex);
                Backoff(attempt, "timed out", ct);
                continue;
            }
            var status = (int)r.StatusCode;
            if (status == 429 || status >= 500)
            {
                if (status == 429) SlowDown(r, attempt);
                var error = r.ReasonPhrase ?? "server error";
                r.Dispose();
                if (attempt >= (status == 429 ? 5 : serverErrorTries - 1)) throw new LarkApiException(status, error, path);
                if (status != 429) Backoff(attempt, $"HTTP {status}", ct);
                continue;
            }
            var ctype = r.Content.Headers.ContentType?.MediaType ?? "";
            if (raw && status == 416) return r;               // range past the end: the caller reads the size from it
            if (raw && r.IsSuccessStatusCode && !ctype.Contains("json"))
            {
                _limiter.Succeeded();
                return r;
            }
            var body = ReadJson(r);
            var code = (long?)body["code"] ?? (r.IsSuccessStatusCode ? 0 : status);
            if (code == 0 && r.IsSuccessStatusCode)
            {
                _limiter.Succeeded();
                r.Content = new StringContent(body.ToJsonString());
                return r;
            }
            if (TokenErrors.Contains(code) && !refreshed)
            {
                refreshed = true;
                r.Dispose();
                _auth.AccessToken(forceRefresh: true, ct: ct);
                attempt--;
                continue;
            }
            if (code == RateLimit)
            {
                SlowDown(r, attempt);
                if (attempt < 5)
                {
                    r.Dispose();
                    continue;
                }
            }
            var message = (string?)body["msg"] ?? r.ReasonPhrase ?? "";
            r.Dispose();
            throw new LarkApiException(code, message, path);
        }
        throw new LarkApiException(-1, "too many retries", path);
    }

    static JsonObject ReadJson(HttpResponseMessage r)
    {
        try { return JsonNode.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    JsonObject Json(HttpMethod method, string path, Dictionary<string, string>? query = null, JsonNode? json = null,
                    Func<HttpContent>? content = null, int? timeoutS = null, int serverErrorTries = 6, bool listing = false)
    {
        var url = _base + path + (query == null ? "" : "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));
        using var r = Send(() => new HttpRequestMessage(method, url)
        {
            Content = content?.Invoke() ?? (json == null ? null : new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json")),
        }, path, serverErrorTries: serverErrorTries, timeout: timeoutS is int sec ? TimeSpan.FromSeconds(sec) : RequestTimeout);
        var body = ReadJson(r);
        if (listing && ((long?)body["code"] != 0 || body["data"] is not JsonObject))
            throw new IOException("Lark returned an invalid folder listing.");
        return body["data"] as JsonObject ?? new JsonObject();
    }

    static string? NextListingPage(JsonObject data, HashSet<string> seen)
    {
        if (data["has_more"] is not JsonValue flag || !flag.TryGetValue<bool>(out var more))
            throw new IOException("Lark's folder listing is missing its pagination status.");
        if (!more) return null;
        var next = (string?)data["next_page_token"] ?? (string?)data["page_token"];
        if (string.IsNullOrWhiteSpace(next) || !seen.Add(next))
            throw new IOException("Lark returned an incomplete folder listing.");
        return next;
    }

    void WaitTask(string taskId)
    {
        var end = DateTime.UtcNow.AddMinutes(2);
        var wait = 0.25;                                  // most folder deletes/moves finish in well under a second
        while (DateTime.UtcNow < end)
        {
            var d = Json(HttpMethod.Get, "/open-apis/drive/v1/files/task_check", new() { ["task_id"] = taskId });
            var st = ((string?)(d["result"] ?? d)?["status"] ?? (string?)d["status"] ?? "").ToLowerInvariant();
            if (st == "success") return;
            if (st is "fail" or "failed") throw new LarkApiException(-1, $"async task {taskId} failed", "task_check");
            Delay(TimeSpan.FromSeconds(wait), _lifetime);
            wait = Math.Min(2.0, wait * 1.5);
        }
        throw new LarkApiException(-1, $"async task {taskId} timed out", "task_check");
    }

    // ------------------------------------------------------------ listing
    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>One bad entry must not stop the whole folder from syncing, and must never look like "deleted in Lark":
    /// it's left out, logged once, and the listing says it's incomplete so nothing under this folder is removed this pass.</summary>
    void Skipped(string where, JsonNode? entry)
    {
        var text = entry?.ToJsonString() ?? "null";
        if (_skippedLogged.TryAdd(where + text, 0))
            Log.Warn($"Lark returned an incomplete entry in {where}; skipping it and leaving everything in that folder alone: {text}");
    }

    public List<RemoteEntry> ListFolder(string folderToken)
    {
        var skipped = false;
        var outList = new RemoteListing();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        while (true)
        {
            var q = new Dictionary<string, string> { ["folder_token"] = folderToken, ["page_size"] = "200" };
            if (page != null) q["page_token"] = page;
            var data = Json(HttpMethod.Get, "/open-apis/drive/v1/files", q, listing: true);
            if (data["files"] is not JsonArray files)
                throw new IOException("Lark's folder listing is missing its files.");
            foreach (var f in files)
            {
                var name = Str(f?["name"]);
                var tok = Str(f?["token"]);
                var type = Str(f?["type"]);
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(tok) || string.IsNullOrEmpty(type))
                {
                    skipped = true;
                    Skipped(folderToken, f);
                    continue;
                }
                long? size = null;
                if (f!["size"] is JsonValue sv && long.TryParse(sv.ToString(), out var s)) size = s;
                outList.Add(new RemoteEntry
                {
                    Path = name, Token = tok, Type = type,
                    MTime = f["modified_time"]?.ToString() ?? "", Size = size,
                    Url = (string?)f["url"], ParentToken = folderToken,
                });
            }
            page = NextListingPage(data, seen);
            if (page == null) break;
        }
        outList.Incomplete = skipped;
        return outList;
    }

    public string RootMeta()
    {
        var d = Json(HttpMethod.Get, "/open-apis/drive/explorer/v2/root_folder/meta");
        return (string?)d["token"] ?? throw new LarkApiException(-1, "Lark did not return your root folder", "root_folder/meta");
    }

    /// <summary>Name of a folder (also proves the person can open it).</summary>
    public string FolderName(string folderToken)
    {
        try
        {
            var d = Json(HttpMethod.Get, $"/open-apis/drive/explorer/v2/folder/{folderToken}/meta");
            if ((string?)d["name"] is { Length: > 0 } n) return n;
        }
        catch (LarkApiException) { }
        Json(HttpMethod.Get, "/open-apis/drive/v1/files", new() { ["folder_token"] = folderToken, ["page_size"] = "1" });
        return "Lark folder";
    }

    // ------------------------------------------------------------ downloads
    public long ProbeSize(string token)
    {
        var path = $"/open-apis/drive/v1/files/{token}/download";
        using var r = Send(() =>
        {
            var q = new HttpRequestMessage(HttpMethod.Get, _base + path);
            q.Headers.Range = new RangeHeaderValue(0, 0);
            return q;
        }, path, raw: true, timeout: RequestTimeout);
        using var cts = new CancellationTokenSource(RequestTimeout);       // for reading the body below
        if (r.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return r.Content.Headers.ContentRange?.Length ?? 0;     // "bytes */0": an empty file has no first byte
        if (r.StatusCode == HttpStatusCode.PartialContent && r.Content.Headers.ContentRange?.Length is long total)
            return total;
        if (r.Content.Headers.ContentLength is long len && r.StatusCode == HttpStatusCode.OK)
            return len;
        // No size in the headers: count the bytes (rare; only for servers without Range support)
        using var s = r.Content.ReadAsStream(cts.Token);
        long n = 0;
        var buf = new byte[81920];
        int k;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _lifetime);
        while ((k = s.ReadAsync(buf.AsMemory(), linked.Token).AsTask().GetAwaiter().GetResult()) > 0) n += k;
        return n;
    }

    public Stream OpenRead(string token, long offset, CancellationToken ct)
    {
        var path = $"/open-apis/drive/v1/files/{token}/download";
        var r = Send(() =>
        {
            var q = new HttpRequestMessage(HttpMethod.Get, _base + path);
            if (offset > 0) q.Headers.Range = new RangeHeaderValue(offset, null);
            return q;
        }, path, raw: true, ct: ct);
        long? total = r.StatusCode == HttpStatusCode.PartialContent ? r.Content.Headers.ContentRange?.Length
            : r.Content.Headers.ContentLength;
        var s = OpenResponseStream(r, ct, total);
        try
        {
            if (offset > 0 && r.StatusCode != HttpStatusCode.PartialContent)
            {
                // Range not honoured: skip ahead ourselves.
                var skip = offset;
                var buf = new byte[81920];
                while (skip > 0)
                {
                    var k = s.Read(buf, 0, (int)Math.Min(buf.Length, skip));
                    if (k <= 0) break;
                    skip -= k;
                }
            }
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    public void Download(string token, string destPath)
    {
        using var src = OpenRead(token, 0, CancellationToken.None);
        using var dst = File.Create(destPath);
        src.CopyTo(dst, 1 << 20);
    }

    // ------------------------------------------------------------ uploads
    public string Upload(string src, string parent, string name, string? existing)
    {
        if (existing != null && OverwriteSupported)
        {
            try
            {
                var tok = UploadOnce(src, parent, name, existing);
                _overwriteFailures = 0;
                return tok;
            }
            catch (LarkApiException ex) when (!ex.IsPermission && !ex.EmptyRefused && !ex.IsBlocked && !ex.IsGone
                                              && ex.Code != 429 && ex.Code != RateLimit && !(ex.Code >= 500 && ex.Code <= 599))
            {
                // Overwrite-in-place (keeps the link and history) isn't available on every tenant:
                // fall back to upload as new + move the old one to the Lark trash.
                // (Temporary errors and "deleted in Lark" aren't that: they pass through to the caller.)
                if (Interlocked.Increment(ref _overwriteFailures) >= 3) OverwriteSupported = false;
                Log.Warn($"In-place overwrite failed ({ex.Message}); using upload + replace");
            }
        }
        var newTok = UploadOnce(src, parent, name, null);
        if (existing != null)
        {
            try { Delete(existing, Kinds.File); }
            catch (Exception ex) { Log.Warn($"Uploaded new version of {name} but could not trash the old one: {ex.Message}"); }
        }
        return newTok;
    }

    string UploadOnce(string src, string parent, string name, string? existing)
    {
        var size = new FileInfo(src).Length;
        if (size == 0)
        {
            var errors = new List<string>();
            foreach (var single in new[] { true, false })
            {
                try { return single ? UploadSingle(src, parent, name, existing, 0, false) : UploadMultipart(src, parent, name, existing, 0, false); }
                catch (LarkApiException ex) when (!ex.IsPermission) { errors.Add(ex.Message); }
            }
            throw new LarkApiException(-1, "Lark did not accept an empty (0-byte) file: " + string.Join(" / ", errors), "upload") { EmptyRefused = true };
        }
        try
        {
            return size <= SingleUploadLimit ? UploadSingle(src, parent, name, existing, size, false) : UploadMultipart(src, parent, name, existing, size, false);
        }
        catch (LarkApiException ex) when (ex.IsBlocked)
        {
            // A filter in front of Lark's API blocked it because of the file's contents (Lark's own apps
            // accept it). Try the other routes, sent as plain binary data. The file is never changed.
            Log.Info($"Upload of {name} was blocked ({ex.Message}); trying Lark's other upload routes");
            foreach (var single in new[] { false, true })
            {
                try { return single ? UploadSingle(src, parent, name, existing, size, true) : UploadMultipart(src, parent, name, existing, size, true); }
                catch (LarkApiException again) when (again.IsBlocked) { }
            }
            throw;
        }
    }

    static HttpContent FilePart(byte[] data, string name, bool binary)
    {
        var c = new ByteArrayContent(data);
        if (binary) c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return c;
    }

    string UploadSingle(string src, string parent, string name, string? existing, long size, bool binary)
    {
        var bytes = File.ReadAllBytes(src);
        var d = Json(HttpMethod.Post, "/open-apis/drive/v1/files/upload_all", content: () =>
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(name), "file_name" },
                { new StringContent("explorer"), "parent_type" },
                { new StringContent(parent), "parent_node" },
                { new StringContent(size.ToString()), "size" },
            };
            if (existing != null) form.Add(new StringContent(existing), "file_token");
            form.Add(FilePart(bytes, name, binary), "file", name);
            return form;
        }, timeoutS: 600);
        return (string?)d["file_token"] ?? throw new LarkApiException(-1, "upload returned no file_token", "upload");
    }

    string UploadMultipart(string src, string parent, string name, string? existing, long size, bool binary)
    {
        var body = new JsonObject { ["file_name"] = name, ["parent_type"] = "explorer", ["parent_node"] = parent, ["size"] = size };
        if (existing != null) body["file_token"] = existing;
        var prep = Json(HttpMethod.Post, "/open-apis/drive/v1/files/upload_prepare", json: body);
        var uploadId = (string)prep["upload_id"]!;
        var blockSize = (long)prep["block_size"]!;
        var blockNum = (int)prep["block_num"]!;
        using (var fh = File.OpenRead(src))
        {
            for (var seq = 0; seq < blockNum; seq++)
            {
                var chunk = new byte[Math.Min(blockSize, size - fh.Position)];
                fh.ReadExactly(chunk);
                var s = seq;
                Json(HttpMethod.Post, "/open-apis/drive/v1/files/upload_part", content: () => new MultipartFormDataContent
                {
                    { new StringContent(uploadId), "upload_id" },
                    { new StringContent(s.ToString()), "seq" },
                    { new StringContent(chunk.Length.ToString()), "size" },
                    { FilePart(chunk, name, binary), "file", name },
                }, timeoutS: 600);
            }
        }
        var fin = Json(HttpMethod.Post, "/open-apis/drive/v1/files/upload_finish",
            json: new JsonObject { ["upload_id"] = uploadId, ["block_num"] = blockNum });
        return (string?)fin["file_token"] ?? throw new LarkApiException(-1, "upload returned no file_token", "upload");
    }

    // ------------------------------------------------------------ folders & changes
    public string CreateFolder(string parent, string name) =>
        (string)Json(HttpMethod.Post, "/open-apis/drive/v1/files/create_folder",
            json: new JsonObject { ["name"] = name, ["folder_token"] = parent })["token"]!;

    public void Delete(string token, string type)
    {
        // A big folder that Lark can't delete in one go fails with a server error every time, so don't spend
        // long retrying: the engine then deletes it in parts.
        var d = Json(HttpMethod.Delete, $"/open-apis/drive/v1/files/{token}", new() { ["type"] = type }, serverErrorTries: type == "folder" ? 2 : 6);
        if ((string?)d["task_id"] is { Length: > 0 } t) WaitTask(t);
    }

    public void Move(string token, string type, string dest)
    {
        var d = Json(HttpMethod.Post, $"/open-apis/drive/v1/files/{token}/move",
            json: new JsonObject { ["type"] = type, ["folder_token"] = dest });
        if ((string?)d["task_id"] is { Length: > 0 } t) WaitTask(t);
    }

    public void Rename(string token, string type, string newName) =>
        Json(HttpMethod.Patch, $"/open-apis/drive/v1/files/{token}", new() { ["type"] = type },
            json: new JsonObject { ["new_title"] = newName });

    // ------------------------------------------------------------ permissions
    public bool? CanEdit(string token, string type)
    {
        try
        {
            if (type == "folder" && token == MyFoldersRoot()) return true;      // your own My Folders (Lark rejects the check for it)
            var d = Json(HttpMethod.Get, $"/open-apis/drive/v1/permissions/{token}/members/auth",
                new() { ["type"] = type, ["action"] = "edit" });
            return (bool?)d["auth_result"];
        }
        catch (LarkApiException ex) when (!ex.IsBlocked)
        {
            Log.Info($"Couldn't check edit permission ({ex.Message})");
            return null;
        }
    }

    string? _myRoot;
    public string MyFoldersRoot() => _myRoot ??= RootMeta();

    // ------------------------------------------------------------ offline copies of Lark docs
    public (string FileToken, long Size) Export(string docToken, string docType, string ext, CancellationToken ct)
    {
        var ticket = (string?)Json(HttpMethod.Post, "/open-apis/drive/v1/export_tasks",
            json: new JsonObject { ["file_extension"] = ext, ["token"] = docToken, ["type"] = docType })["ticket"]
            ?? throw new LarkApiException(-1, "Lark didn't start the export", "export_tasks");
        var end = DateTime.UtcNow.AddSeconds(50);                 // Windows gives an opening file about a minute
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            var r = Json(HttpMethod.Get, $"/open-apis/drive/v1/export_tasks/{ticket}", new() { ["token"] = docToken })["result"];
            var status = (int?)r?["job_status"] ?? 2;
            if (status == 0)
                return ((string?)r!["file_token"] ?? throw new LarkApiException(-1, "export has no file", "export_tasks"),
                        long.TryParse(r["file_size"]?.ToString(), out var sz) ? sz : 0);
            if (status is not (1 or 2))
                throw new LarkApiException(status, (string?)r?["job_error_msg"] is { Length: > 0 } m ? m : $"Lark couldn't export it (status {status})", "export_tasks");
            Delay(TimeSpan.FromSeconds(1), _lifetime);
        }
        throw new LarkApiException(-1, "Lark took too long to export it", "export_tasks");
    }

    public Stream OpenExport(string exportFileToken, CancellationToken ct)
    {
        var path = $"/open-apis/drive/v1/export_tasks/file/{exportFileToken}/download";
        var r = Send(() => new HttpRequestMessage(HttpMethod.Get, _base + path), path, raw: true, ct: ct);
        return OpenResponseStream(r, ct, r.Content.Headers.ContentLength);
    }

    ResponseStream OpenResponseStream(HttpResponseMessage response, CancellationToken ct, long? length)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        try { return new ResponseStream(response.Content.ReadAsStream(linked.Token), response, linked) { TotalLength = length }; }
        catch { linked.Dispose(); response.Dispose(); throw; }
    }

    // ------------------------------------------------------------ finding folders shared with you
    /// <summary>Folders the person can see whose name matches `query` (needs the search:docs:read scope).</summary>
    public List<FoundFolder> SearchFolders(string query)
    {
        var d = Json(HttpMethod.Post, "/open-apis/search/v2/doc_wiki/search", json: new JsonObject
        {
            ["query"] = query, ["page_size"] = 20,
            ["doc_filter"] = new JsonObject { ["doc_types"] = new JsonArray("FOLDER") },
        });
        var outList = new List<FoundFolder>();
        foreach (var u in d["res_units"] as JsonArray ?? new JsonArray())
        {
            var meta = u?["result_meta"];
            var url = (string?)meta?["url"];
            var tok = (string?)meta?["token"] ?? (url != null ? Config.TokenFromLink(url) : null);
            if (string.IsNullOrEmpty(tok)) continue;
            var title = System.Text.RegularExpressions.Regex.Replace((string?)u!["title_highlighted"] ?? (string?)u["title"] ?? "Folder", "<[^>]+>", "");
            outList.Add(new FoundFolder(tok, title, url, (string?)meta?["owner_name"]));
        }
        return outList;
    }

    // ------------------------------------------------------------ wiki
    public List<FoundFolder> WikiSpaces()
    {
        var outList = new List<FoundFolder>();
        string? page = null;
        do
        {
            var q = new Dictionary<string, string> { ["page_size"] = "50" };
            if (page != null) q["page_token"] = page;
            var d = Json(HttpMethod.Get, "/open-apis/wiki/v2/spaces", q);
            foreach (var s in d["items"] as JsonArray ?? new JsonArray())
                if ((string?)s?["space_id"] is { } id) outList.Add(new FoundFolder(id, (string?)s["name"] ?? "Wiki space", null));
            page = ((bool?)d["has_more"] ?? false) ? (string?)d["page_token"] : null;
        } while (!string.IsNullOrEmpty(page));
        return outList;
    }

    public List<WikiNode> WikiNodes(string spaceId, string? parentNode)
    {
        var outList = new WikiNodeList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        do
        {
            var q = new Dictionary<string, string> { ["page_size"] = "50" };
            if (!string.IsNullOrEmpty(parentNode)) q["parent_node_token"] = parentNode;
            if (page != null) q["page_token"] = page;
            var d = Json(HttpMethod.Get, $"/open-apis/wiki/v2/spaces/{spaceId}/nodes", q, listing: true);
            if (d["items"] is not JsonArray items)
                throw new IOException("Lark's wiki listing is missing its items.");
            foreach (var n in items)
            {
                if (n == null || Str(n["node_token"]) is not { Length: > 0 } nt
                    || Str(n["obj_token"]) is not { Length: > 0 } obj
                    || Str(n["obj_type"]) is not { Length: > 0 } type
                    || n["has_child"] is not JsonValue hc || !hc.TryGetValue<bool>(out var hasChild))
                {
                    outList.Incomplete = true;
                    Skipped("wiki space " + spaceId, n);
                    continue;
                }
                outList.Add(new WikiNode(nt, obj, type, Str(n["title"]) ?? "Untitled",
                    hasChild, n["obj_edit_time"]?.ToString() ?? ""));
            }
            page = NextListingPage(d, seen);
        } while (!string.IsNullOrEmpty(page));
        return outList;
    }

    /// <summary>Sub-folders of a folder, for the folder picker.</summary>
    public List<RemoteEntry> ChildFolders(string folderToken) =>
        ListFolder(folderToken).Where(e => e.IsFolder).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    sealed class ResponseStream : Stream, IKnownLength
    {
        public long? TotalLength { get; init; }
        readonly Stream _inner;
        readonly HttpResponseMessage _resp;
        readonly CancellationTokenSource _read;
        public ResponseStream(Stream inner, HttpResponseMessage resp, CancellationTokenSource read) { _inner = inner; _resp = resp; _read = read; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.ReadAsync(buffer.AsMemory(offset, count), _read.Token).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); _resp.Dispose(); _read.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
