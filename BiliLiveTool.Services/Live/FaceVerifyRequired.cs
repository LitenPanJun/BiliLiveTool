namespace BiliLiveTool.Services.Live;

/// <summary>
/// 人脸验证要求：60024 取响应 data.qr，60043 自拼含 mid 的 URL，
/// 指南要求原样返回不吞。
/// </summary>
public sealed record FaceVerifyRequired(string Qr);
