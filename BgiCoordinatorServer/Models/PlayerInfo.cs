namespace BgiCoordinatorServer.Models;

public class PlayerInfo
{
    public string ConnectionId { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string PlayerUid { get; set; } = "";
    public PlayerStatus Status { get; set; } = PlayerStatus.Waiting;
    public DateTime LastHeartbeat { get; set; }

    // === 路线进度信息（需求 6）===
    /// <summary>当前路线索引（0-based），-1 表示未上报</summary>
    public int CurrentRouteIndex { get; set; } = -1;
    /// <summary>当前路线开始时间（UTC）</summary>
    public DateTime RouteStartTime { get; set; }
    /// <summary>当前路线预估总时间（秒）</summary>
    public double RouteEstimatedSeconds { get; set; }

    // === 异常玩家状态字段（multiplayer-abnormal-wait-coordination spec）===
    // Validates: Requirements 1.1, 1.4

    /// <summary>
    /// 是否为异常玩家
    /// 当玩家在执行线路过程中遇到错误状态，触发线路跳过时设置为 true
    /// </summary>
    public bool IsAbnormal { get; set; }

    /// <summary>
    /// 当前等待点ID（异常玩家专用）
    /// 异常玩家上报的等待点，必须是 _tp_ 格式的传送点
    /// </summary>
    public string? WaitPointId { get; set; }
}
