using BiliLiveTool.Core.Auth;

namespace BiliLiveTool.Services.Auth;

/// <summary>
/// 进程内账号注册表（对照原 config_manager 的 users 字典 + current_uid）；
/// 持久化后端随 P2 落地，此处先立接口缝。
/// </summary>
public sealed class InMemoryAccountStore : IAccountStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountRecord> _accounts = new(StringComparer.Ordinal);
    private string? _currentUid;

    public string? CurrentUid
    {
        get
        {
            lock (_gate)
            {
                return _currentUid;
            }
        }
    }

    public AccountRecord? Get(string uid)
    {
        lock (_gate)
        {
            return _accounts.GetValueOrDefault(uid);
        }
    }

    public IReadOnlyList<AccountRecord> List()
    {
        lock (_gate)
        {
            return _accounts.Values.ToList();
        }
    }

    public void Save(AccountRecord account)
    {
        lock (_gate)
        {
            _accounts[account.Uid] = account;
            _currentUid = account.Uid;
        }
    }

    public void Upsert(AccountRecord account)
    {
        lock (_gate)
        {
            _accounts[account.Uid] = account;
        }
    }

    public void SetCurrent(string? uid)
    {
        lock (_gate)
        {
            _currentUid = uid;
        }
    }

    public bool Remove(string uid)
    {
        lock (_gate)
        {
            var removed = _accounts.Remove(uid);
            if (removed && _currentUid == uid) _currentUid = null;
            return removed;
        }
    }
}
