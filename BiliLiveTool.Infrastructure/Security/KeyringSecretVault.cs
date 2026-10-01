using System.Text.Json;
using BiliLiveTool.Core.Security;
using Latchkey;

namespace BiliLiveTool.Infrastructure.Security;

/// <summary>
/// 系统钥匙串机密存储（AGENTS：持久化走系统钥匙串）：Windows 凭据管理器、
/// macOS Keychain、Linux Secret Service，每账号一条 JSON 载荷，OS 侧加密静态
/// 存储，明文仅在受控边界短暂出现。钥匙串不可用（无桌面密钥环/无会话总线）
/// 时降级为仅会话内存并置 <see cref="Available"/>=false，绝不静默落文件；
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
    private readonly ILatchkey? _store;

    public KeyringSecretVault()
    {
        try
        {
            _store = LatchkeyFactory.Create(ServiceName);
        }
        catch (Exception)
        {
            _store = null; // 解析不到系统钥匙串：会话内存降级
        }
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
            catch (Exception)
            {
                return removed; // 钥匙串瞬时故障：内存侧已清理
            }
        }
    }

    private void PersistLocked(string uid, IReadOnlyDictionary<string, SecureCredential> secrets)
    {
        if (_store is null)
            return;

        try
        {
            // 受控边界：序列化仅用于钥匙串载荷，不落任何文件
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, credential) in secrets)
                payload[name] = credential.DangerousGetValue();
            _store.Set(KeyPrefix + uid, JsonSerializer.Serialize(payload));
        }
        catch (Exception)
        {
            // 钥匙串锁定/被拒：会话缓存仍在，下次 Save 重试；不静默降级到文件
        }
    }

    private IReadOnlyDictionary<string, SecureCredential>? ReadKeyringLocked(string uid)
    {
        if (_store is null)
            return null;

        try
        {
            var json = _store.Get(KeyPrefix + uid);
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
        catch (Exception)
        {
            return null; // 读取失败视同不存在：重扫码即可恢复
        }
    }

    // --- Dispose 语义（对齐 InMemorySecretVault，回滚路径依赖实例同一性） ---

    private static void DisposeDisplaced(
        IReadOnlyDictionary<string, SecureCredential> displaced,
        IReadOnlyDictionary<string, SecureCredential> incoming)
    {
        foreach (var (key, credential) in displaced)
        {
            if (incoming.TryGetValue(key, out var kept) && ReferenceEquals(kept, credential))
                continue;
            credential.Dispose();
        }
    }

    private static void DisposeAll(IReadOnlyDictionary<string, SecureCredential> secrets)
    {
        foreach (var credential in secrets.Values)
            credential.Dispose();
    }
}
