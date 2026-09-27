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
