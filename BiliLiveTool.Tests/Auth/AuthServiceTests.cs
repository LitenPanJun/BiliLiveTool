using System.Text.Json.Nodes;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Infrastructure.Bilibili;
using BiliLiveTool.Services;
using BiliLiveTool.Services.Auth;
using BiliLiveTool.Services.Live;
using BiliLiveTool.Services.User;
using BiliLiveTool.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliLiveTool.Tests.Auth;

public class AuthServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static (AuthService Service, FakeBilibiliApiClient Api, AuthSessionStore Sessions,
        InMemoryAccountStore Accounts, InMemorySecretVault Vault) Create()
    {
        var api = new FakeBilibiliApiClient();
        var sessions = new AuthSessionStore();
        var accounts = new InMemoryAccountStore();
        var vault = new InMemorySecretVault();
        var masker = new SecretMasker();
        var user = new UserService(
            api, sessions, accounts, vault, new FakeDanmuMonitor(), masker,
            NullLogger<UserService>.Instance);
        var live = new LiveService(
            api, sessions, accounts, masker, NullLogger<LiveService>.Instance);
        var service = new AuthService(
            api, user, live, sessions, accounts, vault, NullLogger<AuthService>.Instance);
        return (service, api, sessions, accounts, vault);
    }

    private static void PollOk(FakeBilibiliApiClient api, params (string Key, string Value)[] cookies)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in cookies) dict[key] = value;
        api.QrPoll = new ApiResult(
            true,
            0,
            "",
            JsonNode.Parse("""{"code":0,"message":""}""")!.AsObject(),
            dict);
    }

    private static void PollWithCode(FakeBilibiliApiClient api, int code, string message) =>
        api.QrPoll = new ApiResult(
            true,
            0,
            "",
            JsonNode.Parse($$"""{"code":{{code}},"message":"{{message}}"}""")!.AsObject());

    [Fact]
    public async Task GetLoginQrcode_Returns_Data_On_Success()
    {
        var (svc, api, _, _, _) = Create();
        api.QrGenerate = FakeBilibiliApiClient.Ok(
            new JsonObject { ["url"] = "https://login/qr", ["qrcode_key"] = "key-1" });

        var res = await svc.GetLoginQrcodeAsync(Ct);

        res.IsSuccess.Should().BeTrue();
        res.Data!["qrcode_key"]!.GetValue<string>().Should().Be("key-1");
    }

    [Fact]
    public async Task GetLoginQrcode_Fails_Without_Message()
    {
        var (svc, api, _, _, _) = Create();
        api.QrGenerate = FakeBilibiliApiClient.Fail(-400, "参数错误");

        var res = await svc.GetLoginQrcodeAsync(Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("");
    }

    [Fact]
    public async Task PollLogin_Expired_86038_Passes_Through_Without_Chain()
    {
        var (svc, api, sessions, accounts, _) = Create();
        PollWithCode(api, 86038, "QR expired");

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.Code.Should().Be(86038);
        res.Message.Should().Be("QR expired");
        api.CookieUpdates.Should().BeEmpty(); // 登录链未启动
        accounts.CurrentUid.Should().BeNull();
        sessions.CurrentSnapshot.Uid.Should().Be(0);
    }

    [Fact]
    public async Task PollLogin_Scanned_86090_Passes_Through()
    {
        var (svc, api, _, _, _) = Create();
        PollWithCode(api, 86090, "scanned");

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.Code.Should().Be(86090);
        res.Message.Should().Be("scanned");
    }

    [Fact]
    public async Task PollLogin_Network_Failure_Returns_Request_Error()
    {
        var (svc, api, _, _, _) = Create();
        api.QrPoll = FakeBilibiliApiClient.NetworkFail("timeout");

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("网络请求失败");
    }

    [Fact]
    public async Task PollLogin_Missing_Uid_Fails_Early_Without_Cookies()
    {
        var (svc, api, _, _, _) = Create();
        PollOk(api, ("SESSDATA", "ses"));

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("登录响应缺少身份信息");
        api.CookieUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task PollLogin_Success_Runs_Chain_Saves_And_Refreshes_Partitions()
    {
        var (svc, api, sessions, accounts, vault) = Create();
        PollOk(api,
            ("DedeUserID", "42"),
            ("SESSDATA", "ses-val"),
            ("bili_jct", "jct-val"),
            ("buvid3", "b3-val"));
        api.RoomIdByUid = FakeBilibiliApiClient.Ok(new JsonObject { ["room_id"] = 12345 });
        api.UserInfo = FakeBilibiliApiClient.Ok(new JsonObject { ["uname"] = "阿 tester" });
        api.UserStat = FakeBilibiliApiClient.Ok(new JsonObject { ["follower"] = 9 });
        api.AreaList = new ApiResult(
            true,
            0,
            "",
            JsonNode.Parse("""[{"name":"宅系","list":[{"id":235,"name":"A-SOUL"}]}]"""));

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.IsSuccess.Should().BeTrue();
        res.Data!["uid"]!.GetValue<string>().Should().Be("42");
        res.Data!["roomId"]!.GetValue<string>().Should().Be("12345");
        accounts.CurrentUid.Should().Be("42");
        accounts.Get("42")!.Uname.Should().Be("阿 tester");
        vault.Load("42")!["SESSDATA"]!.DangerousGetValue().Should().Be("ses-val");
        api.CookieUpdates[^1].Keys.Should().Contain("DedeUserID");

        var snapshot = sessions.CurrentSnapshot;
        snapshot.Uid.Should().Be(42);
        snapshot.RoomId.Should().Be("12345");
        snapshot.Csrf!.DangerousGetValue().Should().Be("jct-val");
        sessions.ResolveAreaId("宅系", "A-SOUL").Should().Be(235);
    }

    [Fact]
    public async Task PollLogin_Empty_RoomId_Fails_And_Rolls_Back()
    {
        var (svc, api, sessions, accounts, _) = Create();
        PollOk(api, ("DedeUserID", "42"), ("bili_jct", "jct"));
        api.RoomIdByUid = FakeBilibiliApiClient.Fail(-1, "boom");
        api.UserInfo = FakeBilibiliApiClient.Fail(-1, "boom");

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("获取直播间ID失败");
        accounts.CurrentUid.Should().BeNull();
        sessions.CurrentSnapshot.Uid.Should().Be(0);
        api.CookieUpdates.Should().HaveCount(2); // 装入 + 回滚清空
        api.CookieUpdates[^1].Should().BeEmpty();
    }

    [Fact]
    public async Task PollLogin_Fetch_Full_Fails_And_Rolls_Back()
    {
        var (svc, api, _, accounts, _) = Create();
        PollOk(api, ("DedeUserID", "42"));
        api.RoomIdByUid = FakeBilibiliApiClient.Ok(new JsonObject { ["room_id"] = 12345 });
        api.UserInfo = FakeBilibiliApiClient.Fail(-1, "boom");

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("获取用户信息失败");
        accounts.CurrentUid.Should().BeNull();
        api.CookieUpdates[^1].Should().BeEmpty();
    }

    [Fact]
    public async Task PollLogin_Room_Raised_Message_Surfaces_And_Rolls_Back()
    {
        var (svc, api, _, accounts, _) = Create();
        PollOk(api, ("DedeUserID", "42"));
        api.RoomIdByUid = FakeBilibiliApiClient.Fail(404, "not found");

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.Code.Should().Be(-1);
        res.Message.Should().Be("该账号未开通直播间，请先去B站开通。");
        accounts.CurrentUid.Should().BeNull();
        api.CookieUpdates[^1].Should().BeEmpty();
    }

    [Fact]
    public async Task PollLogin_Rollback_Restores_Previous_Account_Cookies()
    {
        var (svc, api, _, accounts, vault) = Create();
        accounts.Save(new AccountRecord { Uid = "1", RoomId = "111" });
        var prevSecrets = new Dictionary<string, SecureCredential>
        {
            ["bili_jct"] = new("prev-jct"),
        };
        vault.Save("1", prevSecrets);

        PollOk(api, ("DedeUserID", "42"));
        api.RoomIdByUid = FakeBilibiliApiClient.Fail(-1, "boom");
        api.UserInfo = FakeBilibiliApiClient.Fail(-1, "boom");

        var res = await svc.PollLoginStatusAsync("key-1", Ct);

        res.IsSuccess.Should().BeFalse();
        accounts.CurrentUid.Should().Be("1");
        api.CookieUpdates[^1]["bili_jct"].Should().BeSameAs(prevSecrets["bili_jct"]);
    }
}
