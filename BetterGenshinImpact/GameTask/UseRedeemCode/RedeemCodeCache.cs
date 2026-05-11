using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.UseRedeemCode;

/// <summary>
/// 内存缓存，记录已使用/已过期/兑换失败的兑换码
/// </summary>
public class RedeemCodeCache
{
    private static readonly ILogger _logger = App.GetLogger<RedeemCodeCache>();

    /// <summary>
    /// 已使用的兑换码缓存 (code -> 记录时间)
    /// </summary>
    private static readonly Dictionary<string, DateTime> _usedCodes = new();

    /// <summary>
    /// 过期或失败的兑换码缓存 (code -> 过期日期)
    /// </summary>
    private static readonly Dictionary<string, DateTime> _failedCodes = new();

    private static readonly TimeSpan _usedCodeExpiration = TimeSpan.FromDays(30);
    private static readonly TimeSpan _failedCodeExpiration = TimeSpan.FromDays(1);

    /// <summary>
    /// 检查兑换码是否已使用或已知过期/失败
    /// </summary>
    public static bool IsCodeKnown(string code)
    {
        CleanExpiredEntries();
        return _usedCodes.ContainsKey(code) || _failedCodes.ContainsKey(code);
    }

    /// <summary>
    /// 记录兑换成功的码
    /// </summary>
    public static void MarkAsUsed(string code)
    {
        _usedCodes[code] = DateTime.Now;
        _logger.LogDebug("兑换码 {Code} 已标记为已使用", code);
    }

    /// <summary>
    /// 记录兑换失败的码（已过期或服务器拒绝）
    /// </summary>
    public static void MarkAsFailed(string code, DateTime? expireDate = null)
    {
        // 注意：不再使用 expireDate 作为缓存过期时间，避免过期日期早于当前日期时
        // 缓存立即失效的问题。统一使用 _failedCodeExpiration（1天）作为缓存有效期，
        // 确保当天已知失败的码不会重复尝试。
        _failedCodes[code] = DateTime.Now.Add(_failedCodeExpiration);
        _logger.LogDebug("兑换码 {Code} 已标记为失败/过期（兑换码有效期：{ExpireDate}）", code, expireDate?.ToString("yyyy-MM-dd") ?? "未知");
    }

    /// <summary>
    /// 清理过期的缓存条目
    /// </summary>
    private static void CleanExpiredEntries()
    {
        var now = DateTime.Now;

        // 清理已使用缓存（30天后清理）
        var usedToRemove = new List<string>();
        foreach (var kvp in _usedCodes)
        {
            if (now - kvp.Value > _usedCodeExpiration)
            {
                usedToRemove.Add(kvp.Key);
            }
        }
        foreach (var key in usedToRemove)
        {
            _usedCodes.Remove(key);
        }

        // 清理失败缓存（根据缓存的过期时间清理）
        var failedToRemove = new List<string>();
        foreach (var kvp in _failedCodes)
        {
            if (now > kvp.Value)
            {
                failedToRemove.Add(kvp.Key);
            }
        }
        foreach (var key in failedToRemove)
        {
            _failedCodes.Remove(key);
        }
    }

    /// <summary>
    /// 清空所有缓存
    /// </summary>
    public static void Clear()
    {
        _usedCodes.Clear();
        _failedCodes.Clear();
        _logger.LogInformation("兑换码缓存已清空");
    }
}
