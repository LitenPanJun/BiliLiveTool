using System.Text.Json;
using BiliLiveTool.Core.Security;
using Latchkey;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliLiveTool.Infrastructure.Security;

/// <summary>
/// 系统钥匙串机密存储（AGENTS：持久化走系统钥匙串）：Windows 凭据管理器、
/// macOS Keychain、Linux Secret Service，每账号一条 JSON 载荷，OS 侧加密静态
/// 存储，明文仅在受控边界短暂出现。钥匙串不可用（无桌面密钥环/无会话总线）
/// 时降级为仅会话内存并置 <see cref="Available"/>=false，绝不静默落文件；
/// 一切创建/写入/读取故障经 ILogger 可见（排障教训：静默降级会让重启后
/// 登录态凭空丢失且日志无痕）。降级后下一次 Save 重试创建一次（工厂缝可测），
/// 缓存与 Dispose 语义对齐 InMemorySecretVault（回滚路径依赖实例同一性）。
/// </summary>
internal sealed class KeyringSecretVault : ISecretVault
{
    // 反向域名空间：避免与其它应用的钥匙串项冲突
    private const string ServiceName = "cn.zacharypan.bilivelivetool";
    private const string KeyPrefix = "account:";

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, SecureCredential>> _cache =
        new(StringComparer.Ordinal);
    private readonly ILogger<KeyringSecretVault> _log;
    private readonly Func<ILogger<KeyringSecretVault>, ILatchkey?>? _storeFactory;
    private ILatchkey? _store;
    private bool _createRetried;

    public KeyringSecretVault(ILogger<KeyringSecretVault> log)
    {
        _log = log;
        _storeFactory = CreateStore;
        _store = CreateStore(log);
    }

    /// <summary>测试注入口：指定钥匙串后端（null 模拟不可用降级）与可选创建工厂。</summary>
    internal KeyringSecretVault(
        ILatchkey? store,
        ILogger<KeyringSecretVault>? log = null,
        Func<ILogger<KeyringSecretVault>, ILatchkey?>? storeFactory = null)
    {
        _store = store;
        _log = log ?? NullLogger<KeyringSecretVault>.Instance;
        _storeFactory = storeFactory;
    }

    /// <summary>系统钥匙串是否可用；false 表示重启后需重新扫码。</summary>
    public bool Available => _store is not null;

    public void Save(string uid, IReadOnlyDictionary<string, SecureCredential> secrets)
    {
        lock (_gate)
        {
            if (_cache.Remove(uid, out var displaced))
                DisposeDisplaced(displaced, secrets);
            _cache[uid] = new Dictionary<string, SecureCredential>(secrets, StringComparer.Ordinal);
            PersistLocked(uid, secrets);
        }
    }

    public IReadOnlyDictionary<string, SecureCredential>? Load(string uid)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(uid, out var cached))
                return new Dictionary<string, SecureCredential>(cached, StringComparer.Ordinal);

            var loaded = ReadKeyringLocked(uid);
            if (loaded is null)
                return null;
            _cache[uid] = new Dictionary<string, SecureCredential>(loaded, StringComparer.Ordinal);
            return loaded;
        }
    }

    public bool Remove(string uid)
    {
        lock (_gate)
        {
            var removed = _cache.Remove(uid, out var displaced);
            if (displaced is not null)
                DisposeAll(displaced);
            if (_store is null)
                return removed;

            try
            {
                return _store.Delete(KeyPrefix + uid) || removed;
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Keyring delete failed; memory-side entry already removed");
                return removed; // 钥匙串瞬时故障：内存侧已清理
            }
        }
    }

    // --- 钥匙串后端 ---

    private static ILatchkey? CreateStore(ILogger log)
    {
        // 有会话总线但无 Secret Service 时 libsecret 会阻塞至 D-Bus 超时
        // （约 25s）：限时 3s 获取，拿不到即降级，启动绝不卡死。
        try
        {
            var pending = Task.Run(() => LatchkeyFactory.Create(ServiceName));
            if (pending.Wait(TimeSpan.FromSeconds(3)))
                return pending.Result;

            // 超时后迟到的故障须被观察，避免未观察异常
            _ = pending.ContinueWith(
                static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously);
            log.LogWarning(
                "System keyring not ready within 3s; credentials remain memory-only (re-scan QR after restart)");
            return null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "System keyring unavailable; credentials remain memory-only (re-scan QR after restart)");
            return null;
        }
    }

    /// <summary>降级后每次会话最多重试创建一次（登录链的 Save 是恢复凭据的最后机会）。</summary>
    private ILatchkey? EnsureStoreLocked()
    {
        if (_store is null && _storeFactory is not null && !_createRetried)
        {
            _createRetried = true;
            _log.LogWarning("Retrying keyring store creation (earlier attempt failed)");
            _store = _storeFactory(_log);
            if (_store is null)
                _log.LogWarning("Keyring persist stays disabled for this session (memory-only)");
        }
        return _store;
    }

    private void PersistLocked(string uid, IReadOnlyDictionary<string, SecureCredential> secrets)
    {
        var store = EnsureStoreLocked();
        if (store is null)
            return;

        try
        {
            // 受控边界：序列化仅用于钥匙串载荷，不落任何文件
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, credential) in secrets)
                payload[name] = credential.DangerousGetValue();
            store.Set(KeyPrefix + uid, JsonSerializer.Serialize(payload));
        }
        catch (Exception e)
        {
            // 钥匙串锁定/被拒：会话缓存仍在；故障必须可见（静默丢凭据不可接受）
            _log.LogWarning(e, "Keyring persist failed; entry not written (memory-only this session)");
        }
    }

    private IReadOnlyDictionary<string, SecureCredential>? ReadKeyringLocked(string uid)
    {
        var store = EnsureStoreLocked();
        if (store is null)
            return null;

        try
        {
            var json = store.Get(KeyPrefix + uid);
            if (json is null)
                return null;
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (payload is not { Count: > 0 })
                return null;

            var result = new Dictionary<string, SecureCredential>(StringComparer.Ordinal);
            foreach (var (name, value) in payload)
                result[name] = new SecureCredential(value);
            return result;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Keyring read failed; treating entry as absent");
            return null; // 读取失败视同不存在：重扫码即可恢复（消费端须告警）
        }
    }

    // --- Dispose 语义（对齐 InMemorySecretVault，回滚路径依赖实例同一性） ---

    private static void DisposeDisplaced(
        Dictionary<string, SecureCredential> displaced,
        IReadOnlyDictionary<string, SecureCredential> incoming)
    {
        foreach (var (key, credential) in displaced)
        {
            if (incoming.TryGetValue(key, out var kept) && ReferenceEquals(kept, credential))
                continue;
            credential.Dispose();
        }
    }

    private static void DisposeAll(Dictionary<string, SecureCredential> secrets)
    {
        foreach (var credential in secrets.Values)
            credential.Dispose();
    }
}
