namespace BgiCoordinatorServer.Models;

public class Room
{
    public string Code { get; set; } = "";
    public string HostConnectionId { get; set; } = "";
    public List<PlayerInfo> Players { get; set; } = [];
    public DateTime CreatedAt { get; set; }

    /// <summary>syncPointId → 已到达的 connectionId 集合</summary>
    public Dictionary<string, HashSet<string>> ArrivalSets { get; set; } = [];

    /// <summary>syncPointId → 已完成战斗的 connectionId 集合</summary>
    public Dictionary<string, HashSet<string>> FightDoneSets { get; set; } = [];

    /// <summary>万叶玩家序号（0=不指定）</summary>
    public int KazuhaPlayerIndex { get; set; } = 0;

    /// <summary>房间白名单</summary>
    public List<string> Whitelist { get; set; } = [];

    /// <summary>已完成路线验证的 connectionId 集合</summary>
    public HashSet<string> RouteVerificationDoneSet { get; set; } = [];

    /// <summary>已加入世界的 connectionId 集合</summary>
    public HashSet<string> WorldJoinedSet { get; set; } = [];

    /// <summary>房间期望人数</summary>
    public int ExpectedPlayerCount { get; set; } = 4;

    /// <summary>房主锄地配置</summary>
    public RoomConfig? HostConfig { get; set; }

    /// <summary>房主是否已进入等待状态</summary>
    public bool HostReady { get; set; } = false;

    /// <summary>房主筛选后的最终路线文件名列表（按执行顺序）</summary>
    public List<string> HostRouteList { get; set; } = [];

    /// <summary>当前世界轮次（多轮世界支持）</summary>
    public int CurrentWorldRound { get; set; } = 0;

    /// <summary>玩家等待点上报缓存：playerUid → WaitPointReport</summary>
    public Dictionary<string, WaitPointReport> WaitPoints { get; set; } = [];

    /// <summary>协调后的统一等待点</summary>
    public CoordinatedWaitPoint? CoordinatedWaitPoint { get; set; }

    // === 异常等待协调机制字段（multiplayer-abnormal-wait-coordination spec）===
    // Validates: Requirements 1.1, 1.3, 1.4

    /// <summary>
    /// 玩家异常状态：playerUid → AbnormalPlayerState
    /// 服务端维护所有玩家的异常状态，用于计算统一等待点
    /// </summary>
    public Dictionary<string, AbnormalPlayerState> AbnormalPlayerStates { get; set; } = new();

    /// <summary>
    /// 当前统一等待点（服务端计算）
    /// 指示正常玩家应在何处等待异常玩家
    /// </summary>
    public UnifiedWaitPoint? CurrentUnifiedWaitPoint { get; set; }

    /// <summary>
    /// 等待点到达记录：syncPointId → 已到达的 playerUid 集合
    /// 用于追踪哪些玩家已到达统一等待点
    /// </summary>
    public Dictionary<string, HashSet<string>> WaitPointArrivals { get; set; } = new();

    // === 异常中断重对齐机制字段（multiplayer-abort-and-realign spec）===

    /// <summary>
    /// 当前重对齐流程（null 表示没有进行中的重对齐）
    /// 当检测到异常玩家时创建，所有玩家对齐完成后清除
    /// </summary>
    public RealignProcess? CurrentRealignProcess { get; set; }
    
    // === 强制线路同步机制字段（multiplayer-route-enforcement spec）===
    
    /// <summary>
    /// 是否启用强制线路同步（默认启用）
    /// 启用后服务器会定期检测线路偏差并强制同步
    /// </summary>
    public bool RouteEnforcementEnabled { get; set; } = true;
    
    /// <summary>
    /// 线路偏差阈值（默认 1）
    /// 当玩家之间线路索引差异超过此阈值时触发强制同步
    /// </summary>
    public int RouteEnforcementThreshold { get; set; } = 1;
}
