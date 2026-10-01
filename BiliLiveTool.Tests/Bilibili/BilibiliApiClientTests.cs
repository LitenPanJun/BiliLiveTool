using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Infrastructure.Bilibili;
using FluentAssertions;

namespace BiliLiveTool.Tests.Bilibili;

public class BilibiliApiClientTests
{
    private const string NavJson = """
        {"code":0,"message":"0","data":{"isLogin":true,"mid":123,"uname":"tester",
         "wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                   "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;

    private const string ClickNowJson = """{"code":0,"message":"0","data":{"now":"1700000000"}}""";

    private const string LiveVersionJson =
        """{"code":0,"message":"0","data":{"build":"114514","curr_version":"3.0.1"}}""";

    private const string StartLiveOkJson = """
        {"code":0,"message":"0","data":{
          "rtmp":{"addr":"rtmp://live.test/xyz","code":"STREAM001"},
          "protocols":[
            {"protocol":"rtmp","addr":"rtmp://live.test/xyz","code":"STREAM001"},
            {"protocol":"srt","addr":"srt://live.test:9000","code":"SRT001"}]}}
        """;

    private const string StartLiveFaceJson =
        """{"code":60024,"message":"需要人脸验证","data":{"qr":"https://face.test/qr"}}""";

    private const string DanmuInfoJson = """
        {"code":0,"message":"0","data":{"token":"TOKEN123",
         "host_list":[{"host":"broadcastlv.test","wss_port":2245,"ws_port":2244}]}}
        """;

    private static (BilibiliApiClient Client, StubHandler Handler) Create()
    {
        var handler = new StubHandler();
        var client = new BilibiliApiClient(new HttpClient(handler));
        return (client, handler);
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var idx = url.IndexOf('?');
        if (idx < 0) return [];
        return url[(idx + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    }

    // --- 冻结 Header ---

    [Fact]
    public async Task Requests_Carry_Frozen_Headers_From_Data_Py()
    {
        var (client, handler) = Create();
        handler.Responder = _ => StubHandler.Json(NavJson);

        await client.GetUserInfoAsync(CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers["user-agent"].Should().Be(BilibiliApiClient.UserAgent);
        request.Headers["accept"].Should().Be("application/json, text/plain, */*");
        request.Headers["accept-language"].Should().Be("zh-CN,zh;q=0.9");
        request.ContentHeaders["content-type"].Should().Be("application/x-www-form-urlencoded; charset=UTF-8");
    }

    [Fact]
    public async Task UpdateCookies_Replaces_Whole_Cookie_Set()
    {
        // 与原 update_cookies 的整体赋值语义一致：换 Cookie 集时旧键必须消失
        var (client, handler) = Create();
        handler.Responder = _ => StubHandler.Json(NavJson);

        using var oldSes = new SecureCredential("old-ses");
        using var oldBuvid = new SecureCredential("old-b3");
        client.UpdateCookies(new Dictionary<string, SecureCredential>
        {
            ["SESSDATA"] = oldSes,
            ["buvid3"] = oldBuvid,
        });

        using var newSes = new SecureCredential("new-ses");
        client.UpdateCookies(new Dictionary<string, SecureCredential> { ["SESSDATA"] = newSes });

        await client.GetUserInfoAsync(CancellationToken.None);

        var cookie = handler.Requests.Should().ContainSingle().Subject.Headers["Cookie"];
        cookie.Should().Contain("SESSDATA=new-ses").And.NotContain("buvid3=");
    }

    // --- 弹幕发送 ---

    [Fact]
    public async Task SendDanmu_Signs_WebLocation_And_Builds_Bullet_Data_Form()
    {
        var (client, handler) = Create();
        handler.Responder = url => url.Contains("/msg/send")
            ? StubHandler.Json("""{"code":0,"message":"0","data":{}}""")
            : StubHandler.Json(NavJson);

        using var sesData = new SecureCredential("sess-val");
        using var biliJct = new SecureCredential("jct-val");
        client.UpdateCookies(new Dictionary<string, SecureCredential>
        {
            ["SESSDATA"] = sesData,
            ["bili_jct"] = biliJct,
        });

        var result = await client.SendDanmuAsync("12345", "hello", biliJct, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Code.Should().Be(0);

        var send = handler.Requests.Single(r => r.Url.Contains("/msg/send"));
        var query = ParseQuery(send.Url);
        query.Should().ContainKey("web_location").WhoseValue.Should().Be("444.8");
        query.Should().ContainKey("wts");
        query["w_rid"].Should().MatchRegex("^[0-9a-f]{32}$");

        send.Body.Should().StartWith("color=16777215&fontsize=25&mode=1&bubble=0&");
        send.Body.Should().Contain("&msg=hello");
        send.Body.Should().Contain("&csrf_token=jct-val");
        send.Body.Should().Contain("&csrf=jct-val");
        send.Body.Should().Contain("&roomid=12345");
        send.Body.Should().Contain("&rnd=");
        send.Headers["Cookie"].Should().Contain("SESSDATA=sess-val").And.Contain("bili_jct=jct-val");
    }

    [Fact]
    public async Task SendDanmu_Caches_Wbi_Keys_For_24h()
    {
        var (client, handler) = Create();
        handler.Responder = _ => StubHandler.Json(NavJson);
        using var biliJct = new SecureCredential("jct-val");

        await client.SendDanmuAsync("1", "a", biliJct, CancellationToken.None);
        await client.SendDanmuAsync("1", "b", biliJct, CancellationToken.None);

        handler.Requests.Count(r => r.Url.Contains("/x/web-interface/nav")).Should().Be(1);
        handler.Requests.Count(r => r.Url.Contains("/msg/send")).Should().Be(2);
    }

    // --- 弹幕服务器信息 ---

    [Fact]
    public async Task GetDanmuInfo_Params_Are_Wbi_Signed_And_Parsed()
    {
        var (client, handler) = Create();
        handler.Responder = url => url.Contains("getDanmuInfo")
            ? StubHandler.Json(DanmuInfoJson)
            : StubHandler.Json(NavJson);

        var result = await client.GetDanmuInfoAsync("12345", CancellationToken.None);

        var info = handler.Requests.Single(r => r.Url.Contains("getDanmuInfo"));
        var query = ParseQuery(info.Url);
        query.Should().Contain(new Dictionary<string, string>
        {
            ["id"] = "12345",
            ["type"] = "0",
        });
        query["w_rid"].Should().MatchRegex("^[0-9a-f]{32}$");

        result.Data!["token"]!.GetValue<string>().Should().Be("TOKEN123");
        result.Data["host_list"]![0]!["wss_port"]!.GetValue<int>().Should().Be(2245);
    }

    // --- 开播三步 ---

    [Fact]
    public async Task StartLive_Follows_Three_Steps_With_AppSign()
    {
        var (client, handler) = Create();
        handler.Responder = url => url switch
        {
            var u when u.Contains("/x/report/click/now") => StubHandler.Json(ClickNowJson),
            var u when u.Contains("liveVersionInfo") => StubHandler.Json(LiveVersionJson),
            var u when u.Contains("/startLive") => StubHandler.Json(StartLiveOkJson),
            _ => StubHandler.Json("{}"),
        };
        using var biliJct = new SecureCredential("jct-val");

        var result = await client.StartLiveAsync("12345", 210, biliJct, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Code.Should().Be(0);
        // 解析金丝雀：双 RTMP 与 SRT
        result.Data!["rtmp"]!["addr"]!.GetValue<string>().Should().Be("rtmp://live.test/xyz");
        var protocols = result.Data["protocols"]!.AsArray();
        protocols.Should().HaveCount(2);
        protocols[1]!["protocol"]!.GetValue<string>().Should().Be("srt");
        protocols[1]!["code"]!.GetValue<string>().Should().Be("SRT001");

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Url.Should().EndWith("/x/report/click/now");

        var version = ParseQuery(handler.Requests[1].Url);
        version.Should().Contain(new Dictionary<string, string>
        {
            ["appkey"] = "aae92bc66f3edfab",
            ["system_version"] = "2",
            ["ts"] = "1700000000",
        });
        version["sign"].Should().MatchRegex("^[0-9a-f]{32}$");

        var start = handler.Requests[2];
        start.Method.Should().Be("POST");
        start.Body.Should().Contain("room_id=12345");
        start.Body.Should().Contain("platform=pc_link");
        start.Body.Should().Contain("area_v2=210");
        start.Body.Should().Contain("backup_stream=0");
        start.Body.Should().Contain("build=114514");
        start.Body.Should().Contain("version=3.0.1");
        start.Body.Should().Contain("ts=1700000000");
        start.Body.Should().Contain("csrf_token=jct-val");
        start.Body.Should().Contain("appkey=aae92bc66f3edfab");
        start.Body.Should().Contain("sign=");
    }

    [Fact]
    public async Task StartLive_FaceVerify_60024_Surfaces_Qr_Without_Swallowing()
    {
        var (client, handler) = Create();
        handler.Responder = url => url.Contains("/x/report/click/now")
            ? StubHandler.Json(ClickNowJson)
            : url.Contains("liveVersionInfo")
                ? StubHandler.Json(LiveVersionJson)
                : StubHandler.Json(StartLiveFaceJson);
        using var biliJct = new SecureCredential("jct-val");

        var result = await client.StartLiveAsync("12345", 210, biliJct, CancellationToken.None);

        result.Code.Should().Be(60024);
        result.Message.Should().Be("需要人脸验证");
        result.Data!["qr"]!.GetValue<string>().Should().Be("https://face.test/qr");
    }

    // --- 二维码登录 ---

    [Fact]
    public async Task PollQrCode_Skips_Cookies_And_Captures_SetCookie()
    {
        var (client, handler) = Create();
        handler.Responder = _ => StubHandler.Json(
            """{"code":0,"message":"0","data":{"code":0,"refresh_token":"RT","timestamp":123}}""",
            setCookie: "SESSDATA=pollval; Path=/; Domain=.bilibili.com");
        using var sesData = new SecureCredential("sess-val");
        client.UpdateCookies(new Dictionary<string, SecureCredential> { ["SESSDATA"] = sesData });

        var result = await client.PollPassportQrcodeAsync("QKEY", CancellationToken.None);

        var poll = handler.Requests.Should().ContainSingle().Subject;
        poll.Url.Should().Contain("qrcode_key=QKEY");
        poll.Headers.Should().NotContainKey("Cookie");

        // poll 身份取自 Set-Cookie；已扫 86090 等状态取自 data.code
        result.ResponseCookies.Should().ContainKey("SESSDATA").WhoseValue.Should().Be("pollval");
        result.Data!["code"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task PollQrCode_Surfaces_Inner_Status_Code()
    {
        var (client, handler) = Create();
        handler.Responder = _ => StubHandler.Json(
            """{"code":0,"message":"0","data":{"code":86090}}""");

        var result = await client.PollPassportQrcodeAsync("QKEY", CancellationToken.None);

        result.Data!["code"]!.GetValue<int>().Should().Be(86090);
    }

    // --- 错误语义 ---

    [Fact]
    public async Task Network_Failure_Returns_Code_Minus_One()
    {
        var (client, handler) = Create();
        handler.Responder = _ => throw new HttpRequestException("boom");

        var result = await client.GetUserInfoAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Code.Should().Be(-1);
        result.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Invalid_Json_Returns_Code_Minus_One_With_Parse_Message()
    {
        var (client, handler) = Create();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>oops</html>", Encoding.UTF8, "text/html"),
        };

        var result = await client.GetAreaListAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Code.Should().Be(-1);
        result.Message.Should().Be("API 返回格式错误");
    }

    [Fact]
    public async Task Array_Data_Is_Preserved_As_JsonArray()
    {
        // 回归：Area/getList 的 data 为数组，须原样保留（对照原 res['data'] 任意形状）
        var (client, handler) = Create();
        handler.Responder = _ => StubHandler.Json(
            """{"code":0,"message":"0","data":[{"id":235,"name":"A-SOUL"}]}""");

        var result = await client.GetAreaListAsync(CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Data.Should().BeOfType<JsonArray>();
        result.Data![0]!["id"]!.GetValue<int>().Should().Be(235);
    }

    // --- buvid3 ---

    [Fact]
    public async Task GetBuvid3_Returns_B3_On_Success_And_Null_Otherwise()
    {
        var (client, handler) = Create();
        handler.Responder = _ => StubHandler.Json(
            """{"code":0,"message":"0","data":{"b_3":"BUVID3-VAL","b_4":"BUVID4-VAL"}}""");
        (await client.GetBuvid3Async(CancellationToken.None)).Should().Be("BUVID3-VAL");

        handler.Responder = _ => StubHandler.Json("""{"code":-400,"message":"参数错误","data":null}""");
        (await client.GetBuvid3Async(CancellationToken.None)).Should().BeNull();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public sealed record Captured(
            string Method,
            string Url,
            Dictionary<string, string> Headers,
            Dictionary<string, string> ContentHeaders,
            string? Body);

        public List<Captured> Requests { get; } = [];

        public Func<string, HttpResponseMessage> Responder { get; set; } = _ => Json("{}");

        public static HttpResponseMessage Json(string json, string? setCookie = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (setCookie is not null)
                response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // NonValidated 取原始上行字节（校验枚举会把 UA 的 product+comment 再序列化出多余逗号）
            var headers = request.Headers.NonValidated.ToDictionary(
                h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            var contentHeaders = request.Content?.Headers.NonValidated.ToDictionary(
                h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Add(new Captured(
                request.Method.Method,
                request.RequestUri!.ToString(),
                headers,
                contentHeaders,
                body));
            return Task.FromResult(Responder(request.RequestUri.ToString()));
        }
    }
}
