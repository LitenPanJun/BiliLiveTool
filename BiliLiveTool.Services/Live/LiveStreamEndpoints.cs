namespace BiliLiveTool.Services.Live;

/// <summary>单路推流端点（对照原返回 data 的 addr/code 字段）。</summary>
public sealed record LiveEndpoint(string Addr, string Code);

/// <summary>开播成功的三路推流地址（rtmp 主体 + protocols 双路解析）。</summary>
public sealed record LiveStreamEndpoints(LiveEndpoint Rtmp1, LiveEndpoint Rtmp2, LiveEndpoint Srt);
