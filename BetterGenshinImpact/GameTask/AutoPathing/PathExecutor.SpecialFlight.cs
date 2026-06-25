using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoPathing.Suspend;
using BetterGenshinImpact.GameTask.AutoSkip;
using BetterGenshinImpact.GameTask.AutoSkip.Assets;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using BetterGenshinImpact.GameTask.Model.Area;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static BetterGenshinImpact.GameTask.SystemControl;
using ActionEnum = BetterGenshinImpact.GameTask.AutoPathing.Model.Enum.ActionEnum;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public partial class PathExecutor
{
    // 特殊飞行并不是新的 waypoint 类型，而是复用 CombatScript / StopFlying / ForceTp 的 ActionParams。
    // 识别规则集中在本文件末尾的 GetSpecialFlightScriptAction：
    // - "角色 e(...)"：起飞；脚本中包含 dash 时视为快飞，否则视为慢飞。
    // - "角色 wait(...)"：途径点；wait(0) 只作为纯途径标记，不继续执行后续普通脚本。
    // - "角色 skill(...)"：落下点；通常用于取消特殊飞行并衔接下落攻击。
    //
    // 主流程只保留少量接入点：起飞后跳过中间普通点、到 via/drop 点提前判定到达、
    // 飞行状态消失时做兜底或登记接续飞行。本文件维护这些状态和脚本解析细节。

    // 当前支持特殊飞行脚本的角色名。默认角色用于兼容未显式写角色名的旧配置；
    // 多角色路线仍以脚本片段开头的角色名为准，避免把 A 角色的 via/drop 匹配给 B 角色。
    private static readonly string[] SpecialFlightAvatarNames = ["恰斯卡", "流浪者", "伊法"];
    private static string DefaultSpecialFlightAvatarName => SpecialFlightAvatarNames[0];

    // 路线坐标距离阈值：靠近特殊飞行动作点时提前转向和预读动作。
    // 预读只保存“这个点是什么特殊动作”，不提前按键；真正按键仍在到点后执行。
    private const double SpecialFlightPreReadDistance = 15;

    // 快飞/慢飞接近 drop 点时，允许提前判定到达并取消特殊飞行的缓冲距离。
    // 快飞惯性更大，所以缓冲距离比慢飞大。
    private const double SpecialFlightFastFlyDropArrivalBufferDistance = 12;
    private const double SpecialFlightSlowFlyDropArrivalBufferDistance = 5;

    // 特殊飞行切回普通飞行/普通移动点时，提前退出特殊飞行的距离阈值。
    // 这类点不是显式 drop，但继续保持特殊飞行容易越过普通路线目标。
    private const double SpecialFlightNormalFlyTransitionDistance = 12;

    // 经过 via 点时，提前判定通过的距离阈值。
    // via 点一般不需要精准落点，只需要保留飞行状态并继续追下一个特殊目标。
    private const double SpecialFlightFastFlyViaArrivalDistance = 18;
    private const double SpecialFlightSlowFlyViaArrivalDistance = 12;

    // 特殊飞行消失后，如果离当前目标过近就不再接续，避免刚起飞又立刻落点造成抖动。
    private const double SpecialFlightLostMinDistance = 6;

    // 登记接续飞行时，经当前目标到下一特殊目标/最终落点的剩余距离必须超过该阈值。
    // 距离太短时继续普通移动更稳定，不值得等待技能 CD 后再次起飞。
    private const double SpecialFlightContinueMinTargetDistance = 35;

    // 接续前如果已经切入普通自由落体/普通飞行状态，等待角色落地或 UI 状态稳定的时间。
    private const int SpecialFlightContinuationFreeFallWaitMs = 2000;

    // 特殊飞行落地后，少数情况下人物会卡在原地；先跳过自由落体缓冲，再在短窗口内检测并跳跃解卡。
    private const int SpecialFlightLandingStuckArmDelayMs = 2000;
    private const int SpecialFlightLandingStuckWatchMs = 3000;
    private const int SpecialFlightLandingStuckStillMs = 400;
    private const int SpecialFlightLandingStuckJumpCooldownMs = 1800;
    private const double SpecialFlightLandingStuckMoveDistance = 0.3;

    // 特殊飞行状态机分三组状态：
    // 1. 本轮飞行状态：当前动作、角色、跳点目标、飞行监控和按键控制。
    // 2. 延迟脚本状态：起飞成功后，分号后面的普通脚本暂存，等本轮飞行结束再执行。
    // 3. 接续飞行状态：飞行状态提前消失后，不原地等待 CD，而是在普通移动中尝试重新起飞。

    // 已提前识别并转向的特殊飞行点，真正执行时直接复用预读动作。
    private Waypoint? _preReadSpecialFlightCombatScriptWaypoint = null;

    // 预读得到的特殊飞行动作，避免执行时因脚本被拆分而重复解析。
    private SpecialFlightAction _preReadSpecialFlightAction = SpecialFlightAction.None;

    // 起飞成功后跳过中间普通点，直到目标 via/drop/落点。
    // 该字段只影响 PathExecutor 主循环“是否执行当前 waypoint”，不改变路线本身。
    private Waypoint? _specialFlightSkipUntilWaypoint = null;

    // 当前正在执行的特殊飞行动作，用于判断到点、监控和兜底逻辑。
    // None 表示没有活跃的特殊飞行段，即使后续 waypoint 含特殊脚本也尚未起飞。
    private SpecialFlightAction _specialFlightAction = SpecialFlightAction.None;

    // 当前特殊飞行所属角色名，用于多角色脚本中只匹配同角色的 via/drop 点。
    private string? _specialFlightAvatarName = null;

    // 特殊飞行中途消失后，切回普通飞行并等待到达的落下兜底点。
    private Waypoint? _specialFlightDropFallbackNormalAttackWaypoint = null;

    // 已提前处理取消飞行的落下点，防止到点后重复下落/攻击。
    private Waypoint? _specialFlightDropHandledWaypoint = null;

    // 普通飞行兜底到落下点后，执行下落攻击前等待的毫秒数。
    private int _specialFlightDropFallbackNormalAttackDelayMs = 0;

    // 普通飞行兜底到落下点后是否需要补一次普通攻击。
    private bool _specialFlightDropFallbackShouldNormalAttack = false;

    // 特殊飞行结束后是否需要补一次攻击，常用于提前落点后的攻击动作。
    private bool _specialFlightAttackAfterFinished = false;

    // 特殊飞行结束后补攻击前等待的毫秒数。
    private int _specialFlightAttackAfterFinishedDelayMs = 0;

    // via 点停留、升降控制等持续按键任务的取消源。
    // 每次开始新的特殊飞行控制命令前都会取消旧任务，避免旧按键延迟释放影响新段。
    private CancellationTokenSource? _specialFlightControlCts = null;

    // 起飞成功后的普通简易策略片段需要挂起，等本轮特殊飞行结束后再执行。
    // 被挂起的分号后普通脚本，避免起飞成功后立即打断飞行。
    private string? _specialFlightDeferredCombatScript = null;

    // 延迟脚本所属点位，恢复执行时用来提供原始上下文。
    private WaypointForTrack? _specialFlightDeferredCombatScriptWaypoint = null;

    // 延迟脚本执行时仍需保留的下一个点位上下文。
    private Waypoint? _specialFlightDeferredCombatScriptNextWaypoint = null;

    // 特殊飞行消失后不原地等 CD；先登记目标，后续普通移动过程中再尝试接续飞行。
    // 等待接续飞行重新起飞后要跳到的目标点。
    private Waypoint? _specialFlightPendingContinuationTargetWaypoint = null;

    // 等待接续的动作类型，通常沿用原来的快飞/慢飞。
    private SpecialFlightAction _specialFlightPendingContinuationAction = SpecialFlightAction.None;

    // 等待接续的角色名，防止跨角色误接续。
    private string? _specialFlightPendingContinuationAvatarName = null;

    // 接续前是否已经刷新过技能 CD，避免每轮移动反复 OCR。
    private bool _specialFlightPendingContinuationCdRefreshed = false;

    // 快飞时鼠标左键是否由特殊飞行逻辑按下，用于释放时避免误操作。
    private bool _specialFlightSprintMouseDown = false;

    // 是否启用特殊飞行状态监控；开启后会检测飞行状态消失并触发兜底。
    private bool _specialFlightMonitorEnabled = false;

    // 本轮监控是否曾识别到飞行状态；未起飞成功时不按“飞行消失”处理。
    private bool _specialFlightWasDetected = false;

    // 上次处理飞行消失的时间，给状态识别留缓冲，避免连续触发。
    private DateTime _lastSpecialFlightLostHandleTime = DateTime.MinValue;

    // 特殊飞行落地后短窗口内的卡死检测状态。窗口只由特殊飞行消失开启，普通路线不会触发。
    private DateTime _specialFlightLandingStuckWatchReadyAt = DateTime.MinValue;
    private DateTime _specialFlightLandingStuckWatchUntil = DateTime.MinValue;
    private DateTime _specialFlightLandingStuckStillSince = DateTime.MinValue;
    private DateTime _lastSpecialFlightLandingStuckJumpTime = DateTime.MinValue;
    private Point2f? _specialFlightLandingStuckLastPosition = null;

    private bool ShouldRunSpecialFlightForStopFlying(Waypoint waypoint)
    {
        var actionParams = waypoint.ActionParams;
        if (string.IsNullOrWhiteSpace(actionParams))
        {
            return false;
        }

        var start = 0;
        while (start < actionParams.Length)
        {
            while (start < actionParams.Length && IsSpecialFlightCommandSeparator(actionParams[start]))
            {
                start++;
            }

            if (start >= actionParams.Length)
            {
                break;
            }

            var end = start;
            while (end < actionParams.Length && !IsSpecialFlightCommandSeparator(actionParams[end]))
            {
                end++;
            }

            var action = GetSpecialFlightScriptAction(actionParams[start..end], out var avatarName);
            if (action == SpecialFlightAction.Drop
                && !string.IsNullOrEmpty(avatarName)
                && IsSpecialFlightAvatarAvailable(avatarName))
            {
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    // 执行含特殊飞行标记的简易策略。
    //
    // 一个 ActionParams 可能同时包含普通脚本和特殊飞行片段，例如：
    //   普通片段 ; 恰斯卡 e(),dash(...) ; 普通片段
    //
    // 执行顺序是：
    // 1. 先跑特殊片段前面的普通脚本；
    // 2. 再把特殊片段单独临时塞回 waypoint.ActionParams，让原有执行函数复用解析逻辑；
    // 3. 如果特殊片段是起飞且起飞成功，后面的普通脚本延迟到飞行结束；
    // 4. 如果特殊片段是 wait(0) via，只作为途径点标记，不继续跑后续普通脚本。
    private async Task<SpecialFlightExecutionResult> ExecuteSpecialFlightCombatScriptInOrderAsync(WaypointForTrack waypoint, Waypoint? nextWaypoint)
    {
        var result = SpecialFlightExecutionResult.Noop;
        var remaining = waypoint.ActionParams ?? string.Empty;
        while (true)
        {
            var parts = SplitSpecialFlightCombatScript(remaining);
            if (await RunCombatScriptFragmentAsync(waypoint, parts.before))
            {
                result = SpecialFlightExecutionResult.Executed;
            }

            if (string.IsNullOrWhiteSpace(parts.special))
            {
                break;
            }

            var specialResult = await RunWithTemporaryActionParamsAsync(waypoint, parts.special, () => ExecuteSpecialFlightCombatScriptAsync(waypoint, nextWaypoint));
            if (specialResult == SpecialFlightExecutionResult.Executed)
            {
                result = SpecialFlightExecutionResult.Executed;
            }

            var specialAction = GetSpecialFlightScriptAction(parts.special, out _);

            // wait(0) 是纯途径点标记，后续普通片段不执行；wait(0.1) 这类非零等待继续按顺序执行。
            if (specialResult == SpecialFlightExecutionResult.Executed
                && specialAction == SpecialFlightAction.Via
                && TryGetSpecialFlightViaStayDelayMs(parts.special, out var viaStayDelayMs, out _)
                && viaStayDelayMs == 0)
            {
                break;
            }

            // 起飞成功后，分号后面的普通脚本延迟到本轮特殊飞行结束，避免起飞后立刻打断移动。
            if (specialResult == SpecialFlightExecutionResult.Executed
                && IsSpecialFlightStartAction(specialAction)
                && _specialFlightMonitorEnabled
                && !string.IsNullOrWhiteSpace(parts.after))
            {
                SetDeferredSpecialFlightCombatScript(waypoint, nextWaypoint, parts.after);
                break;
            }

            remaining = parts.after;
        }

        return result;
    }

    // 普通脚本片段仍交给 CombatScript 的原有 AfterHandler 执行。
    // 这里临时改 ActionParams / CombatScript，finally 再还原，避免影响后续特殊飞行解析。
    private async Task<bool> RunCombatScriptFragmentAsync(WaypointForTrack waypoint, string script)
    {
        script = TrimCombatScriptFragment(script);
        if (string.IsNullOrWhiteSpace(script))
        {
            return false;
        }

        var originalActionParams = waypoint.ActionParams;
        var originalCombatScript = waypoint.CombatScript;
        try
        {
            waypoint.ActionParams = script;
            waypoint.CombatScript = CombatScriptParser.ParseContext(script, false);
            if (waypoint.CombatScript.CombatCommands.Count == 0)
            {
                return false;
            }

            await ActionFactory.GetAfterHandler(ActionEnum.CombatScript.Code).RunAsync(ct, waypoint, PartyConfig);
            return true;
        }
        finally
        {
            waypoint.ActionParams = originalActionParams;
            waypoint.CombatScript = originalCombatScript;
        }
    }

    // 特殊飞行片段也复用 waypoint.ActionParams 作为输入。
    // 这样 ExecuteSpecialFlightCombatScriptAsync 不需要额外参数对象，但必须保证执行后恢复原值。
    private async Task<SpecialFlightExecutionResult> RunWithTemporaryActionParamsAsync(WaypointForTrack waypoint, string actionParams, Func<Task<SpecialFlightExecutionResult>> action)
    {
        var originalActionParams = waypoint.ActionParams;
        try
        {
            waypoint.ActionParams = actionParams;
            return await action();
        }
        finally
        {
            waypoint.ActionParams = originalActionParams;
        }
    }

    // 起飞成功后，分号后面的普通脚本不能马上执行。
    // 例如起飞后立刻 attack/skill 会打断飞行，所以先保存，等 drop、提前落点或飞行消失处理后再跑。
    private void SetDeferredSpecialFlightCombatScript(WaypointForTrack waypoint, Waypoint? nextWaypoint, string script)
    {
        script = TrimCombatScriptFragment(script);
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        _specialFlightDeferredCombatScript = script;
        _specialFlightDeferredCombatScriptWaypoint = waypoint;
        _specialFlightDeferredCombatScriptNextWaypoint = nextWaypoint;
        Logger.LogInformation("特殊飞行结束后执行简易策略片段：{Script}", script);
    }

    private void ClearDeferredSpecialFlightCombatScript()
    {
        _specialFlightDeferredCombatScript = null;
        _specialFlightDeferredCombatScriptWaypoint = null;
        _specialFlightDeferredCombatScriptNextWaypoint = null;
    }

    // 在本轮特殊飞行自然结束、提前落点或兜底结束后调用。
    // 清空状态放在执行前，防止延迟脚本自身再次触发特殊飞行时被旧状态覆盖。
    private async Task RunDeferredSpecialFlightCombatScriptAsync()
    {
        var script = _specialFlightDeferredCombatScript;
        var waypoint = _specialFlightDeferredCombatScriptWaypoint;
        var nextWaypoint = _specialFlightDeferredCombatScriptNextWaypoint;
        if (string.IsNullOrWhiteSpace(script) || waypoint is null)
        {
            return;
        }

        ClearDeferredSpecialFlightCombatScript();
        Logger.LogInformation("特殊飞行结束，执行延迟简易策略片段：{Script}", script);
        await RunWithTemporaryActionParamsAsync(waypoint, script, () => ExecuteSpecialFlightCombatScriptInOrderAsync(waypoint, nextWaypoint));
    }

    // 将 ActionParams 拆成“特殊飞行片段之前 / 特殊飞行片段 / 之后”三段。
    // 分隔符支持英文分号、中文分号和换行，便于路线文件中分行书写。
    // 每次只拆第一个特殊飞行片段；后续片段通过 ExecuteSpecialFlightCombatScriptInOrderAsync 的循环继续处理。
    private static (string before, string special, string after) SplitSpecialFlightCombatScript(Waypoint waypoint)
    {
        return SplitSpecialFlightCombatScript(waypoint.ActionParams ?? string.Empty);
    }

    private static (string before, string special, string after) SplitSpecialFlightCombatScript(string actionParams)
    {
        var markerIndex = GetSpecialFlightActionIndex(actionParams);
        if (markerIndex < 0)
        {
            return (actionParams, string.Empty, string.Empty);
        }

        var start = markerIndex;
        while (start > 0 && !IsSpecialFlightCommandSeparator(actionParams[start - 1]))
        {
            start--;
        }

        var end = markerIndex;
        while (end < actionParams.Length && !IsSpecialFlightCommandSeparator(actionParams[end]))
        {
            end++;
        }

        var afterStart = end;
        while (afterStart < actionParams.Length && IsSpecialFlightCommandSeparator(actionParams[afterStart]))
        {
            afterStart++;
        }

        return (
            actionParams[..start],
            actionParams[start..end],
            afterStart < actionParams.Length ? actionParams[afterStart..] : string.Empty);
    }

    private static string TrimCombatScriptFragment(string script)
    {
        return script.Trim(' ', '\t', '\r', '\n', ';');
    }

    // 靠近特殊飞行动作点时提前转向，减少到点后再转向造成的起飞方向偏差。
    // 这里只做预读和预转向，不会执行按键；真正起飞/落下仍由 AfterMoveToTarget 阶段触发。
    private async Task TryPreReadSpecialFlightCombatScriptAsync(WaypointForTrack waypoint, Waypoint? nextWaypoint, double distance, Point2f position)
    {
        var hasAvailableAction = TryGetAvailableSpecialFlightAction(waypoint, out var action);
        if (distance > SpecialFlightPreReadDistance
            || !IsSpecialFlightActionWaypoint(waypoint)
            || !hasAvailableAction
            || ReferenceEquals(_preReadSpecialFlightCombatScriptWaypoint, waypoint))
        {
            return;
        }

        _preReadSpecialFlightCombatScriptWaypoint = waypoint;
        _preReadSpecialFlightAction = action;
        var rotateTarget = waypoint.Type == WaypointType.Teleport.Code ? nextWaypoint ?? waypoint : waypoint;
        await PreRotateForSpecialFlightAsync(rotateTarget, position);
    }

    // 当前 waypoint 是传送点时，下一点可能紧跟特殊飞行起飞。
    // 这种场景没有普通移动过程可用于提前转向，所以在传送点附近预读下一点。
    private async Task TryPreReadNextSpecialFlightCombatScriptFromTeleportAsync(WaypointForTrack? waypoint, Waypoint? nextWaypoint, double distance, double? nextDistance, Point2f position)
    {
        var action = SpecialFlightAction.None;
        var hasAvailableAction = nextWaypoint is not null && TryGetAvailableSpecialFlightAction(nextWaypoint, out action);
        if (waypoint?.Type != WaypointType.Teleport.Code
            || distance > SpecialFlightPreReadDistance
            || !nextDistance.HasValue
            || nextDistance.Value > SpecialFlightPreReadDistance
            || nextWaypoint is null
            || !IsSpecialFlightActionWaypoint(nextWaypoint)
            || !hasAvailableAction
            || ReferenceEquals(_preReadSpecialFlightCombatScriptWaypoint, nextWaypoint))
        {
            return;
        }

        _preReadSpecialFlightCombatScriptWaypoint = nextWaypoint;
        _preReadSpecialFlightAction = action;
        await PreRotateForSpecialFlightAsync(nextWaypoint, position);
    }

    private async Task PreRotateForSpecialFlightAsync(Waypoint waypoint, Point2f position)
    {
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        await WaitUntilRotatedTo(targetOrientation, 3);
    }

    private async Task PreRotateForSpecialFlightAtTargetAsync(WaypointForTrack waypoint, Waypoint? targetWaypoint = null)
    {
        using var screen = CaptureToRectArea();
        var position = await GetPosition(screen, waypoint);
        await PreRotateForSpecialFlightAsync(targetWaypoint ?? waypoint, position);
    }

    private async Task<SpecialFlightExecutionResult> ExecuteSpecialFlightCombatScriptAsync(WaypointForTrack waypoint, Waypoint? nextWaypoint)
    {
        var action = GetSpecialFlightAction(waypoint);
        var avatarName = GetSpecialFlightAvatarName(waypoint) ?? DefaultSpecialFlightAvatarName;
        if (ReferenceEquals(_preReadSpecialFlightCombatScriptWaypoint, waypoint))
        {
            _preReadSpecialFlightCombatScriptWaypoint = null;
            action = _preReadSpecialFlightAction;
            _preReadSpecialFlightAction = SpecialFlightAction.None;
        }

        if (!IsSpecialFlightAvatarAvailable(avatarName))
        {
            return SpecialFlightExecutionResult.Noop;
        }

        if (ShouldNoopInactiveSpecialFlightAction(action, waypoint))
        {
            return SpecialFlightExecutionResult.Noop;
        }

        var pairedDropWaypoint = FindSpecialFlightPairedDropWaypoint(waypoint, action);
        var nextSpecialFlightTargetWaypoint = FindSpecialFlightNextTargetWaypoint(waypoint, action);
        await PreRotateForSpecialFlightAtTargetAsync(waypoint, nextSpecialFlightTargetWaypoint ?? pairedDropWaypoint ?? nextWaypoint);

        switch (action)
        {
            case SpecialFlightAction.FastFly:
                Logger.LogInformation("特殊飞行简易策略：{AvatarName}快飞", avatarName);
                if (!await SwitchToSpecialFlightAsync(avatarName))
                {
                    return SpecialFlightExecutionResult.Executed;
                }

                if (await TryStartSpecialFlightAsync(SpecialFlightAction.FastFly, avatarName))
                {
                    _specialFlightSkipUntilWaypoint = nextSpecialFlightTargetWaypoint ?? pairedDropWaypoint;
                    _specialFlightAction = SpecialFlightAction.FastFly;
                    _specialFlightAvatarName = avatarName;
                    SetSpecialFlightAttackAfterFinished(waypoint);
                    StartSpecialFlightControlCommands(waypoint.ActionParams, avatarName);
                }
                else
                {
                    ContinueAfterSpecialFlightStartFailed(avatarName, SpecialFlightAction.FastFly);
                }

                break;
            case SpecialFlightAction.SlowFly:
                Logger.LogInformation("特殊飞行简易策略：{AvatarName}慢飞", avatarName);
                if (!await SwitchToSpecialFlightAsync(avatarName))
                {
                    return SpecialFlightExecutionResult.Executed;
                }

                if (await TryStartSpecialFlightAsync(SpecialFlightAction.SlowFly, avatarName))
                {
                    _specialFlightSkipUntilWaypoint = nextSpecialFlightTargetWaypoint ?? pairedDropWaypoint;
                    _specialFlightAction = SpecialFlightAction.SlowFly;
                    _specialFlightAvatarName = avatarName;
                    SetSpecialFlightAttackAfterFinished(waypoint);
                    StartSpecialFlightControlCommands(waypoint.ActionParams, avatarName);
                }
                else
                {
                    ContinueAfterSpecialFlightStartFailed(avatarName, SpecialFlightAction.SlowFly);
                }

                break;
            case SpecialFlightAction.Via:
                Logger.LogInformation("特殊飞行简易策略：{AvatarName}途径", avatarName);
                if (!IsSpecialFlightFlyingNow())
                {
                    Logger.LogInformation("{AvatarName}未处于飞行状态，取消途径跳点", avatarName);
                    _lastSpecialFlightLostHandleTime = DateTime.UtcNow;
                    await RunSpecialFlightAttackAfterFinishedIfNeededAsync(avatarName);
                    PreparePendingSpecialFlightContinuation(waypoint, true);
                    _specialFlightSkipUntilWaypoint = null;
                    StopSpecialFlightMonitor();
                    ReleaseSpecialFlightSprintMouse();
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                }
                else if (TryGetSpecialFlightViaStayDelayMs(waypoint.ActionParams, out var viaControlDelayMs, out _)
                         && viaControlDelayMs == 0
                         && HasSpecialFlightVerticalControlCommands(waypoint.ActionParams))
                {
                    StartSpecialFlightControlCommands(waypoint.ActionParams, avatarName);
                }
                else if (TryGetSpecialFlightViaStayDelayMs(waypoint.ActionParams, out var viaStayDelayMs, out var dashAfterStay)
                         && viaStayDelayMs > 0)
                {
                    Logger.LogInformation("{AvatarName}途径点停留{DelayMs}ms", avatarName, viaStayDelayMs);
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                    ReleaseSpecialFlightSprintMouse();
                    await Delay(viaStayDelayMs, ct);
                    if (dashAfterStay && IsSpecialFlightFlyingNow())
                    {
                        Logger.LogInformation("{AvatarName}途径点停留结束，恢复快飞", avatarName);
                        EnsureMoveForwardDown();
                        Simulation.SendInput.Mouse.RightButtonDown();
                        _specialFlightSprintMouseDown = true;
                    }
                }

                if (IsSpecialFlightFlyingNow())
                {
                    _specialFlightSkipUntilWaypoint = FindSpecialFlightNextTargetWaypoint(waypoint, SpecialFlightAction.Via);
                }

                break;
            case SpecialFlightAction.Drop:
                Logger.LogInformation("特殊飞行简易策略：{AvatarName}落下", avatarName);
                if (ReferenceEquals(_specialFlightDropHandledWaypoint, waypoint))
                {
                    Logger.LogInformation("{AvatarName}落下点已提前取消飞行", avatarName);
                    _specialFlightDropHandledWaypoint = null;
                    return SpecialFlightExecutionResult.Noop;
                }

                if (ReferenceEquals(_specialFlightDropFallbackNormalAttackWaypoint, waypoint))
                {
                    Logger.LogInformation("{AvatarName}落下点已切回普通飞行，到点后处理落下", avatarName);
                    StopSpecialFlightMonitor();
                    ReleaseSpecialFlightSprintMouse();
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                    if (_specialFlightDropFallbackShouldNormalAttack)
                    {
                        await RunSpecialFlightDropAttackAsync(avatarName, _specialFlightDropFallbackNormalAttackDelayMs);
                    }

                    _specialFlightDropFallbackNormalAttackWaypoint = null;
                    _specialFlightDropFallbackNormalAttackDelayMs = 0;
                    _specialFlightDropFallbackShouldNormalAttack = false;
                    await RunDeferredSpecialFlightCombatScriptAsync();
                    break;
                }

                if (!IsSpecialFlightFlyingNow())
                {
                    Logger.LogInformation("{AvatarName}未处于飞行状态，跳过落下", avatarName);
                    StopSpecialFlightMonitor();
                    ReleaseSpecialFlightSprintMouse();
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                    break;
                }

                await CancelSpecialFlightAtDropAsync(waypoint);

                break;
        }

        return SpecialFlightExecutionResult.Executed;
    }

    private bool ShouldNoopInactiveSpecialFlightAction(SpecialFlightAction action, Waypoint waypoint)
    {
        if (action is not (SpecialFlightAction.Via or SpecialFlightAction.Drop))
        {
            return false;
        }

        return !_specialFlightMonitorEnabled
               && _specialFlightAction == SpecialFlightAction.None
               && !ReferenceEquals(_specialFlightDropHandledWaypoint, waypoint)
               && !ReferenceEquals(_specialFlightDropFallbackNormalAttackWaypoint, waypoint);
    }

    private void ContinueAfterSpecialFlightStartFailed(string avatarName, SpecialFlightAction action)
    {
        Logger.LogInformation("{AvatarName}{ActionName}状态未启动，继续执行后续路径点",
            avatarName,
            GetSpecialFlightActionName(action));
        StopSpecialFlightMonitor();
        ReleaseSpecialFlightSprintMouse();
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
    }

    private async Task CancelSpecialFlightAtDropAsync(WaypointForTrack waypoint)
    {
        StopSpecialFlightMonitor();
        ReleaseSpecialFlightSprintMouse();
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
        await TapSpecialFlightElementalSkillAsync();
        if (TryGetCurrentSpecialFlightDropNormalAttackDelayMs(waypoint, out var dropDelayMs))
        {
            await RunSpecialFlightDropAttackAsync(GetSpecialFlightLogAvatarName(waypoint), dropDelayMs);
        }

        await RunDeferredSpecialFlightCombatScriptAsync();
        StartSpecialFlightLandingStuckWatch();
    }

    private async Task RunSpecialFlightDropAttackAsync(string avatarName, int delayMs)
    {
        if (delayMs > 0)
        {
            using (var screen = CaptureToRectArea())
            {
                if (StopFlyingHandler.IsNormalFlightBySpaceKey(screen))
                {
                    Logger.LogInformation("{AvatarName}落下点取消普通飞行", avatarName);
                    Simulation.SendInput.SimulateAction(GIActions.Jump);
                    await Delay(300, ct);
                }
            }

            Logger.LogInformation("{AvatarName}落下点自由落体{DelayMs}ms后下落攻击", avatarName, delayMs);
            await Delay(delayMs, ct);
        }
        else
        {
            Logger.LogInformation("{AvatarName}落下点立即下落攻击", avatarName);
            await Delay(250, ct);
        }

        await SendSpecialFlightNormalAttackAsync();
        await WaitForSpecialFlightDropAttackFinishedAsync(avatarName);
    }

    // 下落攻击前先松开移动/冲刺，避免特殊飞行或冲刺输入吞掉普攻。
    private async Task SendSpecialFlightNormalAttackAsync()
    {
        ReleaseSpecialFlightSprintMouse();
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
        await Delay(100, ct);
        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
    }

    private async Task WaitForSpecialFlightDropAttackFinishedAsync(string avatarName)
    {
        await Delay(300, ct);
        var start = DateTime.UtcNow;
        var hasSpaceKey = false;
        while ((DateTime.UtcNow - start).TotalMilliseconds < 1000)
        {
            using var screen = CaptureToRectArea();
            if (IsAnyFlightSpaceKey(screen))
            {
                hasSpaceKey = true;
                break;
            }

            await Delay(100, ct);
        }

        if (!hasSpaceKey)
        {
            return;
        }

        for (var i = 0; i < 50; i++)
        {
            using var screen = CaptureToRectArea();
            if (IsAnyFlightSpaceKey(screen))
            {
                await Delay(300, ct);
                continue;
            }

            return;
        }

        Logger.LogWarning("{AvatarName}落下攻击等待落地超时，继续后续指令", avatarName);
    }

    private static bool IsAnyFlightSpaceKey(ImageRegion screen)
    {
        return StopFlyingHandler.IsNormalFlightBySpaceKey(screen)
               || IsSpecialFlightFlyingBySpaceKey(screen);
    }

    private void StartSpecialFlightControlCommands(string? actionParams, string avatarName)
    {
        CancelSpecialFlightControlCommands();
        _specialFlightControlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = RunSpecialFlightControlCommandsAsync(
            actionParams,
            avatarName,
            _specialFlightControlCts.Token);
    }

    private void CancelSpecialFlightControlCommands()
    {
        _specialFlightControlCts?.Cancel();
        _specialFlightControlCts?.Dispose();
        _specialFlightControlCts = null;
    }

    private async Task RunSpecialFlightControlCommandsAsync(
        string? actionParams,
        string avatarName,
        CancellationToken controlToken)
    {
        if (string.IsNullOrWhiteSpace(actionParams)
            || !TryParseSpecialFlightCombatScriptSegment(actionParams, out var segmentAvatarName, out var commands)
            || !string.Equals(segmentAvatarName, avatarName, StringComparison.Ordinal))
        {
            return;
        }

        var pressedKeys = new HashSet<User32.VK>();
        try
        {
            foreach (var command in commands)
            {
                if (command.Method == Method.Wait)
                {
                    if (command.Args is { Count: > 0 }
                        && TryParseSpecialFlightDelayMs(command.Args[0], out var delayMs)
                        && delayMs > 0)
                    {
                        await Delay(delayMs, controlToken);
                    }

                    continue;
                }

                if (command.Method != Method.KeyDown && command.Method != Method.KeyUp)
                {
                    continue;
                }

                if (command.Args is not { Count: > 0 }
                    || !TryGetSpecialFlightVerticalControlKey(command.Args[0], out var key))
                {
                    continue;
                }

                if (command.Method == Method.KeyDown)
                {
                    Simulation.SendInput.Keyboard.KeyDown(key);
                    pressedKeys.Add(key);
                }
                else
                {
                    Simulation.SendInput.Keyboard.KeyUp(key);
                    pressedKeys.Remove(key);
                }
            }
        }
        catch (OperationCanceledException) when (controlToken.IsCancellationRequested)
        {
            // 飞行到点或状态结束时由状态机取消，finally 负责释放仍按住的控制键。
        }
        finally
        {
            foreach (var key in pressedKeys)
            {
                Simulation.SendInput.Keyboard.KeyUp(key);
            }
        }
    }

    private async Task RunForceTeleportCombatScriptAsync(WaypointForTrack waypoint, Waypoint? nextWaypoint)
    {
        if (string.IsNullOrWhiteSpace(waypoint.ActionParams))
        {
            return;
        }

        if (HasSpecialFlightAction(waypoint))
        {
            var specialFlightResult = await ExecuteSpecialFlightCombatScriptInOrderAsync(waypoint, nextWaypoint);
            if (specialFlightResult == SpecialFlightExecutionResult.Executed)
            {
                await Delay(PartyConfig.CombatScriptEndDelayMs > 0 ? PartyConfig.CombatScriptEndDelayMs : 1, ct);
            }

            return;
        }

        if (await RunCombatScriptFragmentAsync(waypoint, waypoint.ActionParams))
        {
            await Delay(PartyConfig.CombatScriptEndDelayMs > 0 ? PartyConfig.CombatScriptEndDelayMs : 1, ct);
        }
    }

    private static bool HasSpecialFlightVerticalControlCommands(string? actionParams)
    {
        return !string.IsNullOrWhiteSpace(actionParams)
               && TryParseSpecialFlightCombatScriptSegment(actionParams, out _, out var commands)
               && commands.Any(command =>
                   (command.Method == Method.KeyDown || command.Method == Method.KeyUp)
                   && command.Args is { Count: > 0 }
                   && TryGetSpecialFlightVerticalControlKey(command.Args[0], out _));
    }

    private static bool TryGetSpecialFlightVerticalControlKey(string value, out User32.VK key)
    {
        key = User32.VK.VK_NONAME;
        if (!Enum.TryParse(value.Trim(), true, out User32.VK parsedKey))
        {
            return false;
        }

        if (parsedKey is User32.VK.VK_SPACE
            or User32.VK.VK_CONTROL
            or User32.VK.VK_LCONTROL
            or User32.VK.VK_RCONTROL)
        {
            key = parsedKey;
            return true;
        }

        return false;
    }

    private WaypointForTrack? FindSpecialFlightPairedDropWaypoint(Waypoint waypoint, SpecialFlightAction action)
    {
        if (action is not (SpecialFlightAction.FastFly or SpecialFlightAction.SlowFly))
        {
            return null;
        }

        var avatarName = GetSpecialFlightAvatarName(waypoint);
        if (string.IsNullOrEmpty(avatarName))
        {
            return null;
        }

        var waypoints = CurWaypoints.Item2;
        var startIndex = CurWaypoint.Item1 + 1;
        for (var i = startIndex; i < waypoints.Count; i++)
        {
            var candidate = waypoints[i];
            if (!TryGetSpecialFlightActionForAvatar(candidate, avatarName, out var candidateAction, out _))
            {
                continue;
            }

            if (candidateAction == SpecialFlightAction.Drop)
            {
                return candidate;
            }

            if (IsSpecialFlightStartAction(candidateAction))
            {
                return null;
            }
        }

        return null;
    }

    private WaypointForTrack? FindSpecialFlightNextTargetWaypoint(Waypoint waypoint, SpecialFlightAction action, bool includeOrdinaryLanding = true)
    {
        if (action is not (SpecialFlightAction.FastFly or SpecialFlightAction.SlowFly or SpecialFlightAction.Via))
        {
            return null;
        }

        var avatarName = GetSpecialFlightAvatarName(waypoint);
        if (string.IsNullOrEmpty(avatarName))
        {
            return null;
        }

        var waypoints = CurWaypoints.Item2;
        var startIndex = CurWaypoint.Item1 + 1;
        WaypointForTrack? firstOrdinaryWaypoint = null;
        for (var i = startIndex; i < waypoints.Count; i++)
        {
            var candidate = waypoints[i];
            if (TryGetSpecialFlightActionForAvatar(candidate, avatarName, out var candidateAction, out _))
            {
                if (candidateAction is SpecialFlightAction.Via or SpecialFlightAction.Drop)
                {
                    return candidate;
                }

                if (IsSpecialFlightStartAction(candidateAction))
                {
                    return includeOrdinaryLanding ? firstOrdinaryWaypoint : null;
                }

                continue;
            }

            if (includeOrdinaryLanding)
            {
                firstOrdinaryWaypoint ??= candidate;
            }
        }

        return includeOrdinaryLanding ? firstOrdinaryWaypoint : null;
    }

    private static bool IsSpecialFlightStartAction(SpecialFlightAction action)
    {
        return action is SpecialFlightAction.FastFly or SpecialFlightAction.SlowFly;
    }

    private async Task<bool> SwitchToSpecialFlightAsync(string avatarName)
    {
        var avatar = _combatScenes?.SelectAvatar(avatarName);
        if (avatar is null)
        {
            return false;
        }

        return await SwitchAvatar(avatar.Index.ToString()) is not null;
    }

    private bool IsSpecialFlightAvatarAvailable(Waypoint waypoint)
    {
        var avatarName = GetSpecialFlightAvatarName(waypoint) ?? DefaultSpecialFlightAvatarName;
        return IsSpecialFlightAvatarAvailable(avatarName);
    }

    private bool IsSpecialFlightAvatarAvailable(string avatarName)
    {
        return SpecialFlightAvatarNames.Contains(avatarName, StringComparer.Ordinal)
               && _combatScenes?.SelectAvatar(avatarName) is not null;
    }

    private bool HasSpecialFlightAvatarInParty()
    {
        return SpecialFlightAvatarNames.Any(avatarName => _combatScenes?.SelectAvatar(avatarName) is not null);
    }

    private bool TryGetAvailableSpecialFlightAction(Waypoint waypoint, out SpecialFlightAction action)
    {
        action = SpecialFlightAction.None;
        var actionParams = waypoint.ActionParams;
        if (string.IsNullOrEmpty(actionParams))
        {
            return false;
        }

        var start = 0;
        while (start < actionParams.Length)
        {
            while (start < actionParams.Length && IsSpecialFlightCommandSeparator(actionParams[start]))
            {
                start++;
            }

            if (start >= actionParams.Length)
            {
                break;
            }

            var end = start;
            while (end < actionParams.Length && !IsSpecialFlightCommandSeparator(actionParams[end]))
            {
                end++;
            }

            var candidateAction = GetSpecialFlightScriptAction(actionParams[start..end], out var avatarName);
            if (candidateAction != SpecialFlightAction.None
                && !string.IsNullOrEmpty(avatarName)
                && IsSpecialFlightAvatarAvailable(avatarName))
            {
                action = candidateAction;
                return true;
            }

            start = end;
        }

        return false;
    }

    private async Task<bool> HandleSpecialFlightStateAsync(ImageRegion screen, WaypointForTrack waypoint, double distance)
    {
        if (!_specialFlightMonitorEnabled)
        {
            // 接续飞行是非阻塞的：普通路线继续走，CD 好且距离仍足够时才重新开 E。
            return await TryStartPendingSpecialFlightContinuationAsync(screen, waypoint, distance);
        }

        if (distance <= SpecialFlightLostMinDistance)
        {
            await TryDropSpecialFlightBeforeWaypointAsync(waypoint, distance, screen);
            return false;
        }

        if (IsSpecialFlightFlyingBySpaceKey(screen))
        {
            _specialFlightWasDetected = true;
            if (await TryDropSpecialFlightBeforeWaypointAsync(waypoint, distance, screen))
            {
                return false;
            }

            return true;
        }

        if (!_specialFlightWasDetected
            || (DateTime.UtcNow - _lastSpecialFlightLostHandleTime).TotalMilliseconds < 1500)
        {
            return false;
        }

        _lastSpecialFlightLostHandleTime = DateTime.UtcNow;
        _specialFlightWasDetected = false;
        StartSpecialFlightLandingStuckWatch();

        // 起飞点写了 e,dash,attack 时，特殊飞行消失后优先消费这个 attack，再考虑落下点兜底。
        if (_specialFlightAttackAfterFinished
            && await RunSpecialFlightAttackAfterFinishedIfNeededAsync(GetSpecialFlightLogAvatarName(waypoint)))
        {
            PreparePendingSpecialFlightContinuation(waypoint);
            StopSpecialFlightMonitor();
            ReleaseSpecialFlightSprintMouse();
            await RunDeferredSpecialFlightCombatScriptAsync();
            return false;
        }

        if (GetCurrentSpecialFlightAction(waypoint) == SpecialFlightAction.Drop)
        {
            if (!TryGetCurrentSpecialFlightDropNormalAttackDelayMs(waypoint, out _))
            {
                PreparePendingSpecialFlightContinuation(waypoint);
                StopSpecialFlightMonitor();
                ReleaseSpecialFlightSprintMouse();
                await RunDeferredSpecialFlightCombatScriptAsync();
                await ResumeMoveModeAfterSpecialFlightLostAsync(waypoint);
                return false;
            }

            TryStartSpecialFlightDropFallback(waypoint);
            return false;
        }

        if (_specialFlightSkipUntilWaypoint is not null
            && GetCurrentSpecialFlightAction(_specialFlightSkipUntilWaypoint) == SpecialFlightAction.Drop
            && TryGetCurrentSpecialFlightDropNormalAttackDelayMs(_specialFlightSkipUntilWaypoint, out _))
        {
            // 没有起飞点结束 attack 时，配对落下点的 attack 走普通飞行兜底到点后处理。
            TryStartSpecialFlightDropFallback(_specialFlightSkipUntilWaypoint);
            return false;
        }

        if (waypoint.MoveMode == MoveModeEnum.Fly.Code)
        {
            // 普通飞行点仍由原路线接管；如距离后续特殊点还远，同时登记一次接续飞行。
            PreparePendingSpecialFlightContinuation(waypoint);
            Logger.LogInformation("{AvatarName}飞行状态消失，未到飞行节点，切回普通飞行", GetSpecialFlightLogAvatarName(waypoint));
            StopSpecialFlightMonitor();
            ReleaseSpecialFlightSprintMouse();
            if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
            {
                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
            }

            return false;
        }

        if (IsSpecialFlightFreeFallMoveMode(waypoint.MoveMode))
        {
            Logger.LogInformation("{AvatarName}飞行状态消失，未到{MoveMode}节点，继续普通路线",
                GetSpecialFlightLogAvatarName(waypoint),
                MoveModeEnum.GetMsgByCode(waypoint.MoveMode));
            PreparePendingSpecialFlightContinuation(waypoint);
            StopSpecialFlightMonitor();
            ReleaseSpecialFlightSprintMouse();
            await RunDeferredSpecialFlightCombatScriptAsync();
            await ResumeMoveModeAfterSpecialFlightLostAsync(waypoint);
        }

        return false;
    }

    private void StartSpecialFlightLandingStuckWatch()
    {
        var now = DateTime.UtcNow;
        _specialFlightLandingStuckWatchReadyAt = now.AddMilliseconds(SpecialFlightLandingStuckArmDelayMs);
        _specialFlightLandingStuckWatchUntil = _specialFlightLandingStuckWatchReadyAt.AddMilliseconds(SpecialFlightLandingStuckWatchMs);
        _specialFlightLandingStuckStillSince = DateTime.MinValue;
        _specialFlightLandingStuckLastPosition = null;
    }

    private void ClearSpecialFlightLandingStuckWatch()
    {
        _specialFlightLandingStuckWatchReadyAt = DateTime.MinValue;
        _specialFlightLandingStuckWatchUntil = DateTime.MinValue;
        _specialFlightLandingStuckStillSince = DateTime.MinValue;
        _specialFlightLandingStuckLastPosition = null;
    }

    private async Task TryRecoverSpecialFlightLandingStuckAsync(ImageRegion screen, Point2f position, double distance, bool isSpecialFlightFlying)
    {
        var now = DateTime.UtcNow;
        if (isSpecialFlightFlying || _specialFlightMonitorEnabled)
        {
            return;
        }

        if (now > _specialFlightLandingStuckWatchUntil)
        {
            ClearSpecialFlightLandingStuckWatch();
            return;
        }

        if (now < _specialFlightLandingStuckWatchReadyAt || Bv.GetMotionStatus(screen) != MotionStatus.Normal)
        {
            _specialFlightLandingStuckStillSince = DateTime.MinValue;
            _specialFlightLandingStuckLastPosition = null;
            return;
        }

        if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
        {
            _specialFlightLandingStuckStillSince = DateTime.MinValue;
            _specialFlightLandingStuckLastPosition = null;
            return;
        }

        if (position is { X: 0, Y: 0 })
        {
            return;
        }

        if (_specialFlightLandingStuckLastPosition is not { } lastPosition)
        {
            _specialFlightLandingStuckLastPosition = position;
            _specialFlightLandingStuckStillSince = now;
            return;
        }

        var moveDistance = Math.Sqrt(Math.Pow(position.X - lastPosition.X, 2) + Math.Pow(position.Y - lastPosition.Y, 2));
        if (moveDistance > SpecialFlightLandingStuckMoveDistance)
        {
            _specialFlightLandingStuckLastPosition = position;
            _specialFlightLandingStuckStillSince = now;
            return;
        }

        if (_specialFlightLandingStuckStillSince == DateTime.MinValue)
        {
            _specialFlightLandingStuckStillSince = now;
            return;
        }

        if ((now - _specialFlightLandingStuckStillSince).TotalMilliseconds < SpecialFlightLandingStuckStillMs
            || (now - _lastSpecialFlightLandingStuckJumpTime).TotalMilliseconds < SpecialFlightLandingStuckJumpCooldownMs)
        {
            return;
        }

        Logger.LogInformation("特殊飞行落地后按前进但坐标{StillMs}ms未移动，跳跃解除卡死", SpecialFlightLandingStuckStillMs);
        Simulation.SendInput.SimulateAction(GIActions.Jump);
        _lastSpecialFlightLandingStuckJumpTime = now;
        ClearSpecialFlightLandingStuckWatch();
        await Delay(250, ct);
    }

    private void SetSpecialFlightAttackAfterFinished(Waypoint waypoint)
    {
        _specialFlightAttackAfterFinished = TryGetCurrentSpecialFlightScriptAttackDelayMs(waypoint, out _specialFlightAttackAfterFinishedDelayMs);
    }

    private async Task<bool> RunSpecialFlightAttackAfterFinishedIfNeededAsync(string avatarName)
    {
        if (!_specialFlightAttackAfterFinished)
        {
            return false;
        }

        _specialFlightAttackAfterFinished = false;
        var delayMs = _specialFlightAttackAfterFinishedDelayMs;
        _specialFlightAttackAfterFinishedDelayMs = 0;
        if (delayMs > 0)
        {
            Logger.LogInformation("{AvatarName}特殊飞行结束，等待{DelayMs}ms下落攻击", avatarName, delayMs);
            await Delay(delayMs, ct);
        }
        else
        {
            Logger.LogInformation("{AvatarName}特殊飞行结束，下落攻击", avatarName);
        }

        await SendSpecialFlightNormalAttackAsync();
        await WaitForSpecialFlightDropAttackFinishedAsync(avatarName);
        return true;
    }

    private async Task ResumeMoveModeAfterSpecialFlightLostAsync(Waypoint waypoint)
    {
        EnsureMoveForwardDown();

        if (waypoint.MoveMode == MoveModeEnum.Run.Code)
        {
            Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
            await Delay(350, ct);
            EnsureMoveForwardDown();
            Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
        }
        else if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
        {
            await Delay(250, ct);
            EnsureMoveForwardDown();
            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
            await Delay(500, ct);
            EnsureMoveForwardDown();
            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
        }
    }

    // 只登记接续飞行，不等待 CD；这样特殊飞行结束后不会原地停住。
    private void PreparePendingSpecialFlightContinuation(WaypointForTrack waypoint, bool continueAfterCurrentWaypoint = false)
    {
        if (_specialFlightAction is not (SpecialFlightAction.FastFly or SpecialFlightAction.SlowFly)
            || string.IsNullOrEmpty(_specialFlightAvatarName))
        {
            return;
        }

        var targetWaypoint = GetSpecialFlightContinuationTargetWaypoint(waypoint, continueAfterCurrentWaypoint);
        if (targetWaypoint is null)
        {
            ClearPendingSpecialFlightContinuation();
            return;
        }

        var avatar = _combatScenes?.SelectAvatar(_specialFlightAvatarName);
        if (avatar is null)
        {
            return;
        }

        avatar.LastSkillTime = _lastSpecialFlightLostHandleTime;
        var action = _specialFlightAction;

        _specialFlightPendingContinuationTargetWaypoint = targetWaypoint;
        _specialFlightPendingContinuationAction = action;
        _specialFlightPendingContinuationAvatarName = _specialFlightAvatarName;
        _specialFlightPendingContinuationCdRefreshed = false;
        Logger.LogInformation("{AvatarName}飞行状态消失，继续普通路线并等待CD完成后判断是否接续{ActionName}",
            _specialFlightAvatarName,
            GetSpecialFlightActionName(action));
    }

    // 普通移动过程中轮询待续飞状态：已落地、CD 好、距离仍足够，才真正重新开 E。
    private async Task<bool> TryStartPendingSpecialFlightContinuationAsync(ImageRegion screen, WaypointForTrack waypoint, double distance)
    {
        if (_specialFlightPendingContinuationTargetWaypoint is null
            || _specialFlightPendingContinuationAction is not (SpecialFlightAction.FastFly or SpecialFlightAction.SlowFly)
            || string.IsNullOrEmpty(_specialFlightPendingContinuationAvatarName))
        {
            return false;
        }

        var targetIndex = CurWaypoints.Item2.FindIndex(candidate =>
            ReferenceEquals(candidate, _specialFlightPendingContinuationTargetWaypoint));
        while (targetIndex >= 0
               && CurWaypoint.Item1 > targetIndex
               && TryGetSpecialFlightActionForAvatar(
                   _specialFlightPendingContinuationTargetWaypoint,
                   _specialFlightPendingContinuationAvatarName,
                   out var passedTargetAction,
                   out _)
               && passedTargetAction == SpecialFlightAction.Via)
        {
            _specialFlightPendingContinuationTargetWaypoint =
                GetNextSpecialFlightContinuationWaypoint(targetIndex, _specialFlightPendingContinuationAvatarName);
            if (_specialFlightPendingContinuationTargetWaypoint is null)
            {
                break;
            }

            targetIndex = CurWaypoints.Item2.FindIndex(candidate =>
                ReferenceEquals(candidate, _specialFlightPendingContinuationTargetWaypoint));
        }

        if (targetIndex < 0 || CurWaypoint.Item1 > targetIndex)
        {
            Logger.LogInformation("{AvatarName}已越过接续飞行目标，取消接续",
                _specialFlightPendingContinuationAvatarName);
            ClearPendingSpecialFlightContinuation();
            return false;
        }

        var continuationTargetWaypoint = _specialFlightPendingContinuationTargetWaypoint!;
        if (ReferenceEquals(waypoint, continuationTargetWaypoint))
        {
            var targetRemainingDistance = GetSpecialFlightContinuationRemainingDistance(
                distance,
                continuationTargetWaypoint,
                _specialFlightPendingContinuationAvatarName);
            if (targetRemainingDistance < SpecialFlightContinueMinTargetDistance)
            {
                Logger.LogInformation("{AvatarName}已接近接续飞行目标，后续剩余距离{Distance:F1}，取消接续",
                    _specialFlightPendingContinuationAvatarName,
                    targetRemainingDistance);
                ClearPendingSpecialFlightContinuation();
                return false;
            }
        }

        if ((DateTime.UtcNow - _lastSpecialFlightLostHandleTime).TotalMilliseconds < SpecialFlightContinuationFreeFallWaitMs
            || Bv.GetMotionStatus(screen) == MotionStatus.Fly
            || IsAnyFlightSpaceKey(screen))
        {
            return false;
        }

        var avatar = _combatScenes?.SelectAvatar(_specialFlightPendingContinuationAvatarName);
        if (avatar is null)
        {
            ClearPendingSpecialFlightContinuation();
            return false;
        }

        if (!_specialFlightPendingContinuationCdRefreshed)
        {
            RefreshSpecialFlightSkillCdAfterLanding(avatar, screen);
            _specialFlightPendingContinuationCdRefreshed = true;
        }

        if (!avatar.IsSkillReady())
        {
            return false;
        }

        var currentPosition = await GetPosition(screen, waypoint);
        var remainingDistance = GetSpecialFlightContinuationRemainingDistance(
            Navigation.GetDistance(continuationTargetWaypoint, currentPosition),
            continuationTargetWaypoint,
            _specialFlightPendingContinuationAvatarName);

        if (remainingDistance < SpecialFlightContinueMinTargetDistance)
        {
            Logger.LogInformation("{AvatarName}CD完成时经下一个特殊点到落下点的剩余距离{Distance:F1}，不再接续飞行",
                _specialFlightPendingContinuationAvatarName,
                remainingDistance);
            ClearPendingSpecialFlightContinuation();
            return false;
        }

        var action = _specialFlightPendingContinuationAction;
        var avatarName = _specialFlightPendingContinuationAvatarName;
        Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);
        await PreRotateForSpecialFlightAtTargetAsync(waypoint, continuationTargetWaypoint);
        if (!await TryStartSpecialFlightAsync(action, avatarName))
        {
            ContinueAfterSpecialFlightStartFailed(avatarName, action);
            ClearPendingSpecialFlightContinuation();
            return false;
        }

        _specialFlightAction = action;
        _specialFlightAvatarName = avatarName;
        _specialFlightSkipUntilWaypoint = continuationTargetWaypoint;
        ClearPendingSpecialFlightContinuation();
        Logger.LogInformation("{AvatarName}接续{ActionName}成功", avatarName, GetSpecialFlightActionName(action));
        return true;
    }

    private Waypoint? GetNextSpecialFlightContinuationWaypoint(int currentIndex, string avatarName)
    {
        var waypoints = CurWaypoints.Item2;
        for (var i = currentIndex + 1; i < waypoints.Count; i++)
        {
            if (!TryGetSpecialFlightActionForAvatar(waypoints[i], avatarName, out var candidateAction, out _))
            {
                continue;
            }

            if (IsSpecialFlightStartAction(candidateAction))
            {
                break;
            }

            if (candidateAction is SpecialFlightAction.Via or SpecialFlightAction.Drop)
            {
                return waypoints[i];
            }
        }

        return currentIndex + 1 < waypoints.Count ? waypoints[currentIndex + 1] : null;
    }

    private double GetSpecialFlightContinuationRemainingDistance(double distanceToTarget, Waypoint targetWaypoint, string avatarName)
    {
        var remainingDistance = distanceToTarget;
        var waypoints = CurWaypoints.Item2;
        var currentIndex = waypoints.FindIndex(candidate => ReferenceEquals(candidate, targetWaypoint));
        var currentTarget = targetWaypoint;

        while (currentIndex >= 0
               && TryGetSpecialFlightActionForAvatar(currentTarget, avatarName, out var currentAction, out _)
               && currentAction == SpecialFlightAction.Via)
        {
            var nextSpecialIndex = -1;
            for (var i = currentIndex + 1; i < waypoints.Count; i++)
            {
                if (!TryGetSpecialFlightActionForAvatar(waypoints[i], avatarName, out var candidateAction, out _))
                {
                    continue;
                }

                if (IsSpecialFlightStartAction(candidateAction))
                {
                    break;
                }

                if (candidateAction is SpecialFlightAction.Via or SpecialFlightAction.Drop)
                {
                    nextSpecialIndex = i;
                    break;
                }
            }

            if (nextSpecialIndex < 0)
            {
                if (currentIndex + 1 < waypoints.Count)
                {
                    var dropWaypoint = waypoints[currentIndex + 1];
                    remainingDistance += Navigation.GetDistance(currentTarget, new Point2f((float)dropWaypoint.X, (float)dropWaypoint.Y));
                }

                break;
            }

            var nextTarget = waypoints[nextSpecialIndex];
            remainingDistance += Navigation.GetDistance(currentTarget, new Point2f((float)nextTarget.X, (float)nextTarget.Y));
            currentTarget = nextTarget;
            currentIndex = nextSpecialIndex;
        }

        return remainingDistance;
    }

    private void ClearPendingSpecialFlightContinuation()
    {
        _specialFlightPendingContinuationTargetWaypoint = null;
        _specialFlightPendingContinuationAction = SpecialFlightAction.None;
        _specialFlightPendingContinuationAvatarName = null;
        _specialFlightPendingContinuationCdRefreshed = false;
    }

    // 接续飞行只在当前特殊飞行段内找特殊目标或最终普通落点；遇到同角色新起飞点时停止，不跨段跳点。
    private Waypoint? GetSpecialFlightContinuationTargetWaypoint(WaypointForTrack waypoint, bool continueAfterCurrentWaypoint = false)
    {
        var action = GetCurrentSpecialFlightAction(waypoint);
        if (!continueAfterCurrentWaypoint
            && (action is SpecialFlightAction.Via or SpecialFlightAction.Drop)
            && IsCurrentSpecialFlightAvatarWaypoint(waypoint))
        {
            return waypoint;
        }

        if (_specialFlightSkipUntilWaypoint is not null
            && TryGetSpecialFlightActionForAvatar(_specialFlightSkipUntilWaypoint, _specialFlightAvatarName ?? string.Empty, out action, out _)
            && action is SpecialFlightAction.Via or SpecialFlightAction.Drop)
        {
            return _specialFlightSkipUntilWaypoint;
        }

        return FindSpecialFlightNextTargetWaypoint(waypoint, _specialFlightAction, false);
    }

    // 特殊飞行角色的 E 技能 CD 从飞行状态消失后开始算；落地后再 OCR 刷新，识别不到就保留本地兜底时间。
    // 这里临时恢复 LastSkillTime，是为了只读取 UI 上的 CD，不让 OCR 刷新动作改变本地技能计时起点。
    private void RefreshSpecialFlightSkillCdAfterLanding(Avatar avatar, ImageRegion screen)
    {
        var lastSkillTime = avatar.LastSkillTime;
        var cd = avatar.AfterUseSkill(screen);
        avatar.LastSkillTime = lastSkillTime;
        if (cd > 0)
        {
            Logger.LogInformation("{AvatarName}落地后刷新E技能CD：{Cd}秒", avatar.Name, Math.Round(cd, 2));
        }
    }

    // 确保普通移动/兜底飞行继续向前。
    // 特殊飞行期间可能释放过前进键或改用鼠标按压，切回普通飞行前需要恢复前进输入。
    private static void EnsureMoveForwardDown()
    {
        if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
        }
    }

    // 特殊飞行状态已经消失，但路线还没到 drop 点时的兜底。
    // 如果 drop 点配置了 attack 延迟，就切回普通飞行并继续跳过中间点，到 drop 点后再执行下落攻击；
    // 如果没有 attack 参数，就认为无法可靠补落下动作，直接停止特殊飞行监控。
    private bool TryStartSpecialFlightDropFallback(Waypoint waypoint)
    {
        var avatarName = GetSpecialFlightLogAvatarName(waypoint);
        StopSpecialFlightMonitor(false, false);
        ReleaseSpecialFlightSprintMouse();
        if (!TryGetCurrentSpecialFlightDropNormalAttackDelayMs(waypoint, out _specialFlightDropFallbackNormalAttackDelayMs))
        {
            Logger.LogInformation("{AvatarName}飞行状态消失，落下点未配置下落攻击参数，跳过普通飞行兜底", avatarName);
            StopSpecialFlightMonitor();
            return false;
        }

        Logger.LogInformation("{AvatarName}飞行状态消失，未到落下点，切回普通飞行并跳过中间点", avatarName);
        if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
        }

        _specialFlightSkipUntilWaypoint = waypoint;
        _specialFlightDropFallbackNormalAttackWaypoint = waypoint;
        _specialFlightDropFallbackShouldNormalAttack = true;
        waypoint.MoveMode = MoveModeEnum.Fly.Code;
        return true;
    }

    // 到达目标点前的提前落点处理。
    // drop 点：提前取消特殊飞行，标记该点已处理，避免 AfterMoveToTarget 到点后重复执行。
    // 普通飞行/普通移动点：如果还处于特殊飞行，提前切回普通状态，保证后续路线能按普通移动接上。
    private async Task<bool> TryDropSpecialFlightBeforeWaypointAsync(WaypointForTrack waypoint, double distance, ImageRegion? screen = null)
    {
        if (distance > GetSpecialFlightDropArrivalBufferDistance(waypoint))
        {
            return false;
        }

        var action = GetCurrentSpecialFlightAction(waypoint);
        if (action == SpecialFlightAction.Drop)
        {
            if (!IsSpecialFlightFlyingNow(screen))
            {
                return false;
            }

            Logger.LogInformation("{AvatarName}飞行接近落下点，提前取消飞行", GetSpecialFlightLogAvatarName(waypoint));
            await CancelSpecialFlightAtDropAsync(waypoint);
            _specialFlightDropHandledWaypoint = waypoint;
            return true;
        }

        if (action == SpecialFlightAction.Via || !IsSpecialFlightFreeFallMoveMode(waypoint.MoveMode))
        {
            if (action != SpecialFlightAction.Via
                && waypoint.MoveMode == MoveModeEnum.Fly.Code
                && IsSpecialFlightFlyingNow(screen))
            {
                Logger.LogInformation("{AvatarName}飞行接近普通飞行点，提前取消特殊飞行", GetSpecialFlightLogAvatarName(waypoint));
                StopSpecialFlightMonitor();
                ReleaseSpecialFlightSprintMouse();
                if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
                {
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                }

                await TapSpecialFlightElementalSkillAsync();
                StartSpecialFlightLandingStuckWatch();
                return true;
            }

            return false;
        }

        if (!IsSpecialFlightFlyingNow(screen))
        {
            return false;
        }

        var avatarName = GetSpecialFlightLogAvatarName(waypoint);
        Logger.LogInformation("{AvatarName}飞行接近普通点，提前落点", avatarName);
        StopSpecialFlightMonitor(false);
        ReleaseSpecialFlightSprintMouse();
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
        await TapSpecialFlightElementalSkillAsync();
        await RunSpecialFlightAttackAfterFinishedIfNeededAsync(avatarName);
        StopSpecialFlightMonitor();
        await RunDeferredSpecialFlightCombatScriptAsync();
        StartSpecialFlightLandingStuckWatch();
        return true;
    }

    // 活跃特殊飞行段接近 drop 点时使用的提前到达距离。
    // 当前动作只可能是 FastFly/SlowFly；其他动作进入这里时按慢飞的保守距离处理。
    private double GetSpecialFlightDropArrivalBufferDistance()
    {
        return _specialFlightAction == SpecialFlightAction.FastFly
            ? SpecialFlightFastFlyDropArrivalBufferDistance
            : SpecialFlightSlowFlyDropArrivalBufferDistance;
    }

    // 当前 waypoint 是普通 Fly 模式时，表示特殊飞行要切回普通飞行，使用独立阈值；
    // 否则按当前快飞/慢飞段的 drop 缓冲距离。
    private double GetSpecialFlightDropArrivalBufferDistance(Waypoint waypoint)
    {
        return waypoint.MoveMode == MoveModeEnum.Fly.Code
            ? SpecialFlightNormalFlyTransitionDistance
            : GetSpecialFlightDropArrivalBufferDistance();
    }

    // 通过右下角特殊飞行 Space 图标判断是否仍处于特殊飞行。
    // 调用方已有截图时传入 screen，避免同一轮循环内重复截图。
    private bool IsSpecialFlightFlyingNow(ImageRegion? screen = null)
    {
        if (screen is not null)
        {
            return IsSpecialFlightFlyingBySpaceKey(screen);
        }

        using var currentScreen = CaptureToRectArea();
        return IsSpecialFlightFlyingBySpaceKey(currentScreen);
    }

    // 起飞后短时间轮询特殊飞行 UI。
    // 成功识别到 Space 图标才认为起飞成功；否则释放按键并让主流程按普通路线继续。
    private async Task<bool> WaitForSpecialFlightFlyingAsync()
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < 1500)
        {
            using var screen = CaptureToRectArea();
            if (IsSpecialFlightFlyingBySpaceKey(screen))
            {
                _specialFlightWasDetected = true;
                return true;
            }

            await Delay(100, ct);
        }

        return false;
    }

    private async Task<bool> TryStartSpecialFlightAsync(SpecialFlightAction action, string avatarName)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (action == SpecialFlightAction.FastFly)
            {
                await StartSpecialFlightFastFlyAsync();
            }
            else
            {
                StartSpecialFlightSlowFly();
            }

            if (await WaitForSpecialFlightFlyingAsync())
            {
                return true;
            }

            Logger.LogInformation("{AvatarName}{Action}状态未启动，第{Attempt}次尝试失败",
                avatarName,
                action == SpecialFlightAction.FastFly ? "快飞" : "慢飞",
                attempt);
            StopSpecialFlightMonitor();
            ReleaseSpecialFlightSprintMouse();
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);

            if (attempt < 2)
            {
                await Delay(300, ct);
            }
        }

        return false;
    }
    private async Task TapSpecialFlightElementalSkillAsync()
    {
        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyDown);
        await Delay(100, ct);
        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
        await Delay(200, ct);
    }

    private async Task StartSpecialFlightFastFlyAsync()
    {
        ReleaseSpecialFlightSprintMouse();
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
        await Delay(500, ct);
        Simulation.SendInput.Mouse.RightButtonDown();
        _specialFlightSprintMouseDown = true;
        StartSpecialFlightMonitor();
    }

    private void StartSpecialFlightSlowFly()
    {
        ReleaseSpecialFlightSprintMouse();
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
        StartSpecialFlightMonitor();
    }

    private void StartSpecialFlightMonitor()
    {
        _specialFlightMonitorEnabled = true;
        _specialFlightWasDetected = false;
        _lastSpecialFlightLostHandleTime = DateTime.UtcNow;
    }

    private void StopSpecialFlightMonitor(bool clearFlightAction = true, bool clearSkipWaypoint = true)
    {
        CancelSpecialFlightControlCommands();
        _specialFlightMonitorEnabled = false;
        _specialFlightWasDetected = false;
        if (clearSkipWaypoint)
        {
            _specialFlightSkipUntilWaypoint = null;
        }

        if (clearFlightAction)
        {
            _specialFlightAction = SpecialFlightAction.None;
            _specialFlightAvatarName = null;
            _specialFlightAttackAfterFinished = false;
            _specialFlightAttackAfterFinishedDelayMs = 0;
        }
    }

    private static bool IsSpecialFlightFlyingBySpaceKey(ImageRegion screen)
    {
        var spaceKey = ElementAssets.Instance.SpaceKey.Clone();
        var widthScale = screen.Width / 1920.0;
        var heightScale = screen.Height / 1080.0;
        spaceKey.Name = "SpecialFlightSpaceKey";
        spaceKey.RegionOfInterest = new Rect(
            Math.Max(0, screen.Width - (int)Math.Round(450 * widthScale)),
            Math.Max(0, screen.Height - (int)Math.Round(140 * heightScale)),
            Math.Min(screen.Width, (int)Math.Round(210 * widthScale)),
            Math.Min(screen.Height, (int)Math.Round(140 * heightScale)));

        using var spaceRa = screen.Find(spaceKey);
        return spaceRa.IsExist();
    }

    private bool ShouldSkipAvatarSwitchForSpecialFlightState()
    {
        return _specialFlightMonitorEnabled
               || _specialFlightAction != SpecialFlightAction.None
               || _specialFlightDropFallbackNormalAttackWaypoint is not null
               || _specialFlightPendingContinuationTargetWaypoint is not null;
    }

    private async Task<bool> ShouldRunSpecialFlightViaWaypointAsync(WaypointForTrack waypoint)
    {
        if (GetCurrentSpecialFlightAction(waypoint) != SpecialFlightAction.Via
            || !IsCurrentSpecialFlightAvatarWaypoint(waypoint))
        {
            return false;
        }

        if (IsSpecialFlightFlyingNow())
        {
            return true;
        }

        var avatarName = GetSpecialFlightLogAvatarName(waypoint);
        Logger.LogInformation("{AvatarName}未处于飞行状态，取消途径跳点并继续普通路线", avatarName);
        _lastSpecialFlightLostHandleTime = DateTime.UtcNow;
        await RunSpecialFlightAttackAfterFinishedIfNeededAsync(avatarName);
        PreparePendingSpecialFlightContinuation(waypoint, true);
        _specialFlightSkipUntilWaypoint = null;
        StopSpecialFlightMonitor();
        ReleaseSpecialFlightSprintMouse();
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
        await RunDeferredSpecialFlightCombatScriptAsync();
        return true;
    }

    private async Task<bool> ShouldContinueSpecialFlightSkipToTargetAsync()
    {
        if (!_specialFlightMonitorEnabled)
        {
            return false;
        }

        if (IsSpecialFlightFlyingNow())
        {
            _specialFlightWasDetected = true;
            return true;
        }

        if (!_specialFlightWasDetected
            || (DateTime.UtcNow - _lastSpecialFlightLostHandleTime).TotalMilliseconds < 1500)
        {
            return true;
        }

        _lastSpecialFlightLostHandleTime = DateTime.UtcNow;
        var waypoint = CurWaypoint.Item2;
        var avatarName = GetSpecialFlightLogAvatarName(waypoint);
        await RunSpecialFlightAttackAfterFinishedIfNeededAsync(avatarName);
        PreparePendingSpecialFlightContinuation(waypoint);
        Logger.LogInformation("{AvatarName}飞行状态消失，登记接续飞行并取消跳过中间点", avatarName);
        StopSpecialFlightMonitor();
        ReleaseSpecialFlightSprintMouse();
        await RunDeferredSpecialFlightCombatScriptAsync();
        return false;
    }

    private bool ShouldSkipWaypointBeforeSpecialFlightDropFallback(Waypoint waypoint)
    {
        return _specialFlightDropFallbackNormalAttackWaypoint is not null
               && !ReferenceEquals(waypoint, _specialFlightDropFallbackNormalAttackWaypoint);
    }

    private bool ShouldKeepMoveForwardForSpecialFlightSlowVia(Waypoint? waypoint)
    {
        return waypoint is not null
               && _specialFlightAction is SpecialFlightAction.FastFly or SpecialFlightAction.SlowFly
               && _specialFlightMonitorEnabled
               && GetCurrentSpecialFlightAction(waypoint) == SpecialFlightAction.Via
               && IsCurrentSpecialFlightAvatarWaypoint(waypoint)
               && IsSpecialFlightFlyingNow();
    }

    private bool ShouldPassSpecialFlightViaEarly(Waypoint waypoint)
    {
        return _specialFlightAction is SpecialFlightAction.FastFly or SpecialFlightAction.SlowFly
               && _specialFlightMonitorEnabled
               && GetCurrentSpecialFlightAction(waypoint) == SpecialFlightAction.Via
               && IsCurrentSpecialFlightAvatarWaypoint(waypoint);
    }

    private double GetSpecialFlightViaArrivalDistance()
    {
        return _specialFlightAction == SpecialFlightAction.FastFly
            ? SpecialFlightFastFlyViaArrivalDistance
            : SpecialFlightSlowFlyViaArrivalDistance;
    }

    private bool IsCurrentSpecialFlightAvatarWaypoint(Waypoint waypoint)
    {
        return string.IsNullOrEmpty(_specialFlightAvatarName)
               || TryGetSpecialFlightActionForAvatar(waypoint, _specialFlightAvatarName, out _, out _);
    }

    private string GetSpecialFlightLogAvatarName(Waypoint waypoint)
    {
        return _specialFlightAvatarName
               ?? GetSpecialFlightAvatarName(waypoint)
               ?? DefaultSpecialFlightAvatarName;
    }

    private SpecialFlightAction GetCurrentSpecialFlightAction(Waypoint waypoint)
    {
        if (!string.IsNullOrEmpty(_specialFlightAvatarName))
        {
            return TryGetSpecialFlightActionForAvatar(waypoint, _specialFlightAvatarName, out var action, out _)
                ? action
                : SpecialFlightAction.None;
        }

        return GetSpecialFlightAction(waypoint);
    }

    private bool TryGetCurrentSpecialFlightDropNormalAttackDelayMs(Waypoint waypoint, out int delayMs)
    {
        delayMs = 0;
        if (!string.IsNullOrEmpty(_specialFlightAvatarName))
        {
            return TryGetSpecialFlightActionForAvatar(waypoint, _specialFlightAvatarName, out var action, out var actionParams)
                   && action == SpecialFlightAction.Drop
                   && TryGetSpecialFlightScriptAttackDelayMs(actionParams, out delayMs);
        }

        return TryGetSpecialFlightDropNormalAttackDelayMs(waypoint, out delayMs);
    }

    private bool TryGetCurrentSpecialFlightScriptAttackDelayMs(Waypoint waypoint, out int delayMs)
    {
        delayMs = 0;
        if (!string.IsNullOrEmpty(_specialFlightAvatarName))
        {
            return TryGetSpecialFlightActionForAvatar(waypoint, _specialFlightAvatarName, out _, out var actionParams)
                   && TryGetSpecialFlightScriptAttackDelayMs(actionParams, out delayMs);
        }

        return !string.IsNullOrEmpty(waypoint.ActionParams)
               && TryGetSpecialFlightScriptAttackDelayMs(waypoint.ActionParams, out delayMs);
    }

    private static bool TryGetSpecialFlightDropNormalAttackDelayMs(Waypoint waypoint, out int delayMs)
    {
        delayMs = 0;
        var action = GetSpecialFlightAction(waypoint);
        if (action != SpecialFlightAction.Drop)
        {
            return false;
        }

        var actionParams = SplitSpecialFlightCombatScript(waypoint).special;
        if (string.IsNullOrWhiteSpace(actionParams))
        {
            return false;
        }

        return TryGetSpecialFlightScriptAttackDelayMs(actionParams, out delayMs);
    }

    private static bool TryGetSpecialFlightActionForAvatar(Waypoint waypoint, string avatarName, out SpecialFlightAction action, out string actionParams)
    {
        action = SpecialFlightAction.None;
        actionParams = string.Empty;
        if (string.IsNullOrEmpty(avatarName) || string.IsNullOrEmpty(waypoint.ActionParams))
        {
            return false;
        }

        var start = 0;
        while (start < waypoint.ActionParams.Length)
        {
            while (start < waypoint.ActionParams.Length && IsSpecialFlightCommandSeparator(waypoint.ActionParams[start]))
            {
                start++;
            }

            if (start >= waypoint.ActionParams.Length)
            {
                break;
            }

            var end = start;
            while (end < waypoint.ActionParams.Length && !IsSpecialFlightCommandSeparator(waypoint.ActionParams[end]))
            {
                end++;
            }

            var segment = waypoint.ActionParams[start..end];
            var candidateAction = GetSpecialFlightScriptAction(segment, out var candidateAvatarName);
            if (candidateAction != SpecialFlightAction.None
                && string.Equals(candidateAvatarName, avatarName, StringComparison.Ordinal))
            {
                action = candidateAction;
                actionParams = segment;
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    private void ReleaseSpecialFlightSprintMouse()
    {
        if (!_specialFlightSprintMouseDown)
        {
            return;
        }

        Simulation.SendInput.Mouse.RightButtonUp();
        _specialFlightSprintMouseDown = false;
    }

    private static bool HasSpecialFlightAction(Waypoint waypoint)
    {
        return GetSpecialFlightAction(waypoint) != SpecialFlightAction.None;
    }

    // 这些移动模式表示角色已经不再依赖特殊飞行 UI，可在接近目标时主动切回普通落点逻辑。
    // 普通 Fly 单独处理，因为它还需要保留滑翔/下落攻击兜底。
    private static bool IsSpecialFlightFreeFallMoveMode(string moveMode)
    {
        return moveMode == MoveModeEnum.Walk.Code
               || moveMode == MoveModeEnum.Run.Code
               || moveMode == MoveModeEnum.Dash.Code
               || moveMode == MoveModeEnum.Swim.Code
               || moveMode == MoveModeEnum.Jump.Code;
    }

    // 只有这几类 waypoint 的 ActionParams 会被当成特殊飞行脚本解析。
    // 其他动作即使文本里碰巧包含角色名/e/wait/skill，也不会触发特殊飞行状态机。
    private static bool IsSpecialFlightActionWaypoint(Waypoint waypoint)
    {
        return waypoint.Action == ActionEnum.CombatScript.Code
               || waypoint.Action == ActionEnum.StopFlying.Code
               || waypoint.Action == ActionEnum.ForceTp.Code;
    }

    private static string GetSpecialFlightActionName(SpecialFlightAction action)
    {
        return action switch
        {
            SpecialFlightAction.FastFly => "快飞",
            SpecialFlightAction.SlowFly => "慢飞",
            SpecialFlightAction.Via => "途径",
            SpecialFlightAction.Drop => "落下",
            _ => string.Empty
        };
    }

    // 从完整 ActionParams 中找第一个特殊飞行片段。
    // 只返回动作类型，不返回普通脚本部分；执行阶段会再调用 SplitSpecialFlightCombatScript 拆三段。
    private static SpecialFlightAction GetSpecialFlightAction(Waypoint waypoint)
    {
        var actionParams = waypoint.ActionParams;
        if (string.IsNullOrEmpty(actionParams))
        {
            return SpecialFlightAction.None;
        }

        return TryGetSpecialFlightScriptActionIndex(actionParams, out _, out var action, out _)
            ? action
            : SpecialFlightAction.None;
    }

    private static string? GetSpecialFlightAvatarName(Waypoint waypoint)
    {
        var actionParams = waypoint.ActionParams;
        if (string.IsNullOrEmpty(actionParams))
        {
            return null;
        }

        return TryGetSpecialFlightScriptActionIndex(actionParams, out _, out _, out var avatarName)
            ? avatarName
            : null;
    }

    // 返回第一个特殊飞行片段在 ActionParams 中的起始下标。
    // 这个下标用于 SplitSpecialFlightCombatScript 从原始字符串中保留 before/after 片段。
    private static int GetSpecialFlightActionIndex(string actionParams)
    {
        return TryGetSpecialFlightScriptActionIndex(actionParams, out var index, out _, out _)
            ? index
            : -1;
    }

    // 扫描由 ; / 中文分号 / 换行分隔的脚本片段，找出第一个特殊飞行动作。
    // 识别到后同时返回动作类型和角色名，供后续多角色匹配使用。
    private static bool TryGetSpecialFlightScriptActionIndex(string actionParams, out int index, out SpecialFlightAction action, out string? avatarName)
    {
        index = -1;
        action = SpecialFlightAction.None;
        avatarName = null;
        var start = 0;
        while (start < actionParams.Length)
        {
            while (start < actionParams.Length && IsSpecialFlightCommandSeparator(actionParams[start]))
            {
                start++;
            }

            if (start >= actionParams.Length)
            {
                break;
            }

            var end = start;
            while (end < actionParams.Length && !IsSpecialFlightCommandSeparator(actionParams[end]))
            {
                end++;
            }

            var segment = actionParams[start..end];
            action = GetSpecialFlightScriptAction(segment, out avatarName);
            if (action != SpecialFlightAction.None)
            {
                index = start;
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    // drop 片段中的 attack 参数既表示“是否要补下落攻击”，也可携带延迟。
    // attack() 没有参数时视为立即攻击；attack(0.3) 这类参数会被转成毫秒。
    private static bool TryGetSpecialFlightScriptAttackDelayMs(string actionParams, out int delayMs)
    {
        delayMs = 0;
        if (!TryParseSpecialFlightCombatScriptSegment(actionParams, out _, out var commands))
        {
            return false;
        }

        foreach (var command in commands)
        {
            if (command.Method != Method.Attack)
            {
                continue;
            }

            if (command.Args is not { Count: > 0 })
            {
                delayMs = 0;
                return true;
            }

            if (!TryParseSpecialFlightDelayMs(command.Args[0], out delayMs))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    // 单个脚本片段到特殊飞行动作的映射规则：
    // - wait(x)：via；
    // - skill(...)：drop，这里要求原始方法名是 skill，避免 e(...) 被 CombatScript 归一化后误判；
    // - e(...)：起飞，后续命令里有 dash 就是快飞，否则慢飞。
    private static SpecialFlightAction GetSpecialFlightScriptAction(string segment, out string? avatarName)
    {
        segment = segment.Trim();
        if (!TryParseSpecialFlightCombatScriptSegment(segment, out avatarName, out var commands))
        {
            return SpecialFlightAction.None;
        }

        var firstRawMethod = GetFirstCombatScriptRawMethod(segment);
        var firstCommand = commands[0];
        if (firstCommand.Method == Method.Wait && TryGetSpecialFlightWaitDelayMs(firstCommand, out _))
        {
            return SpecialFlightAction.Via;
        }

        if (firstCommand.Method == Method.Skill
            && string.Equals(firstRawMethod, "skill", StringComparison.OrdinalIgnoreCase))
        {
            return SpecialFlightAction.Drop;
        }

        if (firstCommand.Method == Method.Skill
            && string.Equals(firstRawMethod, "e", StringComparison.OrdinalIgnoreCase))
        {
            return commands.Any(command => command.Method == Method.Dash)
                ? SpecialFlightAction.FastFly
                : SpecialFlightAction.SlowFly;
        }

        return SpecialFlightAction.None;
    }

    // 解析单个特殊飞行脚本片段。
    // 片段必须以受支持角色名开头，例如“恰斯卡 e(),dash(...)”；否则不会进入特殊飞行逻辑。
    // CombatScriptParser 抛错时按“不是特殊飞行片段”处理，避免路线中的普通脚本文本影响主流程。
    private static bool TryParseSpecialFlightCombatScriptSegment(string segment, out string? avatarName, out List<CombatCommand> commands)
    {
        commands = [];
        segment = segment.Trim();
        if (!TryGetSpecialFlightAvatarNameFromSegment(segment, out avatarName))
        {
            return false;
        }

        try
        {
            var combatScript = CombatScriptParser.ParseContext(segment, false);
            commands = combatScript.CombatCommands;
            return commands.Count > 0;
        }
        catch
        {
            avatarName = null;
            commands = [];
            return false;
        }
    }

    // 角色名必须是片段的第一个 token。
    // 这样可以同时保留普通 CombatScript 的语义，又让特殊飞行解析能区分快飞角色归属。
    private static bool TryGetSpecialFlightAvatarNameFromSegment(string segment, out string? avatarName)
    {
        avatarName = null;
        var firstSpaceIndex = segment.IndexOf(' ');
        if (firstSpaceIndex <= 0)
        {
            return false;
        }

        var candidate = segment[..firstSpaceIndex];
        foreach (var supportedAvatarName in SpecialFlightAvatarNames)
        {
            if (string.Equals(candidate, supportedAvatarName, StringComparison.Ordinal))
            {
                avatarName = supportedAvatarName;
                return true;
            }
        }

        return false;
    }

    // CombatScriptParser 会把 e(...) 和 skill(...) 都归一到 Skill 方法。
    // 特殊飞行需要区分“e 起飞”和“skill 落下”，所以这里从原始文本取第一个方法名。
    private static string GetFirstCombatScriptRawMethod(string segment)
    {
        var firstSpaceIndex = segment.IndexOf(' ');
        if (firstSpaceIndex < 0 || firstSpaceIndex + 1 >= segment.Length)
        {
            return string.Empty;
        }

        var commands = segment[(firstSpaceIndex + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commands.Length == 0)
        {
            return string.Empty;
        }

        var firstCommand = commands[0];
        var startIndex = firstCommand.IndexOf('(');
        return (startIndex > 0 ? firstCommand[..startIndex] : firstCommand).Trim();
    }

    // via 片段使用 wait(x) 表示途径点停留时间；wait 后面如果还有 dash，则停留结束后恢复快飞。
    // wait(0) 是纯标记点，表示通过该点即可，不执行后面的普通脚本。
    private static bool TryGetSpecialFlightViaStayDelayMs(string? actionParams, out int delayMs, out bool dashAfterStay)
    {
        delayMs = 0;
        dashAfterStay = false;
        if (string.IsNullOrWhiteSpace(actionParams)
            || !TryParseSpecialFlightCombatScriptSegment(actionParams, out _, out var commands)
            || commands.Count == 0)
        {
            return false;
        }

        dashAfterStay = commands.Skip(1).Any(command => command.Method == Method.Dash);
        return commands[0].Method == Method.Wait
               && TryGetSpecialFlightWaitDelayMs(commands[0], out delayMs);
    }

    // 时间参数兼容两种写法：小于等于 60 认为是秒，大于 60 认为已经是毫秒。
    // 这样既兼容 wait(0.3)，也兼容历史上直接传毫秒的脚本。
    private static bool TryGetSpecialFlightWaitDelayMs(CombatCommand command, out int delayMs)
    {
        delayMs = 0;
        if (command.Args is not { Count: > 0 })
        {
            return false;
        }

        return TryParseSpecialFlightDelayMs(command.Args[0], out delayMs);
    }

    private static bool TryParseSpecialFlightDelayMs(string value, out int delayMs)
    {
        delayMs = 0;
        value = value.Trim();
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay) || delay < 0)
        {
            return false;
        }

        delayMs = delay > 60 ? (int)Math.Round(delay) : (int)Math.Round(delay * 1000);
        return true;
    }

    private static bool IsSpecialFlightCommandSeparator(char c)
    {
        return c is ';' or '；' or '\r' or '\n';
    }

    // 特殊飞行动作：
    // FastFly / SlowFly 是起飞后的两种巡航状态；
    // Via 是飞行中途径点；
    // Drop 是本段特殊飞行的显式落下点。
    private enum SpecialFlightAction
    {
        None,
        FastFly,
        SlowFly,
        Via,
        Drop
    }

    // 特殊飞行脚本执行结果。
    // 用于告诉 AfterMoveToTarget：本次 CombatScript 是否已由特殊飞行逻辑消费，避免重复执行普通脚本。
    private enum SpecialFlightExecutionResult
    {
        Noop,
        Executed
    }

}
