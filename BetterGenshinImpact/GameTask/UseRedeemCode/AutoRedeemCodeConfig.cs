using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterGenshinImpact.GameTask.UseRedeemCode;

[Serializable]
public partial class AutoRedeemCodeConfig: ObservableObject
{
    /// <summary>
    /// 是否启用剪切板监听
    /// </summary>
    [ObservableProperty]
    private bool _clipboardListenerEnabled = true;

    /// <summary>
    /// 是否在一条龙启动时自动检查兑换码
    /// </summary>
    [ObservableProperty]
    private bool _autoRedeemCodeCheckEnabled = false;

    /// <summary>
    /// 每个UID上次检查兑换码的日期 (key=UID, value=yyyy-MM-dd格式)
    /// </summary>
    [ObservableProperty]
    private Dictionary<string, string> _lastRedeemCodeCheckDates = new();
}