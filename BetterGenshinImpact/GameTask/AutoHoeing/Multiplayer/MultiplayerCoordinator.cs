#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

public class MultiplayerCoordinator : IAsyncDisposable
{
    private readonly ILogger<MultiplayerCoordinator> _logger = App.GetLogger<MultiplayerCoordinator>();
    private readonly CoordinatorClient _client;
    private readonly SyncBarrier _barrier;
    private readonly SyncPointResolver _resolver;
    private readonly int _minPlayersToSync;
    private readonly int _syncTimeoutSeconds;

    private int _kazuhaPlayerIndex;
    private int _myPlayerIndex; // 1-based
    private readonly Action<string, MemberStatus> _onMemberStatusChangedHandler;

    // === 同步点超时容错（需求 5）===
    private int _consecutiveSyncTimeouts;
    private bool _consecutiveSyncTimeoutFired;
    private const int MaxConsecutiveSyncTimeouts = 3;

    // === 原子退出（需求 8）===
    private int _exitTriggered; // 0=未触发, 1=已触发
    private CancellationTokenSource? _stopCts; // 用于取消本地任务

    // === 成员离线感知 ===
    private readonly HashSet<string> _offlineMembers = new();
    private readonly object _offlineLock = new();

    // === 路线跳过对齐修复（sync-point-route-skip-alignment）===
    private volatile bool _skipNextSyncPoint;
    private readonly Action<string, int> _onRouteSkippedHandler;

    // === 等待点上报修复（skip-route-wait-point-report）===
    private readonly ConcurrentDictionary<string, WaitPointState> _peerWaitPoints = new();
    private (string routeId, string syncPointId, int worldRound)? _reportedWaitPoint;
    private readonly WaitPointStateManager _stateManager;
    private readonly Action<string, string, string, int, DateTime> _onWaitPointReportedHandler;

    // === 异常玩家超时退出机制 ===
    private readonly ConcurrentDictionary<string, AbnormalPlayerTimeoutContext> _abnormalPlayerTimeouts = new();

    /// <summary>
    /// 获取玩家显示名称（优先使用名称，找不到则显示 UID 前后各3位）
    /// </summary>
    private string GetPlayerDisplayName(string playerUid)
    {
        if (string.IsNullOrEmpty(playerUid)) return "未知玩家";
        
        // 从当前玩家列表查找
        var player = _client.CurrentPlayerList.FirstOrDefault(p => p.PlayerUid == playerUid);
        if (player != null && !string.IsNullOrEmpty(player.PlayerName))
            return player.PlayerName;
        
        // 找不到名称，显示 UID 前后各3位
        if (playerUid.Length > 6)
            return $"{playerUid[..3]}***{playerUid[^3..]}";
        return playerUid;
    }

    /// <summary>
    /// 异常玩家超时上下文
    /// </summary>
    private class AbnormalPlayerTimeoutContext
    {
        public string PlayerUid { get; set; } = "";
        public string SyncPointId { get; set; } = "";
        public string RouteId { get; set; } = "";
        public DateTime StartTime { get; set; }
        public CancellationTokenSource Cts { get; set; } = new();
        public bool HasExited { get; set; }
    }

    public bool IsActive { get; private set; } = true;
    public bool IsKazuhaPlayer => _kazuhaPlayerIndex > 0 && _myPlayerIndex == _kazuhaPlayerIndex;

    /// <summary>当前是否为房主（动态判断，多世界模式下每轮可能不同）</summary>
    public bool IsHost => !string.IsNullOrEmpty(_client.HostPlayerUid)
        && _client.HostPlayerUid == TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;

    /// <summary>外部传入的 CancellationTokenSource，协调停止时 Cancel 它来停止任务。</summary>
    public CancellationTokenSource? StopCts
    {
        get => _stopCts;
        set => _stopCts = value;
    }

    /// <summary>退出是否已触发。</summary>
    public bool IsExitTriggered => _exitTriggered == 1;

    /// <summary>
    /// 等待点状态管理器（skip-route-wait-point-report 修复）
    /// </summary>
    public WaitPointStateManager StateManager => _stateManager;

    /// <summary>
    /// 异常玩家超时时间（5分钟）
    /// </summary>
    private readonly TimeSpan _abnormalPlayerTimeout = TimeSpan.FromMinutes(5);

    public event Action<string>? OnDegraded;

    /// <summary>
    /// 连续超时达到上限时触发。参数 isHost 表示当前是否为房主。
    /// 房主应广播 HostLeft 并停止；成员应上报 Offline 并退出世界。
    /// </summary>
    public event Func<bool, Task>? OnConsecutiveSyncTimeoutExceeded;

    public MultiplayerCoordinator(
        CoordinatorClient client,
        SyncBarrier barrier,
        SyncPointResolver resolver,
        int minPlayersToSync = 0,
        int syncTimeoutSeconds = 60)
    {
        _client = client;
        _barrier = barrier;
        _resolver = resolver;
        _minPlayersToSync = minPlayersToSync;
        _syncTimeoutSeconds = syncTimeoutSeconds;

        _client.OnDegraded += () => Degrade("连接断开且重连失败");
        _client.KazuhaPlayerUpdated += idx =>
        {
            _kazuhaPlayerIndex = idx;
            _logger.LogInformation("[联机] 万叶玩家索引更新为 {Idx}", idx);
        };
        _client.PlayerListUpdated += OnPlayerListUpdated;

        // 成员异常恢复状态变化（需求 7）+ 成员离线感知
        _onMemberStatusChangedHandler = (playerUid, status) =>
        {
            _logger.LogInformation("[联机] 成员状态变化: {PlayerName} → {Status}", GetPlayerDisplayName(playerUid), status);
            if (status == MemberStatus.Offline)
            {
                _logger.LogWarning("[联机] 成员 {PlayerName} 已离线", GetPlayerDisplayName(playerUid));
                lock (_offlineLock) { _offlineMembers.Add(playerUid); }

                // 房主检查剩余在线成员数（需求 2.2）
                if (IsHost)
                {
                    int onlineMembers;
                    lock (_offlineLock)
                    {
                        // 房间总人数 - 离线人数 - 房主自己 = 在线成员数
                        onlineMembers = _client.CurrentRoomPlayerCount - _offlineMembers.Count - 1;
                    }

                    if (onlineMembers <= 0)
                    {
                        _logger.LogError("[联机] 所有成员已离线，房主停止任务");
                        _ = TriggerCoordinatedStop(true, "所有成员已离线");
                    }
                    else
                    {
                        _logger.LogInformation("[联机] 成员离线，剩余 {Count} 个在线成员，继续执行", onlineMembers);
                    }
                }
            }
        };
        _client.OnMemberStatusChanged += _onMemberStatusChangedHandler;

        // 成员侧：监听 RoomClosed 事件（需求 2.5）
        _client.RoomClosed += reason =>
        {
            _logger.LogError("[联机] 收到 RoomClosed: {Reason}，触发协调停止", reason);
            _ = TriggerCoordinatedStop(false, $"房间已关闭: {reason}");
        };

        // 路线跳过事件处理（sync-point-route-skip-alignment 修复）
        _onRouteSkippedHandler = (playerUid, routeIndex) =>
        {
            _logger.LogInformation("[联机] 收到路线跳过通知: {PlayerName} 跳过路线 {Index}，立即放行当前同步点", GetPlayerDisplayName(playerUid), routeIndex);
            _barrier.SignalRouteSkipped();
        };
        _client.RouteSkipped += _onRouteSkippedHandler;

        // 等待点上报事件处理（skip-route-wait-point-report 修复）
        _stateManager = new WaitPointStateManager();
        _onWaitPointReportedHandler = (playerUid, routeId, syncPointId, worldRound, timestamp) =>
        {
            _logger.LogInformation("[联机] 收到等待点上报: {PlayerName} 在路线 {RouteId} 的同步点 {SyncPointId} 等待 (轮次 {WorldRound})", 
                GetPlayerDisplayName(playerUid), routeId, syncPointId, worldRound);
            
            // 更新等待点状态（WaitPointStateManager会自动记录为异常玩家）
            var state = new WaitPointState
            {
                PlayerUid = playerUid,
                RouteId = routeId,
                SyncPointId = syncPointId,
                WorldRound = worldRound,
                LastUpdated = timestamp
            };
            _stateManager.UpdateState(playerUid, state);
            _logger.LogInformation("[联机] 记录异常玩家: {PlayerName} 在同步点 {SyncPointId} 等待", GetPlayerDisplayName(playerUid), syncPointId);
            
            // 同步记录到 SyncBarrier，确保 ShouldSkipWaitForSyncPoint 能正确检测异常等待点
            _barrier.RecordWaitPointReport(routeId, syncPointId, worldRound);
            _logger.LogDebug("[联机] 已记录等待点到 SyncBarrier: {SyncPointId}", syncPointId);
        };
        _client.WaitPointReported += _onWaitPointReportedHandler;
    }

    /// <summary>
    /// 上报等待点（skip-route-wait-point-report 修复）
    /// 调用 CoordinatorClient.SendWaitPointReportAsync，包含容错处理（失败时回退到 RouteSkipped）
    /// 记录上报日志用于监控
    /// </summary>
    public async Task<bool> ReportWaitPointAsync(string routeId, string syncPointId, int worldRound)
    {
        try
        {
            // 更新本地状态
            var playerUid = TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
            var state = new WaitPointState
            {
                PlayerUid = playerUid,
                RouteId = routeId,
                SyncPointId = syncPointId,
                WorldRound = worldRound,
                LastUpdated = DateTime.UtcNow
            };
            _stateManager.UpdateState(playerUid, state);
            _reportedWaitPoint = (routeId, syncPointId, worldRound);
            
            // 发送等待点上报
            var success = await _client.SendWaitPointReportAsync(routeId, syncPointId, worldRound);
            
            if (success)
            {
                _logger.LogInformation("[联机] 等待点上报成功: Route={RouteId}, SyncPoint={SyncPointId}, Round={WorldRound}", 
                    routeId, syncPointId, worldRound);
                
                // 启动5分钟超时计时器
                var myPlayerUid = TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
                StartAbnormalPlayerTimeout(myPlayerUid, syncPointId, routeId);
                
                return true;
            }
            else
            {
                // 上报失败，回退到 RouteSkipped 机制
                _logger.LogWarning("[联机] 等待点上报失败，回退到 RouteSkipped 机制");
                
                // 获取路线索引
                if (int.TryParse(routeId, out var routeIndex))
                {
                    await _client.SendRouteSkippedAsync(routeIndex);
                    _logger.LogInformation("[联机] 已发送 RouteSkipped 作为回退: 路线 {Index}", routeIndex);
                }
                else
                {
                    _logger.LogError("[联机] 无法解析路线ID: {RouteId}", routeId);
                }
                
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[联机] ReportWaitPointAsync 异常");
            return false;
        }
    }

    /// <summary>
    /// 为异常玩家启动5分钟超时计时器
    /// </summary>
    public void StartAbnormalPlayerTimeout(string playerUid, string syncPointId, string routeId)
    {
        var context = new AbnormalPlayerTimeoutContext
        {
            PlayerUid = playerUid,
            SyncPointId = syncPointId,
            RouteId = routeId,
            StartTime = DateTime.UtcNow,
            Cts = new CancellationTokenSource()
        };
        
        _abnormalPlayerTimeouts[playerUid] = context;
        
        // 启动5分钟计时器
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_abnormalPlayerTimeout, context.Cts.Token);
                
                // 超时到达，检查是否已汇合
                if (!context.HasExited && _abnormalPlayerTimeouts.ContainsKey(playerUid))
                {
                    _logger.LogWarning("[联机] 异常玩家 {PlayerName} 等待超时，触发退出", GetPlayerDisplayName(playerUid));
                    await TriggerExitForAbnormalPlayer(playerUid);
                }
            }
            catch (TaskCanceledException) { } // 正常取消
            catch (Exception ex) { _logger.LogError(ex, "[联机] 异常玩家超时计时器异常"); }
        });
        
        _logger.LogInformation("[联机] 已为异常玩家 {PlayerName} 启动5分钟超时计时器，SyncPoint={SyncPointId}", GetPlayerDisplayName(playerUid), syncPointId);
    }

    /// <summary>
    /// 触发异常玩家退出（退世界/退房间）
    /// </summary>
    private async Task TriggerExitForAbnormalPlayer(string playerUid)
    {
        if (!_abnormalPlayerTimeouts.TryGetValue(playerUid, out var context))
            return;
        
        if (context.HasExited)
            return;
        
        context.HasExited = true;
        
        // 检查是否所有人已到达（如果已到达则不触发退出）
        var arrivedCount = GetArrivedPlayerCount(context.SyncPointId);
        var expectedCount = GetExpectedPlayerCountForSyncPoint(context.SyncPointId);
        
        if (arrivedCount >= expectedCount)
        {
            _logger.LogInformation("[联机] 异常玩家 {PlayerName} 超时但人已到齐，不触发退出", GetPlayerDisplayName(playerUid));
            _abnormalPlayerTimeouts.TryRemove(playerUid, out _);
            return;
        }
        
        var isHost = IsHost;
        await TriggerCoordinatedStop(isHost, $"异常玩家等待超时: {GetPlayerDisplayName(playerUid)}");
        
        // 清理
        _abnormalPlayerTimeouts.TryRemove(playerUid, out _);
    }

    /// <summary>
    /// 获取已到达指定同步点的玩家数量
    /// </summary>
    private int GetArrivedPlayerCount(string syncPointId)
    {
        // 通过 SyncBarrier 的状态或成员进度判断
        // 这里简化处理，返回当前同步点等待的异常玩家数 + 已上报到达的正常玩家
        int abnormalAtPoint = _stateManager.GetAbnormalPlayersAtPoint(syncPointId);
        
        // 正常玩家通过 ReportArrival 上报，这里简单返回异常玩家数作为基数
        // 实际应通过更精确的状态跟踪
        return abnormalAtPoint + 1; // +1 为自己
    }

    /// <summary>
    /// 获取指定同步点的预期玩家数量
    /// </summary>
    private int GetExpectedPlayerCountForSyncPoint(string syncPointId)
    {
        return CalculateEffectiveWaitCount(syncPointId);
    }

    /// <summary>
    /// 计算指定同步点的有效等待人数
    /// 核心逻辑：只计算已到达该线路的玩家
    /// </summary>
    private int CalculateEffectiveWaitCount(string syncId)
    {
        // 获取当前同步点等待的异常玩家数
        int abnormalAtPoint = _stateManager.GetAbnormalPlayersAtPoint(syncId);
        
        // 获取当前同步点关联的线路ID
        string? routeId = GetRouteIdForSyncPoint(syncId);
        
        if (routeId == null)
        {
            // 无法确定线路，按原有逻辑计算
            int offlineCount;
            lock (_offlineLock) { offlineCount = _offlineMembers.Count; }
            return Math.Max(1, _client.CurrentRoomPlayerCount - offlineCount);
        }
        
        // 统计已在该线路的玩家数（通过心跳进度判断）
        int playersInRoute = GetPlayersInRoute(routeId);
        
        // 有效等待人数 = 已在该线路的玩家 + 在该点等待的异常玩家
        int effectiveCount = playersInRoute + abnormalAtPoint;
        
        _logger.LogInformation("[联机] 同步点 {SyncId} 有效等待人数计算: 线路{RouteId}玩家={PlayersInRoute}, " +
            "异常等待={AbnormalAtPoint}, 总计={EffectiveCount}", 
            syncId, routeId, playersInRoute, abnormalAtPoint, effectiveCount);
        
        return Math.Max(1, effectiveCount);
    }

    /// <summary>
    /// 获取指定同步点关联的线路ID
    /// </summary>
    private string? GetRouteIdForSyncPoint(string syncId)
    {
        // 从异常玩家等待点状态中获取
        foreach (var state in _stateManager.GetAllValidStates())
        {
            if (state.SyncPointId == syncId)
            {
                return state.RouteId;
            }
        }
        
        // 从自己的上报中获取
        if (_reportedWaitPoint.HasValue && _reportedWaitPoint.Value.syncPointId == syncId)
        {
            return _reportedWaitPoint.Value.routeId;
        }
        
        return null;
    }

    /// <summary>
    /// 获取指定线路ID的玩家数（通过进度缓存判断）
    /// </summary>
    private int GetPlayersInRoute(string routeId)
    {
        if (!int.TryParse(routeId, out var routeIndex))
            return _client.CurrentRoomPlayerCount;
        
        int count = 0;
        var myUid = TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
        
        foreach (var player in _client.CurrentPlayerList)
        {
            if (player.PlayerUid == myUid)
            {
                // 自己是异常玩家，已到达该线路
                if (_reportedWaitPoint.HasValue && _reportedWaitPoint.Value.routeId == routeId)
                    count++;
                else if (!_reportedWaitPoint.HasValue)
                    count++; // 正常玩家也计入
            }
            else
            {
                // 对方进度 >= 目标线路索引，表示对方已到达或会到达该线路
                var peerIndex = _client.GetPeerRouteIndex(player.PlayerUid);
                if (peerIndex.HasValue && peerIndex.Value >= routeIndex)
                    count++;
            }
        }
        
        return count;
    }

    /// <summary>
    /// 设置多轮世界轮次（skip-route-wait-point-report 修复）
    /// 验证 worldRound 一致性，防止跨轮状态污染
    /// </summary>
    public void SetWorldRound(int worldRound)
    {
        _stateManager.SetWorldRound(worldRound);
        _logger.LogInformation("[联机] 设置多轮世界轮次: {WorldRound}", worldRound);
    }

    /// <summary>
    /// 清除指定玩家的异常状态（全员到达同步点后调用）
    /// </summary>
    public void ClearAbnormalStatus(string? playerUid = null)
    {
        if (playerUid != null)
        {
            // 清除指定玩家状态
            if (_reportedWaitPoint.HasValue && playerUid == TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid)
            {
                _reportedWaitPoint = null;
                _stateManager.RemoveState(playerUid);
                
                // 取消该玩家的超时计时器
                if (_abnormalPlayerTimeouts.TryGetValue(playerUid, out var ctx))
                {
                    ctx.HasExited = true; // 标记为已汇合，不再触发退出
                    ctx.Cts.Cancel();
                    _abnormalPlayerTimeouts.TryRemove(playerUid, out _);
                }
                
                _logger.LogInformation("[联机] 已清除玩家 {Uid} 的异常状态", playerUid);
            }
        }
    }

    /// <summary>
    /// 清除所有异常状态（全员汇合后调用）
    /// </summary>
    public void ClearAllAbnormalStatus()
    {
        // 清除自己的异常状态
        var myUid = TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
        if (_reportedWaitPoint.HasValue)
        {
            _reportedWaitPoint = null;
            _stateManager.RemoveState(myUid);
        }
        
        // 取消所有超时计时器
        foreach (var ctx in _abnormalPlayerTimeouts.Values)
        {
            ctx.HasExited = true;
            ctx.Cts.Cancel();
        }
        _abnormalPlayerTimeouts.Clear();
        
        _logger.LogInformation("[联机] 已清除所有异常状态");
    }

    /// <summary>
    /// 获取当前多轮世界轮次（skip-route-wait-point-report 修复）
    /// </summary>
    private int GetCurrentWorldRound()
    {
        try
        {
            // 从状态统计获取当前轮次
            var stats = _stateManager.GetStatistics();
            return stats.CurrentWorldRound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[联机] 获取当前世界轮次时发生异常");
            return 1; // 异常时返回默认值
        }
    }

    private void OnPlayerListUpdated(List<PlayerInfo> players)
    {
        // 根据 ConnectionId 或 PlayerName 确定自己的序号（1-based）
        // 由于我们无法直接获取 ConnectionId，用列表顺序作为序号
        // 服务器广播的列表顺序是稳定的（加入顺序）
        _myPlayerIndex = 0; // 未知时为 0
        // PlayerListUpdated 会在 UI 线程处理，这里只记录人数
    }

    /// <summary>
    /// 重置连续超时计数。在每条路线开始时调用，避免跨路线累积误触发。
    /// </summary>
    public void ResetSyncTimeoutCount()
    {
        _consecutiveSyncTimeouts = 0;
        _logger.LogDebug("[联机] 连续超时计数已重置");
    }

    public async Task WaitForAllPlayers(string syncId, CancellationToken ct)
    {
        if (!IsActive || IsExitTriggered)
        {
            _logger.LogDebug("[联机] 已停止/退出，跳过集合等待 syncId={SyncId}", syncId);
            return;
        }

        // 连续超时已触发退出，跳过后续等待
        if (_consecutiveSyncTimeoutFired)
        {
            _logger.LogWarning("[联机] 连续超时退出已触发，跳过集合等待 syncId={SyncId}", syncId);
            return;
        }

        // 检查是否跳过下一个同步点（sync-point-route-skip-alignment 修复）
        // 但如果是异常等待点，则不能跳过，需要等待异常玩家汇合
        bool isAbnormalWaitingPoint = _stateManager.GetAbnormalPlayersAtPoint(syncId) > 0;
        if (_skipNextSyncPoint && !isAbnormalWaitingPoint)
        {
            _skipNextSyncPoint = false;
            _logger.LogInformation("[联机] 跳过下一个同步点: {SyncId}（路线跳过后的首个同步点）", syncId);
            return;
        }
        else if (isAbnormalWaitingPoint)
        {
            // 异常等待点，清除跳过标志，确保正常等待
            _skipNextSyncPoint = false;
            _logger.LogInformation("[联机] 异常等待点 {SyncId}，清除跳过标志并等待异常玩家汇合", syncId);
        }

        // 异常协调中心逻辑（skip-route-wait-point-report 修复）
        var playerUid = TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
        
        // 检查当前玩家是否是异常玩家（跳过线路的玩家）
        bool isAbnormalPlayer = _reportedWaitPoint.HasValue;
        
        if (isAbnormalPlayer)
        {
            // 异常玩家（跳过路线者）始终等待其他玩家
            // 因为跳过路线后需要等其他人追上来
            var myReportedPoint = _reportedWaitPoint.Value;
            _logger.LogInformation("[联机] 异常玩家在同步点 {SyncId} 等待（跳过路线后需要等待其他玩家），上报点={ReportedPoint}", 
                syncId, myReportedPoint.syncPointId);
            // 异常玩家始终等待，不执行"只上报到达不等待"
        }
        else
        {
            // 正常玩家：检查是否有异常玩家在等待
            var abnormalPlayersCount = _stateManager.GetAbnormalPlayerCount();
            if (abnormalPlayersCount > 0)
            {
                // 有异常玩家在等待，检查是否在异常玩家等待的点
                var abnormalPlayersAtThisPoint = _stateManager.GetAbnormalPlayersAtPoint(syncId);
                
                if (abnormalPlayersAtThisPoint > 0)
                {
                    // 在异常玩家等待的点，正常等待
                    _logger.LogInformation("[联机] 正常玩家在同步点 {SyncId} 等待（有异常玩家在此点等待）", syncId);
                }
                else
                {
                    // 不在异常玩家等待的点，只上报到达不等待
                    // 但仍需传入有效等待人数，让服务器正确判断
                    var effectiveMinForReport = CalculateEffectiveWaitCount(syncId);
                    _logger.LogInformation("[联机] 正常玩家在同步点 {SyncId}（不是异常玩家等待的点），只上报到达不等待，预期人数={EffectiveMin}", 
                        syncId, effectiveMinForReport);
                    await _client.ReportArrivalAsync(syncId, effectiveMinForReport);
                    return;
                }
            }
            else
            {
                // 没有异常玩家，正常等待
                _logger.LogInformation("[联机] 正常玩家在同步点 {SyncId} 等待（无异常玩家）", syncId);
            }
        }

        // 计算有效等待人数：使用新的人数计算方法（只计算已到达该线路的玩家）
        var effectiveMin = _minPlayersToSync;
        if (effectiveMin == 0)
        {
            // 使用新的计算方法：只计算已到达该线路的玩家
            effectiveMin = CalculateEffectiveWaitCount(syncId);
            
            _logger.LogInformation("[联机] 同步点 {SyncId} 有效等待人数: {EffectiveMin}", syncId, effectiveMin);
        }

        if (effectiveMin <= 1)
        {
            _logger.LogInformation("[联机] 有效最低人数={Min}（房间人数={RoomCount}，配置最低={ConfigMin}），跳过集合等待 syncId={SyncId}",
                effectiveMin, _client.CurrentRoomPlayerCount, _minPlayersToSync, syncId);
            return;
        }

        try
        {
            // 异常玩家使用5分钟超时，正常玩家使用标准60秒超时
            bool allArrived;
            if (isAbnormalPlayer)
            {
                // 异常玩家：使用5分钟超时等待，传入有效等待人数
                _logger.LogInformation("[联机] 异常玩家使用5分钟超时等待，syncId={SyncId}，预期人数={EffectiveMin}", syncId, effectiveMin);
                allArrived = await _barrier.WaitExtraAsync(syncId, (int)_abnormalPlayerTimeout.TotalSeconds, effectiveMin, ct);
            }
            else
            {
                // 正常玩家：使用标准60秒超时，传入有效等待人数
                allArrived = await _barrier.WaitAsync(syncId, effectiveMin, ct);
            }

            if (allArrived)
            {
                // 全员到达，清除异常状态
                ClearAllAbnormalStatus();
                
                // 重置连续超时计数
                _consecutiveSyncTimeouts = 0;
                return;
            }

            // 标准超时放行 — 检查是否有异常状态成员需要额外等待
            var hasAbnormalMembers = _client.HasFightingMembers
                                    || _client.HasRejoiningMembers
                                    || _client.HasRevivingMembers;

            if (hasAbnormalMembers)
            {
                // 记录异常成员详情
                var abnormalDetails = _client.MemberStatuses
                    .Where(kv => kv.Value != MemberStatus.Normal)
                    .Select(kv => $"{kv.Key}={kv.Value}")
                    .ToList();
                _logger.LogInformation("[联机] 同步点 {SyncId} 标准超时，检测到异常状态成员: [{Members}]，进入额外等待",
                    syncId, string.Join(", ", abnormalDetails));

                // 额外等待：进度感知动态计算（需求 6）
                // Fighting → 固定 FightExtraWaitSeconds
                // Rejoining/Reviving → 查询进度计算剩余时间，查不到回退 RejoinMaxWaitSeconds
                var config = TaskContext.Instance().Config.AutoHoeingConfig;
                var extraWaitSeconds = await CalculateExtraWaitSecondsAsync(config);

                var extraArrived = await _barrier.WaitExtraAsync(syncId, extraWaitSeconds, effectiveMin, ct);

                if (extraArrived)
                {
                    // 额外等待期间全员到达
                    _consecutiveSyncTimeouts = 0;
                    _logger.LogInformation("[联机] 同步点 {SyncId} 额外等待期间全员到达", syncId);
                    return;
                }

                _logger.LogWarning("[联机] 同步点 {SyncId} 额外等待也超时，放行", syncId);
            }
            else
            {
                // 记录未到达成员信息
                _logger.LogWarning("[联机] 同步点 {SyncId} 标准超时放行，无异常状态成员", syncId);
            }

            // 超时放行 — 递增连续超时计数（重连期间不计入，避免网络抖动触发连续超时退出）
            if (_client.IsReconnecting)
            {
                _logger.LogWarning("[联机] 同步点 {SyncId} 超时放行（重连中，不计入连续超时）", syncId);
            }
            else
            {
                _consecutiveSyncTimeouts++;
                _logger.LogWarning("[联机] 同步点 {SyncId} 超时放行，连续超时次数: {Count}/{Max}",
                    syncId, _consecutiveSyncTimeouts, MaxConsecutiveSyncTimeouts);

                // 检查连续超时是否达到上限
                if (_consecutiveSyncTimeouts >= MaxConsecutiveSyncTimeouts && !_consecutiveSyncTimeoutFired)
                {
                    _consecutiveSyncTimeoutFired = true;
                    _logger.LogError("[联机] 连续超时达到上限（{Max}次），触发退出", MaxConsecutiveSyncTimeouts);

                    if (OnConsecutiveSyncTimeoutExceeded != null)
                    {
                        var isHost = !string.IsNullOrEmpty(_client.HostPlayerUid)
                            && _client.HostPlayerUid == TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
                        await OnConsecutiveSyncTimeoutExceeded.Invoke(isHost);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] WaitForAllPlayers 异常，syncId={SyncId}，跳过同步继续执行", syncId);
        }
    }

    /// <summary>
    /// 通知路线跳过（sync-point-route-skip-alignment 修复）
    /// 检查 IsActive && !IsExitTriggered，调用 _client.SendRouteSkippedAsync，异常静默忽略
    /// </summary>
    public async Task NotifyRouteSkippedAsync(int routeIndex)
    {
        if (!IsActive || IsExitTriggered) return;
        try
        {
            await _client.SendRouteSkippedAsync(routeIndex);
            _logger.LogInformation("[联机] 已发送路线跳过通知: 路线 {Index}", routeIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 发送路线跳过通知失败（静默忽略）");
        }
    }

    /// <summary>
    /// 设置跳过下一个同步点（sync-point-route-skip-alignment 修复）
    /// 跳过路线后调用，使进入下一条路线时的首个同步点立即放行
    /// </summary>
    public void SetSkipNextSyncPoint()
    {
        _skipNextSyncPoint = true;
        _logger.LogDebug("[联机] 已设置跳过下一个同步点标志");
    }

    /// <summary>
    /// 检查指定同步点是否有异常玩家在等待
    /// 用于强制异常等待点生效，不依赖 SyncAtEveryTeleport 配置
    /// </summary>
    public bool IsAbnormalWaitingAtPoint(string syncPointId)
    {
        return _stateManager.GetAbnormalPlayersAtPoint(syncPointId) > 0;
    }

    /// <summary>
    /// 计算额外等待时间（需求 6：进度感知等待）。
    /// Fighting → 固定 FightExtraWaitSeconds。
    /// Rejoining/Reviving → 查询进度计算剩余时间，查不到回退 RejoinMaxWaitSeconds。
    /// </summary>
    private async Task<int> CalculateExtraWaitSecondsAsync(AutoHoeingConfig config)
    {
        // 只有 Fighting 成员 → 固定等待
        if (!_client.HasRejoiningMembers && !_client.HasRevivingMembers)
            return config.FightExtraWaitSeconds;

        // 有 Rejoining/Reviving 成员 → 尝试进度感知
        var maxWait = 0.0;
        foreach (var (uid, status) in _client.MemberStatuses)
        {
            if (status != MemberStatus.Rejoining && status != MemberStatus.Reviving)
                continue;

            var progress = await _client.GetMemberProgressAsync(uid);
            if (progress == null)
            {
                // 查不到进度，回退到固定值
                _logger.LogWarning("[联机] 无法获取成员 {Uid} 的进度信息，回退到固定等待 {Seconds}s", uid, config.RejoinMaxWaitSeconds);
                return config.RejoinMaxWaitSeconds;
            }

            var elapsed = (DateTime.UtcNow - progress.RouteStartTime).TotalSeconds;
            var remaining = progress.RouteEstimatedSeconds - elapsed + 60; // 60s 缓冲
            _logger.LogInformation("[联机] 成员 {Uid} 进度：路线{Index}，已用{Elapsed:F0}s，预估总{Est:F0}s，剩余{Remain:F0}s",
                uid, progress.RouteIndex, elapsed, progress.RouteEstimatedSeconds, remaining);
            maxWait = Math.Max(maxWait, remaining);
        }

        if (maxWait <= 0)
        {
            _logger.LogWarning("[联机] 进度计算结果 <= 0，回退到固定等待 {Seconds}s", config.RejoinMaxWaitSeconds);
            return config.RejoinMaxWaitSeconds;
        }

        var result = (int)Math.Min(maxWait, config.RejoinMaxWaitSeconds);
        _logger.LogInformation("[联机] 进度感知额外等待: {Seconds}s（上限 {Max}s）", result, config.RejoinMaxWaitSeconds);
        return result;
    }

    /// <summary>等待所有玩家完成路线验证。</summary>
    public async Task WaitForRouteVerificationAsync(CancellationToken ct)
    {
        if (!IsActive) return;

        var effectiveMin = _minPlayersToSync == 0 ? _client.CurrentRoomPlayerCount : _minPlayersToSync;
        if (effectiveMin <= 1) return;

        _logger.LogInformation("[联机] 等待所有玩家完成路线验证...");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_syncTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Action? handler = null;
        handler = () => tcs.TrySetResult(true);

        _client.RouteVerificationAllDone += handler;
        try
        {
            // 先上报一次
            await _client.ReportRouteVerificationDoneAsync();

            // 设置重试机制，每10秒重试一次上报
            var retryTimer = new Timer(async _ =>
            {
                if (!tcs.Task.IsCompleted)
                {
                    _logger.LogDebug("[联机] 重试上报路线验证完成状态");
                    await _client.ReportRouteVerificationDoneAsync();
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            using var reg = linkedCts.Token.Register(() =>
            {
                retryTimer?.Dispose();
                if (ct.IsCancellationRequested)
                    tcs.TrySetCanceled(ct);
                else
                {
                    _logger.LogWarning("[联机] 路线验证同步等待超时({Timeout}s)，自动放行", _syncTimeoutSeconds);
                    tcs.TrySetResult(false);
                }
            });

            var result = await tcs.Task;
            retryTimer?.Dispose();
            _logger.LogInformation("[联机] 路线验证同步完成，结果: {Result}", result ? "全员完成" : "超时放行");
        }
        finally
        {
            _client.RouteVerificationAllDone -= handler;
        }
    }

    /// <summary>
    /// 触发协调停止流程（需求 2、8）。
    /// 使用 Interlocked.CompareExchange 保证只执行一次。
    /// RC-02: 先 Cancel _stopCts 停止本地任务，再执行网络操作。
    /// </summary>
    /// <param name="isHost">当前是否为房主</param>
    /// <param name="reason">停止原因</param>
    public async Task TriggerCoordinatedStop(bool isHost, string reason)
    {
        // 原子操作：只有第一个调用者能进入
        if (Interlocked.CompareExchange(ref _exitTriggered, 1, 0) != 0)
        {
            _logger.LogDebug("[联机] 协调停止已触发，忽略重复请求，来源: {Reason}", reason);
            return;
        }

        _logger.LogError("[联机] 触发协调停止，角色: {Role}，原因: {Reason}",
            isHost ? "房主" : "成员", reason);

        // RC-02: 先取消本地任务，确保本地尽快停止
        try { _stopCts?.Cancel(); }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("[联机] StopCts 已被 Dispose，跳过 Cancel（任务已在清理中）");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 取消本地任务失败");
        }

        // 再执行网络操作（所有操作用 try-catch 包裹，确保不抛异常，需求 8.5）
        if (isHost)
        {
            // 房主：发送 CloseRoom（需求 2.4, 2.8）
            try { await _client.CloseRoomAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[联机] 房主发送 CloseRoom 失败（成员靠心跳超时感知）");
            }
        }
        else
        {
            // 成员：上报 Offline（需求 2.1）
            try { await _client.ReportMemberStatusAsync(MemberStatus.Offline); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[联机] 成员上报 Offline 失败（房主靠心跳超时感知）");
            }
        }

        // 标记不再活跃
        IsActive = false;
    }

    /// <summary>
    /// 降级方法重构：不再简单设置 IsActive=false，而是触发协调停止。
    /// 保留方法签名兼容旧调用点。
    /// </summary>
    public void Degrade(string reason)
    {
        _logger.LogWarning("[联机] Degrade 调用，转为协调停止，原因: {Reason}", reason);
        var isHost = IsHost;
        // 异步触发，不阻塞调用方
        _ = Task.Run(async () =>
        {
            try { await TriggerCoordinatedStop(isHost, reason); }
            catch (Exception ex) { _logger.LogWarning(ex, "[联机] Degrade 触发协调停止异常"); }
        });
        OnDegraded?.Invoke(reason);
    }

    /// <summary>
    /// 上报战斗状态。进入战斗时 isFighting=true，战斗结束时 isFighting=false。
    /// 封装 CoordinatorClient.ReportMemberStatusAsync，PathExecutor 无需直接依赖 CoordinatorClient。
    /// </summary>
    public async Task ReportFightingStatusAsync(bool isFighting)
    {
        if (!IsActive) return;
        try
        {
            var status = isFighting ? MemberStatus.Fighting : MemberStatus.Normal;
            await _client.ReportMemberStatusAsync(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 上报战斗状态失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报成员状态（需求 1）。封装 CoordinatorClient.ReportMemberStatusAsync。
    /// </summary>
    public async Task ReportMemberStatusAsync(MemberStatus status)
    {
        if (!IsActive || IsExitTriggered) return; // 需求 8.6: 退出后静默跳过
        try
        {
            await _client.ReportMemberStatusAsync(status);
            _logger.LogInformation("[联机] 上报成员状态: {Status}", status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 上报成员状态失败（静默忽略），状态: {Status}", status);
        }
    }

    /// <summary>多世界轮次切换时重置状态。</summary>
    public void ResetForNewRound()
    {
        _consecutiveSyncTimeouts = 0;
        _consecutiveSyncTimeoutFired = false;
        Interlocked.Exchange(ref _exitTriggered, 0); // 重置退出标志
        lock (_offlineLock) { _offlineMembers.Clear(); }
        IsActive = true;
        
        // 路线跳过对齐修复重置（sync-point-route-skip-alignment 修复）
        _skipNextSyncPoint = false;
        _barrier.Reset();
        
        // 等待点上报修复重置（skip-route-wait-point-report 修复）
        _peerWaitPoints.Clear();
        _reportedWaitPoint = null;
        _stateManager?.ResetCurrentRound();
        
        // 异常玩家超时上下文清理（新增）
        foreach (var ctx in _abnormalPlayerTimeouts.Values)
        {
            ctx.HasExited = true;
            ctx.Cts.Cancel();
        }
        _abnormalPlayerTimeouts.Clear();
        
        _logger.LogInformation("[联机] ResetForNewRound: 状态已重置（包含路线跳过对齐、等待点上报和异常超时状态）");
    }

    public async ValueTask DisposeAsync()
    {
        _client.PlayerListUpdated -= OnPlayerListUpdated;
        _client.OnMemberStatusChanged -= _onMemberStatusChangedHandler;
        _client.RouteSkipped -= _onRouteSkippedHandler;
        _client.WaitPointReported -= _onWaitPointReportedHandler;
        
        // 清理等待点状态管理器资源
        _stateManager?.Dispose();
        
        await _client.DisposeAsync();
    }
}
