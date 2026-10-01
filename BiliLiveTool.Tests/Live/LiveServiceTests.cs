using System.Text.Json.Nodes;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Core.State;
using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.Live;
using BiliLiveTool.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliLiveTool.Tests.Live;

public class LiveServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static (LiveService Service, FakeBilibiliApiClient Api, AuthSessionStore Sessions,
        InMemoryAccountStore Accounts) Create()
    {
        var api = new FakeBilibiliApiClient();
        var sessions = new AuthSessionStore();
        var accounts = new InMemoryAccountStore();
        var service = new LiveService(
            api, sessions, accounts, new SecretMasker(), NullLogger<LiveService>.Instance);
        return (service, api, sessions, accounts);
    }

    private static AuthSessionStore LoginAs(
        AuthSessionStore sessions, long uid = 42, string roomId = "12345")
    {
        sessions.Replace(new SessionSnapshot
        {
            Uid = uid,
            RoomId = roomId,
            BiliJct = new SecureCredential("jct-secret"),
        });
        return sessions;
    }

    private static void SaveCurrent(InMemoryAccountStore accounts, string uid = "42") =>
        accounts.Save(new AccountRecord { Uid = uid });

    private static ApiResult OkData(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        return new(true, 0, "", root["data"]?.DeepClone());
    }

    // --- 分区 ---

    [Fact]
    public async Task RefreshPartitions_Parses_Area_List_And_Restores_Last_Area()
    {
        var (svc, api, sessions, accounts) = Create();
        SaveCurrent(accounts);
        accounts.Upsert(accounts.Get("42")! with { LastAreaId = 3259 });
        api.AreaList = OkData("""
            {"code":0,"data":[
              {"name":"宅系","list":[{"id":235,"name":"A-SOUL"},{"id":3259,"name":"虚拟主播"}]},
              {"name":"知识","list":[{"id":204,"name":"科学"}]}]}
            """);

        (await svc.RefreshPartitionsAsync(Ct)).Should().BeTrue();

        sessions.ResolveAreaId("宅系", "A-SOUL").Should().Be(235);
        sessions.ResolveAreaId("宅系", "虚拟主播").Should().Be(3259);
        sessions.ResolveAreaId("知识", "科学").Should().Be(204);
        sessions.CurrentSnapshot.CurrentAreaId.Should().Be(3259);
    }

    [Fact]
    public async Task RefreshPartitions_Fails_On_Non_Array_Data()
    {
        var (svc, api, sessions, _) = Create();
        api.AreaList = OkData("""{"code":0,"data":{}}""");

        (await svc.RefreshPartitionsAsync(Ct)).Should().BeFalse();
        sessions.Partitions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPartitions_Returns_Sub_Names_And_Refreshes_When_Empty()
    {
        var (svc, api, _, _) = Create();
        api.AreaList = OkData("""
            {"code":0,"data":[{"name":"宅系","list":[{"id":235,"name":"A-SOUL"},{"id":3259,"name":"虚拟主播"}]}]}
            """);

        var res = await svc.GetPartitionsAsync(Ct);

        res.IsSuccess.Should().BeTrue();
        api.CallCount(nameof(FakeBilibiliApiClient.GetAreaListAsync)).Should().Be(1);
        res.Data!["宅系"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("A-SOUL", "虚拟主播");

        // 已有映射时不再刷新
        await svc.GetPartitionsAsync(Ct);
        api.CallCount(nameof(FakeBilibiliApiClient.GetAreaListAsync)).Should().Be(1);
    }

    // --- 房间资料 ---

    [Fact]
    public async Task SyncRoomProfile_Guards_Without_Login()
    {
        var (svc, _, _, _) = Create();

        var res = await svc.SyncRoomProfileAsync(Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("请先登录");
    }

    [Fact]
    public async Task SyncRoomProfile_Merges_Dual_Fields_Into_Snapshot_And_Account()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);
        api.RoomInfo = OkData("""
            {"code":0,"data":{"title":"开播啦","area_id":null,"area_v2_id":3259,
             "parent_area_name":"","parent_area_v2_name":"宅系",
             "area_name":"","area_v2_name":"虚拟主播"}}
            """);
        api.RoomNews = OkData("""{"code":0,"data":{"content":"今晚八点"}}""");

        var res = await svc.SyncRoomProfileAsync(Ct);

        res.IsSuccess.Should().BeTrue();
        res.Data!["last_title"]!.GetValue<string>().Should().Be("开播啦");
        res.Data!["last_area_id"]!.GetValue<int>().Should().Be(3259);
        res.Data!["last_announcement"]!.GetValue<string>().Should().Be("今晚八点");

        var snapshot = sessions.CurrentSnapshot;
        snapshot.CurrentAreaId.Should().Be(3259);
        snapshot.CurrentAreaNames.Should().Equal("宅系", "虚拟主播");

        var record = accounts.Get("42")!;
        record.LastTitle.Should().Be("开播啦");
        record.LastAreaId.Should().Be(3259);
        record.LastAreaName.Should().Equal("宅系", "虚拟主播");
        record.LastAnnouncement.Should().Be("今晚八点");
    }

    [Fact]
    public async Task SyncRoomProfile_Fails_When_Both_Endpoints_Fail()
    {
        var (svc, api, sessions, _) = Create();
        LoginAs(sessions);
        api.RoomInfo = FakeBilibiliApiClient.NetworkFail("timeout");
        api.RoomNews = FakeBilibiliApiClient.Fail(-1, "boom");

        var res = await svc.SyncRoomProfileAsync(Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("同步直播信息失败");
    }

    // --- 标题 / 公告 / 分区 ---

    [Fact]
    public async Task UpdateTitle_Guards_Without_Login()
    {
        var (svc, _, _, _) = Create();

        var res = await svc.UpdateTitleAsync("标题", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("未登录");
    }

    [Fact]
    public async Task UpdateTitle_Success_Saves_Last_Title()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);

        var res = await svc.UpdateTitleAsync("新标题", Ct);

        res.IsSuccess.Should().BeTrue();
        api.UpdateTitleTitle.Should().Be("新标题");
        accounts.Get("42")!.LastTitle.Should().Be("新标题");
    }

    [Fact]
    public async Task UpdateTitle_Failure_Returns_Server_Message()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);
        api.UpdateTitleResult = FakeBilibiliApiClient.Fail(-400, "参数错误");

        var res = await svc.UpdateTitleAsync("x", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("参数错误");
        accounts.Get("42")!.LastTitle.Should().Be("");
    }

    [Fact]
    public async Task UpdateAnnouncement_Success_Saves_Last_Announcement()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);

        var res = await svc.UpdateAnnouncementAsync("公告内容", Ct);

        res.IsSuccess.Should().BeTrue();
        api.UpdateAnnouncementContent.Should().Be("公告内容");
        accounts.Get("42")!.LastAnnouncement.Should().Be("公告内容");
    }

    [Fact]
    public async Task UpdateArea_Resolves_Saves_And_Updates_Snapshot()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);
        sessions.SetPartitions(new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["宅系"] = new Dictionary<string, int> { ["A-SOUL"] = 235 },
        });

        var res = await svc.UpdateAreaAsync("宅系", "A-SOUL", Ct);

        res.IsSuccess.Should().BeTrue();
        api.UpdateAreaAreaId.Should().Be(235);
        sessions.CurrentSnapshot.CurrentAreaId.Should().Be(235);
        sessions.CurrentSnapshot.CurrentAreaNames.Should().Equal("宅系", "A-SOUL");
        accounts.Get("42")!.LastAreaId.Should().Be(235);
        accounts.Get("42")!.LastAreaName.Should().Equal("宅系", "A-SOUL");
    }

    [Fact]
    public async Task UpdateArea_Unknown_Partition_Returns_Invalid()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);
        api.AreaList = OkData("""{"code":0,"data":[]}""");

        var res = await svc.UpdateAreaAsync("不存在", "不存在", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("无效分区");
    }

    // --- 开播 / 停播 ---

    [Fact]
    public async Task StartLive_Throws_Without_Room()
    {
        var (svc, _, _, _) = Create();

        var act = () => svc.StartLiveAsync("", "", Ct);

        (await act.Should().ThrowAsync<BilibiliException>())
            .Where(e => e.Code == -1 && e.Message == "请先登录");
    }

    [Fact]
    public async Task StartLive_Success_Builds_Endpoints_And_Toggles_Live()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);
        sessions.SetPartitions(new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["宅系"] = new Dictionary<string, int> { ["A-SOUL"] = 235 },
        });
        api.StartLiveResult = OkData("""
            {"code":0,"data":{
              "rtmp":{"addr":"rtmp://a/live/x","code":"C1"},
              "protocols":[
                {"protocol":"rtmp","addr":"rtmp://b/live/y","code":"C2"},
                {"protocol":"srt","addr":"srt://c:9000","code":"S3"},
                {"protocol":"rtmp","addr":"","code":""}]}}
            """);

        var result = await svc.StartLiveAsync("宅系", "A-SOUL", Ct);

        result.IsOk.Should().BeTrue();
        result.Value!.Rtmp1.Should().Be(new LiveEndpoint("rtmp://a/live/x", "C1"));
        result.Value.Rtmp2.Should().Be(new LiveEndpoint("rtmp://b/live/y", "C2"));
        result.Value.Srt.Should().Be(new LiveEndpoint("srt://c:9000", "S3"));

        api.StartLiveRoomId.Should().Be("12345");
        api.StartLiveAreaId.Should().Be(235);
        sessions.CurrentSnapshot.IsLive.Should().BeTrue();
        sessions.CurrentSnapshot.CurrentAreaNames.Should().Equal("宅系", "A-SOUL");
        accounts.Get("42")!.LastAreaId.Should().Be(235);
        accounts.Get("42")!.LastAreaName.Should().Equal("宅系", "A-SOUL");
    }

    [Fact]
    public async Task StartLive_Falls_Back_To_Account_Area_Then_235()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);
        accounts.Upsert(accounts.Get("42")! with { LastAreaId = 3259 });
        api.StartLiveResult = OkData("""{"code":0,"data":{}}""");

        await svc.StartLiveAsync("", "", Ct);
        api.StartLiveAreaId.Should().Be(3259);

        // 账号无 last_area_id → 兜底 235
        var fresh = Create();
        fresh.Accounts.Save(new AccountRecord { Uid = "42" });
        LoginAs(fresh.Sessions);
        fresh.Api.StartLiveResult = OkData("""{"code":0,"data":{}}""");

        await fresh.Service.StartLiveAsync("", "", Ct);
        fresh.Api.StartLiveAreaId.Should().Be(235);
    }

    [Fact]
    public async Task StartLive_FaceVerify_60024_Returns_Qr_From_Data()
    {
        var (svc, api, sessions, _) = Create();
        LoginAs(sessions);
        api.StartLiveResult = new ApiResult(true, 60024, "", JsonNode.Parse("""{"qr":"https://face/qr"}""")!.AsObject());

        var result = await svc.StartLiveAsync("", "", Ct);

        result.IsOk.Should().BeFalse();
        result.Error!.Qr.Should().Be("https://face/qr");
    }

    [Fact]
    public async Task StartLive_FaceVerify_60043_SelfBuilds_Url_With_Mid()
    {
        var (svc, api, sessions, _) = Create();
        LoginAs(sessions, uid: 4242);
        api.StartLiveResult = FakeBilibiliApiClient.Fail(60043, "");

        var result = await svc.StartLiveAsync("", "", Ct);

        result.IsOk.Should().BeFalse();
        result.Error!.Qr.Should().Be(
            "https://www.bilibili.com/blackboard/live/face-auth-middle.html?source_event=400&mid=4242");
    }

    [Fact]
    public async Task StartLive_Other_Error_Throws_With_Original_Code()
    {
        var (svc, api, sessions, _) = Create();
        LoginAs(sessions);
        api.StartLiveResult = FakeBilibiliApiClient.Fail(403, "禁止开播");

        var act = () => svc.StartLiveAsync("", "", Ct);

        (await act.Should().ThrowAsync<BilibiliException>())
            .Where(e => e.Code == 403 && e.Message == "禁止开播");
    }

    [Fact]
    public async Task StartLive_Network_Failure_Throws_Network_Error()
    {
        var (svc, api, sessions, _) = Create();
        LoginAs(sessions);
        api.StartLiveResult = FakeBilibiliApiClient.NetworkFail("timeout");

        var act = () => svc.StartLiveAsync("", "", Ct);

        (await act.Should().ThrowAsync<BilibiliException>())
            .Where(e => e.Code == -1 && e.Message == "网络错误");
    }

    [Fact]
    public async Task StartLive_Unknown_Partition_Throws_Cannot_Recognize()
    {
        var (svc, api, sessions, accounts) = Create();
        LoginAs(sessions);
        SaveCurrent(accounts);
        api.AreaList = OkData("""{"code":0,"data":[]}""");

        var act = () => svc.StartLiveAsync("不存在", "子区", Ct);

        (await act.Should().ThrowAsync<BilibiliException>())
            .Where(e => e.Code == -1 && e.Message == "无法识别分区: 不存在-子区");
    }

    [Fact]
    public async Task StopLive_Success_Clears_Live_Flag()
    {
        var (svc, api, sessions, _) = Create();
        sessions.Replace(new SessionSnapshot { Uid = 42, RoomId = "12345", IsLive = true, BiliJct = new SecureCredential("j") });

        var res = await svc.StopLiveAsync(Ct);

        res.IsSuccess.Should().BeTrue();
        sessions.CurrentSnapshot.IsLive.Should().BeFalse();
        api.CallCount(nameof(FakeBilibiliApiClient.StopLiveAsync)).Should().Be(1);
    }

    [Fact]
    public async Task StopLive_Failure_Returns_Minus_One_Without_Message()
    {
        var (svc, api, sessions, _) = Create();
        sessions.Replace(new SessionSnapshot { Uid = 42, RoomId = "12345", IsLive = true, BiliJct = new SecureCredential("j") });
        api.StopLiveResult = FakeBilibiliApiClient.Fail(-1, "boom");

        var res = await svc.StopLiveAsync(Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("");
        sessions.CurrentSnapshot.IsLive.Should().BeTrue();
    }
}
