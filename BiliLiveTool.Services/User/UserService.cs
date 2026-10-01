using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Danmu;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Core.State;
using BiliLiveTool.Services.Auth;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Services.User;

/// <summary>
/// 账号与登录链服务，对照原 user_service.py：初始化/保存/刷新当前账号、
/// 房间号回退获取、账号列表、切换与登出。机密一律经 <see cref="ISecretVault"/>，
/// 记录不含 cookie/csrf（安全硬规则优先于原 new_data）。
/// </summary>
public sealed class UserService
{
    private readonly IBilibiliApiClient _api;
    private readonly AuthSessionStore _sessions;
    private readonly IAccountStore _accounts;
    private readonly ISecretVault _vault;
    private readonly IDanmuMonitor _danmu;
    private readonly ISecretMasker _masker;
    private readonly ILogger<UserService> _log;

    public UserService(
        IBilibiliApiClient api,
        AuthSessionStore sessions,
        IAccountStore accounts,
        ISecretVault vault,
        IDanmuMonitor danmu,
        ISecretMasker masker,
        ILogger<UserService> log)
    {
        _api = api;
        _sessions = sessions;
        _accounts = accounts;
        _vault = vault;
        _danmu = danmu;
        _masker = masker;
        _log = log;
    }

    // --- 登录链（AuthService 调用） ---

    /// <summary>取 nav 全量资料并入 stat（对照 fetch_full_user_data）。</summary>
    public async Task<(bool Ok, JsonObject? Full)> FetchFullUserDataAsync(CancellationToken ct)
    {
        _log.LogDebug("Fetching full user data...");
        var nav = await _api.GetUserInfoAsync(ct).ConfigureAwait(false);
        if (!nav.Success || nav.Code != 0)
        {
            _log.LogWarning("Failed to fetch user info: code={Code} msg={Message}", nav.Code, nav.Message);
            return (false, null);
        }

        var stat = await _api.GetUserStatAsync(ct).ConfigureAwait(false);
        var full = nav.Data as JsonObject ?? new JsonObject();
        full["stat"] = (stat.Success && stat.Code == 0)
            ? stat.Data as JsonObject ?? new JsonObject()
            : new JsonObject();
        return (true, full);
    }

    /// <summary>
    /// 取房间号（对照 fetch_room_id）：先按 uid 查，404 抛未开通提示；
    /// 其余（含网络失败）回退 nav.live_room，roomid=="0" 抛未开通；
    /// 两条路都失败返回 ""。
    /// </summary>
    public async Task<string> FetchRoomIdAsync(long uid, CancellationToken ct)
    {
        var masked = _masker.MaskString(uid.ToString(CultureInfo.InvariantCulture), 2, 2);
        _log.LogDebug("Fetching room id for uid: {Uid}", masked);

        var res = await _api.GetRoomIdByUidAsync(uid, ct).ConfigureAwait(false);
        if (res.Success && res.Code == 0)
        {
            return JsonRead.ReadLong((res.Data as JsonObject)?["room_id"]) is { } roomId
                ? roomId.ToString(CultureInfo.InvariantCulture)
                : "";
        }

        if (res.Success && res.Code == 404)
            throw new BilibiliException(-1, "该账号未开通直播间，请先去B站开通。");

        var nav = await _api.GetUserInfoAsync(ct).ConfigureAwait(false);
        if (nav.Success && nav.Code == 0)
        {
            var room = (nav.Data as JsonObject)?["live_room"] as JsonObject;
            var rid = JsonRead.ReadText(room?["roomid"]) ?? "";
            if (rid == "0")
                throw new BilibiliException(-1, "该账号未开通直播间。");
            return rid;
        }

        return "";
    }

    /// <summary>
    /// 保存账号并置为当前（对照 save_user_data）：非机密字段取自 full，
    /// 旧 last_* 字段保留；机密入 vault，快照就地更新（保活 is_live）。
    /// </summary>
    public AccountRecord SaveUserData(
        string uid,
        JsonObject full,
        string roomId,
        IReadOnlyDictionary<string, SecureCredential> secrets)
    {
        _log.LogInformation("Saving user data for uid: {Uid}", _masker.MaskString(uid, 2, 2));

        var previous = _accounts.Get(uid);
        var levelInfo = full["level_info"] as JsonObject ?? new JsonObject();
        var wallet = full["wallet"] as JsonObject ?? new JsonObject();
        var stat = full["stat"] as JsonObject ?? new JsonObject();

        var record = new AccountRecord
        {
            Uid = uid,
            Uname = JsonRead.ReadText(full["uname"]) ?? "未知用户",
            Face = JsonRead.ReadText(full["face"]) ?? "",
            RoomId = roomId,
            Level = JsonRead.ReadInt(levelInfo["current_level"]) ?? 0,
            CurrentExp = JsonRead.ReadLong(levelInfo["current_exp"]) ?? 0,
            NextExp = JsonRead.ReadLong(levelInfo["next_exp"]) ?? 0,
            Money = JsonRead.ReadDouble(full["money"]),
            Bcoin = JsonRead.ReadDouble(wallet["bcoin_balance"]),
            Following = JsonRead.ReadInt(stat["following"]) ?? 0,
            Follower = JsonRead.ReadInt(stat["follower"]) ?? 0,
            DynamicCount = JsonRead.ReadInt(stat["dynamic_count"]) ?? 0,
            LastTitle = previous?.LastTitle ?? "",
            LastAreaId = previous?.LastAreaId,
            LastAreaName = previous?.LastAreaName ?? [],
            LastAnnouncement = previous?.LastAnnouncement ?? "",
        };

        _accounts.Save(record);
        _vault.Save(uid, secrets);

        long.TryParse(uid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUid);
        _sessions.Replace(_sessions.CurrentSnapshot with
        {
            Uid = parsedUid,
            RoomId = roomId,
            SesData = Secret(secrets, "SESSDATA"),
            BiliJct = Secret(secrets, "bili_jct"),
            Buvid3 = Secret(secrets, "buvid3"),
            CurrentAreaId = record.LastAreaId,
            CurrentAreaNames = record.LastAreaName,
        });

        return record;
    }

    // --- 启动恢复 ---

    /// <summary>按当前账号恢复会话与 cookies（对照 init_current_user）。</summary>
    public void InitCurrentUser()
    {
        var uid = _accounts.CurrentUid;
        if (uid is not null && _accounts.Get(uid) is { } record)
        {
            _log.LogInformation(
                "Init user: {Uname} ({Uid})", record.Uname, _masker.MaskString(uid, 2, 2));

            var secrets = _vault.Load(uid);
            _api.UpdateCookies(secrets ?? EmptySecrets());

            long.TryParse(uid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUid);
            _sessions.Replace(new SessionSnapshot
            {
                Uid = parsedUid,
                RoomId = record.RoomId,
                SesData = Secret(secrets, "SESSDATA"),
                BiliJct = Secret(secrets, "bili_jct"),
                Buvid3 = Secret(secrets, "buvid3"),
                CurrentAreaId = record.LastAreaId,
                CurrentAreaNames = record.LastAreaName,
            });
        }
        else
        {
            _log.LogInformation("No current user found, clearing session state.");
            _sessions.Replace(SessionSnapshot.Empty);
        }
    }

    // --- UI 门面（对照 api_service 的 user 代理方法） ---

    public ServiceResult LoadSavedConfig()
    {
        var uid = _accounts.CurrentUid;
        var record = uid is null ? null : _accounts.Get(uid);
        return ServiceResult.Ok(record is null ? new JsonObject() : SerializeRecord(record));
    }

    public async Task<ServiceResult> RefreshCurrentUserAsync(CancellationToken ct)
    {
        _log.LogInformation("Refreshing current user...");
        var uid = _accounts.CurrentUid;
        if (uid is null || _accounts.Get(uid) is not { } record)
        {
            _log.LogWarning("Refresh failed: No user logged in.");
            return ServiceResult.Fail(-1, "未登录");
        }

        var (ok, full) = await FetchFullUserDataAsync(ct).ConfigureAwait(false);
        if (!ok || full is null)
            return ServiceResult.Fail(-1, "刷新失败");

        var secrets = _vault.Load(uid) ?? EmptySecrets();
        var saved = SaveUserData(uid, full, record.RoomId, secrets);
        return ServiceResult.Ok(SerializeRecord(saved));
    }

    public ServiceResult GetAccountList()
    {
        var list = new JsonArray(_accounts.List().Select(a => (JsonNode?)SerializeRecord(a)).ToArray());
        var data = new JsonObject
        {
            ["list"] = list,
            ["current_uid"] = JsonValue.Create(_accounts.CurrentUid),
        };
        return ServiceResult.Ok(data);
    }

    /// <summary>
    /// 切换账号（对照 switch_account + 指南步骤 5）：先 await 停弹幕，
    /// 再置当前并重建会话；失败回滚旧指针、快照与 cookies。
    /// </summary>
    public async Task<ServiceResult> SwitchAccountAsync(string uid, CancellationToken ct)
    {
        var masked = _masker.MaskString(uid, 2, 2);
        _log.LogInformation("Switching account to: {Uid}", masked);

        var record = _accounts.Get(uid);
        if (record is null)
        {
            _log.LogWarning("Switch account failed: User {Uid} not found.", masked);
            return ServiceResult.Fail(-1, "账户不存在");
        }

        try
        {
            await _danmu.StopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Switch account aborted for {Uid}", masked);
            return ServiceResult.Fail(-1, e.Message);
        }

        var prevUid = _accounts.CurrentUid;
        var prevSnapshot = _sessions.CurrentSnapshot;
        var prevSecrets = prevUid is null ? null : _vault.Load(prevUid);

        try
        {
            _accounts.SetCurrent(uid);
            InitCurrentUser();
            return ServiceResult.Ok(SerializeRecord(record));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _accounts.SetCurrent(prevUid);
            _sessions.Replace(prevSnapshot);
            _api.UpdateCookies(prevSecrets ?? EmptySecrets());
            _log.LogError(e, "Switch account failed for {Uid}", masked);
            return ServiceResult.Fail(-1, e.Message);
        }
    }

    /// <summary>
    /// 登出（对照 logout + 指南步骤 5）：先 await 停弹幕再删除；
    /// 当前账号登出时清空会话与 cookies，机密随 vault 删除并清零。
    /// </summary>
    public async Task<ServiceResult> LogoutAsync(string uid, CancellationToken ct)
    {
        var masked = _masker.MaskString(uid, 2, 2);
        _log.LogInformation("Logging out user: {Uid}", masked);

        if (_accounts.Get(uid) is null)
        {
            _log.LogWarning("Logout failed: User {Uid} not found.", masked);
            return ServiceResult.Fail(-1, "账户不存在");
        }

        try
        {
            await _danmu.StopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Logout aborted for {Uid}", masked);
            return ServiceResult.Fail(-1, e.Message);
        }

        var wasCurrent = _accounts.CurrentUid == uid;
        _accounts.Remove(uid);
        _vault.Remove(uid);
        if (wasCurrent)
        {
            _sessions.Replace(SessionSnapshot.Empty);
            _api.UpdateCookies(EmptySecrets());
        }

        return ServiceResult.Ok();
    }

    // --- 内部 ---

    private static JsonObject SerializeRecord(AccountRecord record) =>
        JsonSerializer.SerializeToNode(record)!.AsObject();

    private static IReadOnlyDictionary<string, SecureCredential> EmptySecrets() =>
        new Dictionary<string, SecureCredential>(StringComparer.Ordinal);

    private static SecureCredential? Secret(
        IReadOnlyDictionary<string, SecureCredential>? secrets, string key) =>
        secrets is not null && secrets.TryGetValue(key, out var value) ? value : null;
}
