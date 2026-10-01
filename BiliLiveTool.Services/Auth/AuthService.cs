using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Services.Live;
using BiliLiveTool.Services.User;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Services.Auth;

/// <summary>
/// 扫码登录，对照原 auth_service.py：生成二维码、轮询状态、
/// data.code==0 时走登录链（装机密 → 房间号 → 全量资料 → 保存 → 刷分区）。
/// 轮询节奏 1500ms 由 UI 层驱动；86038/86090 原样透传；登录链失败原子回滚。
/// </summary>
public sealed class AuthService
{
    private readonly IBilibiliApiClient _api;
    private readonly UserService _user;
    private readonly LiveService _live;
    private readonly AuthSessionStore _sessions;
    private readonly IAccountStore _accounts;
    private readonly ISecretVault _vault;
    private readonly ILogger<AuthService> _log;

    public AuthService(
        IBilibiliApiClient api,
        UserService user,
        LiveService live,
        AuthSessionStore sessions,
        IAccountStore accounts,
        ISecretVault vault,
        ILogger<AuthService> log)
    {
        _api = api;
        _user = user;
        _live = live;
        _sessions = sessions;
        _accounts = accounts;
        _vault = vault;
        _log = log;
    }

    /// <summary>生成登录二维码（对照 get_login_qrcode）。</summary>
    public async Task<ServiceResult> GetLoginQrcodeAsync(CancellationToken ct)
    {
        var res = await _api.GetPassportQrcodeAsync(ct).ConfigureAwait(false);
        return res.Success && res.Code == 0
            ? ServiceResult.Ok(res.Data as JsonObject ?? new JsonObject())
            : ServiceResult.Fail(-1, "");
    }

    /// <summary>
    /// 轮询扫码状态（对照 poll_login_status）：data.code==0 走登录链，
    /// 否则原样透传内层 code/message（过期 86038、已扫 86090）。
    /// </summary>
    public async Task<ServiceResult> PollLoginStatusAsync(string qrcodeKey, CancellationToken ct)
    {
        var res = await _api.PollPassportQrcodeAsync(qrcodeKey, ct).ConfigureAwait(false);
        if (!res.Success)
            return ServiceResult.Fail(-1, "网络请求失败");

        var data = res.Data as JsonObject ?? new JsonObject();
        var innerCode = JsonRead.ReadInt(data["code"]);
        if (innerCode == 0)
        {
            var cookies = res.ResponseCookies ?? EmptyCookies();
            return await CompleteLoginAsync(cookies, ct).ConfigureAwait(false);
        }

        return ServiceResult.Fail(
            innerCode ?? -1,
            JsonRead.ReadText(data["message"]) ?? "");
    }

    // --- 登录链 ---

    private async Task<ServiceResult> CompleteLoginAsync(
        IReadOnlyDictionary<string, string> cookies, CancellationToken ct)
    {
        var uidText = cookies.GetValueOrDefault("DedeUserID");
        if (uidText is null
            || !long.TryParse(uidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
        {
            return ServiceResult.Fail(-1, "登录响应缺少身份信息");
        }

        var secrets = BuildSecrets(cookies);
        var saved = false;
        var prevUid = _accounts.CurrentUid;
        var prevSnapshot = _sessions.CurrentSnapshot;
        var prevSecrets = prevUid is null ? null : _vault.Load(prevUid);

        void Rollback()
        {
            _accounts.SetCurrent(prevUid);
            _sessions.Replace(prevSnapshot);
            _api.UpdateCookies(prevSecrets ?? EmptySecrets());
            if (!saved) DisposeAll(secrets); // 未入 vault 的机密即刻清零
        }

        try
        {
            // 对照原 update_cookies(cookies)：机密从这里开始在客户端生效；
            // 原子化设计不先清状态，失败回滚替代原 clear-first。
            _api.UpdateCookies(secrets);

            var roomId = await _user.FetchRoomIdAsync(uid, ct).ConfigureAwait(false);
            if (roomId == "")
            {
                Rollback();
                return ServiceResult.Fail(-1, "获取直播间ID失败");
            }

            var (ok, full) = await _user.FetchFullUserDataAsync(ct).ConfigureAwait(false);
            if (!ok || full is null)
            {
                Rollback();
                return ServiceResult.Fail(-1, "获取用户信息失败");
            }

            var record = _user.SaveUserData(uidText, full, roomId, secrets);
            saved = true;

            // 对照 _refresh_partitions_internal：失败自吞，不阻断登录
            await _live.RefreshPartitionsAsync(ct).ConfigureAwait(false);
            return ServiceResult.Ok(SerializeRecord(record));
        }
        catch (OperationCanceledException)
        {
            Rollback(); // 取消即失败：同样回滚，未入 vault 的机密清零
            throw;
        }
        catch (Exception e)
        {
            Rollback();
            _log.LogError(e, "Login chain failed");
            return ServiceResult.Fail(-1, e.Message);
        }
    }

    // --- 内部 ---

    private static IReadOnlyDictionary<string, SecureCredential> BuildSecrets(
        IReadOnlyDictionary<string, string> cookies)
    {
        // 全量 cookie 成对封装：身份机密禁止裸 string，其余同权对待
        var secrets = new Dictionary<string, SecureCredential>(StringComparer.Ordinal);
        foreach (var (key, value) in cookies) secrets[key] = new SecureCredential(value);
        return secrets;
    }

    private static IReadOnlyDictionary<string, string> EmptyCookies() =>
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, SecureCredential> EmptySecrets() =>
        new Dictionary<string, SecureCredential>(StringComparer.Ordinal);

    private static void DisposeAll(IReadOnlyDictionary<string, SecureCredential> secrets)
    {
        foreach (var credential in secrets.Values) credential.Dispose();
    }

    private static JsonObject SerializeRecord(AccountRecord record) =>
        JsonSerializer.SerializeToNode(record)!.AsObject();
}
