using BiliLiveTool.Core.Security;

namespace BiliLiveTool.Services.Auth;

/// <summary>
/// 进程内存机密集合（P1）：整体替换、删除即清零；
/// 系统钥匙串后端随 P2 落地，此处先立接口缝。
/// </summary>
public sealed class InMemorySecretVault : ISecretVault
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, SecureCredential>> _vault =
        new(StringComparer.Ordinal);

    public void Save(string uid, IReadOnlyDictionary<string, SecureCredential> secrets)
    {
        lock (_gate)
        {
            if (_vault.Remove(uid, out var displaced)) DisposeDisplaced(displaced, secrets);
            _vault[uid] = new Dictionary<string, SecureCredential>(secrets, StringComparer.Ordinal);
        }
    }

    public IReadOnlyDictionary<string, SecureCredential>? Load(string uid)
    {
        lock (_gate)
        {
            return _vault.TryGetValue(uid, out var secrets)
                ? new Dictionary<string, SecureCredential>(secrets, StringComparer.Ordinal)
                : null;
        }
    }

    public bool Remove(string uid)
    {
        lock (_gate)
        {
            if (!_vault.Remove(uid, out var displaced)) return false;
            DisposeAll(displaced);
            return true;
        }
    }

    // 回滚路径会把 Load 出的原实例重新 Save：同一实例不得被误清零
    private static void DisposeDisplaced(
        IReadOnlyDictionary<string, SecureCredential> displaced,
        IReadOnlyDictionary<string, SecureCredential> incoming)
    {
        foreach (var (key, credential) in displaced)
        {
            if (incoming.TryGetValue(key, out var kept) && ReferenceEquals(kept, credential)) continue;
            credential.Dispose();
        }
    }

    private static void DisposeAll(IReadOnlyDictionary<string, SecureCredential> secrets)
    {
        foreach (var credential in secrets.Values) credential.Dispose();
    }
}
