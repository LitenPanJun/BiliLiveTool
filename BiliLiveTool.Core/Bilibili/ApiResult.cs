using System.Text.Json.Nodes;

namespace BiliLiveTool.Core.Bilibili;

/// <summary>
/// 通用请求结果，对应原 Python _req 的 (success, json) 返回：
/// Success 表示网络与 JSON 解析成功，Code/Message/Data 取自响应体。
/// ResponseCookies 仅在需要从 Set-Cookie 取身份的接口（如二维码 poll）返回。
/// </summary>
public sealed record ApiResult(
    bool Success,
    int Code,
    string Message,
    JsonObject? Data,
    IReadOnlyDictionary<string, string>? ResponseCookies = null);
