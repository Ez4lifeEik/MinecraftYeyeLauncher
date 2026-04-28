using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ArclightLauncher.Helpers;

/// <summary>
/// 文件哈希工具
/// </summary>
public static class HashHelper
{
    /// <summary>
    /// 计算文件的 SHA1，返回小写十六进制字符串
    /// </summary>
    public static async Task<string> ComputeSha1Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);

        var bytes = await SHA1.HashDataAsync(stream, ct);
        return BytesToHex(bytes);
    }

    /// <summary>
    /// 计算字节数组的 SHA1，返回小写十六进制字符串
    /// </summary>
    public static string ComputeSha1(byte[] data)
    {
        var bytes = SHA1.HashData(data);
        return BytesToHex(bytes);
    }

    /// <summary>
    /// 字节数组 → 小写十六进制字符串（不含分隔符）
    /// </summary>
    public static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
