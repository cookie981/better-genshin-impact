#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 联机锄地自动组队：房主在 F2 界面等待并按 Y 同意 / 成员搜索房主 UID 申请加入
/// </summary>
public class AutoPartyTask
{
    private readonly ILogger _logger = App.GetLogger<AutoPartyTask>();

    // 确认按钮模板（comfirm_btn1.png）
    private static readonly RecognitionObject ConfirmBtnRo = new RecognitionObject
    {
        Name = "CoOpConfirmBtn",
        RecognitionType = RecognitionTypes.TemplateMatch,
        TemplateImageMat = GameTaskManager.LoadAssetImage("AutoSkip", "comfirm_btn1.png"),
        Threshold = 0.7,
        DrawOnWindow = false
    }.InitTemplate();

    // 1080P 坐标常量（多人游戏界面）
    private const double UidInputX = 222, UidInputY = 120;
    private const double SearchBtnX = 1676, SearchBtnY = 123;
    private const double ApplyBtnX = 1625, ApplyBtnY = 245;

    /// <summary>
    /// 成员流程：搜索房主 UID，申请加入，等待进入世界
    /// 申请后按钮倒数 10 秒，倒数结束后可再次点击。房主同意后直接加载。
    /// </summary>
    public async Task<bool> JoinHostWorldAsync(string hostUid, CancellationToken ct)
    {
        _logger.LogInformation("[自动组队-成员] 开始，房主 UID: {Uid}", hostUid);

        // 1. 尝试回到主界面
        _logger.LogInformation("[自动组队-成员] 尝试回到主界面");
        try
        {
            await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(ct);
        }
        catch { /* 忽略 */ }
        await Delay(500, ct);

        if (!await WaitForMainUi(ct, 10))
        {
            _logger.LogError("[自动组队-成员] 回到主界面失败");
            return false;
        }

        // 2. 打开 F2 多人游戏界面
        if (!await OpenCoOpScreen(ct))
        {
            _logger.LogError("[自动组队-成员] 打开多人游戏界面失败");
            return false;
        }

        // 3. 输入房主 UID 并搜索（只需做一次）
        await InputUidAndSearch(hostUid, ct);

        // 4. 循环申请加入，最多 30 次
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[自动组队-成员] 第 {N}/30 次申请加入", attempt);

            // 点击"申请加入"按钮（点两次确保响应）
            GameCaptureRegion.GameRegion1080PPosClick(ApplyBtnX, ApplyBtnY);
            await Delay(300, ct);
            GameCaptureRegion.GameRegion1080PPosClick(ApplyBtnX, ApplyBtnY);
            await Delay(1000, ct);

            // 等待：按钮倒数 10 秒 + 可能的加载时间
            // 每秒检测一次是否进入了加载（派蒙消失 = 可能在加载）
            for (int wait = 0; wait < 15; wait++)
            {
                await Delay(1000, ct);

                // 检测是否已进入主界面（加载完成，进入了房主世界）
                using var ra = CaptureToRectArea();
                if (Bv.IsInMainUi(ra))
                {
                    _logger.LogInformation("[自动组队-成员] 检测到主界面，已进入房主世界");
                    return true;
                }
            }

            // 15 秒后还没进入世界，可能申请被忽略或拒绝
            // 检查是否还在 F2 界面（派蒙不可见 = 还在 F2 或加载中）
            using var checkRa = CaptureToRectArea();
            if (Bv.IsInMainUi(checkRa))
            {
                // 已经回到主界面但不是房主世界（被拒绝后回到自己世界）
                // 需要重新打开 F2 并搜索
                _logger.LogInformation("[自动组队-成员] 回到主界面，重新打开 F2 搜索");
                if (!await OpenCoOpScreen(ct)) continue;
                await InputUidAndSearch(hostUid, ct);
            }
            // 否则还在 F2 界面，按钮倒数结束，可以再次点击申请
        }

        _logger.LogError("[自动组队-成员] 30 次尝试后仍未加入，放弃");
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
        await Delay(500, ct);
        return false;
    }

    /// <summary>
    /// 房主流程：在 F2 界面等待成员加入，按 Y 同意申请。
    /// 返回值：-1=失败，0=超时，>0=实际就绪人数（含房主）
    /// 房主可按回车跳过等待，以当前人数开始。
    /// </summary>
    public async Task<int> WaitForMembersAsync(
        int expectedCount,
        string[]? whitelist,
        CoordinatorClient client,
        int timeoutSeconds,
        CancellationToken ct)
    {
        _logger.LogInformation("[自动组队-房主] 开始，期望人数: {N}", expectedCount);

        // 1. 尝试回到主界面
        _logger.LogInformation("[自动组队-房主] 尝试回到主界面");
        try
        {
            await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(ct);
        }
        catch { /* 忽略 */ }
        await Delay(500, ct);

        if (!await WaitForMainUi(ct, 10))
        {
            _logger.LogError("[自动组队-房主] 回到主界面失败");
            return -1;
        }

        // 2. 打开 F2
        if (!await OpenCoOpScreen(ct))
        {
            _logger.LogError("[自动组队-房主] 打开多人游戏界面失败");
            return -1;
        }

        // 设置等待状态为 true
        AutoHoeingTask.IsWaitingForParty = true;

        try
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            bool skipMainUiCheck = false; // 重新打开 F2 后跳过一次顶部主界面检测
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogDebug("[自动组队-房主] 循环检测，剩余时间: {Sec}s，当前人数: {Count}", (int)(deadline - DateTime.Now).TotalSeconds, client.CurrentRoomPlayerCount);

                // 检测"立即开始"标志（房主在 BGI UI 点击了立即开始按钮）
                if (AutoHoeingTask.SkipPartyWait)
                {
                    AutoHoeingTask.SkipPartyWait = false;
                    var currentCount = client.CurrentRoomPlayerCount;
                    _logger.LogInformation("[自动组队-房主] 收到立即开始信号，以当前 {N} 人开始锄地", currentCount);
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    await Delay(500, ct);
                    await WaitForMainUi(ct, 5);
                    return currentCount > 0 ? currentCount : 1;
                }

                // 检测 ESC（用户手动关闭了 F2，视为跳过）或加载后回到主界面
                // skipMainUiCheck 为 true 时跳过，避免重新打开 F2 后误判
                if (!skipMainUiCheck)
                {
                    using (var checkRa = CaptureToRectArea())
                    {
                        if (Bv.IsInMainUi(checkRa))
                        {
                            var currentCount = client.CurrentRoomPlayerCount;
                            // 人数已满，直接开始
                            if (currentCount >= expectedCount)
                            {
                                _logger.LogInformation("[自动组队-房主] 检测到主界面且人数已满 {N}，开始锄地", currentCount);
                                return currentCount;
                            }
                            // 人数未满，可能是加载完成回到主界面，重新打开 F2 继续等待
                            _logger.LogInformation("[自动组队-房主] 检测到主界面但人数未满（{Count}/{Expected}），重新打开 F2 继续等待", currentCount, expectedCount);
                            if (!await OpenCoOpScreen(ct, whitelist))
                            {
                                _logger.LogWarning("[自动组队-房主] 重新打开 F2 失败，等待加载完成后重试");
                                await WaitForMainUi(ct, 15);
                            }
                            skipMainUiCheck = true;
                            continue;
                        }
                    }
                }
                skipMainUiCheck = false;

                // 先按 Y 触发申请弹窗
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_Y);
                await Delay(500, ct);

                // 检测是否有确认弹窗出现（模板匹配接受按钮）
                bool hasPopup;
                using (var checkRa = CaptureToRectArea())
                {
                    hasPopup = checkRa.Find(ConfirmBtnRo).IsExist();
                }

                if (!hasPopup)
                {
                    await Delay(1500, ct);
                }
                else
                {
                    var shouldAccept = true;
                    if (whitelist != null && whitelist.Length > 0)
                    {
                        var applicantName = OcrApplicantName();
                        if (!string.IsNullOrEmpty(applicantName))
                        {
                            shouldAccept = IsInWhitelist(applicantName, whitelist);
                            _logger.LogInformation("[自动组队-房主] OCR 识别申请者: {Name}，白名单匹配: {Match}",
                                applicantName, shouldAccept);
                        }
                        else
                        {
                            _logger.LogWarning("[自动组队-房主] OCR 识别失败，跳过本次申请");
                            shouldAccept = false;
                        }
                    }

                    if (shouldAccept)
                    {
                        ClickConfirmButton();
                        await Delay(300, ct);
                        ClickConfirmButton();
                        await Delay(700, ct);
                        _logger.LogDebug("[自动组队-房主] 已点击确认，等待加载...");
                    }
                    else
                    {
                        ClickRejectButton();
                        await Delay(500, ct);
                    }
                }

                // 检测是否被踢回主界面（玩家加入触发加载）
                using var ra = CaptureToRectArea();
                var isMainUiAfterConfirm = Bv.IsInMainUi(ra);
                _logger.LogDebug("[自动组队-房主] 确认后检测主界面: {IsMain}，当前房间人数: {Count}", isMainUiAfterConfirm, client.CurrentRoomPlayerCount);
                if (isMainUiAfterConfirm)
                {
                    _logger.LogInformation("[自动组队-房主] 检测到主界面（玩家加入触发加载），等待加载完成后重新打开 F2");
                    await Delay(3000, ct);
                    await WaitForMainUi(ct, 15);
                    _logger.LogDebug("[自动组队-房主] 加载完成，当前房间人数: {Count}", client.CurrentRoomPlayerCount);

                    // 检查人数是否已达到期望值
                    if (client.CurrentRoomPlayerCount >= expectedCount)
                    {
                        _logger.LogInformation("[自动组队-房主] 加载后人数已达到期望值 {N}，开始锄地", client.CurrentRoomPlayerCount);
                        return client.CurrentRoomPlayerCount;
                    }

                    if (!await OpenCoOpScreen(ct, whitelist))
                    {
                        _logger.LogWarning("[自动组队-房主] 重新打开 F2 失败，重试");
                        await Delay(2000, ct);
                        continue;
                    }
                    // 重新打开 F2 成功，跳回循环顶部，并跳过一次主界面检测（F2 刚打开，主界面已不可见）
                    skipMainUiCheck = true;
                    continue;
                }
                else
                {
                    _logger.LogDebug("[自动组队-房主] 未检测到主界面，继续等待");
                }

                await Delay(1500, ct);
            }

            // 超时：返回 0，由调用方根据 PartyTimeoutAction 决定
            _logger.LogWarning("[自动组队-房主] 等待超时 ({Timeout}s)，当前 {N} 人", timeoutSeconds, client.CurrentRoomPlayerCount);
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
            await Delay(500, ct);
            return 0;
        }
        finally
        {
            // 重置等待状态
            AutoHoeingTask.IsWaitingForParty = false;
        }
    }

    /// <summary>在 F2 界面输入 UID 并搜索</summary>
    private async Task InputUidAndSearch(string uid, CancellationToken ct)
    {
        // 点击 UID 输入框
        GameCaptureRegion.GameRegion1080PPosClick(UidInputX, UidInputY);
        await Delay(300, ct);
        // 再点一次确保输入框获得焦点
        GameCaptureRegion.GameRegion1080PPosClick(UidInputX, UidInputY);
        await Delay(1000, ct);

        // Ctrl+A 全选
        Simulation.SendInput.Keyboard.KeyDown(false, User32.VK.VK_CONTROL);
        await Delay(20, ct);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_A);
        await Delay(20, ct);
        Simulation.SendInput.Keyboard.KeyUp(false, User32.VK.VK_CONTROL);
        await Delay(50, ct);

        // Ctrl+V 粘贴 UID
        UIDispatcherHelper.Invoke(() => Clipboard.SetDataObject(uid));
        await Delay(50, ct);
        Simulation.SendInput.Keyboard.KeyDown(false, User32.VK.VK_CONTROL);
        await Delay(20, ct);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_V);
        await Delay(20, ct);
        Simulation.SendInput.Keyboard.KeyUp(false, User32.VK.VK_CONTROL);
        await Delay(300, ct);

        // 点击搜索（点两次确保响应）
        GameCaptureRegion.GameRegion1080PPosClick(SearchBtnX, SearchBtnY);
        await Delay(300, ct);
        GameCaptureRegion.GameRegion1080PPosClick(SearchBtnX, SearchBtnY);
        await Delay(1500, ct);

        _logger.LogInformation("[自动组队] 已输入 UID {Uid} 并搜索", uid);
    }

    /// <summary>打开 F2 多人游戏界面，重试 3 次。每次重试间隙检测申请弹窗并处理。</summary>
    private async Task<bool> OpenCoOpScreen(CancellationToken ct, string[]? whitelist = null)
    {
        for (int i = 0; i < 3; i++)
        {
            Simulation.SendInput.SimulateAction(GIActions.OpenCoOpScreen);
            await Delay(1500, ct);

            // 派蒙消失 = 界面打开了
            using var ra = CaptureToRectArea();
            if (!Bv.IsInMainUi(ra))
            {
                _logger.LogInformation("[自动组队] F2 界面已打开");
                return true;
            }

            // F2 未打开，检测是否有申请弹窗（加载中主界面也可见弹窗）
            var popupFound = ra.Find(ConfirmBtnRo).IsExist();
            if (popupFound)
            {
                _logger.LogInformation("[自动组队] 打开 F2 失败但检测到申请弹窗，处理中");
                var shouldAccept = true;
                if (whitelist != null && whitelist.Length > 0)
                {
                    var applicantName = OcrApplicantName();
                    if (!string.IsNullOrEmpty(applicantName))
                    {
                        shouldAccept = IsInWhitelist(applicantName, whitelist);
                        _logger.LogInformation("[自动组队] OCR 识别申请者: {Name}，白名单匹配: {Match}", applicantName, shouldAccept);
                    }
                    else
                    {
                        _logger.LogWarning("[自动组队] OCR 识别失败，跳过本次申请");
                        shouldAccept = false;
                    }
                }
                if (shouldAccept)
                {
                    ClickConfirmButton();
                    await Delay(300, ct);
                    ClickConfirmButton();
                    await Delay(700, ct);
                }
                else
                {
                    ClickRejectButton();
                    await Delay(500, ct);
                }
                // 处理完弹窗后继续尝试打开 F2（不计入重试次数，直接进入下一次循环）
                i--; // 不消耗重试次数
                if (i < -1) i = -1; // 防止无限递减
                continue;
            }

            _logger.LogWarning("[自动组队] 打开 F2 失败，重试 {N}/3", i + 1);
            await Delay(500, ct);
        }
        return false;
    }

    /// <summary>等待主界面出现（派蒙可见）</summary>
    private async Task<bool> WaitForMainUi(CancellationToken ct, int maxSeconds)
    {
        for (int i = 0; i < maxSeconds * 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var ra = CaptureToRectArea();
            if (Bv.IsInMainUi(ra))
                return true;
            await Delay(500, ct);
        }
        return false;
    }

    /// <summary>模板匹配确认按钮并点击</summary>
    private void ClickConfirmButton()
    {
        try
        {
            using var ra = CaptureToRectArea();
            var found = ra.Find(ConfirmBtnRo);
            if (found.IsExist())
            {
                found.Click();
                _logger.LogInformation("[自动组队] 点击了确认/接受按钮");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自动组队] 模板匹配确认按钮失败");
        }
    }

    /// <summary>点击拒绝按钮（接受按钮 X 轴左移 150）</summary>
    private void ClickRejectButton()
    {
        try
        {
            using var ra = CaptureToRectArea();
            var found = ra.Find(ConfirmBtnRo);
            if (found.IsExist())
            {
                // 拒绝按钮在接受按钮左边约 150 像素（1080P 坐标）
                var scale = TaskContext.Instance().SystemInfo.ScaleTo1080PRatio;
                var rejectX = found.X - (int)(150 * scale);
                var rejectY = found.Y;
                if (rejectX > 0)
                {
                    GameCaptureRegion.GameRegion1080PPosClick(rejectX / scale, rejectY / scale);
                    _logger.LogInformation("[自动组队] 点击了拒绝按钮");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自动组队] 点击拒绝按钮失败");
        }
    }

    /// <summary>OCR 识别申请弹窗中的玩家名称（1080P 区域: x=702, y=512, w=400, h=50）</summary>
    private string OcrApplicantName()
    {
        try
        {
            using var ra = CaptureToRectArea();
            // 按 1080P 比例裁剪名称区域，x 向左偏移 10px 避免首字被截断
            var scale = TaskContext.Instance().SystemInfo.ScaleTo1080PRatio;
            var x = (int)(692 * scale);
            var y = (int)(512 * scale);
            var w = (int)(410 * scale);
            var h = (int)(50 * scale);

            // 边界检查
            if (x + w > ra.SrcMat.Width) w = ra.SrcMat.Width - x;
            if (y + h > ra.SrcMat.Height) h = ra.SrcMat.Height - y;
            if (w <= 0 || h <= 0) return "";

            using var roi = new OpenCvSharp.Mat(ra.SrcMat, new OpenCvSharp.Rect(x, y, w, h));
            var text = OcrFactory.Paddle.Ocr(roi);
            _logger.LogInformation("[自动组队] OCR 原始结果: {Text}", text);
            return text?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自动组队] OCR 识别异常");
            return "";
        }
    }

    /// <summary>
    /// 检查 OCR 识别的名称是否在白名单中。
    /// 支持原始名和括号内备注名匹配，容错率 70%（允许 OCR 错 1-2 个字）。
    /// </summary>
    private static bool IsInWhitelist(string ocrText, string[] whitelist)
    {
        if (whitelist.Length == 0) return true;

        // 提取原始名和备注名
        // 格式如: "叶宝 (BGI红姐)" → 原始名="叶宝", 备注名="BGI红姐"
        var names = new System.Collections.Generic.List<string>();
        var bracketIdx = ocrText.IndexOfAny(new[] { '(', '（' });
        if (bracketIdx > 0)
        {
            names.Add(ocrText[..bracketIdx].Trim());
            var endIdx = ocrText.IndexOfAny(new[] { ')', '）' });
            if (endIdx > bracketIdx)
                names.Add(ocrText[(bracketIdx + 1)..endIdx].Trim());
        }
        else
        {
            names.Add(ocrText.Trim());
        }

        foreach (var wlName in whitelist)
        {
            var wl = wlName.Trim();
            if (string.IsNullOrEmpty(wl)) continue;
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (FuzzyMatch(name, wl, 0.7))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 退出多人游戏，回到自己的世界。
    /// 操作：确保在主界面 → 打开 F2 → 点击"离开队伍"坐标(1600,1020) → 等待加载回到自己世界
    /// </summary>
    public async Task<bool> LeaveWorldAsync(CancellationToken ct)
    {
        _logger.LogInformation("[自动组队] 开始退出多人游戏，回到自己的世界");

        // 确保在主界面
        try { await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(ct); }
        catch { /* 忽略 */ }
        await Delay(500, ct);

        if (!await WaitForMainUi(ct, 10))
        {
            _logger.LogError("[自动组队] 退出前回到主界面失败");
            return false;
        }

        // 打开 F2
        if (!await OpenCoOpScreen(ct))
        {
            _logger.LogError("[自动组队] 打开 F2 失败，无法退出多人游戏");
            return false;
        }

        // 点击"离开队伍/回到自己世界"按钮（1080P 坐标 1600,1020）
        _logger.LogInformation("[自动组队] 点击离开队伍按钮 (1600,1020)");
        GameCaptureRegion.GameRegion1080PPosClick(1600, 1020);
        await Delay(1000, ct);

        // 可能有确认弹窗，点击确认
        using (var ra = CaptureToRectArea())
        {
            if (ra.Find(ConfirmBtnRo).IsExist())
            {
                ClickConfirmButton();
                await Delay(500, ct);
            }
        }

        // 等待加载完成（最多 20 秒），见到派蒙即为回到自己世界
        _logger.LogInformation("[自动组队] 等待回到自己的世界...");
        if (await WaitForMainUi(ct, 20))
        {
            _logger.LogInformation("[自动组队] 已回到自己的世界");
            return true;
        }

        _logger.LogWarning("[自动组队] 等待超时，可能仍在加载中");
        return false;
    }

    /// <summary>模糊匹配：两个字符串的相同字符比例 >= threshold</summary>
    private static bool FuzzyMatch(string a, string b, double threshold)
    {
        if (a == b) return true;
        var longer = a.Length >= b.Length ? a : b;
        var shorter = a.Length >= b.Length ? b : a;
        if (longer.Length == 0) return false;

        int matchCount = 0;
        var used = new bool[longer.Length];
        foreach (var c in shorter)
        {
            for (int i = 0; i < longer.Length; i++)
            {
                if (!used[i] && longer[i] == c)
                {
                    used[i] = true;
                    matchCount++;
                    break;
                }
            }
        }

        var ratio = (double)matchCount / longer.Length;
        return ratio >= threshold;
    }
}
