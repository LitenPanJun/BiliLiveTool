using System.Text.Json.Nodes;
using BiliLiveTool.Core;
using BiliLiveTool.Core.Auth;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Core.State;
using BiliLiveTool.Services.Auth;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Services.Live;

/// <summary>
/// 直播管理，对照原 live_service.py：分区刷新、房间资料同步、标题/公告/分区
/// 更新、开播三步与停播。非人脸路径失败抛 <see cref="BilibiliException"/>（指南样例）。
/// </summary>
public sealed class LiveService
{
    private readonly IBilibiliApiClient _api;
    private readonly AuthSessionStore _sessions;
    private readonly IAccountStore _accounts;
    private readonly ISecretMasker _masker;
    private readonly ILogger<LiveService> _log;

    public LiveService(
        IBilibiliApiClient api,
        AuthSessionStore sessions,
        IAccountStore accounts,
        ISecretMasker masker,
        ILogger<LiveService> log)
    {
        _api = api;
        _sessions = sessions;
        _accounts = accounts;
        _masker = masker;
        _log = log;
    }

    // --- 分区 ---

    /// <summary>刷新分区映射（对照 _refresh_partitions_internal），返回是否成功。</summary>
    public async Task<bool> RefreshPartitionsAsync(CancellationToken ct)
    {
        var res = await _api.GetAreaListAsync(ct).ConfigureAwait(false);
        if (!res.Success || res.Code != 0 || res.Data is not JsonArray list)
        {
            _log.LogError("Failed to refresh partitions: code={Code} msg={Message}", res.Code, res.Message);
            return false;
        }

        _sessions.SetPartitions(BuildPartitionMap(list));

        // 刷新后尝试恢复当前账号的 last_area_id（对照原逻辑）
        if (_accounts.CurrentUid is { } uid && _accounts.Get(uid) is { LastAreaId: { } lastAreaId })
            _sessions.Replace(_sessions.CurrentSnapshot with { CurrentAreaId = lastAreaId });
        return true;
    }

    /// <summary>分区名映射（对照 get_partitions）：{主:[子名...]}。</summary>
    public async Task<ServiceResult> GetPartitionsAsync(CancellationToken ct)
    {
        if (_sessions.Partitions.Count == 0)
            await RefreshPartitionsAsync(ct).ConfigureAwait(false);

        var data = new JsonObject();
        foreach (var (parent, subs) in _sessions.Partitions)
            data[parent] = new JsonArray(subs.Keys.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        return ServiceResult.Ok(data);
    }

    // --- 房间资料 ---

    /// <summary>同步房间资料（对照 sync_room_profile）：双域字段回退并落账号。</summary>
    public async Task<ServiceResult> SyncRoomProfileAsync(CancellationToken ct)
    {
        var snapshot = _sessions.CurrentSnapshot;
        if (snapshot.RoomId == "" || snapshot.Uid == 0)
            return ServiceResult.Fail(-1, "请先登录");

        var data = new JsonObject();
        var next = snapshot;
        var changed = false;

        var room = await _api.GetRoomInfoAsync(snapshot.RoomId, ct).ConfigureAwait(false);
        if (room.Success && room.Code == 0)
        {
            var roomData = room.Data as JsonObject ?? new JsonObject();

            // 对照原 Python `or` 回退：null/0/空串均落到 v2 域
            var areaId = JsonRead.ReadInt(roomData["area_id"]);
            if (areaId is null or 0) areaId = JsonRead.ReadInt(roomData["area_v2_id"]);
            if (areaId is 0) areaId = null;

            var parentName = JsonRead.ReadText(roomData["parent_area_name"]);
            if (string.IsNullOrEmpty(parentName))
                parentName = JsonRead.ReadText(roomData["parent_area_v2_name"]);

            var areaName = JsonRead.ReadText(roomData["area_name"]);
            if (string.IsNullOrEmpty(areaName))
                areaName = JsonRead.ReadText(roomData["area_v2_name"]);

            data["last_title"] = JsonRead.ReadText(roomData["title"]) ?? "";
            if (areaId is { } aid)
            {
                data["last_area_id"] = aid;
                next = next with { CurrentAreaId = aid };
                changed = true;
            }

            if (!string.IsNullOrEmpty(parentName) && !string.IsNullOrEmpty(areaName))
            {
                data["last_area_name"] = new JsonArray(parentName, areaName);
                next = next with { CurrentAreaNames = [parentName, areaName] };
                changed = true;
            }
        }
        else
        {
            _log.LogWarning("Sync room info failed: code={Code} msg={Message}", room.Code, room.Message);
        }

        var news = await _api.GetRoomNewsAsync(snapshot.RoomId, snapshot.Uid, ct).ConfigureAwait(false);
        if (news.Success && news.Code == 0)
        {
            var newsData = news.Data as JsonObject ?? new JsonObject();
            data["last_announcement"] = JsonRead.ReadText(newsData["content"]) ?? "";
        }
        else
        {
            _log.LogWarning("Sync room news failed: code={Code} msg={Message}", news.Code, news.Message);
        }

        if (data.Count == 0)
            return ServiceResult.Fail(-1, "同步直播信息失败");

        if (changed) _sessions.Replace(next);
        SaveAccountFields(data);
        return ServiceResult.Ok(data);
    }

    // --- 标题 / 公告 / 分区 ---

    public async Task<ServiceResult> UpdateTitleAsync(string title, CancellationToken ct)
    {
        _log.LogInformation("Updating title to: {Title}", title);
        var snapshot = _sessions.CurrentSnapshot;
        if (_accounts.CurrentUid is null || snapshot.Csrf is null)
            return ServiceResult.Fail(-1, "未登录");

        var res = await _api.UpdateTitleAsync(snapshot.RoomId, title, snapshot.Csrf, ct).ConfigureAwait(false);
        if (res.Success && res.Code == 0)
        {
            SaveAccountFields(new JsonObject { ["last_title"] = title });
            return ServiceResult.Ok();
        }

        _log.LogError("Update title failed: code={Code} msg={Message}", res.Code, res.Message);
        return ServiceResult.Fail(-1, res.Message);
    }

    public async Task<ServiceResult> UpdateAnnouncementAsync(string announcement, CancellationToken ct)
    {
        _log.LogInformation("Updating announcement...");
        var snapshot = _sessions.CurrentSnapshot;
        if (_accounts.CurrentUid is null || snapshot.Csrf is null)
            return ServiceResult.Fail(-1, "未登录");

        var res = await _api.UpdateAnnouncementAsync(
            snapshot.RoomId, snapshot.Uid, announcement, snapshot.Csrf, ct).ConfigureAwait(false);
        if (res.Success && res.Code == 0)
        {
            SaveAccountFields(new JsonObject { ["last_announcement"] = announcement });
            return ServiceResult.Ok();
        }

        _log.LogError("Update announcement failed: code={Code} msg={Message}", res.Code, res.Message);
        return ServiceResult.Fail(-1, res.Message);
    }

    public async Task<ServiceResult> UpdateAreaAsync(string parentName, string subName, CancellationToken ct)
    {
        _log.LogInformation("Updating area to: {Parent} - {Sub}", parentName, subName);
        var snapshot = _sessions.CurrentSnapshot;
        if (_accounts.CurrentUid is null || snapshot.Csrf is null)
            return ServiceResult.Fail(-1, "未登录");

        // 对照原逻辑：仅当映射为空时刷新，缺键不强制刷新
        if (_sessions.Partitions.Count == 0)
            await RefreshPartitionsAsync(ct).ConfigureAwait(false);
        var areaId = _sessions.ResolveAreaId(parentName, subName);
        if (areaId is null)
        {
            _log.LogWarning("Invalid area: {Parent} - {Sub}", parentName, subName);
            return ServiceResult.Fail(-1, "无效分区");
        }

        var res = await _api.UpdateAreaAsync(snapshot.RoomId, areaId.Value, snapshot.Csrf, ct).ConfigureAwait(false);
        if (res.Success && res.Code == 0)
        {
            _sessions.Replace(snapshot with
            {
                CurrentAreaId = areaId,
                CurrentAreaNames = [parentName, subName],
            });
            SaveAccountFields(new JsonObject
            {
                ["last_area_id"] = areaId.Value,
                ["last_area_name"] = new JsonArray(parentName, subName),
            });
            return ServiceResult.Ok();
        }

        _log.LogError("Update area failed: code={Code} msg={Message}", res.Code, res.Message);
        return ServiceResult.Fail(-1, res.Message);
    }

    // --- 开播 / 停播 ---

    /// <summary>
    /// 开播三步（客户端内完成 now/liveVersionInfo/startLive）：
    /// 0 → 三路端点，60024/60043 → 人脸要求原样返回，其余失败抛业务异常。
    /// </summary>
    public async Task<Result<LiveStreamEndpoints, FaceVerifyRequired>> StartLiveAsync(
        string parentName,
        string subName,
        CancellationToken ct)
    {
        _log.LogInformation("Starting live stream...");
        var snapshot = _sessions.CurrentSnapshot;
        if (snapshot.RoomId == "")
            throw new BilibiliException(-1, "请先登录");

        var current = snapshot;
        if (!string.IsNullOrEmpty(parentName) && !string.IsNullOrEmpty(subName))
        {
            if (_sessions.Partitions.Count == 0)
                await RefreshPartitionsAsync(ct).ConfigureAwait(false);
            var areaId = _sessions.ResolveAreaId(parentName, subName);

            // 未命中强制刷新一次（对照原逻辑）
            if (areaId is null)
            {
                await RefreshPartitionsAsync(ct).ConfigureAwait(false);
                areaId = _sessions.ResolveAreaId(parentName, subName);
            }

            if (areaId is not int resolved)
            {
                _log.LogWarning("Unknown partition: {Parent}-{Sub}", parentName, subName);
                throw new BilibiliException(-1, $"无法识别分区: {parentName}-{subName}");
            }

            current = current with { CurrentAreaId = resolved, CurrentAreaNames = [parentName, subName] };
            _sessions.Replace(current);
        }

        if (current.CurrentAreaId is null)
        {
            // 对照原逻辑：缺省时从账号 last_area_id 恢复，兜底 235
            var uid = _accounts.CurrentUid;
            var record = uid is null ? null : _accounts.Get(uid);
            current = current with
            {
                CurrentAreaId = record?.LastAreaId ?? 235,
                CurrentAreaNames = record is null ? current.CurrentAreaNames : record.LastAreaName,
            };
            _sessions.Replace(current);
        }

        var csrf = current.Csrf ?? throw new BilibiliException(-1, "未登录");
        var area = current.CurrentAreaId.Value;
        var res = await _api.StartLiveAsync(current.RoomId, area, csrf, ct).ConfigureAwait(false);
        if (!res.Success)
            throw new BilibiliException(-1, "网络错误"); // 对照原网络层失败返回

        switch (res.Code)
        {
            case 0:
            {
                _log.LogInformation("Live stream started successfully.");
                current = current with { IsLive = true };
                _sessions.Replace(current);

                // 成功后强制反查分区名，确保数据一致
                var names = await FindAreaNamesAsync(area, ct).ConfigureAwait(false);
                if (names is not null)
                {
                    current = current with { CurrentAreaNames = names };
                    _sessions.Replace(current);
                }

                SaveAccountFields(new JsonObject
                {
                    ["last_area_id"] = area,
                    ["last_area_name"] = new JsonArray(
                        current.CurrentAreaNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                });

                var endpoints = ParseEndpoints(StartLiveResponse.Parse(res.Data));
                _log.LogInformation("RTMP-1 Addr: {Addr}", _masker.MaskString(endpoints.Rtmp1.Addr, 10, 5));
                _log.LogInformation("RTMP-1 Code: {Code}", _masker.MaskString(endpoints.Rtmp1.Code, 5, 5));
                return Result<LiveStreamEndpoints, FaceVerifyRequired>.Ok(endpoints);
            }

            case 60024:
                _log.LogInformation("Live stream requires face verification (60024).");
                return Result<LiveStreamEndpoints, FaceVerifyRequired>.Fail(
                    new FaceVerifyRequired(JsonRead.ReadText(res.Data?["qr"]) ?? ""));

            case 60043:
                _log.LogInformation("Live stream requires face verification (60043).");
                return Result<LiveStreamEndpoints, FaceVerifyRequired>.Fail(
                    new FaceVerifyRequired(
                        $"https://www.bilibili.com/blackboard/live/face-auth-middle.html?source_event=400&mid={current.Uid}"));

            default:
                _log.LogError("Start live failed: code={Code} msg={Message}", res.Code, res.Message);
                throw new BilibiliException(res.Code, res.Message);
        }
    }

    public async Task<ServiceResult> StopLiveAsync(CancellationToken ct)
    {
        _log.LogInformation("Stopping live stream...");
        var snapshot = _sessions.CurrentSnapshot;
        if (snapshot.Csrf is not { } csrf)
        {
            _log.LogError("Stop live failed: csrf missing");
            return ServiceResult.Fail(-1, "");
        }

        var res = await _api.StopLiveAsync(snapshot.RoomId, csrf, ct).ConfigureAwait(false);
        if (res.Success && res.Code == 0)
        {
            _log.LogInformation("Live stream stopped successfully.");
            _sessions.Replace(snapshot with { IsLive = false });
            return ServiceResult.Ok();
        }

        _log.LogError("Stop live failed: code={Code} msg={Message}", res.Code, res.Message);
        return ServiceResult.Fail(-1, "");
    }

    // --- 内部 ---

    // 对照 _save_current_user_fields：仅更新当前账号的非机密字段
    private void SaveAccountFields(JsonObject fields)
    {
        if (_accounts.CurrentUid is not { } uid || _accounts.Get(uid) is not { } record) return;

        var updated = record;
        if (JsonRead.ReadText(fields["last_title"]) is { } titleText)
            updated = updated with { LastTitle = titleText };
        if (JsonRead.ReadInt(fields["last_area_id"]) is { } areaId)
            updated = updated with { LastAreaId = areaId };
        if (fields["last_area_name"] is JsonArray names)
            updated = updated with
            {
                LastAreaName = names.Select(n => JsonRead.ReadText(n) ?? "").ToList(),
            };
        if (JsonRead.ReadText(fields["last_announcement"]) is { } annText)
            updated = updated with { LastAnnouncement = annText };

        _accounts.Upsert(updated);
    }

    private async Task<IReadOnlyList<string>?> FindAreaNamesAsync(int areaId, CancellationToken ct)
    {
        if (_sessions.Partitions.Count == 0)
            await RefreshPartitionsAsync(ct).ConfigureAwait(false);

        foreach (var (parent, subs) in _sessions.Partitions)
            foreach (var (sub, id) in subs)
                if (id == areaId) return new[] { parent, sub };
        return null;
    }

    // 对照 Area/getList：[{name, list:[{id,name}]}] → {主:{子:id}}
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> BuildPartitionMap(JsonArray list)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var node in list)
        {
            if (node is not JsonObject parent) continue;
            var parentName = JsonRead.ReadText(parent["name"]) ?? "";
            var subs = new Dictionary<string, int>(StringComparer.Ordinal);
            if (parent["list"] is JsonArray subList)
            {
                foreach (var subNode in subList)
                {
                    if (subNode is not JsonObject sub) continue;
                    var subName = JsonRead.ReadText(sub["name"]);
                    var subId = JsonRead.ReadInt(sub["id"]);
                    if (subName is null || subId is null) continue;
                    subs[subName] = subId.Value;
                }
            }

            map[parentName] = subs;
        }

        return map;
    }

    // 对照 start_live 成功路径：rtmp 主体 + protocols 首个有数据项
    internal static LiveStreamEndpoints ParseEndpoints(StartLiveResponse response)
    {
        var rtmp = response.Rtmp;
        return new LiveStreamEndpoints(
            new LiveEndpoint(JsonRead.ReadText(rtmp?["addr"]) ?? "", JsonRead.ReadText(rtmp?["code"]) ?? ""),
            FirstProtocol(response.Protocols, "rtmp"),
            FirstProtocol(response.Protocols, "srt"));
    }

    private static LiveEndpoint FirstProtocol(JsonArray protocols, string name)
    {
        foreach (var node in protocols)
        {
            if (node is not JsonObject protocol) continue;
            if (JsonRead.ReadText(protocol["protocol"]) != name) continue;
            var addr = JsonRead.ReadText(protocol["addr"]) ?? "";
            var code = JsonRead.ReadText(protocol["code"]) ?? "";
            if (addr == "" || code == "") continue; // 对照 p.get('addr') and p.get('code')
            return new LiveEndpoint(addr, code);
        }

        return new LiveEndpoint("", "");
    }
}
