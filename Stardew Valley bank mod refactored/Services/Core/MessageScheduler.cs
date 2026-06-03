using BankMod.Services.Abstractions;

namespace BankMod.Services.Core;

/// <summary>计划中（P4）：消息调度器桩实现。</summary>
public class MessageScheduler : IMessageScheduler
{
    public void ScheduleToday()
    {
        // 计划中：每日早晨推送金融消息（三幕剧节奏）
    }

    public int GetRemainingFakeQuota() => 2;
    public int GetRemainingRealQuota() => 1;
}
