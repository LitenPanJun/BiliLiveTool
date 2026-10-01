namespace BiliLiveTool.Core.Security;

/// <summary>日志脱敏的唯一出口，任何输出前必须经过它。</summary>
public interface ISecretMasker
{
    /// <summary>
    /// 掩码字符串（util.mask_string 语义）：空值返回空串；长度不足时按位补 *；
    /// 否则保留前后各 N 位，中间以 5 个 * 替代。
    /// </summary>
    string MaskString(string? value, int visibleStart = 4, int visibleEnd = 4);

    /// <summary>脱敏 URL 查询串中的敏感参数（uid/room_id/key/token/csrf/csrf_token/access_key/qrcode_key）。</summary>
    string MaskUrl(string url);

    /// <summary>
    /// 递归脱敏 JSON 数据中敏感键（rtmp/addr/code/key/token/csrf 等）的值；
    /// 解析失败时返回 "Parse Error"。
    /// </summary>
    string MaskData(string json);
}
