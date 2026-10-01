using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BiliLiveTool.Infrastructure.Danmu;

/// <summary>
/// 弹幕帧编解码，复刻 danmu_service.py 的 struct !IHHII 与解包分支：
/// body 恒从 16 字节起（不信任 header_len 字段）、proto 2=zlib / 3=brotli
/// 递归解包、坏帧跳过并保留剩余缓冲（对照分析报告的解包缺陷修正）。
/// </summary>
internal sealed class DanmuPacketCodec
{
    private const int HeaderSize = 16;

    public byte[] Encode(int operation, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var buf = new byte[HeaderSize + payload.Length];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[..4], (uint)buf.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], (uint)operation);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], 1);
        payload.CopyTo(span[HeaderSize..]);
        return buf;
    }

    public DanmuDecodeResult Decode(byte[] data)
    {
        var frames = new List<DanmuFrame>();
        string? error = null;
        var offset = 0;

        while (offset < data.Length)
        {
            if (data.Length - offset < HeaderSize)
            {
                // 帧头截断：剩余字节无法定位下一帧，停止但保留已解出的帧
                error ??= $"帧头截断：剩余 {data.Length - offset} 字节";
                break;
            }

            var span = data.AsSpan(offset);
            var packetLen = BinaryPrimitives.ReadUInt32BigEndian(span[..4]);
            var protoVer = BinaryPrimitives.ReadUInt16BigEndian(span[6..8]);
            var operation = BinaryPrimitives.ReadInt32BigEndian(span[8..12]);

            if (packetLen < HeaderSize || packetLen > (uint)(data.Length - offset))
            {
                error ??= $"非法包长 {packetLen}";
                break;
            }

            var body = data.AsSpan(offset + HeaderSize, (int)packetLen - HeaderSize);
            if (protoVer == 2 || protoVer == 3)
            {
                try
                {
                    var inner = Decompress(body, protoVer == 2);
                    var nested = Decode(inner);
                    frames.AddRange(nested.Frames);
                    error ??= nested.Error;
                }
                catch (Exception e)
                {
                    // 解压失败：跳过坏帧继续消费剩余缓冲，错误只记首个
                    error ??= $"解压失败：{e.Message}";
                }
            }
            else
            {
                frames.Add(new DanmuFrame(operation, body.ToArray()));
            }

            offset += (int)packetLen;
        }

        return new DanmuDecodeResult(frames, error);
    }

    private static byte[] Decompress(ReadOnlySpan<byte> body, bool isZlib)
    {
        using var input = new MemoryStream(body.ToArray(), writable: false);
        using Stream inflater = isZlib
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflater.CopyTo(output);
        return output.ToArray();
    }
}
