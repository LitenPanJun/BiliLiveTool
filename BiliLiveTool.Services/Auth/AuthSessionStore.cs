using BiliLiveTool.Core.State;

namespace BiliLiveTool.Services.Auth;

/// <summary>
/// 唯一会话真相：可变会话集中于此并加锁，对外只暴露不可变 record 快照。
/// csrf 由 bili_jct（SecureCredential）派生，禁止在任何地方留存明文副本。
/// </summary>
public sealed class AuthSessionStore
{
    private readonly object _gate = new();
    private SessionSnapshot _current = SessionSnapshot.Empty;
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> _partitions =
        new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);

    public SessionSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>分区映射（Area/getList 建成 {主:{子:id}}），随会话统一持有。</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Partitions
    {
        get
        {
            lock (_gate)
            {
                return _partitions;
            }
        }
    }

    public void SetPartitions(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> partitions)
    {
        lock (_gate)
        {
            _partitions = partitions;
        }
    }

    /// <summary>按主/子分区名反查 area_id，未命中返回 null。</summary>
    public int? ResolveAreaId(string parentName, string subName)
    {
        lock (_gate)
        {
            return _partitions.TryGetValue(parentName, out var subs) && subs.TryGetValue(subName, out var id)
                ? id
                : null;
        }
    }

    /// <summary>替换当前快照并返回旧快照，供切换失败时回滚。</summary>
    public SessionSnapshot Replace(SessionSnapshot next)
    {
        lock (_gate)
        {
            var previous = _current;
            _current = next;
            return previous;
        }
    }

    /// <summary>清空会话（登出），返回旧快照供失败回滚。</summary>
    public SessionSnapshot Clear() => Replace(SessionSnapshot.Empty);
}
