namespace BankMod.Services.Abstractions;

/// <summary>计划中（P4）：金融频道消息系统。
/// 每日早晨推送真假消息，三幕剧节奏，营造市场氛围。</summary>
public interface IMessageScheduler
{
    /// <summary>调度今日的金融消息。</summary>
    void ScheduleToday();

    /// <summary>获取本季剩余假消息配额。</summary>
    int GetRemainingFakeQuota();

    /// <summary>获取本季剩余真消息配额。</summary>
    int GetRemainingRealQuota();
}
