#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 等待点信息（用于 SyncBarrier 缓存）
/// </summary>
public class WaitPointInfo
{
    public string SyncPointId { get; set; } = string.Empty;
    public DateTime ReceivedTime { get; set; }
    public string RouteId { get; set; } = string.Empty;
    public int WorldRound { get; set; }
    
    public bool IsExpired(TimeSpan expiry)
    {
        return DateTime.UtcNow - ReceivedTime > expiry;
    }
    
    public override string ToString()
    {
        return $"WaitPointInfo[SyncPoint={SyncPointId}, Route={RouteId}, Round={WorldRound}, Received={ReceivedTime:HH:mm:ss}]";
    }
}

public class SyncBarrier
{
    private readonly ILogger<SyncBarrier> _logger = App.GetLogger<SyncBarrier>();
    private readonly CoordinatorClient _client;
    private readonly TimeSpan _timeout;

    // === 路线跳过对齐修复（sync-point-route-skip-alignment）===
    private CancellationTokenSource? _routeSkippedCts;
    private volatile bool _routeSkippedSignalPending;

    // === 等待点上报修复（skip-route-wait-point-report）===
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WaitPointInfo> _recentWaitPoints = new();
    private const int MaxRecentWaitPoints = 10;
    private readonly object _waitPointLock = new();
    private string? _currentWaitingSyncPoint;

    public SyncBarrier(CoordinatorClient client, int timeoutSeconds = 60)
    {
        _client = client;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    public async Task<bool> WaitAsync(string syncPointId, CancellationToken ct)
    {
        return await WaitAsync(syncPointId, 0, ct);
    }

    /// <summary>
    /// 等待集合点同步
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="expectedCount">预期到达人数，0表示使用房间总人数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true=正常同步完成，false=超时放行</returns>
    public async Task<bool> WaitAsync(string syncPointId, int expectedCount, CancellationToken ct)
    {
        _logger.LogInformation("[SyncBarrier] 开始等待集合点: {SyncId}，超时={Timeout}s，预期人数={Expected}", 
            syncPointId, _timeout.TotalSeconds, expectedCount > 0 ? expectedCount : "全部");
        
        // 检查是否有待处理的路线跳过信号（sync-point-route-skip-alignment 修复）
        // 注意：如果是异常等待点，则不能被 RouteSkipped 信号放行
        bool isAbnormalWaitingPoint = IsAbnormalWaitingPoint(syncPointId);
        
        if (_routeSkippedSignalPending && !isAbnormalWaitingPoint)
        {
            _routeSkippedSignalPending = false;
            _logger.LogInformation("[SyncBarrier] 检测到路线跳过信号，立即放行集合点: {SyncId}", syncPointId);
            return false;
        }
        else if (isAbnormalWaitingPoint)
        {
            // 异常等待点，清除 RouteSkipped 信号，确保正常等待
            _routeSkippedSignalPending = false;
            _logger.LogInformation("[SyncBarrier] 异常等待点 {SyncId}，清除 RouteSkipped 信号并正常等待", syncPointId);
        }
        
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timeoutCts = new CancellationTokenSource(_timeout);
        
        // 创建路线跳过专用的 CancellationTokenSource（sync-point-route-skip-alignment 修复）
        var routeSkippedCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _routeSkippedCts, routeSkippedCts);
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, routeSkippedCts.Token);

        Action<string>? handler = null;
        handler = (arrivedSyncPointId) =>
        {
            _logger.LogInformation("[SyncBarrier] 收到 AllArrived 广播: {Arrived}，等待的: {SyncId}", arrivedSyncPointId, syncPointId);
            if (arrivedSyncPointId == syncPointId)
                tcs.TrySetResult(true);
        };

        _client.AllArrived += handler;
        try
        {
            _logger.LogInformation("[SyncBarrier] 上报到达集合点: {SyncId}，当前房间人数={RoomCount}，预期人数={Expected}", 
                syncPointId, _client.CurrentRoomPlayerCount, expectedCount > 0 ? expectedCount : "全部");
            await _client.ReportArrivalAsync(syncPointId, expectedCount);
            _logger.LogInformation("[SyncBarrier] 上报完成，等待其他玩家...");

            using var reg = linkedCts.Token.Register(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("[SyncBarrier] 外部取消，集合点: {SyncId}", syncPointId);
                    tcs.TrySetCanceled(ct);
                }
                else
                {
                    _logger.LogWarning("[SyncBarrier] 等待超时({Timeout}s)，自动放行，集合点: {SyncId}", _timeout.TotalSeconds, syncPointId);
                    tcs.TrySetResult(false);
                }
            });

            var result = await tcs.Task;
            _logger.LogInformation("[SyncBarrier] 集合点 {SyncId} 完成，结果: {Result}（true=正常同步，false=超时放行）", syncPointId, result);
            return result;
        }
        finally
        {
            _client.AllArrived -= handler;
            // 清理路线跳过专用的 CancellationTokenSource（sync-point-route-skip-alignment 修复）
            Interlocked.Exchange(ref _routeSkippedCts, null)?.Dispose();
            // 清理当前等待的同步点状态
            if (_currentWaitingSyncPoint == syncPointId)
            {
                _currentWaitingSyncPoint = null;
            }
        }
    }

    /// <summary>
    /// 检查指定同步点是否是异常等待点
    /// 异常等待点：有异常玩家在此点等待，不应被 RouteSkipped 信号放行
    /// </summary>
    public bool IsAbnormalWaitingPoint(string syncPointId)
    {
        try
        {
            // 清理过期等待点
            var expiredKeys = _recentWaitPoints
                .Where(kv => kv.Value.IsExpired(TimeSpan.FromMinutes(5)))
                .Select(kv => kv.Key)
                .ToList();
            
            foreach (var key in expiredKeys)
            {
                _recentWaitPoints.TryRemove(key, out _);
            }
            
            // 检查是否有匹配的异常等待点（5分钟内有效）
            foreach (var waitPoint in _recentWaitPoints.Values)
            {
                if (waitPoint.SyncPointId == syncPointId && !waitPoint.IsExpired(TimeSpan.FromMinutes(5)))
                {
                    _logger.LogDebug("[SyncBarrier] 找到异常等待点: {WaitPoint}", waitPoint);
                    return true;
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncBarrier] IsAbnormalWaitingPoint 异常");
            return false;
        }
    }

    /// <summary>
    /// 检查是否应该跳过指定同步点的等待（skip-route-wait-point-report 修复）
    /// 支持延迟到达的等待点上报（缓存最近N个）
    /// 注意：此方法已弃用，保留用于向后兼容
    /// </summary>
    [Obsolete("使用 IsAbnormalWaitingPoint 代替")]
    private bool ShouldSkipWaitForSyncPoint(string syncPointId)
    {
        try
        {
            // 清理过期等待点（超过60秒）
            var expiredKeys = _recentWaitPoints
                .Where(kv => kv.Value.IsExpired(TimeSpan.FromSeconds(60)))
                .Select(kv => kv.Key)
                .ToList();
            
            foreach (var key in expiredKeys)
            {
                _recentWaitPoints.TryRemove(key, out _);
            }
            
            // 检查是否有匹配的等待点
            foreach (var waitPoint in _recentWaitPoints.Values)
            {
                if (waitPoint.SyncPointId == syncPointId && !waitPoint.IsExpired(TimeSpan.FromSeconds(60)))
                {
                    _logger.LogDebug("[SyncBarrier] 找到匹配的等待点: {WaitPoint}", waitPoint);
                    return true;
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncBarrier] ShouldSkipWaitForSyncPoint 异常");
            return false;
        }
    }

    /// <summary>
    /// 记录等待点上报（skip-route-wait-point-report 修复）
    /// 供 MultiplayerCoordinator 调用
    /// </summary>
    public void RecordWaitPointReport(string routeId, string syncPointId, int worldRound)
    {
        try
        {
            var waitPoint = new WaitPointInfo
            {
                SyncPointId = syncPointId,
                RouteId = routeId,
                WorldRound = worldRound,
                ReceivedTime = DateTime.UtcNow
            };
            
            _recentWaitPoints[syncPointId] = waitPoint;
            
            // 限制缓存大小
            if (_recentWaitPoints.Count > MaxRecentWaitPoints)
            {
                var oldestKey = _recentWaitPoints
                    .OrderBy(kv => kv.Value.ReceivedTime)
                    .First().Key;
                _recentWaitPoints.TryRemove(oldestKey, out _);
            }
            
            _logger.LogDebug("[SyncBarrier] 记录等待点上报: {WaitPoint}", waitPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncBarrier] RecordWaitPointReport 异常");
        }
    }

    /// <summary>
    /// 额外等待：标准超时后，为异常状态成员提供额外等待时间。
    /// 监听 AllArrived 事件，超时后返回 false。
    /// </summary>
    public async Task<bool> WaitExtraAsync(string syncPointId, int extraWaitSeconds, CancellationToken ct)
    {
        return await WaitExtraAsync(syncPointId, extraWaitSeconds, 0, ct);
    }

    /// <summary>
    /// 额外等待：标准超时后，为异常状态成员提供额外等待时间。
    /// 监听 AllArrived 事件，超时后返回 false。
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="extraWaitSeconds">额外等待秒数</param>
    /// <param name="expectedCount">预期到达人数，0表示使用房间总人数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true=全员到达，false=超时放行</returns>
    public async Task<bool> WaitExtraAsync(string syncPointId, int extraWaitSeconds, int expectedCount, CancellationToken ct)
    {
        _logger.LogInformation("[SyncBarrier] 开始额外等待: {SyncId}，额外超时={Extra}s，预期人数={Expected}", 
            syncPointId, extraWaitSeconds, expectedCount > 0 ? expectedCount : "全部");
        
        // 检查是否有待处理的路线跳过信号（sync-point-route-skip-alignment 修复）
        // 注意：如果是异常等待点，则不能被 RouteSkipped 信号放行
        bool isAbnormalWaitingPoint = IsAbnormalWaitingPoint(syncPointId);
        
        if (_routeSkippedSignalPending && !isAbnormalWaitingPoint)
        {
            _routeSkippedSignalPending = false;
            _logger.LogInformation("[SyncBarrier] 额外等待期间检测到路线跳过信号，立即放行: {SyncId}", syncPointId);
            return false;
        }
        else if (isAbnormalWaitingPoint)
        {
            // 异常等待点，清除 RouteSkipped 信号，确保正常等待
            _routeSkippedSignalPending = false;
            _logger.LogInformation("[SyncBarrier] 异常等待点 {SyncId}，清除 RouteSkipped 信号并正常额外等待", syncPointId);
        }
        
        // 跟踪当前等待的同步点
        _currentWaitingSyncPoint = syncPointId;
        
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(extraWaitSeconds));
        
        // 创建路线跳过专用的 CancellationTokenSource（sync-point-route-skip-alignment 修复）
        var routeSkippedCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _routeSkippedCts, routeSkippedCts);
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, routeSkippedCts.Token);

        Action<string>? handler = null;
        handler = (arrivedSyncPointId) =>
        {
            if (arrivedSyncPointId == syncPointId)
            {
                _logger.LogInformation("[SyncBarrier] 额外等待期间收到 AllArrived: {SyncId}", syncPointId);
                tcs.TrySetResult(true);
            }
        };

        _client.AllArrived += handler;
        try
        {
            // 上报到达（带预期人数）
            await _client.ReportArrivalAsync(syncPointId, expectedCount);
            
            using var reg = linkedCts.Token.Register(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("[SyncBarrier] 额外等待期间外部取消: {SyncId}", syncPointId);
                    tcs.TrySetCanceled(ct);
                }
                else
                {
                    _logger.LogWarning("[SyncBarrier] 额外等待超时({Extra}s)，放行: {SyncId}", extraWaitSeconds, syncPointId);
                    tcs.TrySetResult(false);
                }
            });

            var result = await tcs.Task;
            _logger.LogInformation("[SyncBarrier] 额外等待完成: {SyncId}，结果: {Result}（true=全员到达，false=超时放行）", syncPointId, result);
            return result;
        }
        finally
        {
            _client.AllArrived -= handler;
            // 清理路线跳过专用的 CancellationTokenSource（sync-point-route-skip-alignment 修复）
            Interlocked.Exchange(ref _routeSkippedCts, null)?.Dispose();
            // 清理当前等待的同步点状态
            if (_currentWaitingSyncPoint == syncPointId)
            {
                _currentWaitingSyncPoint = null;
            }
        }
    }

    /// <summary>
    /// 信号路线跳过（sync-point-route-skip-alignment 修复）
    /// 设置 _routeSkippedSignalPending 标志并取消当前等待
    /// </summary>
    public void SignalRouteSkipped()
    {
        _routeSkippedSignalPending = true;
        Interlocked.Exchange(ref _routeSkippedCts, null)?.Cancel();
        _logger.LogInformation("[SyncBarrier] 路线跳过信号已发送");
    }

    /// <summary>
    /// 重置路线跳过状态（sync-point-route-skip-alignment 修复）
    /// 每轮开始时调用，防止跨轮残留信号
    /// </summary>
    public void Reset()
    {
        _routeSkippedSignalPending = false;
        Interlocked.Exchange(ref _routeSkippedCts, null)?.Dispose();
        
        // 清理等待点状态（skip-route-wait-point-report 修复）
        _recentWaitPoints.Clear();
        _currentWaitingSyncPoint = null;
        
        _logger.LogDebug("[SyncBarrier] 路线跳过状态和等待点状态已重置");
    }
}
