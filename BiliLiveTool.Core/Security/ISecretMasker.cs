namespace BiliLiveTool.Core.Security;

/// <summary>日志脱敏的唯一出口，任何输出前必须经过它。</summary>
public interface ISecretMasker
{
    /// <summary>
    /// 掩码字符串：保留前后各 N 位，中间以 *** 替代；长度不足时返回 ***。
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
