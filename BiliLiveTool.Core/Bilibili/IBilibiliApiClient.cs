using BiliLiveTool.Core.Security;

namespace BiliLiveTool.Core.Bilibili;

/// <summary>
/// B站 REST 薄封装接口，方法名与原 Python BilibiliApi 一一对应（异步加 Async 后缀）。
/// URL、参数名与签名语义按 API 冻结清单实现，不得增删改。
/// </summary>
public interface IBilibiliApiClient
{
    /// <summary>更新随请求透传的 Cookie（值为 SecureCredential，禁止裸字符串）。</summary>
    void UpdateCookies(IReadOnlyDictionary<string, SecureCredential> cookies);

    // --- 扫码登录 ---
    Task<ApiResult> GetPassportQrcodeAsync(CancellationToken ct);
    Task<ApiResult> PollPassportQrcodeAsync(string qrcodeKey, CancellationToken ct);

    // --- 用户信息 ---
    Task<ApiResult> GetUserInfoAsync(CancellationToken ct);
    Task<ApiResult> GetUserStatAsync(CancellationToken ct);
    Task<ApiResult> GetRoomIdByUidAsync(long uid, CancellationToken ct);

    // --- 直播管理 ---
    Task<ApiResult> GetAreaListAsync(CancellationToken ct);
    Task<ApiResult> GetRoomInfoAsync(string roomId, CancellationToken ct);
    Task<ApiResult> GetRoomNewsAsync(string roomId, long uid, CancellationToken ct);
    Task<ApiResult> UpdateTitleAsync(string roomId, string title, SecureCredential csrf, CancellationToken ct);
    Task<ApiResult> UpdateAnnouncementAsync(string roomId, long uid, string content, SecureCredential csrf, CancellationToken ct);
    Task<ApiResult> UpdateAreaAsync(string roomId, int areaId, SecureCredential csrf, CancellationToken ct);
    Task<ApiResult> StartLiveAsync(string roomId, int areaId, SecureCredential csrf, CancellationToken ct);
    Task<ApiResult> StopLiveAsync(string roomId, SecureCredential csrf, CancellationToken ct);

    // --- 弹幕 ---
    Task<ApiResult> SendDanmuAsync(string roomId, string msg, SecureCredential csrf, CancellationToken ct);
    Task<ApiResult> GetDanmuInfoAsync(string roomId, CancellationToken ct);

    // --- buvid3 ---
    Task<string?> GetBuvid3Async(CancellationToken ct);
}
