using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace LarkCloud.Core;

public sealed class NeedLoginException : Exception
{
    public NeedLoginException(string msg) : base(msg) { }
}

/// <summary>Lark can't be reached right now (no network, DNS, Lark busy). Not a problem with any file:
/// the whole check is tried again a little later.</summary>
public sealed class LarkUnreachableException : Exception
{
    public LarkUnreachableException(string msg, Exception? inner = null) : base(msg, inner) { }
}

/// <summary>Encrypts the saved sign-in. On Windows the app uses DPAPI (only this Windows user can read it).</summary>
public interface ITokenProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class PlainProtector : ITokenProtector
{
    public byte[] Protect(byte[] d) => d;
    public byte[] Unprotect(byte[] d) => d;
}

public sealed class TokenStore
{
    readonly string _path;
    readonly ITokenProtector _prot;

    public TokenStore(ITokenProtector prot, string? path = null)
    {
        _prot = prot;
        _path = path ?? Path.Combine(AppPaths.DataDir, "token.dat");
    }

    public JsonObject? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonNode.Parse(Encoding.UTF8.GetString(_prot.Unprotect(File.ReadAllBytes(_path)))) as JsonObject;
        }
        catch (Exception ex)
        {
            Log.Warn($"Saved sign-in unreadable ({ex.Message}); please sign in again");
            return null;
        }
    }

    public void Save(JsonObject tok)
    {
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, _prot.Protect(Encoding.UTF8.GetBytes(tok.ToJsonString())));
        File.Move(tmp, _path, overwrite: true);
    }

    public void Clear() { try { File.Delete(_path); } catch { } }
}

public sealed record DeviceFlow(string DeviceCode, string UserCode, string Url, int Interval, int ExpiresIn);

/// <summary>Signing in with the OAuth device flow (same as the Python app): the person approves a
/// code in the browser with their own Lark account; the app gets a ~2 h access token plus a refresh
/// token and refreshes silently.</summary>
public sealed class LarkAuth
{
    public static readonly Dictionary<string, (string Open, string Accounts)> Endpoints = new()
    {
        ["lark"] = ("https://open.larksuite.com", "https://accounts.larksuite.com"),
        ["feishu"] = ("https://open.feishu.cn", "https://accounts.feishu.cn"),
    };

    public const string Scopes = "drive:drive space:document:retrieve drive:drive.metadata:readonly drive:file:download " +
                                 "drive:file:upload space:folder:create space:document:delete space:document:move offline_access";

    readonly Config _cfg;
    readonly TokenStore _store;
    readonly HttpClient _http;
    readonly object _lock = new();
    JsonObject? _tok;
    public Func<DateTime> Now = () => DateTime.UtcNow;

    public LarkAuth(Config cfg, TokenStore store, HttpClient http)
    {
        _cfg = cfg;
        _store = store;
        _http = http;
        _tok = store.Load();
    }

    public string OpenBase => Endpoints.GetValueOrDefault(_cfg.Brand, Endpoints["lark"]).Open;
    string AccountsBase => Endpoints.GetValueOrDefault(_cfg.Brand, Endpoints["lark"]).Accounts;
    double NowS => (Now() - DateTime.UnixEpoch).TotalSeconds;

    public bool SignedIn
    {
        get { var t = _tok; return t != null && (string?)t["refresh_token"] is { Length: > 0 } && ((double?)t["refresh_expires_at"] ?? 0) > NowS; }
    }

    public string UserName => (string?)_tok?["user_name"] ?? "";

    public event Action? SignedOut;

    public void SignOut()
    {
        lock (_lock) { _tok = null; _store.Clear(); }
        SignedOut?.Invoke();
    }

    public async Task<DeviceFlow> StartDeviceFlowAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, AccountsBase + "/oauth/v1/device_authorization")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _cfg.AppId, ["scope"] = (_cfg.Scopes ?? Scopes) + (_cfg.ExtraScopes.Count > 0 ? " " + string.Join(" ", _cfg.ExtraScopes) : ""),
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_cfg.AppId}:{_cfg.AppSecret}")));
        var r = await _http.SendAsync(req).ConfigureAwait(false);
        var data = await ReadJson(r).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode || data["error"] != null)
            throw new InvalidOperationException("Lark refused the sign-in request: " +
                ((string?)data["error_description"] ?? (string?)data["error"] ?? $"HTTP {(int)r.StatusCode}"));
        return new DeviceFlow((string)data["device_code"]!, (string?)data["user_code"] ?? "",
            (string?)data["verification_uri_complete"] ?? (string?)data["verification_uri"] ?? "",
            (int?)data["interval"] ?? 5, (int?)data["expires_in"] ?? 240);
    }

    public async Task PollDeviceFlowAsync(DeviceFlow flow, CancellationToken ct)
    {
        var interval = Math.Max(1, flow.Interval);
        var deadline = DateTime.UtcNow.AddSeconds(flow.ExpiresIn);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            HttpResponseMessage r;
            try
            {
                r = await _http.PostAsync(AccountsBase + "/oauth/v3/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = flow.DeviceCode,
                    ["client_id"] = _cfg.AppId,
                    ["client_secret"] = _cfg.AppSecret,
                }), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) { interval = Math.Min(interval + 1, 60); continue; }
            var data = await ReadJson(r).ConfigureAwait(false);
            var err = (string?)data["error"];
            if (err == null && (string?)data["access_token"] != null) { await AcceptAsync(data, false).ConfigureAwait(false); return; }
            switch (err)
            {
                case "authorization_pending": continue;
                case "slow_down": interval = Math.Min(interval + 5, 60); continue;
                case "access_denied": throw new NeedLoginException("Sign-in was declined in the browser");
                case "expired_token" or "invalid_grant": throw new NeedLoginException("The sign-in code expired; please try again");
                default: throw new NeedLoginException((string?)data["error_description"] ?? err ?? $"HTTP {(int)r.StatusCode}");
            }
        }
        throw new NeedLoginException("The sign-in code expired; please try again");
    }

    /// <summary>A valid access token, refreshed when it's about to expire.</summary>
    double _nextRefreshTry;                    // after a failed renewal, wait before asking Lark again
    public Action<TimeSpan>? Sleep;

    public string AccessToken(bool forceRefresh = false, CancellationToken ct = default)
    {
        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();
            var t = _tok ?? throw new NeedLoginException("Not signed in");
            var expires = (double?)t["expires_at"] ?? 0;
            if (!forceRefresh && expires - 300 > NowS) return (string)t["access_token"]!;
            if ((string?)t["refresh_token"] is not { Length: > 0 } || ((double?)t["refresh_expires_at"] ?? 0) < NowS)
                throw new NeedLoginException("Sign-in expired");
            // The current token is renewed 5 minutes early. If a renewal just failed (network blip, Lark busy),
            // keep using the current token while it's still valid instead of asking again for every request.
            if (!forceRefresh && NowS < _nextRefreshTry && expires - 15 > NowS) return (string)t["access_token"]!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { return Refresh(t, timeout.Token); }
            catch (LarkUnreachableException)
            {
                _nextRefreshTry = NowS + 30;
                if (!forceRefresh && expires - 15 > NowS) return (string)t["access_token"]!;
                throw;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _nextRefreshTry = NowS + 30;
                if (!forceRefresh && expires - 15 > NowS) return (string)t["access_token"]!;
                throw new LarkUnreachableException("Timed out while renewing the Lark sign-in.", ex);
            }
        }
    }

    /// <summary>Renew the sign-in, trying a few times on network errors / "too many requests" / server errors.</summary>
    void Delay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Sleep != null) Sleep(delay);
        else Task.Delay(delay, ct).GetAwaiter().GetResult();
    }

    string Refresh(JsonObject t, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var body = new JsonObject
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = (string)t["refresh_token"]!,
                ["client_id"] = _cfg.AppId, ["client_secret"] = _cfg.AppSecret,
            };
            HttpResponseMessage r;
            try
            {
                r = _http.PostAsync(AccountsBase + "/oauth/v3/token",
                    new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt >= 2) throw new LarkUnreachableException($"Can't reach Lark to renew the sign-in ({ex.Message})", ex);
                Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                continue;
            }
            using var response = r;
            var code = (int)r.StatusCode;
            if (code == 429 || code >= 500)
            {
                if (attempt >= 2) throw new LarkUnreachableException($"Lark is busy and couldn't renew the sign-in (HTTP {code})");
                Delay(TimeSpan.FromSeconds(3 << attempt), ct);
                continue;
            }
            return RefreshAnswer(r, ct);
        }
    }

    string RefreshAnswer(HttpResponseMessage r, CancellationToken ct)
    {
        {
            var data = ReadJson(r).GetAwaiter().GetResult();
            if ((string?)data["access_token"] != null)
            {
                AcceptAsync(data, true, ct).GetAwaiter().GetResult();
                return (string)_tok!["access_token"]!;
            }
            var err = (string?)data["error"] ?? (string?)data["msg"] ?? $"HTTP {(int)r.StatusCode}";
            if ((int)r.StatusCode is 400 or 401 || (string?)data["error"] is "invalid_grant" or "invalid_token")
            {
                _tok = null;
                _store.Clear();
                throw new NeedLoginException($"Sign-in expired ({err})");
            }
            throw new InvalidOperationException($"Could not refresh sign-in: {err}");
        }
    }

    async Task AcceptAsync(JsonObject data, bool keepUser, CancellationToken ct = default)
    {
        var now = NowS;
        var old = _tok ?? new JsonObject();
        var refresh = (string?)data["refresh_token"] ?? (string?)old["refresh_token"];
        if (string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException("Lark did not return a refresh token. In the developer console add the " +
                "\"offline_access\" scope, publish a new app version, then sign in again.");
        var tok = new JsonObject
        {
            ["access_token"] = (string)data["access_token"]!,
            ["expires_at"] = now + ((double?)data["expires_in"] ?? 7200),
            ["refresh_token"] = refresh,
            ["refresh_expires_at"] = data["refresh_token"] != null
                ? now + ((double?)data["refresh_token_expires_in"] ?? 604800)
                : (double?)old["refresh_expires_at"] ?? 0,
            ["user_name"] = keepUser ? (string?)old["user_name"] ?? "" : "",
        };
        _tok = tok;
        if (string.IsNullOrEmpty((string?)tok["user_name"]))
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, OpenBase + "/open-apis/authen/v1/user_info");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (string)tok["access_token"]!);
                using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var info = (await ReadJson(response).ConfigureAwait(false))["data"];
                tok["user_name"] = (string?)info?["name"] ?? (string?)info?["en_name"] ?? "";
            }
            catch (Exception) { }
        }
        _store.Save(tok);
    }

    internal static async Task<JsonObject> ReadJson(HttpResponseMessage r)
    {
        try { return JsonNode.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false)) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }
}
