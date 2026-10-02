using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BiliLiveTool.Core.Bilibili;
using BiliLiveTool.Core.Danmu;
using BiliLiveTool.Core.Security;
using BiliLiveTool.Infrastructure.Danmu;
using BiliLiveTool.Services.Auth;
using Microsoft.Extensions.Logging;

namespace BiliLiveTool.Services.Danmu;

/// <summary>
/// 弹幕监听与发送，对照原 danmu_service.py：getDanmuInfo 取首 host 走 wss、
/// op=7 认证（protover 3，未登录 uid=0）、op=2 心跳 30 秒、六类 cmd 分发、
/// 未知 cmd 记 debug；重连指数退避加 jitter 与熔断，generation 丢弃旧连接事件。
/// </summary>
public sealed class DanmuService : IDanmuMonitor
{
    private readonly IBilibiliApiClient _api;
    private readonly AuthSessionStore _sessions;
    private readonly ISecretVault _vault;
    private readonly ISecretMasker _masker;
    private readonly ILogger<DanmuService> _log;
    private readonly DanmuOptions _options;
    private readonly Func<Uri, CancellationToken, Task<IWebSocketConnection>> _connector;
    private readonly DanmuPacketCodec _codec = new();
    private readonly Channel<DanmuEvent> _events = CreateEventChannel();

    private int _generation;
    private volatile bool _running;
    private volatile bool _reconnectScheduled;
    private int _reconnectAttempts;
    private string _roomId = "";

    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _connCts;
    private IWebSocketConnection? _socket;
    private Task? _heartbeatTask;
    private Task? _receiveTask;
    private Task? _reconnectTask;

    public DanmuService(
        IBilibiliApiClient api,
        AuthSessionStore sessions,
        ISecretVault vault,
        ISecretMasker masker,
        ILogger<DanmuService> log)
        : this(api, sessions, vault, masker, log, new DanmuOptions(), null)
    {
    }

    internal DanmuService(
        IBilibiliApiClient api,
        AuthSessionStore sessions,
        ISecretVault vault,
        ISecretMasker masker,
        ILogger<DanmuService> log,
        DanmuOptions options,
        Func<Uri, CancellationToken, Task<IWebSocketConnection>>? connector)
    {
        _api = api;
        _sessions = sessions;
        _vault = vault;
        _masker = masker;
        _log = log;
        _options = options;
        _connector = connector ?? ClientWebSocketConnection.ConnectAsync;
    }

    /// <summary>推送给 UI 的事件流（Bounded 2000，DropOldest，对照原 message_callback）。</summary>
    public ChannelReader<DanmuEvent> Events => _events.Reader;

    // --- 连接生命周期 ---

    /// <summary>连接指定房间的弹幕服务器（对照原 connect），返回首轮连接是否成功。</summary>
    public async Task<bool> ConnectAsync(string roomId, CancellationToken ct)
    {
        if (_running)
            await StopAsync(ct).ConfigureAwait(false);

        _running = true;
        _generation = Interlocked.Increment(ref _generation);
        _reconnectAttempts = 0;
        _reconnectScheduled = false;
        _roomId = roomId;
        _sessionCts = new CancellationTokenSource();

        return await ConnectInternalAsync(roomId, Volatile.Read(ref _generation), _sessionCts.Token)
            .ConfigureAwait(false);
    }

    /// <summary>停止监听并清理连接（IDanmuMonitor）；对照原 stop。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _running = false;
        _reconnectScheduled = false;
        _generation = Interlocked.Increment(ref _generation);

        var sessionCts = _sessionCts;
        _sessionCts = null;
        sessionCts?.Cancel();

        await CleanupConnectionAsync().ConfigureAwait(false);

        var reconnect = _reconnectTask;
        _reconnectTask = null;
        if (reconnect is not null)
            await WhenCompleteQuietly(reconnect).ConfigureAwait(false);
        sessionCts?.Dispose();

        _log.LogInformation("Danmu service stopped");
    }

    /// <summary>内部连接：清理旧连接、补 buvid3/uid、取弹幕服务器、认证并启动循环。</summary>
    private async Task<bool> ConnectInternalAsync(string roomId, int gen, CancellationToken sessionToken)
    {
        await CleanupConnectionAsync().ConfigureAwait(false);
        if (sessionToken.IsCancellationRequested)
            return false;

        await EnsureBuvid3Async(sessionToken).ConfigureAwait(false);
        var uid = await EnsureUidAsync(sessionToken).ConfigureAwait(false);

        var res = await _api.GetDanmuInfoAsync(roomId, sessionToken).ConfigureAwait(false);
        var data = res.Success && res.Code == 0 ? res.Data as JsonObject : null;
        if (data is null)
        {
            _log.LogError("Failed to get danmu info: code={Code} msg={Message}", res.Code, res.Message);
            ScheduleReconnect(roomId, gen);
            return false;
        }

        var host = (data["host_list"] as JsonArray) is { Count: > 0 } list ? list[0] as JsonObject : null;
        var hostName = host is null ? null : JsonRead.ReadText(host["host"]);
        var wssPort = host is null ? null : JsonRead.ReadInt(host["wss_port"]);
        if (hostName is null || wssPort is null)
        {
            _log.LogError("Failed to get danmu info: host_list missing");
            ScheduleReconnect(roomId, gen);
            return false;
        }

        var url = $"wss://{hostName}:{wssPort}/sub";
        var token = JsonRead.ReadText(data["token"]) ?? "";

        _connCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        try
        {
            var socket = await _connector(new Uri(url), sessionToken).ConfigureAwait(false);

            // 认证包 op=7，字段与顺序对照原 auth_data
            var auth = new JsonObject
            {
                ["uid"] = uid,
                ["roomid"] = long.Parse(roomId, CultureInfo.InvariantCulture),
                ["protover"] = 3,
                ["platform"] = "web",
                ["type"] = 2,
                ["key"] = token,
            };
            await socket.SendAsync(_codec.Encode(7, auth.ToJsonString()), sessionToken)
                .ConfigureAwait(false);

            _socket = socket;
            var connToken = _connCts.Token;
            _heartbeatTask = HeartbeatLoopAsync(socket, gen, sessionToken, connToken);
            _receiveTask = ReceiveLoopAsync(socket, gen, sessionToken, connToken);

            _log.LogInformation("Connected to danmu server: {Url}", url);
            Publish(new DanmuEvent(DanmuEventTypes.System, "弹幕服务器连接成功"), gen);
            Interlocked.Exchange(ref _reconnectAttempts, 0);
            _reconnectScheduled = false;
            return true;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to connect to danmu server");
            await CleanupConnectionAsync().ConfigureAwait(false);
            ScheduleReconnect(roomId, gen);
            return false;
        }
    }

    private async Task CleanupConnectionAsync()
    {
        var connCts = _connCts;
        _connCts = null;
        try
        {
            connCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已清理过
        }

        var heartbeat = _heartbeatTask;
        var receive = _receiveTask;
        _heartbeatTask = null;
        _receiveTask = null;
        if (heartbeat is not null)
            await WhenCompleteQuietly(heartbeat).ConfigureAwait(false);
        if (receive is not null)
            await WhenCompleteQuietly(receive).ConfigureAwait(false);
        connCts?.Dispose();

        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseAsync(closeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 对照原 close 的 except pass
            }
        }
    }

    private static async Task WhenCompleteQuietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止或切换取消
        }
        catch
        {
            // 循环内部已记日志，这里吞掉避免清理路径二次抛出
        }
    }

    // --- 重连 ---

    private void ScheduleReconnect(string roomId, int gen)
    {
        if (!_running || gen != Volatile.Read(ref _generation))
            return;
        if (_reconnectScheduled)
            return; // 防重复触发（对照原 _reconnecting）

        var attempt = Interlocked.Increment(ref _reconnectAttempts);
        if (attempt > _options.BreakerThreshold)
        {
            _log.LogWarning("Danmu reconnect breaker tripped after {Attempt} failures", attempt);
            Publish(
                new DanmuEvent(DanmuEventTypes.System, $"弹幕连接连续失败{attempt}次，已停止重连"),
                gen);
            return;
        }

        _reconnectScheduled = true;
        var baseDelay = ComputeDelay(attempt, _options);
        var seconds = (int)baseDelay.TotalSeconds;
        _log.LogInformation(
            "弹幕连接断开，{Seconds}秒后尝试第{Attempt}次重连...", seconds, attempt);
        Publish(
            new DanmuEvent(
                DanmuEventTypes.System, $"弹幕连接断开，{seconds}秒后尝试第{attempt}次重连..."),
            gen);

        var token = _sessionCts?.Token ?? CancellationToken.None;
        _reconnectTask = ReconnectAsync(roomId, gen, baseDelay, token);
    }

    private async Task ReconnectAsync(string roomId, int gen, TimeSpan baseDelay, CancellationToken sessionToken)
    {
        try
        {
            var delay = ApplyJitter(baseDelay, _options.JitterRatio, Random.Shared);
            await Task.Delay(delay, sessionToken).ConfigureAwait(false);
            _reconnectScheduled = false;
            if (!_running || gen != Volatile.Read(ref _generation))
                return;
            await ConnectInternalAsync(roomId, gen, sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止或切换取消
        }
        catch (Exception e)
        {
            // 观察后台任务异常，不静默（对照分析报告 fire-and-forget 缺陷）
            _log.LogError(e, "Reconnect task failed");
        }
    }

    /// <summary>指数退避基数：5, 10, 20, 40, 60, 60...（对照原公式，无 jitter）。</summary>
    internal static TimeSpan ComputeDelay(int attempt, DanmuOptions options)
    {
        var factor = Math.Pow(2, attempt - 1);
        var seconds = options.InitialReconnectDelay.TotalSeconds * factor;
        return TimeSpan.FromSeconds(Math.Min(seconds, options.MaxReconnectDelay.TotalSeconds));
    }

    /// <summary>按比例均匀抖动（指南增补，原实现无）。</summary>
    internal static TimeSpan ApplyJitter(TimeSpan delay, double ratio, Random random)
    {
        if (ratio <= 0)
            return delay;
        var scale = 1 + ((random.NextDouble() * 2) - 1) * ratio;
        return TimeSpan.FromTicks((long)(delay.Ticks * scale));
    }

    // --- 收发循环 ---

    private async Task HeartbeatLoopAsync(
        IWebSocketConnection socket, int gen, CancellationToken sessionToken, CancellationToken connToken)
    {
        try
        {
            while (_running && gen == Volatile.Read(ref _generation) && !sessionToken.IsCancellationRequested)
            {
                await socket.SendAsync(_codec.Encode(2, ""), connToken).ConfigureAwait(false);
                await Task.Delay(_options.HeartbeatInterval, connToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止或切换取消
        }
        catch (Exception e)
        {
            _log.LogError(e, "Heartbeat error");
            ScheduleReconnect(_roomId, gen);
        }
    }

    private async Task ReceiveLoopAsync(
        IWebSocketConnection socket, int gen, CancellationToken sessionToken, CancellationToken connToken)
    {
        while (_running && gen == Volatile.Read(ref _generation) && !connToken.IsCancellationRequested)
        {
            byte[]? data;
            try
            {
                data = await socket.ReceiveAsync(connToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // 停止或切换取消
            }
            catch (Exception e)
            {
                _log.LogError(e, "Receive error");
                ScheduleReconnect(_roomId, gen);
                return;
            }

            if (data is null)
            {
                _log.LogWarning("WebSocket connection closed");
                ScheduleReconnect(_roomId, gen);
                return;
            }

            HandlePayload(data, gen);
        }
    }

    // --- 解包与命令分发 ---

    private void HandlePayload(byte[] data, int gen)
    {
        if (!_running || gen != Volatile.Read(ref _generation))
            return; // 旧连接在途数据直接丢弃，旧房间消息不残留

        var result = _codec.Decode(data);
        if (result.Error is { } error)
            _log.LogDebug("Packet decode error: {Error}", error); // 坏帧跳过，保留已解出的帧

        foreach (var frame in result.Frames)
            HandleFrame(frame, gen);
    }

    private void HandleFrame(DanmuFrame frame, int gen)
    {
        switch (frame.Operation)
        {
            case 5:
                try
                {
                    if (JsonNode.Parse(Encoding.UTF8.GetString(frame.Body)) is JsonObject command)
                        HandleCommand(command, gen);
                }
                catch (Exception e)
                {
                    _log.LogError(e, "JSON decode error");
                }

                break;

            case 3:
                // 心跳回复（人气值），对照原注释掉的 debug
                break;

            case 8:
                try
                {
                    var body = Encoding.UTF8.GetString(frame.Body);
                    if (JsonNode.Parse(body) is JsonObject auth
                        && JsonRead.ReadInt(auth["code"]) == 0)
                    {
                        _log.LogInformation("Danmu authentication successful");
                    }
                    else
                    {
                        _log.LogError("Danmu authentication failed: {Body}", body);
                    }
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Auth response decode error");
                }

                break;
        }
    }

    private void HandleCommand(JsonObject command, int gen)
    {
        var cmd = JsonRead.ReadText(command["cmd"]) ?? "";
        if (cmd.StartsWith("DANMU_MSG", StringComparison.Ordinal))
        {
            HandleDanmu(command, gen);
        }
        else if (cmd == "INTERACT_WORD")
        {
            var data = AsObject(command["data"]);
            var msgText = MapInteractText(JsonRead.ReadInt(data["msg_type"]));
            if (msgText is null)
                return;
            var uname = JsonRead.ReadText(data["uname"]) ?? "";
            _log.LogInformation("Interact: {Uname} {Msg}", uname, msgText);
            Publish(
                new DanmuEvent(
                    DanmuEventTypes.Interact,
                    msgText,
                    JsonRead.ReadLong(data["uid"]) ?? 0,
                    uname),
                gen);
        }
        else if (cmd.StartsWith("ENTRY_EFFECT", StringComparison.Ordinal))
        {
            var data = AsObject(command["data"]);
            var copyWriting = JsonRead.ReadText(data["copy_writing"]);
            if (string.IsNullOrEmpty(copyWriting))
                return;
            _log.LogInformation("Entry Effect: {CopyWriting}", copyWriting);
            Publish(
                new DanmuEvent(
                    DanmuEventTypes.Interact,
                    copyWriting.Replace("<%", "").Replace("%>", ""),
                    JsonRead.ReadLong(data["uid"]) ?? 0),
                gen);
        }
        else if (cmd.StartsWith("SEND_GIFT_V2", StringComparison.Ordinal))
        {
            // B站已将礼物推送改为 protobuf 载荷（data.pb），须先于 SEND_GIFT 前缀分支匹配
            HandleGiftV2(AsObject(command["data"]), gen);
        }
        else if (cmd.StartsWith("SEND_GIFT", StringComparison.Ordinal))
        {
            var data = AsObject(command["data"]);
            var giftName = JsonRead.ReadText(data["giftName"]);
            if (string.IsNullOrEmpty(giftName))
                giftName = JsonRead.ReadText(data["gift_name"]);
            var uname = JsonRead.ReadText(data["uname"]) ?? "";
            _log.LogInformation("Gift: {Uname} sent {GiftName}", uname, giftName);
            var action = JsonRead.ReadText(data["action"]);
            Publish(
                new DanmuEvent(
                    DanmuEventTypes.Gift,
                    "",
                    JsonRead.ReadLong(data["uid"]) ?? 0,
                    uname,
                    JsonRead.ReadText(data["face"]) ?? "",
                    giftName ?? "",
                    JsonRead.ReadInt(data["num"]) ?? 0,
                    string.IsNullOrEmpty(action) ? "投喂" : action),
                gen);
        }
        else if (cmd.StartsWith("COMBO_SEND", StringComparison.Ordinal))
        {
            var data = AsObject(command["data"]);
            var giftName = JsonRead.ReadText(data["gift_name"]);
            if (string.IsNullOrEmpty(giftName))
                giftName = JsonRead.ReadText(data["giftName"]);
            var uname = JsonRead.ReadText(data["uname"]) ?? "";
            var comboNum = JsonRead.ReadInt(data["combo_num"]) ?? 0;
            _log.LogInformation("Combo Gift: {Uname} sent {GiftName} x {ComboNum}", uname, giftName, comboNum);
            var action = JsonRead.ReadText(data["action"]);
            Publish(
                new DanmuEvent(
                    DanmuEventTypes.Gift,
                    "",
                    JsonRead.ReadLong(data["uid"]) ?? 0,
                    uname,
                    "",
                    giftName ?? "",
                    comboNum,
                    string.IsNullOrEmpty(action) ? "投喂" : action),
                gen);
        }
        else if (cmd.StartsWith("INTERACT_WORD_V2", StringComparison.Ordinal))
        {
            HandleInteractV2(AsObject(command["data"]), gen);
        }
        else
        {
            _log.LogDebug("Unknown cmd: {Cmd}", cmd); // 对照 AGENTS：未知 cmd 记 debug 不静默丢弃
        }
    }

    private void HandleDanmu(JsonObject command, int gen)
    {
        if (command["info"] is not JsonArray info || info.Count == 0)
            return;

        var msg = JsonRead.ReadText(info[1]) ?? "";
        long uid = 0;
        var uname = "";
        if (info.Count > 2 && info[2] is JsonArray user && user.Count > 1)
        {
            uid = JsonRead.ReadLong(user[0]) ?? 0;
            uname = JsonRead.ReadText(user[1]) ?? "";
        }

        var meta = info[0] as JsonArray;

        // 头像 meta：info[0][15].user.base.face（对照原 try 取头像）
        var face = "";
        if (meta is { Count: > 15 } && meta[15] is JsonObject extra
            && extra["user"] is JsonObject userObj && userObj["base"] is JsonObject basis)
        {
            face = JsonRead.ReadText(basis["face"]) ?? "";
        }

        var emotes = ReadEmotes(meta, msg);

        Publish(new DanmuEvent(DanmuEventTypes.Danmu, msg, uid, uname, face, Emotes: emotes), gen);
    }

    /// <summary>
    /// 表情元数据（对照网页端 info[] 映射：dmType=t[0][12]、emoticonOptions=t[0][13]、
    /// emots 取 info[0][15].extra.emots）：普通弹幕按 [token] 查表，单表情弹幕整条作 key。
    /// </summary>
    private static IReadOnlyDictionary<string, DanmuEmote>? ReadEmotes(JsonArray? meta, string msg)
    {
        if (meta is null || msg.Length == 0)
            return null;

        var dmType = meta.Count > 12 ? JsonRead.ReadInt(meta[12]) ?? 0 : 0;
        if (dmType != 0)
        {
            var single = ParseJsonObject(meta.Count > 13 ? meta[13] : null);
            if (single is null)
                return null;
            var url = JsonRead.ReadText(single["url"]) ?? "";
            if (url.Length == 0)
                return null;
            return new Dictionary<string, DanmuEmote>(StringComparer.Ordinal)
            {
                [msg] = new DanmuEmote(
                    url,
                    JsonRead.ReadInt(single["width"]) ?? 0,
                    JsonRead.ReadInt(single["height"]) ?? 0),
            };
        }

        if (meta.Count <= 15 || meta[15] is not JsonObject meta15
            || JsonRead.ReadText(meta15["extra"]) is not { Length: > 0 } extraJson
            || ParseJsonObject(extraJson)?["emots"] is not JsonObject emots
            || emots.Count == 0)
        {
            return null;
        }

        Dictionary<string, DanmuEmote>? map = null;
        foreach (var (token, node) in emots)
        {
            if (node is not JsonObject emote)
                continue;
            var url = JsonRead.ReadText(emote["url"]) ?? "";
            if (url.Length == 0)
                continue;
            map ??= new Dictionary<string, DanmuEmote>(StringComparer.Ordinal);
            map[token] = new DanmuEmote(
                url,
                JsonRead.ReadInt(emote["width"]) ?? 0,
                JsonRead.ReadInt(emote["height"]) ?? 0);
        }

        return map;
    }

    private static JsonObject? ParseJsonObject(JsonNode? node)
    {
        if (node is JsonObject obj)
            return obj;
        if (node is JsonValue val && val.TryGetValue<string>(out var text) && text.Length > 1)
        {
            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private void HandleInteractV2(JsonObject data, int gen)
    {
        try
        {
            var pb = Convert.FromBase64String(JsonRead.ReadText(data["pb"]) ?? "");
            var v2 = InteractWordV2.Parser.ParseFrom(pb);
            var msgText = MapInteractText((int)v2.MsgType);
            if (msgText is null)
                return;
            _log.LogInformation("Interact V2: {Uname} {Msg}", v2.Uname, msgText);
            Publish(
                new DanmuEvent(
                    DanmuEventTypes.Interact, msgText, (long)v2.Uid, v2.Uname),
                gen);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Decode INTERACT_WORD_V2 error");
        }
    }

    private void HandleGiftV2(JsonObject data, int gen)
    {
        try
        {
            var pb = Convert.FromBase64String(JsonRead.ReadText(data["pb"]) ?? "");
            var gift = SendGiftV2.Parser.ParseFrom(pb);
            foreach (var item in gift.GiftList)
            {
                if (item.GiftName.Length == 0)
                    continue; // 载荷缺礼物名不发空行
                var num = (int)Math.Min(item.Num, int.MaxValue);
                _log.LogInformation("Gift V2: {Uname} sent {GiftName} x {Num}", gift.Uname, item.GiftName, num);
                Publish(
                    new DanmuEvent(
                        DanmuEventTypes.Gift,
                        "",
                        (long)gift.Uid,
                        gift.Uname,
                        gift.Face,
                        item.GiftName,
                        num,
                        item.Action.Length == 0 ? "投喂" : item.Action),
                    gen);
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Decode SEND_GIFT_V2 error");
        }
    }

    private static JsonObject AsObject(JsonNode? node) =>
        node as JsonObject ?? new JsonObject();

    private static string? MapInteractText(int? msgType) => msgType switch
    {
        1 => "进入直播间",
        2 => "关注了直播间",
        3 => "分享了直播间",
        _ => null,
    };

    private void Publish(DanmuEvent evt, int gen)
    {
        if (!_running || gen != Volatile.Read(ref _generation))
            return; // 旧连接事件丢弃（旧房间不残留）
        _events.Writer.TryWrite(evt);
    }

    // --- 弹幕发送 ---

    /// <summary>发送弹幕（对照原 send_danmu），错误码映射 0/1003212/-101/-400/10031。</summary>
    public async Task<ServiceResult> SendDanmuAsync(string msg, CancellationToken ct)
    {
        var snapshot = _sessions.CurrentSnapshot;
        if (snapshot.RoomId == "")
            return ServiceResult.Fail(-1, "未获取到房间ID");
        if (snapshot.Csrf is not { } csrf)
            return ServiceResult.Fail(-1, "未获取到 CSRF Token");

        var res = await _api.SendDanmuAsync(snapshot.RoomId, msg, csrf, ct).ConfigureAwait(false);
        if (!res.Success)
            return ServiceResult.Fail(-1, "网络请求失败");

        var fallback = res.Message == "" ? "未知错误" : res.Message;
        var text = res.Code switch
        {
            1003212 => "超出限制长度",
            0 => "发送成功",
            -101 => "未登录",
            -400 => "参数错误",
            10031 => "发送频率过高",
            _ => fallback,
        };
        return new ServiceResult(res.Code, text);
    }

    // --- 会话补全 ---

    /// <summary>jar 缺 buvid3 时经 finger/spi 取回并合入 vault 与客户端（对照原兜底）。</summary>
    private async Task EnsureBuvid3Async(CancellationToken ct)
    {
        var snapshot = _sessions.CurrentSnapshot;
        if (snapshot.Buvid3 is not null)
            return;

        var buvid3 = await _api.GetBuvid3Async(ct).ConfigureAwait(false);
        if (buvid3 is null)
        {
            _log.LogWarning("Failed to fetch buvid3");
            return;
        }

        var credential = new SecureCredential(buvid3);
        if (snapshot.Uid > 0)
        {
            var uidKey = snapshot.Uid.ToString(CultureInfo.InvariantCulture);
            var secrets = new Dictionary<string, SecureCredential>(StringComparer.Ordinal);
            var existing = _vault.Load(uidKey);
            if (existing is not null)
            {
                foreach (var (key, value) in existing)
                    secrets[key] = value;
            }

            secrets["buvid3"] = credential;
            _vault.Save(uidKey, secrets);
            _api.UpdateCookies(secrets);
        }
        else
        {
            _api.UpdateCookies(new Dictionary<string, SecureCredential>(StringComparer.Ordinal)
            {
                ["buvid3"] = credential,
            });
        }

        _sessions.Replace(snapshot with { Buvid3 = credential });
        _log.LogInformation("Fetched buvid3: {Buvid3}", _masker.MaskString(buvid3, 4, 4));
    }

    /// <summary>会话无 uid 时经 nav 取回（对照原 state.uid 兜底），未登录用 0。</summary>
    private async Task<long> EnsureUidAsync(CancellationToken ct)
    {
        var uid = _sessions.CurrentSnapshot.Uid;
        if (uid != 0)
            return uid;

        var nav = await _api.GetUserInfoAsync(ct).ConfigureAwait(false);
        if (nav.Success && nav.Code == 0 && nav.Data is JsonObject data
            && data["isLogin"] is JsonValue login && login.TryGetValue<bool>(out var isLogin) && isLogin)
        {
            uid = JsonRead.ReadLong(data["mid"]) ?? 0;
            _sessions.Replace(_sessions.CurrentSnapshot with { Uid = uid });
            _log.LogInformation(
                "Fetched uid: {Uid}", _masker.MaskString(uid.ToString(CultureInfo.InvariantCulture), 2, 2));
            return uid;
        }

        _log.LogInformation("User not logged in, using uid=0");
        return 0;
    }

    // --- 内部 ---

    internal static Channel<DanmuEvent> CreateEventChannel() =>
        Channel.CreateBounded<DanmuEvent>(
            new BoundedChannelOptions(2000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
}
