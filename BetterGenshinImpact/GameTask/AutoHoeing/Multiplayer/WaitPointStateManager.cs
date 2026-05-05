#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 等待点状态管理器（skip-route-wait-point-report spec）
/// 管理多个玩家的等待点状态，支持状态清理和多轮世界状态隔离
/// </summary>
public class WaitPointStateManager : IDisposable
{
    private readonly ILogger<WaitPointStateManager> _logger = App.GetLogger<WaitPointStateManager>();
    
    /// <summary>
    /// 玩家等待点状态缓存（key=playerUid, value=等待点状态）
    /// </summary>
    private readonly ConcurrentDictionary<string, WaitPointState> _states = new();
    
    /// <summary>
    /// 清理定时器
    /// </summary>
    private readonly Timer _cleanupTimer;
    
    /// <summary>
    /// 清理间隔（默认30秒）
    /// </summary>
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// 状态过期时间（默认60秒）
    /// 异常玩家状态过期时间延长到5分钟，与超时等待时间一致
    /// </summary>
    private readonly TimeSpan _stateExpiry = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// 异常玩家状态过期时间（5分钟，与超时等待时间一致）
    /// </summary>
    private readonly TimeSpan _abnormalStateExpiry = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// 当前多轮世界轮次
    /// </summary>
    private int _currentWorldRound;
    
    /// <summary>
    /// 按轮次隔离的状态（用于调试和历史记录）
    /// </summary>
    private readonly Dictionary<int, RoundState> _roundStates = new();
    
    /// <summary>
    /// 异常玩家记录（key=playerUid, value=等待点状态）
    /// </summary>
    private readonly ConcurrentDictionary<string, WaitPointState> _abnormalPlayers = new();

    /// <summary>
    /// 创建新的等待点状态管理器
    /// </summary>
    public WaitPointStateManager()
    {
        _cleanupTimer = new Timer(_ => CleanupExpiredStates(), null, _cleanupInterval, _cleanupInterval);
        _logger.LogDebug("WaitPointStateManager 已初始化，清理间隔: {Interval}s", _cleanupInterval.TotalSeconds);
    }

    /// <summary>
    /// 设置当前多轮世界轮次
    /// </summary>
    public void SetWorldRound(int round)
    {
        if (round != _currentWorldRound)
        {
            _logger.LogInformation("等待点状态管理器轮次更新: {OldRound} → {NewRound}", _currentWorldRound, round);
            _currentWorldRound = round;
            
            // 清理旧轮次状态（保留最近3轮用于调试）
            var oldRounds = _roundStates.Keys
                .Where(r => r < round - 2)
                .ToList();
            
            foreach (var oldRound in oldRounds)
            {
                _roundStates.Remove(oldRound);
                _logger.LogDebug("清理旧轮次状态: Round {Round}", oldRound);
            }
            
            // 初始化新轮次状态
            if (!_roundStates.ContainsKey(round))
                _roundStates[round] = new RoundState();
        }
    }

    /// <summary>
    /// 获取当前轮次状态
    /// </summary>
    public RoundState GetCurrentRoundState()
    {
        if (!_roundStates.ContainsKey(_currentWorldRound))
            _roundStates[_currentWorldRound] = new RoundState();
        
        return _roundStates[_currentWorldRound];
    }

    /// <summary>
    /// 重置当前轮次状态
    /// </summary>
    public void ResetCurrentRound()
    {
        if (_roundStates.ContainsKey(_currentWorldRound))
        {
            _roundStates[_currentWorldRound].Reset();
            _logger.LogDebug("重置当前轮次状态: Round {Round}", _currentWorldRound);
        }
        
        // 清理所有玩家状态
        _states.Clear();
        _abnormalPlayers.Clear();
    }

    /// <summary>
    /// 更新玩家等待点状态
    /// </summary>
    public void UpdateState(string playerUid, WaitPointState state)
    {
        state.LastUpdated = DateTime.UtcNow;
        _states[playerUid] = state;
        
        // 记录异常玩家（跳过线路的玩家）
        _abnormalPlayers[playerUid] = state;
        
        // 记录到当前轮次状态
        var roundState = GetCurrentRoundState();
        roundState.RecordStateUpdate(playerUid, state);
        
        _logger.LogDebug("更新玩家等待点状态: {PlayerUid}, Route={RouteId}, SyncPoint={SyncPointId}, Round={WorldRound}", 
            playerUid, state.RouteId, state.SyncPointId, state.WorldRound);
    }

    /// <summary>
    /// 获取玩家等待点状态
    /// </summary>
    public WaitPointState? GetState(string playerUid)
    {
        if (_states.TryGetValue(playerUid, out var state) && !IsStateExpired(state))
            return state;
        
        return null;
    }

    /// <summary>
    /// 移除玩家等待点状态
    /// </summary>
    public void RemoveState(string playerUid)
    {
        _states.TryRemove(playerUid, out _);
        _logger.LogDebug("移除玩家等待点状态: {PlayerUid}", playerUid);
    }

    /// <summary>
    /// 获取所有有效状态
    /// </summary>
    public List<WaitPointState> GetAllValidStates()
    {
        return _states.Values
            .Where(s => !IsStateExpired(s))
            .ToList();
    }

    /// <summary>
    /// 获取协调后的等待点（异常协调中心：不再进行复杂协调，返回null）
    /// </summary>
    public CoordinatedWaitPoint? GetCoordinatedWaitPoint()
    {
        // 异常协调中心：不再进行复杂协调，每个异常玩家在自己的点等待
        return null;
    }

    /// <summary>
    /// 获取异常玩家数量
    /// </summary>
    public int GetAbnormalPlayerCount()
    {
        return _abnormalPlayers.Count;
    }

    /// <summary>
    /// 获取在指定同步点等待的异常玩家数量
    /// </summary>
    public int GetAbnormalPlayersAtPoint(string syncPointId)
    {
        return _abnormalPlayers.Values
            .Count(s => s.SyncPointId == syncPointId && !IsStateExpired(s));
    }

    /// <summary>
    /// 检查玩家是否是异常玩家
    /// </summary>
    public bool IsAbnormalPlayer(string playerUid)
    {
        return _abnormalPlayers.ContainsKey(playerUid);
    }

    /// <summary>
    /// 清理过期状态
    /// </summary>
    private void CleanupExpiredStates()
    {
        try
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _states
                .Where(kv => IsStateExpired(kv.Value))
                .Select(kv => kv.Key)
                .ToList();
            
            foreach (var key in expiredKeys)
            {
                _states.TryRemove(key, out _);
                _abnormalPlayers.TryRemove(key, out _);
                _logger.LogDebug("清理过期等待点状态: {PlayerUid}", key);
            }
            
            // 记录清理统计
            var roundState = GetCurrentRoundState();
            roundState.RecordCleanup(expiredKeys.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理过期状态时发生异常");
        }
    }

    /// <summary>
    /// 检查状态是否过期
    /// 异常玩家使用5分钟过期时间，与超时等待时间一致
    /// </summary>
    private bool IsStateExpired(WaitPointState state)
    {
        // 异常玩家使用更长的过期时间
        var expiry = _abnormalPlayers.ContainsKey(state.PlayerUid) 
            ? _abnormalStateExpiry 
            : _stateExpiry;
        
        return DateTime.UtcNow - state.LastUpdated > expiry;
    }

    /// <summary>
    /// 获取统计信息
    /// </summary>
    public StateStatistics GetStatistics()
    {
        var validStates = GetAllValidStates();
        
        return new StateStatistics
        {
            TotalStates = _states.Count,
            ValidStates = validStates.Count,
            AbnormalPlayers = _abnormalPlayers.Count,
            CurrentWorldRound = _currentWorldRound,
            RoundStatesCount = _roundStates.Count,
            LastCleanupTime = GetCurrentRoundState().LastCleanupTime
        };
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _states.Clear();
        _abnormalPlayers.Clear();
        _roundStates.Clear();
        
        _logger.LogDebug("WaitPointStateManager 已释放");
    }
}

/// <summary>
/// 等待点状态
/// </summary>
public class WaitPointState
{
    public string PlayerUid { get; set; } = string.Empty;
    public string RouteId { get; set; } = string.Empty;
    public string SyncPointId { get; set; } = string.Empty;
    public int WorldRound { get; set; }
    public DateTime LastUpdated { get; set; }
    
    public override string ToString()
    {
        return $"WaitPointState[Player={PlayerUid}, Route={RouteId}, SyncPoint={SyncPointId}, Round={WorldRound}, Updated={LastUpdated:HH:mm:ss}]";
    }
}

/// <summary>
/// 轮次状态（用于调试和历史记录）
/// </summary>
public class RoundState
{
    public int Round { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    public int StateUpdates { get; set; }
    public int Coordinations { get; set; }
    public int Cleanups { get; set; }
    public DateTime? LastCleanupTime { get; set; }
    
    public void RecordStateUpdate(string playerUid, WaitPointState state)
    {
        StateUpdates++;
    }
    
    public void RecordCoordination(CoordinatedWaitPoint point)
    {
        Coordinations++;
    }
    
    public void RecordCleanup(int count)
    {
        Cleanups++;
        LastCleanupTime = DateTime.UtcNow;
    }
    
    public void Reset()
    {
        StateUpdates = 0;
        Coordinations = 0;
        Cleanups = 0;
        LastCleanupTime = null;
    }
    
    public override string ToString()
    {
        return $"RoundState[Round={Round}, Updates={StateUpdates}, Coordinations={Coordinations}, Cleanups={Cleanups}]";
    }
}

/// <summary>
/// 状态统计信息
/// </summary>
public class StateStatistics
{
    public int TotalStates { get; set; }
    public int ValidStates { get; set; }
    public int AbnormalPlayers { get; set; }
    public int CurrentWorldRound { get; set; }
    public int RoundStatesCount { get; set; }
    public DateTime? LastCleanupTime { get; set; }
    
    public override string ToString()
    {
        return $"StateStatistics[Total={TotalStates}, Valid={ValidStates}, Abnormal={AbnormalPlayers}, Round={CurrentWorldRound}]";
    }
}