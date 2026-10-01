namespace BiliLiveTool.Core.Auth;

/// <summary>扫码会话，字段对照 passport qrcode/generate 的 data{url, qrcode_key}。</summary>
public sealed record QrCodeSession(string Url, string QrcodeKey);
