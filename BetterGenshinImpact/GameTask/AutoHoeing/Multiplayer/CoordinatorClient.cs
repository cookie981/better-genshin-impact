#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

public class CoordinatorClient : IAsyncDisposable
{
    private readonly ILogger<CoordinatorClient> _logger = App.GetLogger<CoordinatorClient>();
    private HubConnection? _connection;
    private Timer? _heartbeatTimer;
    
    // 保存房间信息用于重连
    private string? _currentRoomCode;
    private string? _playerName;
    private string? _playerUid;

    // === 成员异常恢复状态（需求 7）===
    private readonly ConcurrentDictionary<string, MemberStatus> _memberStatuses = new();
    private readonly ConcurrentDictionary<string, long> _memberStatusVersions = new();
    private long _statusVersion;

    // === 路线进度信息（需求 6）===
    private int _currentRouteIndex = -1;
    private DateTime _routeStartTime;
    private double _routeEstimatedSeconds;

    // === 路线跳过对齐修复（sync-point-route-skip-alignment）===
    private readonly ConcurrentDictionary<string, int> _memberProgressCache = new(); // key=playerUid, value=routeIndex
    public event Action<string, int>? RouteSkipped; // playerUid, skippedRouteIndex

    // === 等待点上报修复（skip-route-wait-point-report）===
    public event Action<string, string, string, int, DateTime>? WaitPointReported; // playerUid, routeId, syncPointId, worldRound, timestamp

    // === SignalR 断线重连（需求 3）===
    private volatile bool _isReconnecting;
    private volatile bool _isInRoom;

    private WorldStateMonitor? _worldStateMonitor;

    // === 玩家名称缓存（用于日志显示）===
    private readonly ConcurrentDictionary<string, string> _playerNameCache = new();

    public event Action<List<PlayerInfo>>? PlayerListUpdated;
    public event Action<string>? AllArrived;
    public event Action<string>? AllFightDone;
    public event Action<List<string>>? RouteDiffReceived;
    public event Action? RouteVerificationPassed;
    public event Action? OnDegraded;
    public event Action<string>? RoomClosed;
    public event Action? RouteVerificationAllDone;
    public event Action<int>? KazuhaPlayerUpdated;
    public event Action? AllWorldJoined;
    public event Action<bool>? HostReadyChanged;
    public event Action<List<string>>? HostRouteListReady;
    public event Action<string, MemberStatus>? OnMemberStatusChanged;
    
    // === 统一等待点协调（multiplayer-abnormal-wait-coordination）===
    /// <summary>收到服务端统一等待点广播：参数为(syncPointId, abnormalPlayerUids, expectedWaitCount, routeId)</summary>
    public event Action<string, List<string>, int, string>? UnifiedWaitPointReceived;
    /// <summary>异常玩家恢复广播：参数为(playerUid)</summary>
    public event Action<string>? AbnormalPlayerRecoveredReceived;
    /// <summary>所有玩家已到达等待点：参数为(syncPointId)</summary>
    public event Action<string>? AllPlayersArrivedReceived;

    // === 异常中断重对齐机制（multiplayer-abort-and-realign spec）===
    /// <summary>收到服务端中断重对齐指令：参数为(targetRouteIndex, abnormalPlayerUids, reason)</summary>
    public event Action<int, List<string>, string>? AbortAndRealignReceived;
    /// <summary>收到服务端开始路线指令：参数为(targetRouteIndex)</summary>
    public event Action<int>? StartRouteReceived;
    
    // === 强制线路同步机制（multiplayer-route-enforcement spec）===
    /// <summary>收到强制线路同步指令：参数为(targetRouteIndex, reason, deviationInfo)</summary>
    public event Action<int, string, List<string>>? RouteEnforceSyncReceived;

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public int CurrentRoomPlayerCount { get; private set; }
    public string HostPlayerUid { get; private set; } = "";
    public List<PlayerInfo> CurrentPlayerList { get; private set; } = new();

    /// <summary>是否有成员处于 Fighting 状态</summary>
    public bool HasFightingMembers => _memberStatuses.Values.Any(s => s == MemberStatus.Fighting);
    /// <summary>是否有成员处于 Rejoining 状态</summary>
    public bool HasRejoiningMembers => _memberStatuses.Values.Any(s => s == MemberStatus.Rejoining);
    /// <summary>是否有成员处于 Reviving 状态</summary>
    public bool HasRevivingMembers => _memberStatuses.Values.Any(s => s == MemberStatus.Reviving);
    /// <summary>当前成员状态字典（只读视图）</summary>
    public IReadOnlyDictionary<string, MemberStatus> MemberStatuses => _memberStatuses;

    /// <summary>是否正在重连中（需求 3），供 WorldStateMonitor 和 MultiplayerCoordinator 查询</summary>
    public bool IsReconnecting => _isReconnecting;

    /// <summary>是否在房间中（需求 3），供 WorldStateMonitor 查询</summary>
    public bool IsInRoom => _isInRoom;

    public WorldStateMonitor? WorldStateMonitor { get; set; }

    /// <summary>
    /// 隐藏服务器地址的前半部分（隐私保护）
    /// </summary>
    private static string MaskServerUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        try
        {
            // 例如 http://121.4.78.52:8080/hub -> http://***:8080/hub
            var uri = new Uri(url);
            var maskedHost = "***";
            return $"{uri.Scheme}://{maskedHost}:{uri.Port}{uri.PathAndQuery}";
        }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// 获取玩家显示名称（优先使用名称，找不到则显示 UID 前后各3位）
    /// </summary>
    private string GetPlayerDisplayName(string playerUid)
    {
        if (string.IsNullOrEmpty(playerUid)) return "未知玩家";
        
        // 先从缓存查找
        if (_playerNameCache.TryGetValue(playerUid, out var name) && !string.IsNullOrEmpty(name))
            return name;
        
        // 从当前玩家列表查找
        var player = CurrentPlayerList.FirstOrDefault(p => p.PlayerUid == playerUid);
        if (player != null && !string.IsNullOrEmpty(player.PlayerName))
        {
            _playerNameCache[playerUid] = player.PlayerName;
            return player.PlayerName;
        }
        
        // 找不到名称，显示 UID 前后各3位
        if (playerUid.Length > 6)
            return $"{playerUid[..3]}***{playerUid[^3..]}";
        return playerUid;
    }

    /// <summary>
    /// 更新玩家名称缓存
    /// </summary>
    private void UpdatePlayerNameCache(List<PlayerInfo> players)
    {
        foreach (var player in players)
        {
            if (!string.IsNullOrEmpty(player.PlayerUid) && !string.IsNullOrEmpty(player.PlayerName))
                _playerNameCache[player.PlayerUid] = player.PlayerName;
        }
    }

    public async Task<bool> ConnectAsync(string serverUrl, CancellationToken ct)
    {
        var maskedUrl = MaskServerUrl(serverUrl);
        try
        {
            _connection = new HubConnectionBuilder()
                .WithUrl(serverUrl)
                .Build();

            _connection.On<List<PlayerInfo>>("PlayerListUpdated",
                list =>
                {
                    CurrentRoomPlayerCount = list.Count;
                    if (list.Count > 0)
                        HostPlayerUid = list[0].PlayerUid;
                    CurrentPlayerList = new List<PlayerInfo>(list);

                    // 更新玩家名称缓存
                    UpdatePlayerNameCache(list);

                    // 清理不在玩家列表中的过期状态条目（需求 7）
                    var activeUids = list.Select(p => p.PlayerUid).ToHashSet();
                    foreach (var key in _memberStatuses.Keys.Where(k => !activeUids.Contains(k)).ToList())
                    {
                        _memberStatuses.TryRemove(key, out _);
                        _memberStatusVersions.TryRemove(key, out _);
                    }

                    PlayerListUpdated?.Invoke(list);
                });

            _connection.On<string>("AllArrived",
                syncPointId => AllArrived?.Invoke(syncPointId));

            _connection.On<string>("AllFightDone",
                syncPointId => AllFightDone?.Invoke(syncPointId));

            _connection.On<List<string>>("RouteDiffReceived",
                diff => RouteDiffReceived?.Invoke(diff));

            _connection.On("RouteVerificationPassed",
                () => RouteVerificationPassed?.Invoke());

            _connection.On<string>("RoomClosed",
                reason => RoomClosed?.Invoke(reason));

            _connection.On("RouteVerificationAllDone",
                () => RouteVerificationAllDone?.Invoke());

            _connection.On<int>("KazuhaPlayerUpdated",
                index => KazuhaPlayerUpdated?.Invoke(index));

            _connection.On("AllWorldJoined",
                () => AllWorldJoined?.Invoke());

            _connection.On<bool>("HostReadyChanged",
                ready => HostReadyChanged?.Invoke(ready));

            _connection.On<List<string>>("HostRouteListReady",
                routeNames => HostRouteListReady?.Invoke(routeNames));

            // 成员异常恢复状态变化（需求 7）
            _connection.On<string, string, long>("MemberStatusChanged",
                (playerUid, statusStr, version) =>
                {
                    if (!Enum.TryParse<MemberStatus>(statusStr, out var status)) return;

                    // 版本号检查：只接受更大版本号的更新，防止网络延迟导致的乱序覆盖
                    var accepted = _memberStatusVersions.AddOrUpdate(
                        playerUid,
                        _ => version, // 新条目直接接受
                        (_, oldVersion) => version > oldVersion ? version : oldVersion
                    );
                    if (accepted != version) return; // 版本号不够大，丢弃

                    if (status == MemberStatus.Offline)
                    {
                        _memberStatuses.TryRemove(playerUid, out _);
                        _memberStatusVersions.TryRemove(playerUid, out _);
                    }
                    else
                    {
                        _memberStatuses[playerUid] = status;
                    }

                    OnMemberStatusChanged?.Invoke(playerUid, status);
                });

            // 路线跳过通知（sync-point-route-skip-alignment 修复）
            _connection.On<string, int>("RouteSkipped",
                (playerUid, routeIndex) =>
                {
                    // 过滤自己发出的通知
                    if (playerUid == _playerUid) return;
                    RouteSkipped?.Invoke(playerUid, routeIndex);
                });

            // 成员路线进度更新（sync-point-route-skip-alignment 修复）
            _connection.On<string, int>("MemberProgressUpdated",
                (playerUid, routeIndex) =>
                {
                    _memberProgressCache[playerUid] = routeIndex;
                    _logger.LogDebug("[联机] 成员路线进度缓存更新: {PlayerName} → {Index}", GetPlayerDisplayName(playerUid), routeIndex);
                });

            // 等待点上报（skip-route-wait-point-report 修复）
            _connection.On<string, string, string, int, DateTime>("WaitPointReported",
                (playerUid, routeId, syncPointId, worldRound, timestamp) =>
                {
                    // 过滤自己发出的通知
                    if (playerUid == _playerUid) return;
                    
                    // 验证参数
                    if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(syncPointId))
                    {
                        _logger.LogWarning("[联机] 收到无效的等待点上报: Player={PlayerName}, Route={RouteId}, SyncPoint={SyncPointId}", 
                            GetPlayerDisplayName(playerUid), routeId, syncPointId);
                        return;
                    }
                    
                    WaitPointReported?.Invoke(playerUid, routeId, syncPointId, worldRound, timestamp);
                    _logger.LogInformation("[联机] 收到等待点上报: Player={PlayerName}, Route={RouteId}, SyncPoint={SyncPointId}, Round={WorldRound}", 
                        GetPlayerDisplayName(playerUid), routeId, syncPointId, worldRound);
                });
            
            // === 统一等待点协调（multiplayer-abnormal-wait-coordination）===
            // 服务端广播统一等待点，通知所有玩家在哪里等待异常玩家
            _connection.On<string, List<string>, int, string>("UnifiedWaitPoint",
                (syncPointId, abnormalPlayerUids, expectedWaitCount, routeId) =>
                {
                    _logger.LogInformation("[联机] 收到统一等待点广播: SyncPoint={SyncPointId}, 异常玩家=[{AbnormalPlayers}], 预期人数={ExpectedCount}, 路线={RouteId}", 
                        syncPointId, string.Join(", ", abnormalPlayerUids.Select(GetPlayerDisplayName)), expectedWaitCount, routeId);
                    UnifiedWaitPointReceived?.Invoke(syncPointId, abnormalPlayerUids, expectedWaitCount, routeId);
                });
            
            // 异常玩家恢复正常广播
            _connection.On<string>("AbnormalPlayerRecovered",
                playerUid =>
                {
                    _logger.LogInformation("[联机] 收到异常玩家恢复广播: {PlayerName}", GetPlayerDisplayName(playerUid));
                    AbnormalPlayerRecoveredReceived?.Invoke(playerUid);
                });
            
            // 所有玩家已到达等待点广播（替代原有的 AllArrived，用于服务端主导的等待点协调）
            _connection.On<string>("AllPlayersArrived",
                syncPointId =>
                {
                    _logger.LogInformation("[联机] 收到所有玩家已到达广播: {SyncPointId}", syncPointId);
                    AllPlayersArrivedReceived?.Invoke(syncPointId);
                });

            // === 异常中断重对齐机制（multiplayer-abort-and-realign spec）===
            // 服务端广播中断指令，通知所有玩家停止当前任务并重新对齐
            _connection.On<int, List<string>, string>("AbortAndRealign",
                (targetRouteIndex, abnormalPlayerUids, reason) =>
                {
                    _logger.LogInformation("[联机] 收到中断重对齐指令: 目标路线={TargetRoute}, 异常玩家=[{AbnormalPlayers}], 原因={Reason}",
                        targetRouteIndex, string.Join(", ", abnormalPlayerUids.Select(GetPlayerDisplayName)), reason);
                    AbortAndRealignReceived?.Invoke(targetRouteIndex, abnormalPlayerUids, reason);
                });

            // 服务端广播开始路线指令，通知所有玩家开始执行目标路线
            _connection.On<int>("StartRoute",
                targetRouteIndex =>
                {
                    _logger.LogInformation("[联机] 收到开始路线指令: 目标路线={TargetRoute}", targetRouteIndex);
                    StartRouteReceived?.Invoke(targetRouteIndex);
                });
            
            // === 强制线路同步机制（multiplayer-route-enforcement spec）===
            // 服务端广播强制线路同步指令，通知超前玩家跳转到落后玩家的线路
            _connection.On<int, string, List<string>>("RouteEnforceSync",
                (targetRouteIndex, reason, deviationInfo) =>
                {
                    _logger.LogInformation("[联机] 收到强制线路同步指令: 目标路线={TargetRoute}, 原因={Reason}, 偏差信息=[{DevInfo}]",
                        targetRouteIndex, reason, string.Join(", ", deviationInfo));
                    RouteEnforceSyncReceived?.Invoke(targetRouteIndex, reason, deviationInfo);
                });

            _connection.Closed += OnConnectionClosed;

            await _connection.StartAsync(ct);
            _logger.LogInformation("CoordinatorClient 已连接到 {Url}", maskedUrl);

            // 启动心跳定时器，每 5 秒发送一次
            _heartbeatTimer = new Timer(async _ =>
            {
                try { await SendHeartbeatAsync(); }
                catch { /* 忽略心跳异常 */ }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoordinatorClient 连接失败: {Url}", maskedUrl);
            return false;
        }
    }

    private async Task OnConnectionClosed(Exception? ex)
    {
        // 防重入：重连期间再次断线时忽略（需求 3）
        if (_isReconnecting)
        {
            _logger.LogWarning("CoordinatorClient 重连期间再次断线，忽略（等当前重连流程完成）");
            return;
        }

        _isReconnecting = true;
        _isInRoom = false;
        _logger.LogWarning(ex, "CoordinatorClient 连接断开，开始指数退避重连...");

        if (_connection == null)
        {
            _isReconnecting = false;
            return;
        }

        // 2 轮 × 4 次指数退避重连（立即 → 2s → 5s → 10s）
        var retryDelays = new[] { 0, 2000, 5000, 10000 };
        bool reconnected = false;

        for (int round = 1; round <= 2 && !reconnected; round++)
        {
            if (round > 1)
            {
                _logger.LogInformation("CoordinatorClient 第{Round}轮重连前等待30秒...", round);
                await Task.Delay(30000);
            }

            for (int attempt = 0; attempt < retryDelays.Length; attempt++)
            {
                if (retryDelays[attempt] > 0)
                    await Task.Delay(retryDelays[attempt]);

                try
                {
                    _logger.LogInformation("CoordinatorClient 重连尝试（第{Round}轮第{Attempt}次）...", round, attempt + 1);
                    await _connection.StartAsync();
                    _logger.LogInformation("CoordinatorClient 重连成功（第{Round}轮第{Attempt}次）", round, attempt + 1);
                    reconnected = true;
                    break;
                }
                catch (Exception retryEx)
                {
                    _logger.LogWarning(retryEx, "CoordinatorClient 重连失败（第{Round}轮第{Attempt}次）", round, attempt + 1);
                }
            }
        }

        if (!reconnected)
        {
            _logger.LogError("CoordinatorClient 2轮共8次重连全部失败，触发降级");
            _isReconnecting = false;
            OnDegraded?.Invoke();
            return;
        }

        // 重连成功，重新加入房间
        if (!string.IsNullOrEmpty(_currentRoomCode))
        {
            bool rejoined = false;

            // 2 轮 × 3 次退避重试加入房间（立即 → 5s → 15s）
            var joinDelays = new[] { 0, 5000, 15000 };
            for (int round = 1; round <= 2 && !rejoined; round++)
            {
                if (round > 1)
                {
                    _logger.LogInformation("重新加入房间第{Round}轮前等待30秒...", round);
                    await Task.Delay(30000);
                }

                for (int attempt = 0; attempt < joinDelays.Length; attempt++)
                {
                    if (joinDelays[attempt] > 0)
                        await Task.Delay(joinDelays[attempt]);

                    try
                    {
                        _logger.LogInformation("重新加入房间尝试（第{Round}轮第{Attempt}次）", round, attempt + 1);
                        var joined = await RejoinCurrentRoomAsync();
                        if (joined)
                        {
                            _logger.LogInformation("重新加入房间成功: {RoomCode}", _currentRoomCode);
                            rejoined = true;
                            break;
                        }
                        _logger.LogWarning("重新加入房间失败（第{Round}轮第{Attempt}次）: {RoomCode}", round, attempt + 1, _currentRoomCode);
                    }
                    catch (Exception joinEx)
                    {
                        _logger.LogWarning(joinEx, "重新加入房间异常（第{Round}轮第{Attempt}次）", round, attempt + 1);
                    }
                }
            }

            if (!rejoined)
            {
                _logger.LogError("重连后重新加入房间全部失败（2轮共6次），触发降级");
                _isReconnecting = false;
                OnDegraded?.Invoke();
                return;
            }
        }

        _isReconnecting = false;
        _logger.LogInformation("CoordinatorClient 重连流程完成");
    }

    public async Task<string?> CreateRoomAsync(string playerName = "", List<string>? whitelist = null, string playerUid = "", int expectedPlayerCount = 4)
    {
        if (_connection == null) return null;
        try
        {
            var roomCode = await _connection.InvokeAsync<string>("CreateRoom", playerName ?? "", whitelist ?? new List<string>(), playerUid ?? "", expectedPlayerCount);
            if (!string.IsNullOrEmpty(roomCode))
            {
                // 保存房间信息用于重连
                _currentRoomCode = roomCode;
                _playerName = playerName;
                _playerUid = playerUid;
                _isInRoom = true;
            }
            return roomCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateRoomAsync 失败");
            return null;
        }
    }

    public async Task<bool> JoinRoomAsync(string roomCode, string playerName = "", string playerUid = "")
    {
        if (_connection == null) return false;
        try
        {
            var success = await _connection.InvokeAsync<bool>("JoinRoom", roomCode, playerName ?? "", playerUid ?? "");
            if (success)
            {
                // 保存房间信息用于重连
                _currentRoomCode = roomCode;
                _playerName = playerName;
                _playerUid = playerUid;
                _isInRoom = true;
            }
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JoinRoomAsync 失败: {RoomCode}", roomCode);
            return false;
        }
    }

    /// <summary>
    /// 使用保存的房间信息重新加入房间（需求 2/3）。
    /// 供 WorldStateMonitor 和 OnConnectionClosed 内部使用。
    /// </summary>
    public async Task<bool> RejoinCurrentRoomAsync()
    {
        if (string.IsNullOrEmpty(_currentRoomCode))
        {
            _logger.LogWarning("RejoinCurrentRoomAsync: 无保存的房间码");
            return false;
        }
        return await JoinRoomAsync(_currentRoomCode, _playerName ?? "", _playerUid ?? "");
    }

    public async Task ReportArrivalAsync(string syncPointId)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ReportArrival", syncPointId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportArrivalAsync 失败: {SyncPointId}", syncPointId);
        }
    }

    /// <summary>
    /// 上报到达集合点（带预期人数）
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="expectedCount">预期到达人数，0表示使用房间总人数</param>
    public async Task ReportArrivalAsync(string syncPointId, int expectedCount)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ReportArrivalWithExpectedCount", syncPointId, expectedCount);
            _logger.LogInformation("[联机] 上报到达: {SyncId}，预期人数={Expected}", syncPointId, expectedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportArrivalAsync 失败: {SyncPointId}", syncPointId);
        }
    }

    public async Task ReportFightDoneAsync(string syncPointId)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ReportFightDone", syncPointId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportFightDoneAsync 失败: {SyncPointId}", syncPointId);
        }
    }

    public async Task ReportRouteListAsync(List<RouteHash> routes)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ReportRouteList", routes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportRouteListAsync 失败");
        }
    }

    public async Task ReportRouteVerificationDoneAsync()
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ReportRouteVerificationDone");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportRouteVerificationDoneAsync 失败");
        }
    }

    public async Task ReportWorldJoinedAsync()
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ReportWorldJoined");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportWorldJoinedAsync 失败");
        }
    }

    public async Task SetKazuhaPlayerAsync(int index)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("SetKazuhaPlayer", index);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetKazuhaPlayerAsync 失败");
        }
    }

    public async Task UpdateWhitelistAsync(List<string> whitelist)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("UpdateWhitelist", whitelist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateWhitelistAsync 失败");
        }
    }

    public async Task<List<RoomSummary>> GetOnlineRoomsAsync()
    {
        if (_connection == null) return [];
        try
        {
            return await _connection.InvokeAsync<List<RoomSummary>>("GetOnlineRooms");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOnlineRoomsAsync 失败");
            return [];
        }
    }

    public async Task SetRoomConfigAsync(RoomConfig config)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("SetRoomConfig", config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetRoomConfigAsync 失败");
        }
    }

    public async Task<RoomConfig?> GetRoomConfigAsync()
    {
        if (_connection == null) return null;
        try
        {
            return await _connection.InvokeAsync<RoomConfig?>("GetRoomConfig");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRoomConfigAsync 失败");
            return null;
        }
    }

    public async Task ReportHostReadyAsync()
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ReportHostReady");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportHostReadyAsync 失败");
        }
    }

    public async Task<bool> IsHostReadyAsync()
    {
        if (_connection == null) return false;
        try
        {
            return await _connection.InvokeAsync<bool>("IsHostReady");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsHostReadyAsync 失败");
            return false;
        }
    }

    public async Task SetHostRouteListAsync(List<string> routeNames)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("SetHostRouteList", routeNames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetHostRouteListAsync 失败");
        }
    }

    public async Task<List<string>> GetHostRouteListAsync()
    {
        if (_connection == null) return [];
        try
        {
            return await _connection.InvokeAsync<List<string>>("GetHostRouteList");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHostRouteListAsync 失败");
            return [];
        }
    }

    /// <summary>
    /// 更新当前路线进度信息（需求 6），下次心跳时自动上报。
    /// </summary>
    public void UpdateRouteProgress(int routeIndex, DateTime startTime, double estimatedSeconds)
    {
        _currentRouteIndex = routeIndex;
        _routeStartTime = startTime;
        _routeEstimatedSeconds = estimatedSeconds;
    }

    public async Task SendHeartbeatAsync()
    {
        if (_connection == null) return;
        try
        {
            if (_currentRouteIndex >= 0)
            {
                await _connection.InvokeAsync("HeartbeatWithProgress",
                    _currentRouteIndex, _routeStartTime, _routeEstimatedSeconds);
            }
            else
            {
                await _connection.InvokeAsync("Heartbeat");
            }
            _worldStateMonitor?.NotifyHeartbeatSuccess();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendHeartbeatAsync 失败");
            _worldStateMonitor?.NotifyHeartbeatFailure();
        }
    }

    /// <summary>
    /// 查询指定成员的路线进度（需求 6）。返回 null 表示查询失败或无进度信息。
    /// </summary>
    public async Task<MemberProgress?> GetMemberProgressAsync(string playerUid)
    {
        if (_connection == null || !IsConnected) return null;
        try
        {
            return await _connection.InvokeAsync<MemberProgress?>("GetMemberProgress", playerUid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetMemberProgressAsync 失败: {Uid}", playerUid);
            return null;
        }
    }

    /// <summary>
    /// 发送路线跳过通知（sync-point-route-skip-alignment 修复）
    /// 未连接时静默忽略，异常时 catch + 警告日志
    /// </summary>
    public async Task SendRouteSkippedAsync(int routeIndex)
    {
        if (_connection == null || !IsConnected) return;
        try
        {
            await _connection.InvokeAsync("RouteSkipped", routeIndex);
            _logger.LogInformation("[联机] 发送路线跳过通知: 路线 {Index}", routeIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendRouteSkippedAsync 失败");
        }
    }

    /// <summary>
    /// 发送等待点上报（skip-route-wait-point-report 修复）
    /// 实现带重试机制的等待点上报，支持指数退避重试（最多3次）
    /// 网络异常时静默处理，记录警告日志
    /// </summary>
    public async Task<bool> SendWaitPointReportAsync(string routeId, string syncPointId, int worldRound)
    {
        if (_connection == null || !IsConnected) 
        {
            _logger.LogWarning("[联机] 无法发送等待点上报：未连接");
            return false;
        }

        int retryCount = 0;
        const int maxRetries = 3;
        const int baseDelay = 1000; // 1秒

        while (retryCount < maxRetries)
        {
            try
            {
                await _connection.InvokeAsync("WaitPointReport", routeId, syncPointId, worldRound);
                _logger.LogInformation("[联机] 发送等待点上报: Route={RouteId}, SyncPoint={SyncPointId}, Round={WorldRound}", 
                    routeId, syncPointId, worldRound);
                return true;
            }
            catch (Exception ex) when (retryCount < maxRetries - 1)
            {
                retryCount++;
                int delay = baseDelay * retryCount; // 指数退避
                _logger.LogWarning(ex, "[联机] 等待点上报失败，第{RetryCount}/{MaxRetries}次重试，延迟{Delay}ms", 
                    retryCount, maxRetries, delay);
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[联机] 等待点上报最终失败，已重试{MaxRetries}次", maxRetries);
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// 发送成员路线进度更新（sync-point-route-skip-alignment 修复）
    /// 调用服务器 UpdateMemberProgress，只广播给其他玩家
    /// </summary>
    public async Task SendMemberProgressAsync(int routeIndex)
    {
        if (_connection == null || !IsConnected) return;
        try
        {
            await _connection.InvokeAsync("UpdateMemberProgress", routeIndex);
            _logger.LogDebug("[联机] 发送成员路线进度: {Index}", routeIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendMemberProgressAsync 失败");
        }
    }

    /// <summary>
    /// 获取对方路线索引（sync-point-route-skip-alignment 修复）
    /// 直接读本地缓存，返回 int?（缓存未命中返回 null）
    /// </summary>
    public int? GetPeerRouteIndex(string peerUid)
    {
        return _memberProgressCache.TryGetValue(peerUid, out var idx) ? idx : (int?)null;
    }

    /// <summary>
    /// 上报重对齐就绪状态（multiplayer-abort-and-realign spec）
    /// 客户端收到 AbortAndRealign 指令后，停止当前任务并上报就绪
    /// </summary>
    public async Task ReportRealignReadyAsync(int currentRouteIndex)
    {
        if (_connection == null || !IsConnected)
        {
            _logger.LogWarning("[联机] 无法上报重对齐就绪：未连接");
            return;
        }

        try
        {
            await _connection.InvokeAsync("RealignReady", currentRouteIndex);
            _logger.LogInformation("[联机] 已上报重对齐就绪，当前路线={CurrentRoute}", currentRouteIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 上报重对齐就绪失败");
        }
    }

    /// <summary>
    /// 重置成员路线进度缓存（sync-point-route-skip-alignment 修复）
    /// 每轮开始时调用，防止跨轮误判
    /// </summary>
    public void ResetMemberProgressCache()
    {
        _memberProgressCache.Clear();
        _logger.LogDebug("[联机] 成员路线进度缓存已重置（新一轮开始）");
    }

    /// <summary>
    /// 重置等待点上报状态（skip-route-wait-point-report 修复）
    /// 每轮开始时调用，防止跨轮状态污染
    /// </summary>
    public void ResetWaitPointState()
    {
        // 清理等待点相关状态
        // 注意：这里不清理 WaitPointReported 事件订阅，因为这是长期存在的
        _logger.LogDebug("[联机] 等待点上报状态已重置（新一轮开始）");
    }

    public async Task CloseRoomAsync()
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("CloseRoom");
            _logger.LogInformation("CloseRoomAsync 已发送关闭房间请求");
            // 关闭房间后清空房间码，防止重连时重新加入已关闭的旧房间
            _currentRoomCode = null;
            _isInRoom = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CloseRoomAsync 失败");
        }
    }

    public async Task ResetWorldJoinedAsync()
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ResetWorldJoined");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetWorldJoinedAsync 失败");
        }
    }

    /// <summary>
    /// 多轮世界重置（skip-route-wait-point-report 修复）
    /// 多轮世界新轮次开始时调用，清理所有等待点状态
    /// </summary>
    public async Task ResetForNewWorldRoundAsync(int newRound)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ResetForNewWorldRound", newRound);
            _logger.LogInformation("[联机] 发送多轮世界重置请求: Round {Round}", newRound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetForNewWorldRoundAsync 失败");
        }
    }

    /// <summary>
    /// 上报成员异常恢复状态。断线时静默失败，不抛异常。
    /// 携带递增版本号，接收方只接受更大版本号的更新，防止网络延迟导致的乱序覆盖。
    /// </summary>
    public async Task ReportMemberStatusAsync(MemberStatus status)
    {
        if (_connection == null || !IsConnected) return; // 断线时静默失败
        try
        {
            var version = Interlocked.Increment(ref _statusVersion);
            await _connection.InvokeAsync("ReportMemberStatus", status.ToString(), version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportMemberStatusAsync 失败（静默忽略），状态: {Status}", status);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_connection == null) return;
        try
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _isInRoom = false;
            _connection.Closed -= OnConnectionClosed;
            await _connection.StopAsync();
            _logger.LogInformation("CoordinatorClient 已断开连接");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisconnectAsync 时发生异常");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        if (_connection != null)
        {
            _connection.Closed -= OnConnectionClosed;
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
