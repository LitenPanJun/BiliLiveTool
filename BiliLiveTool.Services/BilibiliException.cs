namespace BiliLiveTool.Services;

/// <summary>B站业务失败（指南样例：非人脸路径统一抛出，携带响应 code 与 message）。</summary>
public sealed class BilibiliException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
