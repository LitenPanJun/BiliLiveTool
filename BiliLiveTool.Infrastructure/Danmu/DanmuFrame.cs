namespace BiliLiveTool.Infrastructure.Danmu;

/// <summary>解包后的普通帧（压缩帧已递归展开）。</summary>
internal readonly record struct DanmuFrame(int Operation, byte[] Body);

/// <summary>解包结果：帧序列加首个结构性错误，供调用方记 debug 日志。</summary>
internal sealed record DanmuDecodeResult(IReadOnlyList<DanmuFrame> Frames, string? Error);
