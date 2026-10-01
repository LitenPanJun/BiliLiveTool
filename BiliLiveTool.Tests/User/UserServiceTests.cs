using System.Text.Json.Nodes;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Core.State;
using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.Services;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Tests.Fakes;
using BiliLiveTool.Services.User;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliLiveTool.Tests.User;

public class UserServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static (UserService Service, FakeBilibiliApiClient Api, AuthSessionStore Sessions,
        InMemoryAccountStore Accounts, InMemorySecretVault Vault, FakeDanmuMonitor Danmu) Create()
    {
        var api = new FakeBilibiliApiClient();
        var sessions = new AuthSessionStore();
        var accounts = new InMemoryAccountStore();
        var vault = new InMemorySecretVault();
        var danmu = new FakeDanmuMonitor();
        var service = new UserService(
            api, sessions, accounts, vault, danmu, new SecretMasker(), NullLogger<UserService>.Instance);
        return (service, api, sessions, accounts, vault, danmu);
    }

    private static Dictionary<string, SecureCredential> Secrets(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, SecureCredential>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs) dict[key] = new SecureCredential(value);
        return dict;
    }

    private static JsonObject FullData(string uname = "阿 tester") =>
        JsonNode.Parse($$"""
            {"uname":"{{uname}}","face":"https://x/f.png","money":88,
             "level_info":{"current_level":4,"current_exp":500,"next_exp":1000},
             "wallet":{"bcoin_balance":1.5},
             "stat":{"following":10,"follower":20,"dynamic_count":3}
            }
            """)!.AsObject();

    // --- SaveUserData ---

    [Fact]
    public void SaveUserData_Maps_Full_Data_And_Preserves_Last_Fields()
    {
        var (svc, _, sessions, accounts, vault, _) = Create();
        accounts.Save(new AccountRecord
        {
            Uid = "42",
            LastTitle = "旧标题",
            LastAreaId = 3259,
            LastAreaName = ["宅系", "虚拟主播"],
            LastAnnouncement = "旧公告",
        });
        accounts.SetCurrent(null);
        var secrets = Secrets(("SESSDATA", "ses-val"), ("bili_jct", "jct-val"));

        var record = svc.SaveUserData("42", FullData(), "12345", secrets);

        record.Uname.Should().Be("阿 tester");
        record.Face.Should().Be("https://x/f.png");
        record.RoomId.Should().Be("12345");
        record.Level.Should().Be(4);
        record.CurrentExp.Should().Be(500);
        record.NextExp.Should().Be(1000);
        record.Money.Should().Be(88);
        record.Bcoin.Should().Be(1.5);
        record.Following.Should().Be(10);
        record.Follower.Should().Be(20);
        record.DynamicCount.Should().Be(3);
        record.LastTitle.Should().Be("旧标题");
        record.LastAreaId.Should().Be(3259);
        record.LastAreaName.Should().Equal("宅系", "虚拟主播");
        record.LastAnnouncement.Should().Be("旧公告");

        accounts.CurrentUid.Should().Be("42");
        vault.Load("42")!["SESSDATA"].Should().BeSameAs(secrets["SESSDATA"]);

        var snapshot = sessions.CurrentSnapshot;
        snapshot.Uid.Should().Be(42);
        snapshot.RoomId.Should().Be("12345");
        snapshot.Csrf!.DangerousGetValue().Should().Be("jct-val");
        snapshot.CurrentAreaId.Should().Be(3259);
        snapshot.IsLive.Should().BeFalse();
    }

    [Fact]
    public void SaveUserData_Fresh_Account_Gets_Default_Last_Fields()
    {
        var (svc, _, _, accounts, _, _) = Create();

        var record = svc.SaveUserData("7", FullData("新人"), "1", Secrets(("bili_jct", "j")));

        record.LastTitle.Should().Be("");
        record.LastAreaId.Should().BeNull();
        record.LastAreaName.Should().BeEmpty();
        record.LastAnnouncement.Should().Be("");
        accounts.CurrentUid.Should().Be("7");
    }

    // --- FetchFullUserData ---

    [Fact]
    public async Task FetchFullUserData_Merges_Stat_Into_Full()
    {
        var (svc, api, _, _, _, _) = Create();
        api.UserInfo = FakeBilibiliApiClient.Ok(new JsonObject { ["uname"] = "阿 tester" });
        api.UserStat = FakeBilibiliApiClient.Ok(new JsonObject { ["following"] = 7 });

        var (ok, full) = await svc.FetchFullUserDataAsync(Ct);

        ok.Should().BeTrue();
        full!["uname"]!.GetValue<string>().Should().Be("阿 tester");
        full["stat"]!["following"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task FetchFullUserData_Fails_On_Nav_Error_And_Empties_Stat_On_Stat_Error()
    {
        var (svc, api, _, _, _, _) = Create();
        api.UserInfo = FakeBilibiliApiClient.Fail(-1, "boom");
        (await svc.FetchFullUserDataAsync(Ct)).Ok.Should().BeFalse();

        api.UserInfo = FakeBilibiliApiClient.Ok(new JsonObject { ["uname"] = "x" });
        api.UserStat = FakeBilibiliApiClient.NetworkFail("timeout");
        var (ok, full) = await svc.FetchFullUserDataAsync(Ct);

        ok.Should().BeTrue();
        full!["stat"]!.AsObject().Should().BeEmpty();
    }

    // --- FetchRoomId ---

    [Fact]
    public async Task FetchRoomId_Uses_RoomId_By_Uid_First()
    {
        var (svc, api, _, _, _, _) = Create();
        api.RoomIdByUid = FakeBilibiliApiClient.Ok(new JsonObject { ["room_id"] = 12345 });

        (await svc.FetchRoomIdAsync(42, Ct)).Should().Be("12345");
        api.RoomIdByUidValue.Should().Be(42);
    }

    [Fact]
    public async Task FetchRoomId_404_Raises_Not_Dev_Live()
    {
        var (svc, api, _, _, _, _) = Create();
        api.RoomIdByUid = FakeBilibiliApiClient.Fail(404, "not found");

        var act = () => svc.FetchRoomIdAsync(42, Ct);

        (await act.Should().ThrowAsync<BilibiliException>())
            .WithMessage("该账号未开通直播间，请先去B站开通。");
    }

    [Fact]
    public async Task FetchRoomId_Falls_Back_To_Nav_Live_Room()
    {
        var (svc, api, _, _, _, _) = Create();
        api.RoomIdByUid = FakeBilibiliApiClient.Fail(-1, "boom");
        api.UserInfo = FakeBilibiliApiClient.Ok(
            new JsonObject { ["live_room"] = new JsonObject { ["roomid"] = 6789 } });

        (await svc.FetchRoomIdAsync(42, Ct)).Should().Be("6789");
    }

    [Fact]
    public async Task FetchRoomId_Nav_Room_Zero_Raises()
    {
        var (svc, api, _, _, _, _) = Create();
        api.RoomIdByUid = FakeBilibiliApiClient.Fail(-1, "boom");
        api.UserInfo = FakeBilibiliApiClient.Ok(
            new JsonObject { ["live_room"] = new JsonObject { ["roomid"] = 0 } });

        var act = () => svc.FetchRoomIdAsync(42, Ct);

        (await act.Should().ThrowAsync<BilibiliException>())
            .WithMessage("该账号未开通直播间。");
    }

    [Fact]
    public async Task FetchRoomId_Returns_Empty_When_All_Fallbacks_Fail()
    {
        var (svc, api, _, _, _, _) = Create();
        api.RoomIdByUid = FakeBilibiliApiClient.NetworkFail("timeout");
        api.UserInfo = FakeBilibiliApiClient.Fail(-1, "boom");

        (await svc.FetchRoomIdAsync(42, Ct)).Should().Be("");
    }

    // --- InitCurrentUser ---

    [Fact]
    public void InitCurrentUser_Restores_Session_And_Cookies()
    {
        var (svc, api, sessions, accounts, vault, _) = Create();
        accounts.Save(new AccountRecord
        {
            Uid = "42",
            RoomId = "12345",
            LastAreaId = 3259,
            LastAreaName = ["宅系", "虚拟主播"],
        });
        var secrets = Secrets(("SESSDATA", "ses-val"), ("bili_jct", "jct-val"));
        vault.Save("42", secrets);

        svc.InitCurrentUser();

        var snapshot = sessions.CurrentSnapshot;
        snapshot.Uid.Should().Be(42);
        snapshot.RoomId.Should().Be("12345");
        snapshot.CurrentAreaId.Should().Be(3259);
        snapshot.CurrentAreaNames.Should().Equal("宅系", "虚拟主播");
        snapshot.Csrf!.DangerousGetValue().Should().Be("jct-val");
        api.CookieUpdates.Should().HaveCount(1);
        api.CookieUpdates[0]["SESSDATA"].Should().BeSameAs(secrets["SESSDATA"]);
    }

    [Fact]
    public void InitCurrentUser_Clears_Session_Without_Login()
    {
        var (svc, api, sessions, _, _, _) = Create();
        sessions.Replace(new SessionSnapshot { Uid = 9, RoomId = "1" });

        svc.InitCurrentUser();

        sessions.CurrentSnapshot.Should().Be(SessionSnapshot.Empty);
        api.CookieUpdates.Should().BeEmpty();
    }

    // --- 配置 / 刷新 / 列表 ---

    [Fact]
    public void LoadSavedConfig_Returns_Record_Or_Empty()
    {
        var (svc, _, _, accounts, _, _) = Create();
        svc.LoadSavedConfig().Data.Should().BeEmpty();

        accounts.Save(new AccountRecord { Uid = "42", RoomId = "12345" });
        var res = svc.LoadSavedConfig();

        res.IsSuccess.Should().BeTrue();
        res.Data!["uid"]!.GetValue<string>().Should().Be("42");
        res.Data!["roomId"]!.GetValue<string>().Should().Be("12345");
        res.Data!.AsObject().Should().NotContainKeys("cookie", "csrf"); // 安全硬规则：不回传机密
    }

    [Fact]
    public void GetAccountList_Returns_List_And_Current()
    {
        var (svc, _, _, accounts, _, _) = Create();
        accounts.Save(new AccountRecord { Uid = "1" });
        accounts.Upsert(new AccountRecord { Uid = "2" });

        var res = svc.GetAccountList();

        res.Data!["list"]!.AsArray().Should().HaveCount(2);
        res.Data!["current_uid"]!.GetValue<string>().Should().Be("1");
    }

    [Fact]
    public async Task RefreshCurrentUser_Guards_Without_Login()
    {
        var (svc, _, _, _, _, _) = Create();

        var res = await svc.RefreshCurrentUserAsync(Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("未登录");
    }

    [Fact]
    public async Task RefreshCurrentUser_Fails_When_Fetch_Fails()
    {
        var (svc, api, _, accounts, _, _) = Create();
        accounts.Save(new AccountRecord { Uid = "42", RoomId = "12345" });
        api.UserInfo = FakeBilibiliApiClient.Fail(-1, "boom");

        var res = await svc.RefreshCurrentUserAsync(Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("刷新失败");
    }

    [Fact]
    public async Task RefreshCurrentUser_Saves_Fresh_Data_And_Returns_Record()
    {
        var (svc, api, _, accounts, vault, _) = Create();
        accounts.Save(new AccountRecord { Uid = "42", RoomId = "12345", LastTitle = "保留标题" });
        vault.Save("42", Secrets(("bili_jct", "j")));
        api.UserInfo = FakeBilibiliApiClient.Ok(new JsonObject { ["uname"] = "新名字" });
        api.UserStat = FakeBilibiliApiClient.Ok(new JsonObject { ["follower"] = 99 });

        var res = await svc.RefreshCurrentUserAsync(Ct);

        res.IsSuccess.Should().BeTrue();
        res.Data!["uname"]!.GetValue<string>().Should().Be("新名字");
        var record = accounts.Get("42")!;
        record.Uname.Should().Be("新名字");
        record.Follower.Should().Be(99);
        record.RoomId.Should().Be("12345");
        record.LastTitle.Should().Be("保留标题");
    }

    // --- 切换 / 登出 ---

    [Fact]
    public async Task SwitchAccount_Stops_Danmu_Before_Init_And_Loads_Target_Secrets()
    {
        var (svc, api, sessions, accounts, vault, danmu) = Create();
        accounts.Save(new AccountRecord { Uid = "1", RoomId = "111" });
        accounts.Upsert(new AccountRecord { Uid = "2", RoomId = "222", LastAreaId = 235 });
        vault.Save("1", Secrets(("bili_jct", "j1")));
        vault.Save("2", Secrets(("SESSDATA", "ses2"), ("bili_jct", "j2")));

        var res = await svc.SwitchAccountAsync("2", Ct);

        res.IsSuccess.Should().BeTrue();
        danmu.StopCount.Should().Be(1);
        accounts.CurrentUid.Should().Be("2");
        var snapshot = sessions.CurrentSnapshot;
        snapshot.Uid.Should().Be(2);
        snapshot.RoomId.Should().Be("222");
        snapshot.CurrentAreaId.Should().Be(235);
        api.CookieUpdates[^1]["bili_jct"]!.DangerousGetValue().Should().Be("j2");
    }

    [Fact]
    public async Task SwitchAccount_Unknown_Uid_Fails_Without_Stop()
    {
        var (svc, _, sessions, accounts, _, danmu) = Create();
        accounts.Save(new AccountRecord { Uid = "1" });

        var res = await svc.SwitchAccountAsync("nope", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("账户不存在");
        danmu.StopCount.Should().Be(0);
        accounts.CurrentUid.Should().Be("1");
        sessions.CurrentSnapshot.Uid.Should().Be(0);
    }

    [Fact]
    public async Task SwitchAccount_Stop_Failure_Aborts_Without_Mutation()
    {
        var (svc, _, sessions, accounts, _, danmu) = Create();
        accounts.Save(new AccountRecord { Uid = "1" });
        accounts.Upsert(new AccountRecord { Uid = "2" });
        accounts.SetCurrent("1");
        var before = sessions.CurrentSnapshot;
        danmu.FailWith = new InvalidOperationException("ws busy");

        var res = await svc.SwitchAccountAsync("2", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("ws busy");
        danmu.StopCount.Should().Be(1);
        accounts.CurrentUid.Should().Be("1");
        sessions.CurrentSnapshot.Should().BeSameAs(before);
    }

    [Fact]
    public async Task Logout_Current_Account_Stops_Danmu_Clears_Session_And_Vault()
    {
        var (svc, api, sessions, accounts, vault, danmu) = Create();
        accounts.Save(new AccountRecord { Uid = "42", RoomId = "12345" });
        vault.Save("42", Secrets(("SESSDATA", "ses"), ("bili_jct", "j")));
        svc.InitCurrentUser();
        sessions.CurrentSnapshot.Uid.Should().Be(42);

        var res = await svc.LogoutAsync("42", Ct);

        res.IsSuccess.Should().BeTrue();
        danmu.StopCount.Should().Be(1);
        accounts.Get("42").Should().BeNull();
        accounts.CurrentUid.Should().BeNull();
        sessions.CurrentSnapshot.Should().Be(SessionSnapshot.Empty);
        vault.Load("42").Should().BeNull();
        api.CookieUpdates[^1].Should().BeEmpty();
    }

    [Fact]
    public async Task Logout_Other_Account_Stops_Danmu_But_Keeps_Session()
    {
        var (svc, api, sessions, accounts, vault, danmu) = Create();
        accounts.Save(new AccountRecord { Uid = "1" });
        accounts.Upsert(new AccountRecord { Uid = "2" });
        vault.Save("1", Secrets(("bili_jct", "j1")));
        vault.Save("2", Secrets(("bili_jct", "j2")));
        svc.InitCurrentUser();
        var cookieCallsBefore = api.CookieUpdates.Count;

        var res = await svc.LogoutAsync("2", Ct);

        res.IsSuccess.Should().BeTrue();
        danmu.StopCount.Should().Be(1); // 上游门面无条件先停
        accounts.Get("2").Should().BeNull();
        accounts.CurrentUid.Should().Be("1");
        sessions.CurrentSnapshot.Uid.Should().Be(1);
        vault.Load("2").Should().BeNull();
        vault.Load("1").Should().NotBeNull();
        api.CookieUpdates.Count.Should().Be(cookieCallsBefore);
    }

    [Fact]
    public async Task Logout_Unknown_Uid_Fails_Without_Stop()
    {
        var (svc, _, _, accounts, _, danmu) = Create();
        accounts.Save(new AccountRecord { Uid = "1" });

        var res = await svc.LogoutAsync("nope", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("账户不存在");
        danmu.StopCount.Should().Be(0);
        accounts.CurrentUid.Should().Be("1");
    }
}
