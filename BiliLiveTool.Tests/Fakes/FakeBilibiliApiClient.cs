using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Security;

namespace BiliLiveTool.Tests.Fakes;

/// <summary>服务层测试用假客户端：逐方法可设响应并记录调用参数。</summary>
internal sealed class FakeBilibiliApiClient : IBilibiliApiClient
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

    public static ApiResult Ok(JsonObject? data = null) => new(true, 0, "", data ?? new JsonObject());

    public static ApiResult Fail(int code, string message) => new(true, code, message, null);

    public static ApiResult NetworkFail(string message) => new(false, -1, message, null);

    public ApiResult QrGenerate { get; set; } = Ok();
    public ApiResult QrPoll { get; set; } = Ok();
    public ApiResult UserInfo { get; set; } = Ok();
    public ApiResult UserStat { get; set; } = Ok();
    public ApiResult RoomIdByUid { get; set; } = Ok();
    public ApiResult AreaList { get; set; } = Ok();
    public ApiResult RoomInfo { get; set; } = Ok();
    public ApiResult RoomNews { get; set; } = Ok();
    public ApiResult UpdateTitleResult { get; set; } = Ok();
    public ApiResult UpdateAnnouncementResult { get; set; } = Ok();
    public ApiResult UpdateAreaResult { get; set; } = Ok();
    public ApiResult StartLiveResult { get; set; } = Ok();
    public ApiResult StopLiveResult { get; set; } = Ok();
    public ApiResult SendDanmuResult { get; set; } = Ok();
    public ApiResult DanmuInfoResult { get; set; } = Ok();
    public string? Buvid3 { get; set; }

    // 调用参数记录
    public string? StartLiveRoomId { get; private set; }
    public int StartLiveAreaId { get; private set; }
    public int UpdateAreaAreaId { get; private set; }
    public string? UpdateTitleTitle { get; private set; }
    public string? UpdateAnnouncementContent { get; private set; }
    public long? RoomIdByUidValue { get; private set; }
    public string? SendDanmuRoomId { get; private set; }
    public string? SendDanmuMessage { get; private set; }
    public string? DanmuInfoRoomId { get; private set; }
    public List<IReadOnlyDictionary<string, SecureCredential>> CookieUpdates { get; } = [];

    public int CallCount(string method) => _calls.GetValueOrDefault(method);

    private void Count([CallerMemberName] string? method = null)
    {
        if (method is not null) _calls[method] = CallCount(method) + 1;
    }

    public void UpdateCookies(IReadOnlyDictionary<string, SecureCredential> cookies)
    {
        Count();
        CookieUpdates.Add(cookies);
    }

    public Task<ApiResult> GetPassportQrcodeAsync(CancellationToken ct)
    {
        Count();
        return Task.FromResult(QrGenerate);
    }

    public Task<ApiResult> PollPassportQrcodeAsync(string qrcodeKey, CancellationToken ct)
    {
        Count();
        return Task.FromResult(QrPoll);
    }

    public Task<ApiResult> GetUserInfoAsync(CancellationToken ct)
    {
        Count();
        return Task.FromResult(UserInfo);
    }

    public Task<ApiResult> GetUserStatAsync(CancellationToken ct)
    {
        Count();
        return Task.FromResult(UserStat);
    }

    public Task<ApiResult> GetRoomIdByUidAsync(long uid, CancellationToken ct)
    {
        Count();
        RoomIdByUidValue = uid;
        return Task.FromResult(RoomIdByUid);
    }

    public Task<ApiResult> GetAreaListAsync(CancellationToken ct)
    {
        Count();
        return Task.FromResult(AreaList);
    }

    public Task<ApiResult> GetRoomInfoAsync(string roomId, CancellationToken ct)
    {
        Count();
        return Task.FromResult(RoomInfo);
    }

    public Task<ApiResult> GetRoomNewsAsync(string roomId, long uid, CancellationToken ct)
    {
        Count();
        return Task.FromResult(RoomNews);
    }

    public Task<ApiResult> UpdateTitleAsync(
        string roomId, string title, SecureCredential csrf, CancellationToken ct)
    {
        Count();
        UpdateTitleTitle = title;
        return Task.FromResult(UpdateTitleResult);
    }

    public Task<ApiResult> UpdateAnnouncementAsync(
        string roomId, long uid, string content, SecureCredential csrf, CancellationToken ct)
    {
        Count();
        UpdateAnnouncementContent = content;
        return Task.FromResult(UpdateAnnouncementResult);
    }

    public Task<ApiResult> UpdateAreaAsync(
        string roomId, int areaId, SecureCredential csrf, CancellationToken ct)
    {
        Count();
        UpdateAreaAreaId = areaId;
        return Task.FromResult(UpdateAreaResult);
    }

    public Task<ApiResult> StartLiveAsync(
        string roomId, int areaId, SecureCredential csrf, CancellationToken ct)
    {
        Count();
        StartLiveRoomId = roomId;
        StartLiveAreaId = areaId;
        return Task.FromResult(StartLiveResult);
    }

    public Task<ApiResult> StopLiveAsync(string roomId, SecureCredential csrf, CancellationToken ct)
    {
        Count();
        return Task.FromResult(StopLiveResult);
    }

    public Task<ApiResult> SendDanmuAsync(
        string roomId, string msg, SecureCredential csrf, CancellationToken ct)
    {
        Count();
        SendDanmuRoomId = roomId;
        SendDanmuMessage = msg;
        return Task.FromResult(SendDanmuResult);
    }

    public Task<ApiResult> GetDanmuInfoAsync(string roomId, CancellationToken ct)
    {
        Count();
        DanmuInfoRoomId = roomId;
        return Task.FromResult(DanmuInfoResult);
    }

    public Task<string?> GetBuvid3Async(CancellationToken ct)
    {
        Count();
        return Task.FromResult(Buvid3);
    }
}
