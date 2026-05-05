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
    
    // === 统一等待点协调（multiplayer-abnormal-wait-coordination）===
    /// <summary>服务端指令的待处理等待点</summary>
    private PendingWaitPoint? _pendingWaitPoint;
    /// <summary>是否处于降级模式（服务端不可用时回退到本地计算）</summary>
    private volatile bool _isDegraded;
    /// <summary>最后一次与服务端成功通信的时间</summary>
    private DateTime _lastServerContactTime = DateTime.UtcNow;
    /// <summary>服务端不可用阈值（超过此时间视为服务端不可用）</summary>
    private static readonly TimeSpan ServerUnavailableThreshold = TimeSpan.FromSeconds(10);
    /// <summary>统一等待点事件处理器</summary>
    private readonly Action<string, List<string>, int, string> _onUnifiedWaitPointReceivedHandler;
    /// <summary>异常玩家恢复事件处理器</summary>
    private readonly Action<string> _onAbnormalPlayerRecoveredHandler;
    /// <summary>所有玩家已到达事件处理器</summary>
    private readonly Action<string> _onAllPlayersArrivedHandler;

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
            else if (status == MemberStatus.Reviving || status == MemberStatus.Rejoining)
            {
                // 成员进入异常恢复状态，记录到状态管理器
                // 注意：此时还不知道具体的等待点，但可以标记该玩家为异常状态
                _logger.LogInformation("[联机] 成员 {PlayerName} 进入异常恢复状态: {Status}", GetPlayerDisplayName(playerUid), status);
            }
            else if (status == MemberStatus.Normal)
            {
                // 成员恢复正常，从异常状态中移除（如果有的话）
                // 注意：不清除 _stateManager 中的等待点状态，因为这需要等待点同步完成后才能清除
                _logger.LogDebug("[联机] 成员 {PlayerName} 状态恢复正常", GetPlayerDisplayName(playerUid));
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
        
        // 统一等待点事件处理（multiplayer-abnormal-wait-coordination）
        _onUnifiedWaitPointReceivedHandler = (syncPointId, abnormalPlayerUids, expectedWaitCount, routeId) =>
        {
            _logger.LogInformation("[联机] 收到统一等待点广播: SyncPoint={SyncPointId}, 异常玩家=[{AbnormalPlayers}], 预期人数={ExpectedCount}, 路线={RouteId}", 
                syncPointId, string.Join(", ", abnormalPlayerUids.Select(GetPlayerDisplayName)), expectedWaitCount, routeId);
            OnUnifiedWaitPointReceived(syncPointId, abnormalPlayerUids, expectedWaitCount, routeId);
        };
        _client.UnifiedWaitPointReceived += _onUnifiedWaitPointReceivedHandler;
        
        // 异常玩家恢复事件处理
        _onAbnormalPlayerRecoveredHandler = playerUid =>
        {
            _logger.LogInformation("[联机] 收到异常玩家恢复广播: {PlayerName}", GetPlayerDisplayName(playerUid));
            OnAbnormalPlayerRecoveredReceived(playerUid);
        };
        _client.AbnormalPlayerRecoveredReceived += _onAbnormalPlayerRecoveredHandler;
        
        // 所有玩家已到达事件处理
        _onAllPlayersArrivedHandler = syncPointId =>
        {
            _logger.LogInformation("[联机] 收到所有玩家已到达广播: {SyncPointId}", syncPointId);
            OnAllPlayersArrivedReceived(syncPointId);
        };
        _client.AllPlayersArrivedReceived += _onAllPlayersArrivedHandler;
    }
    
    // === 异常玩家等待点上报辅助方法（multiplayer-abnormal-wait-coordination）===
    
    /// <summary>
    /// 验证等待点是否为传送点格式（需求 4.1, 7.1）
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <returns>true=有效的传送点格式，false=无效</returns>
    public static bool IsValidWaitPoint(string syncPointId)
    {
        return !string.IsNullOrEmpty(syncPointId) && syncPointId.Contains("_tp_");
    }
    
    /// <summary>
    /// 获取下一条线路的第一个传送点（需求 7.2, 7.3）
    /// </summary>
    /// <param name="currentRouteIndex">当前路线索引</param>
    /// <param name="routeFileName">路线文件名（可选，用于构建完整ID）</param>
    /// <returns>下一条线路的第一个传送点ID，格式：{routeId}_tp_0_0</returns>
    public static string GetNextRouteFirstTeleportPoint(int currentRouteIndex, string? routeFileName = null)
    {
        var nextRouteId = (currentRouteIndex + 1).ToString();
        // 传送点格式：{routeFileName}_{routeId}_tp_{listIdx}_{wpIdx}
        // 简化格式：{routeId}_tp_0_0（服务端会验证并补充）
        return $"{nextRouteId}_tp_0_0";
    }

    /// <summary>
    /// 上报等待点（需求 4.1, 4.2, 7.1, 7.2, 7.3, 7.4）
    /// 调用 CoordinatorClient.SendWaitPointReportAsync，包含容错处理（失败时回退到 RouteSkipped）
    /// 记录上报日志用于监控
    /// </summary>
    public async Task<bool> ReportWaitPointAsync(string routeId, string syncPointId, int worldRound)
    {
        try
        {
            // 验证等待点格式（需求 7.1）
            if (!IsValidWaitPoint(syncPointId))
            {
                _logger.LogWarning("[联机] 等待点格式无效（非传送点）: {SyncPointId}，尝试获取下一条线路的第一个传送点", syncPointId);
                // 尝试获取下一条线路的第一个传送点
                if (int.TryParse(routeId, out var routeIndex))
                {
                    syncPointId = GetNextRouteFirstTeleportPoint(routeIndex);
                    _logger.LogInformation("[联机] 使用下一条线路的第一个传送点: {SyncPointId}", syncPointId);
                }
            }
            
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
    /// 计算指定同步点的有效等待人数（需求 6.1, 6.2, 6.4）
    /// 优先使用服务端提供的预期人数，服务端不可用时回退到本地计算
    /// </summary>
    private int CalculateEffectiveWaitCount(string syncId)
    {
        // 优先使用服务端指令的预期人数（需求 6.2）
        if (_pendingWaitPoint != null && !_pendingWaitPoint.IsProcessed && !_pendingWaitPoint.IsExpired())
        {
            _logger.LogInformation("[联机] 使用服务端预期等待人数: {ExpectedCount}（服务端指令）", _pendingWaitPoint.ExpectedWaitCount);
            return _pendingWaitPoint.ExpectedWaitCount;
        }
        
        // 服务端不可用时回退到本地计算（需求 6.4）
        if (!IsServerAvailable())
        {
            _logger.LogWarning("[联机] 服务端不可用，使用本地降级计算");
            return CalculateLocalEffectiveWaitCount(syncId);
        }
        
        // 本地计算：获取当前同步点等待的异常玩家数
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
    /// 本地降级计算有效等待人数（需求 6.3, 6.4）
    /// 服务端不可用时使用简化逻辑
    /// </summary>
    private int CalculateLocalEffectiveWaitCount(string syncId)
    {
        // 降级模式：使用房间人数减去离线人数
        int offlineCount;
        lock (_offlineLock) { offlineCount = _offlineMembers.Count; }
        
        int effectiveCount = _client.CurrentRoomPlayerCount - offlineCount;
        
        _logger.LogInformation("[联机] 降级模式计算有效等待人数: 房间人数={RoomCount}, 离线人数={OfflineCount}, 有效人数={EffectiveCount}", 
            _client.CurrentRoomPlayerCount, offlineCount, effectiveCount);
        
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
            
            // 自己上报的状态也需要本地记录（因为 SignalR 广播不会发给自己）
            var myUid = TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
            if (status == MemberStatus.Reviving || status == MemberStatus.Rejoining)
            {
                // 进入异常恢复状态，标记自己为异常玩家
                // 注意：此时还不知道具体的等待点，用空字符串标记
                var state = new WaitPointState
                {
                    PlayerUid = myUid,
                    RouteId = "",
                    SyncPointId = "",
                    WorldRound = GetCurrentWorldRound(),
                    LastUpdated = DateTime.UtcNow
                };
                _stateManager.UpdateState(myUid, state);
                _logger.LogInformation("[联机] 本地记录异常状态: {PlayerName} → {Status}", GetPlayerDisplayName(myUid), status);
            }
            else if (status == MemberStatus.Normal)
            {
                // 恢复正常状态，但不清除等待点记录（需要等待点同步完成后再清除）
                _logger.LogDebug("[联机] 本地状态恢复正常: {PlayerName}", GetPlayerDisplayName(myUid));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 上报成员状态失败（静默忽略），状态: {Status}", status);
        }
    }
    
    // === 统一等待点协调处理（multiplayer-abnormal-wait-coordination）===
    
    /// <summary>
    /// 处理服务端广播的统一等待点（需求 3.1, 3.2）
    /// </summary>
    /// <param name="syncPointId">统一等待点ID（传送点格式）</param>
    /// <param name="abnormalPlayerUids">异常玩家UID列表</param>
    /// <param name="expectedWaitCount">预期等待人数</param>
    /// <param name="routeId">路线ID</param>
    private void OnUnifiedWaitPointReceived(string syncPointId, List<string> abnormalPlayerUids, int expectedWaitCount, string routeId)
    {
        try
        {
            // 更新服务端通信时间
            _lastServerContactTime = DateTime.UtcNow;
            
            // 验证等待点格式（必须是传送点）
            if (!syncPointId.Contains("_tp_"))
            {
                _logger.LogWarning("[联机] 收到非传送点格式的统一等待点: {SyncPointId}，忽略", syncPointId);
                return;
            }
            
            var myUid = TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
            bool iAmAbnormal = _reportedWaitPoint.HasValue;
            bool needToMoveToNewWaitPoint = false;
            
            // 如果我是异常玩家，检查是否需要移动到新的等待点
            if (iAmAbnormal)
            {
                var currentWaitPoint = _reportedWaitPoint.Value.syncPointId;
                int currentRouteIndex = ExtractRouteIndexFromSyncPoint(currentWaitPoint);
                int newRouteIndex = ExtractRouteIndexFromSyncPoint(syncPointId);
                
                // 如果新的统一等待点与当前等待点不同
                if (currentWaitPoint != syncPointId)
                {
                    _logger.LogInformation("[联机] 异常玩家收到新的统一等待点: 当前={CurrentPoint}（线路{CurrentIndex}）, 新={NewPoint}（线路{NewIndex}）", 
                        currentWaitPoint, currentRouteIndex, syncPointId, newRouteIndex);
                    
                    // 如果新等待点的路线索引 > 当前等待点，需要移动到新等待点
                    if (newRouteIndex > currentRouteIndex)
                    {
                        needToMoveToNewWaitPoint = true;
                        _logger.LogInformation("[联机] 异常玩家需要移动到新的统一等待点 {SyncPointId}", syncPointId);
                        
                        // 清除当前的异常等待状态，让 PathExecutor 继续执行
                        _reportedWaitPoint = null;
                        
                        // 取消当前的超时计时器
                        if (_abnormalPlayerTimeouts.TryGetValue(myUid, out var ctx))
                        {
                            ctx.HasExited = true;
                            ctx.Cts.Cancel();
                            _abnormalPlayerTimeouts.TryRemove(myUid, out _);
                            _logger.LogInformation("[联机] 已取消异常玩家超时计时器，准备移动到新等待点");
                        }
                        
                        // 通知 SyncBarrier 放行当前等待，让 PathExecutor 继续执行
                        _barrier.SignalRouteSkipped();
                    }
                    else
                    {
                        _logger.LogInformation("[联机] 新等待点 {NewPoint}（线路{NewIndex}）不比当前 {CurrentPoint}（线路{CurrentIndex}）更靠后，保持当前等待",
                            syncPointId, newRouteIndex, currentWaitPoint, currentRouteIndex);
                    }
                }
            }
            
            // 创建待处理等待点
            _pendingWaitPoint = new PendingWaitPoint
            {
                SyncPointId = syncPointId,
                RouteId = routeId,
                AbnormalPlayerUids = abnormalPlayerUids,
                ExpectedWaitCount = expectedWaitCount,
                ReceivedTime = DateTime.UtcNow,
                IsForced = true, // 服务端指令的等待点，强制等待
                IsProcessed = false
            };
            
            // 更新等待点状态管理器，记录异常玩家
            foreach (var uid in abnormalPlayerUids)
            {
                var state = new WaitPointState
                {
                    PlayerUid = uid,
                    RouteId = routeId,
                    SyncPointId = syncPointId,
                    WorldRound = GetCurrentWorldRound(),
                    LastUpdated = DateTime.UtcNow
                };
                _stateManager.UpdateState(uid, state);
            }
            
            _logger.LogInformation("[联机] 已设置服务端指令的统一等待点: {SyncPointId}, 异常玩家=[{AbnormalPlayers}], 预期人数={ExpectedCount}, 我是否需要移动={NeedMove}", 
                syncPointId, string.Join(", ", abnormalPlayerUids.Select(GetPlayerDisplayName)), expectedWaitCount, needToMoveToNewWaitPoint);
            
            // 同步到 SyncBarrier，确保 PathExecutor 能检测到异常等待点
            _barrier.RecordWaitPointReport(routeId, syncPointId, GetCurrentWorldRound());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[联机] 处理统一等待点广播异常");
        }
    }
    
    /// <summary>
    /// 从同步点ID中提取路线索引
    /// 格式：{routeId}_tp_{listIdx}_{wpIdx} 或 {fileName}_{routeId}_tp_{listIdx}_{wpIdx}
    /// 或 D023须弥降诸魔山神像.json_tp_0_0 (文件名包含 D+数字 前缀)
    /// </summary>
    private int ExtractRouteIndexFromSyncPoint(string syncPointId)
    {
        if (string.IsNullOrEmpty(syncPointId)) return -1;
        
        // 查找 _tp_ 标记
        int tpIndex = syncPointId.IndexOf("_tp_");
        if (tpIndex < 0) return -1;
        
        // _tp_ 前面的部分是文件名
        // 格式可能是：
        // - "2_tp_0_0" (纯数字)
        // - "fileName_2_tp_0_0" (文件名_数字)
        // - "D023须弥降诸魔山神像.json_tp_0_0" (文件名包含 D+数字 前缀)
        string beforeTp = syncPointId.Substring(0, tpIndex);
        
        // 尝试解析最后一个下划线分隔的部分作为数字
        var parts = beforeTp.Split('_');
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(parts[i], out int routeIndex))
            {
                return routeIndex;
            }
        }
        
        // 如果上述方法失败，尝试从文件名开头提取 D+数字 格式
        // 例如 "D023须弥降诸魔山神像.json" -> 23
        var fileName = beforeTp;
        if (fileName.StartsWith("D") && fileName.Length > 1)
        {
            // 提取 D 后面的数字
            int digitStart = 1;
            int digitEnd = digitStart;
            while (digitEnd < fileName.Length && char.IsDigit(fileName[digitEnd]))
            {
                digitEnd++;
            }
            if (digitEnd > digitStart)
            {
                var numberStr = fileName.Substring(digitStart, digitEnd - digitStart);
                if (int.TryParse(numberStr, out int routeIndexFromFileName))
                {
                    _logger.LogDebug("[联机] 从文件名前缀提取路线索引: {SyncPointId} -> {RouteIndex}", 
                        syncPointId, routeIndexFromFileName);
                    return routeIndexFromFileName;
                }
            }
        }
        
        return -1;
    }
    
    /// <summary>
    /// 处理异常玩家恢复广播（需求 5.4）
    /// </summary>
    /// <param name="playerUid">恢复的异常玩家UID</param>
    private void OnAbnormalPlayerRecoveredReceived(string playerUid)
    {
        try
        {
            _lastServerContactTime = DateTime.UtcNow;
            
            // 清除异常玩家状态
            _stateManager.RemoveState(playerUid);
            
            // 取消该玩家的超时计时器
            if (_abnormalPlayerTimeouts.TryGetValue(playerUid, out var ctx))
            {
                ctx.HasExited = true;
                ctx.Cts.Cancel();
                _abnormalPlayerTimeouts.TryRemove(playerUid, out _);
            }
            
            // 如果是当前玩家恢复，清除本地状态
            var myUid = TaskContext.Instance().Config.AutoHoeingConfig.PlayerUid;
            if (playerUid == myUid && _reportedWaitPoint.HasValue)
            {
                _reportedWaitPoint = null;
                _logger.LogInformation("[联机] 当前玩家异常状态已清除");
            }
            
            _logger.LogInformation("[联机] 异常玩家 {PlayerName} 已恢复", GetPlayerDisplayName(playerUid));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[联机] 处理异常玩家恢复广播异常");
        }
    }
    
    /// <summary>
    /// 处理所有玩家已到达广播（需求 5.3）
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    private void OnAllPlayersArrivedReceived(string syncPointId)
    {
        try
        {
            _lastServerContactTime = DateTime.UtcNow;
            
            // 标记待处理等待点已处理
            if (_pendingWaitPoint != null && _pendingWaitPoint.SyncPointId == syncPointId)
            {
                _pendingWaitPoint.IsProcessed = true;
                _logger.LogInformation("[联机] 统一等待点 {SyncPointId} 全员已到达，标记为已处理", syncPointId);
            }
            
            // 清除所有异常状态
            ClearAllAbnormalStatus();
            
            // 通知 SyncBarrier 放行
            _barrier.SignalRouteSkipped(); // 复用此机制放行当前等待
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[联机] 处理所有玩家已到达广播异常");
        }
    }
    
    /// <summary>
    /// 检查是否有待处理的服务端指令等待点
    /// </summary>
    public bool HasPendingWaitPoint => _pendingWaitPoint != null && !_pendingWaitPoint.IsProcessed && !_pendingWaitPoint.IsExpired();
    
    /// <summary>
    /// 获取待处理等待点信息（供 PathExecutor 使用）
    /// </summary>
    public PendingWaitPoint? GetPendingWaitPoint()
    {
        return _pendingWaitPoint;
    }
    
    /// <summary>
    /// 清除待处理等待点
    /// </summary>
    public void ClearPendingWaitPoint()
    {
        if (_pendingWaitPoint != null)
        {
            _logger.LogInformation("[联机] 清除待处理等待点: {SyncPointId}", _pendingWaitPoint.SyncPointId);
            _pendingWaitPoint = null;
        }
    }
    
    /// <summary>
    /// 检查服务端是否可用（需求 8.1）
    /// </summary>
    /// <returns>true=服务端可用，false=服务端不可用，应进入降级模式</returns>
    public bool IsServerAvailable()
    {
        // 检查客户端连接状态
        if (!_client.IsConnected)
        {
            // 连接断开超过阈值，进入降级模式
            var disconnectedTime = DateTime.UtcNow - _lastServerContactTime;
            if (disconnectedTime > ServerUnavailableThreshold)
            {
                if (!_isDegraded)
                {
                    _logger.LogWarning("[联机] 服务端连接断开超过 {Threshold}s，进入降级模式", ServerUnavailableThreshold.TotalSeconds);
                    _isDegraded = true;
                }
                return false;
            }
        }
        
        // 连接正常或断开时间未超过阈值
        if (_isDegraded && _client.IsConnected)
        {
            _logger.LogInformation("[联机] 服务端连接恢复，退出降级模式");
            _isDegraded = false;
            _lastServerContactTime = DateTime.UtcNow;
        }
        
        return _client.IsConnected;
    }
    
    /// <summary>
    /// 获取服务端不可用持续时长
    /// </summary>
    public TimeSpan GetServerUnavailableDuration()
    {
        if (_client.IsConnected) return TimeSpan.Zero;
        return DateTime.UtcNow - _lastServerContactTime;
    }
    
    /// <summary>
    /// 更新服务端通信时间（在心跳成功时调用）
    /// </summary>
    public void UpdateServerContactTime()
    {
        _lastServerContactTime = DateTime.UtcNow;
    }
    
    /// <summary>
    /// 是否处于降级模式
    /// </summary>
    public bool IsDegraded => _isDegraded;

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
        
        // 异常玩家超时上下文清理
        foreach (var ctx in _abnormalPlayerTimeouts.Values)
        {
            ctx.HasExited = true;
            ctx.Cts.Cancel();
        }
        _abnormalPlayerTimeouts.Clear();
        
        // 统一等待点协调重置（multiplayer-abnormal-wait-coordination）
        _pendingWaitPoint = null;
        _isDegraded = false;
        _lastServerContactTime = DateTime.UtcNow;
        
        _logger.LogInformation("[联机] ResetForNewRound: 状态已重置（包含路线跳过对齐、等待点上报、异常超时和统一等待点协调状态）");
    }

    public async ValueTask DisposeAsync()
    {
        _client.PlayerListUpdated -= OnPlayerListUpdated;
        _client.OnMemberStatusChanged -= _onMemberStatusChangedHandler;
        _client.RouteSkipped -= _onRouteSkippedHandler;
        _client.WaitPointReported -= _onWaitPointReportedHandler;
        
        // 清理统一等待点协调事件订阅（multiplayer-abnormal-wait-coordination）
        _client.UnifiedWaitPointReceived -= _onUnifiedWaitPointReceivedHandler;
        _client.AbnormalPlayerRecoveredReceived -= _onAbnormalPlayerRecoveredHandler;
        _client.AllPlayersArrivedReceived -= _onAllPlayersArrivedHandler;
        
        // 清理等待点状态管理器资源
        _stateManager?.Dispose();
        
        await _client.DisposeAsync();
    }
}
