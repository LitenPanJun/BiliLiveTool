using System.Text;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Danmu;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Core.State;
using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.Infrastructure.Danmu;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.Danmu;
using BiliLiveTool.Tests.Fakes;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Tests.Danmu;

public class DanmuServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DanmuPacketCodec Codec = new();

    private static (DanmuService Service, FakeBilibiliApiClient Api, AuthSessionStore Sessions,
        InMemorySecretVault Vault, FakeLogger<DanmuService> Logger, FakeConnector Connector) Create(
        DanmuOptions? options = null)
    {
        options ??= new DanmuOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(30),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(20),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(60),
            BreakerThreshold = 2,
            JitterRatio = 0.2,
        };
        var api = new FakeBilibiliApiClient();
        var sessions = new AuthSessionStore();
        var vault = new InMemorySecretVault();
        var logger = new FakeLogger<DanmuService>();
        var connector = new FakeConnector();
        var service = new DanmuService(
            api, sessions, vault, new SecretMasker(), logger, options, connector.ConnectAsync);
        return (service, api, sessions, vault, logger, connector);
    }

    private static void SetDanmuInfo(FakeBilibiliApiClient api) =>
        api.DanmuInfoResult = FakeBilibiliApiClient.Ok(
            JsonNode.Parse(
                """{"token":"tok-1","host_list":[{"host":"broadcast.test","wss_port":2245,"ws_port":2244}]}""")
                !.AsObject());

    private static async Task<FakeWebSocketConnection> ConnectAsync(
        DanmuService svc, FakeConnector connector, string roomId = "12345")
    {
        (await svc.ConnectAsync(roomId, Ct)).Should().BeTrue();
        (await NextEvent(svc)).Msg.Should().Be("弹幕服务器连接成功"); // 消费连接事件，业务断言在其后
        return connector.Sockets[^1];
    }

    private static Task<DanmuEvent> NextEvent(DanmuService svc) =>
        svc.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        condition().Should().BeTrue("前置条件应在超时内成立");
    }

    private static async Task AssertNoEvent(DanmuService svc)
    {
        await Task.Delay(120);
        svc.Events.TryRead(out _).Should().BeFalse();
    }

    private static byte[] Command(string json) => Codec.Encode(5, json);

    private static (int Operation, string Body) DecodeOne(byte[] packet)
    {
        var result = Codec.Decode(packet);
        result.Frames.Should().HaveCount(1);
        return (result.Frames[0].Operation, Encoding.UTF8.GetString(result.Frames[0].Body));
    }

    private static JsonArray BuildInfo(string msg, long uid, string uname, string? face)
    {
        var meta = new JsonArray();
        for (var i = 0; i < 15; i++)
            meta.Add(i);
        if (face is not null)
        {
            meta.Add(new JsonObject
            {
                ["user"] = new JsonObject { ["base"] = new JsonObject { ["face"] = face } },
            });
        }

        return new JsonArray(meta, msg, new JsonArray(uid, uname));
    }

    // --- 连接 ---

    [Fact]
    public async Task Connect_Sends_Auth_Packet_To_First_Host()
    {
        var (svc, api, sessions, _, logger, connector) = Create();
        sessions.Replace(new SessionSnapshot { Uid = 42 });
        SetDanmuInfo(api);

        (await svc.ConnectAsync("12345", Ct)).Should().BeTrue();
        var socket = connector.Sockets[^1];

        connector.Urls[0].Should().Be(new Uri("wss://broadcast.test:2245/sub"));
        var (op, body) = DecodeOne(socket.SentPackets[0]);
        op.Should().Be(7);
        body.Should().Be(
            """{"uid":42,"roomid":12345,"protover":3,"platform":"web","type":2,"key":"tok-1"}""");
        logger.Has(LogLevel.Information, "Connected to danmu server").Should().BeTrue();
        (await NextEvent(svc)).Msg.Should().Be("弹幕服务器连接成功");
    }

    [Fact]
    public async Task Connect_Fetches_Uid_From_Nav_When_Missing()
    {
        var (svc, api, sessions, _, logger, connector) = Create();
        SetDanmuInfo(api);
        api.UserInfo = FakeBilibiliApiClient.Ok(
            new JsonObject { ["isLogin"] = true, ["mid"] = 777 });

        var socket = await ConnectAsync(svc, connector);

        DecodeOne(socket.SentPackets[0]).Body.Should().Contain("\"uid\":777");
        sessions.CurrentSnapshot.Uid.Should().Be(777);
        logger.Has(LogLevel.Information, "Fetched uid:").Should().BeTrue();
    }

    [Fact]
    public async Task Connect_Uses_Zero_Uid_When_Nav_Says_Logged_Out()
    {
        var (svc, api, _, _, logger, connector) = Create();
        SetDanmuInfo(api);
        api.UserInfo = FakeBilibiliApiClient.Ok(new JsonObject { ["isLogin"] = false });

        var socket = await ConnectAsync(svc, connector);

        DecodeOne(socket.SentPackets[0]).Body.Should().Contain("\"uid\":0");
        logger.Has(LogLevel.Information, "User not logged in, using uid=0").Should().BeTrue();
    }

    [Fact]
    public async Task Connect_Fetches_And_Merges_Buvid3_When_Missing()
    {
        var (svc, api, sessions, vault, logger, connector) = Create();
        SetDanmuInfo(api);
        api.Buvid3 = "BUVID3-NEW";
        vault.Save("42", new Dictionary<string, SecureCredential>(StringComparer.Ordinal)
        {
            ["SESSDATA"] = new("ses-val"),
            ["bili_jct"] = new("jct-val"),
        });
        sessions.Replace(new SessionSnapshot { Uid = 42, RoomId = "12345" });

        await ConnectAsync(svc, connector);

        api.CallCount("GetBuvid3Async").Should().Be(1);
        var merged = api.CookieUpdates[^1];
        merged["buvid3"]!.DangerousGetValue().Should().Be("BUVID3-NEW");
        merged["SESSDATA"]!.DangerousGetValue().Should().Be("ses-val"); // 合并不丢原机密
        vault.Load("42")!["buvid3"]!.DangerousGetValue().Should().Be("BUVID3-NEW");
        sessions.CurrentSnapshot.Buvid3.Should().NotBeNull();
        logger.Has(LogLevel.Information, "Fetched buvid3:").Should().BeTrue();
    }

    [Fact]
    public async Task Connect_Skips_Buvid3_Fetch_When_Present()
    {
        var (svc, api, sessions, _, _, connector) = Create();
        SetDanmuInfo(api);
        sessions.Replace(new SessionSnapshot { Uid = 42, Buvid3 = new SecureCredential("b3-present") });

        await ConnectAsync(svc, connector);

        api.CallCount("GetBuvid3Async").Should().Be(0);
    }

    [Fact]
    public async Task Connect_DanmuInfo_Failure_Schedules_Reconnect_Then_Recovers()
    {
        var (svc, api, _, _, logger, connector) = Create();
        api.DanmuInfoResult = FakeBilibiliApiClient.Fail(-1, "boom");

        (await svc.ConnectAsync("12345", Ct)).Should().BeFalse();
        SetDanmuInfo(api); // 恢复响应，退避后重连应成功

        logger.Has(LogLevel.Error, "Failed to get danmu info").Should().BeTrue();
        var msg = (await NextEvent(svc)).Msg;
        msg.Should().Contain("弹幕连接断开").And.Contain("第1次重连");
        await WaitUntil(() => connector.Sockets.Count == 1);
        await svc.StopAsync(Ct);
    }

    // --- 心跳与接收 ---

    [Fact]
    public async Task Heartbeat_Sends_Op2_Frame_After_Auth()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);

        await WaitUntil(() => socket.SentPackets.Count >= 2);

        var (op, body) = DecodeOne(socket.SentPackets[1]);
        op.Should().Be(2);
        body.Should().Be("");
        await svc.StopAsync(Ct);
    }

    [Fact]
    public async Task Receive_Danmu_Cmd_Maps_Info_Fields_And_Face()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);
        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "DANMU_MSG",
            ["info"] = BuildInfo("开播啦", 42, "tester", "https://f/x.png"),
        }.ToJsonString()));

        var evt = await NextEvent(svc);

        evt.Type.Should().Be(DanmuEventTypes.Danmu);
        evt.Msg.Should().Be("开播啦");
        evt.Uid.Should().Be(42);
        evt.Uname.Should().Be("tester");
        evt.Face.Should().Be("https://f/x.png");
    }

    [Fact]
    public async Task Receive_Danmu_Cmd_Without_Face_Meta_Yields_Empty_Face()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);
        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "DANMU_MSG",
            ["info"] = BuildInfo("hi", 1, "u", null),
        }.ToJsonString()));

        var evt = await NextEvent(svc);

        evt.Msg.Should().Be("hi");
        evt.Face.Should().Be("");
    }

    [Fact]
    public async Task Multi_Frame_Packet_Dispatches_Command_And_Ignores_Popularity()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);
        var popularity = Codec.Encode(3, "");
        var command = Command(new JsonObject
        {
            ["cmd"] = "DANMU_MSG",
            ["info"] = BuildInfo("multi", 3, "mu", null),
        }.ToJsonString());

        socket.Enqueue(popularity.Concat(command).ToArray());

        (await NextEvent(svc)).Msg.Should().Be("multi");
    }

    [Fact]
    public async Task Receive_Interact_Word_Maps_Known_Types_And_Skips_Unknown()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);

        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "INTERACT_WORD",
            ["data"] = new JsonObject { ["uid"] = 7, ["uname"] = "u7", ["msg_type"] = 2 },
        }.ToJsonString()));

        var evt = await NextEvent(svc);
        evt.Type.Should().Be(DanmuEventTypes.Interact);
        evt.Msg.Should().Be("关注了直播间");
        evt.Uid.Should().Be(7);
        evt.Uname.Should().Be("u7");

        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "INTERACT_WORD",
            ["data"] = new JsonObject { ["uid"] = 7, ["msg_type"] = 9 },
        }.ToJsonString()));
        await AssertNoEvent(svc); // 未映射的 msg_type 不产生事件
    }

    [Fact]
    public async Task Receive_Entry_Effect_Strips_Markers()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);
        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "ENTRY_EFFECT",
            ["data"] = new JsonObject { ["uid"] = 9, ["copy_writing"] = "欢迎<%大佬%>驾到" },
        }.ToJsonString()));

        var evt = await NextEvent(svc);

        evt.Type.Should().Be(DanmuEventTypes.Interact);
        evt.Msg.Should().Be("欢迎大佬驾到");
        evt.Uid.Should().Be(9);
        evt.Uname.Should().Be("");
    }

    [Fact]
    public async Task Receive_Send_Gift_Keeps_Fields_And_Defaults()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);

        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "SEND_GIFT",
            ["data"] = new JsonObject
            {
                ["uid"] = 5,
                ["uname"] = "giver",
                ["giftName"] = "超级辣条",
                ["gift_name"] = "辣条",
                ["num"] = 3,
                ["face"] = "https://f/g.png",
            },
        }.ToJsonString()));

        var evt = await NextEvent(svc);
        evt.Type.Should().Be(DanmuEventTypes.Gift);
        evt.GiftName.Should().Be("超级辣条"); // giftName 优先
        evt.Num.Should().Be(3);
        evt.Action.Should().Be("投喂"); // 缺省 action
        evt.Face.Should().Be("https://f/g.png");

        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "SEND_GIFT",
            ["data"] = new JsonObject { ["uid"] = 5, ["gift_name"] = "辣条", ["num"] = 1 },
        }.ToJsonString()));

        (await NextEvent(svc)).GiftName.Should().Be("辣条"); // 回退 gift_name
    }

    [Fact]
    public async Task Receive_Combo_Send_Uses_Combo_Num_And_Defaults()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);
        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "COMBO_SEND",
            ["data"] = new JsonObject
            {
                ["uid"] = 6,
                ["uname"] = "comboer",
                ["gift_name"] = "小心心",
                ["combo_num"] = 10,
            },
        }.ToJsonString()));

        var evt = await NextEvent(svc);

        evt.Type.Should().Be(DanmuEventTypes.Gift);
        evt.GiftName.Should().Be("小心心");
        evt.Num.Should().Be(10);
        evt.Face.Should().Be("");
        evt.Action.Should().Be("投喂");
        evt.Uname.Should().Be("comboer");
    }

    [Fact]
    public async Task Receive_Interact_V2_Parses_Protobuf()
    {
        var (svc, api, _, _, logger, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);
        var payload = new InteractWordV2
        {
            Uid = 9,
            Uname = "v2user",
            MsgType = InteractWordV2.Types.MsgType.MsgEnterRoom,
        };
        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "INTERACT_WORD_V2",
            ["data"] = new JsonObject { ["pb"] = Convert.ToBase64String(payload.ToByteArray()) },
        }.ToJsonString()));

        var evt = await NextEvent(svc);

        evt.Type.Should().Be(DanmuEventTypes.Interact);
        evt.Msg.Should().Be("进入直播间");
        evt.Uid.Should().Be(9);
        evt.Uname.Should().Be("v2user");
        logger.Has(LogLevel.Information, "Interact V2:").Should().BeTrue();
    }

    [Fact]
    public async Task Receive_Unknown_Cmd_Logs_Debug()
    {
        var (svc, api, _, _, logger, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);

        socket.Enqueue(Command("""{"cmd":"SOME_NEW_CMD"}"""));

        await AssertNoEvent(svc);
        logger.Has(LogLevel.Debug, "Unknown cmd: SOME_NEW_CMD").Should().BeTrue();
    }

    [Fact]
    public async Task Receive_Op8_Auth_Response_Logs_Success_And_Failure()
    {
        var (svc, api, _, _, logger, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);

        socket.Enqueue(Codec.Encode(8, """{"code":0}"""));
        await WaitUntil(() => logger.Has(LogLevel.Information, "Danmu authentication successful"));

        socket.Enqueue(Codec.Encode(8, """{"code":1,"message":"room error"}"""));
        await WaitUntil(() => logger.Has(LogLevel.Error, "Danmu authentication failed"));
    }

    // --- 停止与切换 ---

    [Fact]
    public async Task Stop_Closes_Socket_Drops_Further_Events_And_Logs()
    {
        var (svc, api, _, _, logger, connector) = Create();
        SetDanmuInfo(api);
        var socket = await ConnectAsync(svc, connector);

        await svc.StopAsync(Ct);

        socket.Closed.Should().BeTrue();
        logger.Has(LogLevel.Information, "Danmu service stopped").Should().BeTrue();
        socket.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "DANMU_MSG",
            ["info"] = BuildInfo("late", 1, "u", null),
        }.ToJsonString()));
        await AssertNoEvent(svc); // 停止后旧连接消息不残留
    }

    [Fact]
    public async Task Connect_New_Room_Drops_Old_Socket_Events()
    {
        var (svc, api, _, _, _, connector) = Create();
        SetDanmuInfo(api);
        var old = await ConnectAsync(svc, connector, "11111");

        (await svc.ConnectAsync("22222", Ct)).Should().BeTrue();
        (await NextEvent(svc)).Msg.Should().Be("弹幕服务器连接成功"); // 消费第二间房的连接事件

        old.Closed.Should().BeTrue();
        connector.Sockets.Should().HaveCount(2);
        DecodeOne(connector.Sockets[^1].SentPackets[0]).Body.Should().Contain("\"roomid\":22222");

        old.Enqueue(Command(new JsonObject
        {
            ["cmd"] = "DANMU_MSG",
            ["info"] = BuildInfo("stale", 1, "u", null),
        }.ToJsonString()));
        await AssertNoEvent(svc); // 旧房间消息不残留
    }

    // --- 重连、退避与熔断 ---

    [Fact]
    public async Task Reconnect_Failures_Trip_Breaker_After_Threshold()
    {
        var (svc, api, _, _, logger, connector) = Create(); // BreakerThreshold = 2
        SetDanmuInfo(api);
        connector.FailNext = 99;

        (await svc.ConnectAsync("12345", Ct)).Should().BeFalse();

        await WaitUntil(() => connector.Urls.Count == 3); // 首连 + 两次重连
        (await NextEvent(svc)).Msg.Should().Contain("第1次重连");
        (await NextEvent(svc)).Msg.Should().Contain("第2次重连");
        (await NextEvent(svc)).Msg.Should().Contain("已停止重连");
        logger.Has(LogLevel.Warning, "breaker").Should().BeTrue();

        await Task.Delay(100);
        connector.Urls.Count.Should().Be(3); // 熔断后不再重连
        await svc.StopAsync(Ct);
    }

    [Fact]
    public void ComputeDelay_Implements_Exponential_Backoff_With_Ceiling()
    {
        var options = new DanmuOptions(); // 原生 5 秒起、60 秒封顶

        DanmuService.ComputeDelay(1, options).Should().Be(TimeSpan.FromSeconds(5));
        DanmuService.ComputeDelay(2, options).Should().Be(TimeSpan.FromSeconds(10));
        DanmuService.ComputeDelay(3, options).Should().Be(TimeSpan.FromSeconds(20));
        DanmuService.ComputeDelay(4, options).Should().Be(TimeSpan.FromSeconds(40));
        DanmuService.ComputeDelay(5, options).Should().Be(TimeSpan.FromSeconds(60));
        DanmuService.ComputeDelay(6, options).Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ApplyJitter_Stays_Within_Ratio_Bounds()
    {
        var random = new Random(42);
        var baseDelay = TimeSpan.FromSeconds(10);

        for (var i = 0; i < 100; i++)
        {
            var jittered = DanmuService.ApplyJitter(baseDelay, 0.2, random);
            jittered.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(8))
                .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(12));
        }
    }

    [Fact]
    public void Event_Channel_Drops_Oldest_Beyond_Capacity()
    {
        var channel = DanmuService.CreateEventChannel();
        for (var i = 0; i < 2005; i++)
            channel.Writer.TryWrite(new DanmuEvent(DanmuEventTypes.Danmu, $"event-{i}"));

        var count = 0;
        string? first = null;
        while (channel.Reader.TryRead(out var evt))
        {
            first ??= evt.Msg;
            count++;
        }

        first.Should().Be("event-5"); // 前 5 条被丢弃
        count.Should().Be(2000);
    }

    // --- 弹幕发送 ---

    [Theory]
    [InlineData(0, "发送成功")]
    [InlineData(1003212, "超出限制长度")]
    [InlineData(-101, "未登录")]
    [InlineData(-400, "参数错误")]
    [InlineData(10031, "发送频率过高")]
    public async Task SendDanmu_Maps_Known_Error_Codes(int code, string expected)
    {
        var (svc, api, sessions, _, _, _) = Create();
        sessions.Replace(new SessionSnapshot { RoomId = "12345", BiliJct = new SecureCredential("jct-val") });
        api.SendDanmuResult = new ApiResult(true, code, "", null);

        var res = await svc.SendDanmuAsync("hello", Ct);

        res.Code.Should().Be(code);
        res.Message.Should().Be(expected);
        api.SendDanmuRoomId.Should().Be("12345");
        api.SendDanmuMessage.Should().Be("hello");
    }

    [Fact]
    public async Task SendDanmu_Falls_Back_To_Unknown_On_Empty_Message()
    {
        var (svc, api, sessions, _, _, _) = Create();
        sessions.Replace(new SessionSnapshot { RoomId = "12345", BiliJct = new SecureCredential("j") });
        api.SendDanmuResult = new ApiResult(true, -500, "", null);

        var res = await svc.SendDanmuAsync("hi", Ct);

        res.Code.Should().Be(-500);
        res.Message.Should().Be("未知错误");
    }

    [Fact]
    public async Task SendDanmu_Network_Failure_Returns_Request_Error()
    {
        var (svc, api, sessions, _, _, _) = Create();
        sessions.Replace(new SessionSnapshot { RoomId = "12345", BiliJct = new SecureCredential("j") });
        api.SendDanmuResult = FakeBilibiliApiClient.NetworkFail("timeout");

        var res = await svc.SendDanmuAsync("hi", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("网络请求失败");
    }

    [Fact]
    public async Task SendDanmu_Guards_Missing_Room_And_Csrf_Without_Request()
    {
        var (svc, api, sessions, _, _, _) = Create();

        var noRoom = await svc.SendDanmuAsync("hi", Ct);
        noRoom.Code.Should().Be(-1);
        noRoom.Message.Should().Be("未获取到房间ID");

        sessions.Replace(new SessionSnapshot { RoomId = "12345" });
        var noCsrf = await svc.SendDanmuAsync("hi", Ct);
        noCsrf.Code.Should().Be(-1);
        noCsrf.Message.Should().Be("未获取到 CSRF Token");

        api.CallCount("SendDanmuAsync").Should().Be(0);
    }
}
