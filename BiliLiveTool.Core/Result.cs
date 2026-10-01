namespace BiliLiveTool.Core;

/// <summary>双路结果：成功携带 <typeparamref name="TValue"/>，失败携带 <typeparamref name="TError"/>。</summary>
public sealed record Result<TValue, TError>
{
    private Result(bool isOk, TValue? value, TError? error)
    {
        IsOk = isOk;
        Value = value;
        Error = error;
    }

    public bool IsOk { get; }

    public TValue? Value { get; }

    public TError? Error { get; }

    public static Result<TValue, TError> Ok(TValue value) => new(true, value, default);

    public static Result<TValue, TError> Fail(TError error) => new(false, default, error);
}
