using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Security;

namespace BiliLiveTool.Infrastructure.Bilibili;

/// <summary>
/// B站 REST 薄封装：方法名与原 bilibili_api.py 一一对应，统一走 HttpClient。
/// 冻结约束：Header 逐项复刻 data.py、超时 10 秒、GET 经 query、POST 经 form、
/// 仅 HTTPS 由调用方 HttpClient 保证；Cookie 以 SecureCredential 透传。
/// </summary>
public sealed class BilibiliApiClient : IBilibiliApiClient
{
    // 对应 data.py:1-7，逐项复刻，不得改动
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string AcceptHeader = "application/json, text/plain, */*";
    private const string AcceptLanguageHeader = "zh-CN,zh;q=0.9";
    private const string ContentTypeHeader = "application/x-www-form-urlencoded; charset=UTF-8";

    private static readonly TimeSpan WbiKeyCacheDuration = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly AppSigner _appSigner = new();
    private readonly WbiSigner _wbiSigner = new();
    private readonly Dictionary<string, SecureCredential> _cookies = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wbiGate = new(1, 1);
    private (string ImgKey, string SubKey, DateTimeOffset Expire)? _wbiKeys;

    public BilibiliApiClient(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        if (!_http.DefaultRequestHeaders.Contains("accept"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("accept", AcceptHeader);
        if (!_http.DefaultRequestHeaders.Contains("accept-language"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("accept-language", AcceptLanguageHeader);
        if (!_http.DefaultRequestHeaders.Contains("user-agent"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", UserAgent);
        // content-type 属内容头，HttpRequestHeaders 拒绝承载（TryAddWithoutValidation 返回 false）；
        // 统一在 SendAsync 中挂到内容对象上，GET 以空体携带，保持与 data.py 全局 header 一致
    }

    public void UpdateCookies(IReadOnlyDictionary<string, SecureCredential> cookies)
    {
        lock (_gate)
        {
            foreach (var (key, value) in cookies)
                _cookies[key] = value;
        }
    }

    // --- 扫码登录 ---

    public Task<ApiResult> GetPassportQrcodeAsync(CancellationToken ct) =>
        GetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", null, ct);

    public Task<ApiResult> PollPassportQrcodeAsync(string qrcodeKey, CancellationToken ct) =>
        GetAsync(
            "https://passport.bilibili.com/x/passport-login/web/qrcode/poll",
            [new("qrcode_key", qrcodeKey)],
            ct,
            includeCookies: false,
            captureCookies: true);

    // --- 用户信息 ---

    public Task<ApiResult> GetUserInfoAsync(CancellationToken ct) =>
        GetAsync("https://api.bilibili.com/x/web-interface/nav", null, ct);

    public Task<ApiResult> GetUserStatAsync(CancellationToken ct) =>
        GetAsync("https://api.bilibili.com/x/web-interface/nav/stat", null, ct);

    public Task<ApiResult> GetRoomIdByUidAsync(long uid, CancellationToken ct) =>
        GetAsync(
            "https://api.live.bilibili.com/room/v2/Room/room_id_by_uid",
            [new("uid", uid.ToString(CultureInfo.InvariantCulture))],
            ct);

    // --- 直播管理 ---

    public Task<ApiResult> GetAreaListAsync(CancellationToken ct) =>
        GetAsync(
            "https://api.live.bilibili.com/room/v1/Area/getList",
            [new("show_pinyin", "1")],
            ct);

    public Task<ApiResult> GetRoomInfoAsync(string roomId, CancellationToken ct) =>
        GetAsync(
            "https://api.live.bilibili.com/room/v1/Room/get_info",
            [new("room_id", roomId)],
            ct);

    public Task<ApiResult> GetRoomNewsAsync(string roomId, long uid, CancellationToken ct) =>
        GetAsync(
            "https://api.live.bilibili.com/xlive/app-blink/v1/index/getRoomNews",
            [
                new("room_id", roomId),
                new("uid", uid.ToString(CultureInfo.InvariantCulture)),
            ],
            ct);

    public Task<ApiResult> UpdateTitleAsync(string roomId, string title, SecureCredential csrf, CancellationToken ct)
    {
        var csrfValue = csrf.DangerousGetValue();
        return PostAsync(
            "https://api.live.bilibili.com/room/v1/Room/update",
            new Dictionary<string, string>
            {
                ["room_id"] = roomId,
                ["platform"] = "pc_link",
                ["title"] = title,
                ["csrf_token"] = csrfValue,
                ["csrf"] = csrfValue,
            },
            ct);
    }

    public Task<ApiResult> UpdateAnnouncementAsync(string roomId, long uid, string content, SecureCredential csrf, CancellationToken ct)
    {
        var csrfValue = csrf.DangerousGetValue();
        return PostAsync(
            "https://api.live.bilibili.com/xlive/app-blink/v1/index/updateRoomNews",
            new Dictionary<string, string>
            {
                ["room_id"] = roomId,
                ["uid"] = uid.ToString(CultureInfo.InvariantCulture),
                ["content"] = content,
                ["csrf_token"] = csrfValue,
                ["csrf"] = csrfValue,
            },
            ct);
    }

    public Task<ApiResult> UpdateAreaAsync(string roomId, int areaId, SecureCredential csrf, CancellationToken ct)
    {
        var csrfValue = csrf.DangerousGetValue();
        return PostAsync(
            "https://api.live.bilibili.com/room/v1/Room/update",
            new Dictionary<string, string>
            {
                ["room_id"] = roomId,
                ["area_id"] = areaId.ToString(CultureInfo.InvariantCulture),
                ["platform"] = "pc_link",
                ["csrf_token"] = csrfValue,
                ["csrf"] = csrfValue,
            },
            ct);
    }

    public async Task<ApiResult> StartLiveAsync(string roomId, int areaId, SecureCredential csrf, CancellationToken ct)
    {
        // 第 1 步：取服务器时间
        var now = await GetAsync("https://api.bilibili.com/x/report/click/now", null, ct).ConfigureAwait(false);
        if (!now.Success || now.Code != 0) return now;
        var ts = RawString(now.Data?["now"]);

        // 第 2 步：取版本信息（经 _appsign）
        var versionParams = _appSigner.Sign(new Dictionary<string, string>
        {
            ["system_version"] = "2",
            ["ts"] = ts,
        });
        var version = await GetAsync(
            "https://api.live.bilibili.com/xlive/app-blink/v1/liveVersionInfo/getHomePageLiveVersion",
            versionParams,
            ct).ConfigureAwait(false);
        if (!version.Success || version.Code != 0) return version;
        var build = RawString(version.Data?["build"]);
        var currVersion = RawString(version.Data?["curr_version"]);

        // 第 3 步：开播（表单经 _appsign），60024/60043 原样返回
        var csrfValue = csrf.DangerousGetValue();
        var form = _appSigner.Sign(new Dictionary<string, string>
        {
            ["room_id"] = roomId,
            ["platform"] = "pc_link",
            ["area_v2"] = areaId.ToString(CultureInfo.InvariantCulture),
            ["backup_stream"] = "0",
            ["csrf_token"] = csrfValue,
            ["csrf"] = csrfValue,
            ["build"] = build,
            ["version"] = currVersion,
            ["ts"] = ts,
        });
        return await PostAsync(
            "https://api.live.bilibili.com/room/v1/Room/startLive",
            form,
            ct).ConfigureAwait(false);
    }

    public Task<ApiResult> StopLiveAsync(string roomId, SecureCredential csrf, CancellationToken ct)
    {
        var csrfValue = csrf.DangerousGetValue();
        return PostAsync(
            "https://api.live.bilibili.com/room/v1/Room/stopLive",
            new Dictionary<string, string>
            {
                ["room_id"] = roomId,
                ["platform"] = "pc_link",
                ["csrf_token"] = csrfValue,
                ["csrf"] = csrfValue,
            },
            ct);
    }

    // --- 弹幕 ---

    public async Task<ApiResult> SendDanmuAsync(string roomId, string msg, SecureCredential csrf, CancellationToken ct)
    {
        var (imgKey, subKey) = await GetWbiKeysAsync(ct).ConfigureAwait(false);
        var signed = _wbiSigner.Sign(
            new Dictionary<string, string> { ["web_location"] = "444.8" },
            imgKey,
            subKey);
        var csrfValue = csrf.DangerousGetValue();
        var form = new Dictionary<string, string>
        {
            // bullet_data 模板（data.py:10-15），顺序与字段不得改
            ["color"] = "16777215",
            ["fontsize"] = "25",
            ["mode"] = "1",
            ["bubble"] = "0",
            ["msg"] = msg,
            ["csrf_token"] = csrfValue,
            ["csrf"] = csrfValue,
            ["roomid"] = roomId,
            ["rnd"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
        return await SendAsync(
            HttpMethod.Post,
            "https://api.live.bilibili.com/msg/send",
            signed,
            form,
            includeCookies: true,
            captureCookies: false,
            ct).ConfigureAwait(false);
    }

    public async Task<ApiResult> GetDanmuInfoAsync(string roomId, CancellationToken ct)
    {
        var (imgKey, subKey) = await GetWbiKeysAsync(ct).ConfigureAwait(false);
        var signed = _wbiSigner.Sign(
            new Dictionary<string, string>
            {
                ["id"] = roomId,
                ["type"] = "0",
            },
            imgKey,
            subKey);
        return await GetAsync(
            "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo",
            signed,
            ct).ConfigureAwait(false);
    }

    // --- buvid3 ---

    public async Task<string?> GetBuvid3Async(CancellationToken ct)
    {
        var res = await GetAsync("https://api.bilibili.com/x/frontend/finger/spi", null, ct).ConfigureAwait(false);
        return res.Success && res.Code == 0 ? RawString(res.Data?["b_3"]) is { Length: > 0 } b3 ? b3 : null : null;
    }

    // --- 内部：WBI Key 缓存（24 小时） ---

    internal async Task<(string ImgKey, string SubKey)> GetWbiKeysAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _wbiKeys;
        if (cached is { } c && c.Expire > now)
            return (c.ImgKey, c.SubKey);

        await _wbiGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            cached = _wbiKeys;
            if (cached is { } c2 && c2.Expire > now)
                return (c2.ImgKey, c2.SubKey);

            // 对应 get_wbi.py 的 getWbiKeys：不带 Cookie 取 nav 的 wbi_img
            var res = await SendAsync(
                HttpMethod.Get,
                "https://api.bilibili.com/x/web-interface/nav",
                null,
                null,
                includeCookies: false,
                captureCookies: false,
                ct).ConfigureAwait(false);

            var imgKey = ExtractWbiKey(res.Data?["wbi_img"]?["img_url"]);
            var subKey = ExtractWbiKey(res.Data?["wbi_img"]?["sub_url"]);
            _wbiKeys = (imgKey, subKey, now.Add(WbiKeyCacheDuration));
            return (imgKey, subKey);
        }
        finally
        {
            _wbiGate.Release();
        }
    }

    private static string ExtractWbiKey(JsonNode? url)
    {
        var raw = RawString(url);
        if (raw.Length == 0) return "";
        var lastSlash = raw.LastIndexOf('/');
        var name = lastSlash >= 0 ? raw[(lastSlash + 1)..] : raw;
        var dot = name.IndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    // --- 内部：请求组装 ---

    private Task<ApiResult> GetAsync(
        string url,
        IEnumerable<KeyValuePair<string, string>>? query,
        CancellationToken ct,
        bool includeCookies = true,
        bool captureCookies = false) =>
        SendAsync(HttpMethod.Get, url, query, null, includeCookies, captureCookies, ct);

    private Task<ApiResult> PostAsync(
        string url,
        IEnumerable<KeyValuePair<string, string>>? form,
        CancellationToken ct) =>
        SendAsync(HttpMethod.Post, url, null, form, includeCookies: true, captureCookies: false, ct);

    private async Task<ApiResult> SendAsync(
        HttpMethod method,
        string url,
        IEnumerable<KeyValuePair<string, string>>? query,
        IEnumerable<KeyValuePair<string, string>>? form,
        bool includeCookies,
        bool captureCookies,
        CancellationToken ct)
    {
        var pairs = query?.ToList();
        if (pairs is { Count: > 0 })
            url += (url.Contains('?') ? '&' : '?') + Encode(pairs);

        using var request = new HttpRequestMessage(method, url);
        // data.py 全局 content-type 对 GET/POST 均生效：GET 携带空体，POST 携带表单
        var content = new StringContent(form is null ? "" : Encode(form), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentTypeHeader);
        request.Content = content;
        AttachCookies(request, includeCookies);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            JsonObject? root;
            try
            {
                root = JsonNode.Parse(body) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                root = null;
            }
            if (root is null)
                return new ApiResult(false, -1, "API 返回格式错误", null);

            var code = -1;
            if (root["code"] is JsonValue codeValue && codeValue.TryGetValue<int>(out var parsedCode))
                code = parsedCode;

            var message = "";
            if (root["msg"] is JsonValue msgValue && msgValue.TryGetValue<string>(out var parsedMsg))
                message = parsedMsg;
            else if (root["message"] is JsonValue messageValue && messageValue.TryGetValue<string>(out var parsedMessage))
                message = parsedMessage;

            var data = (root["data"] as JsonObject)?.DeepClone() as JsonObject;

            IReadOnlyDictionary<string, string>? cookies = null;
            if (captureCookies && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                cookies = ParseSetCookies(setCookies);

            return new ApiResult(true, code, message, data, cookies);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new ApiResult(false, -1, e.Message, null);
        }
    }

    private void AttachCookies(HttpRequestMessage request, bool includeCookies)
    {
        if (!includeCookies) return;

        List<KeyValuePair<string, string>> pairs;
        lock (_gate)
        {
            if (_cookies.Count == 0) return;
            pairs = _cookies
                .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.DangerousGetValue()))
                .ToList();
        }
        var header = string.Join("; ", pairs.Select(kv => $"{kv.Key}={kv.Value}"));
        request.Headers.TryAddWithoutValidation("Cookie", header);
    }

    private static IReadOnlyDictionary<string, string> ParseSetCookies(IEnumerable<string> setCookies)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in setCookies)
        {
            var first = line.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
            var idx = first.IndexOf('=');
            if (idx <= 0) continue;
            result[first[..idx].Trim()] = first[(idx + 1)..].Trim();
        }
        return result;
    }

    private static string Encode(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static string RawString(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        null => "",
        _ => node.ToJsonString(),
    };
}
