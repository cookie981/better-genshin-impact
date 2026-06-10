using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

public class StopFlyingHandler : IActionHandler
{
    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
    {
        // 如果有参数，先自由落体，然后恢复飞行
        if (waypointForTrack != null
            && TryParseStopFlyingWaitTime(waypointForTrack.ActionParams, out var stopFlyingWaitTime))
        {
            Simulation.SendInput.SimulateAction(GIActions.Jump);
            await Delay(stopFlyingWaitTime, ct);
            Simulation.SendInput.SimulateAction(GIActions.Jump);
            await Delay(300, ct);
        }

        await RunStopFlyingAttackAsync(ct);
    }

    // 普通飞行的下落攻击流程也会被 PathExecutor 的特殊飞行兜底分支复用。
    public static async Task RunStopFlyingAttackAsync(CancellationToken ct)
    {
        // 下落攻击接近目的地
        Logger.LogInformation("动作：下落攻击");
        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
        int i;
        for (i = 0; i < 50; i++)
        {
            var screen = CaptureToRectArea();
            var isFlying = Bv.GetMotionStatus(screen) == MotionStatus.Fly;
            if (isFlying)
            {
                await Delay(300, ct);
                if(i <= 2)Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
            }
            else
            {
                break;
            }
        }

        if (i == 50)
        {
            Logger.LogWarning("动作：下落攻击 超时结束");
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
        }
        else
        {
            Logger.LogInformation("动作：下落攻击 结束");
        }
    }

    // 普通飞行 Space 图标在右下角偏右；特殊飞行 Space 图标由 PathExecutor 单独识别，区域不同。
    public static bool IsNormalFlightBySpaceKey(ImageRegion screen)
    {
        var spaceKey = ElementAssets.Instance.SpaceKey.Clone();
        var widthScale = screen.Width / 1920.0;
        var heightScale = screen.Height / 1080.0;
        spaceKey.Name = "NormalFlightSpaceKey";
        spaceKey.RegionOfInterest = new Rect(
            System.Math.Max(0, screen.Width - (int)System.Math.Round(280 * widthScale)),
            System.Math.Max(0, screen.Height - (int)System.Math.Round(130 * heightScale)),
            System.Math.Min(screen.Width, (int)System.Math.Round(220 * widthScale)),
            System.Math.Min(screen.Height, (int)System.Math.Round(130 * heightScale)));

        using var spaceRa = screen.Find(spaceKey);
        return spaceRa.IsExist();
    }

    // 普通 stop_flying 参数保持严格整数，避免把组合简易策略误当作等待时间解析。
    public static bool TryParseStopFlyingWaitTime(string? actionParams, out int stopFlyingWaitTime)
    {
        stopFlyingWaitTime = 0;
        if (string.IsNullOrWhiteSpace(actionParams))
        {
            return false;
        }

        return int.TryParse(actionParams, out stopFlyingWaitTime);
    }
}
