namespace BankMod.Services.Abstractions;

/// <summary>计划中（P2）：计算玩家净资产、动态额度分配和借款前上限校验。</summary>
public interface ICreditLimitService
{
    /// <summary>获取当前玩家可用的总借款额度。</summary>
    int GetTotalCreditLimit();

    /// <summary>获取指定公司的借款额度分配。</summary>
    int GetCompanyCreditLimit(string companyName);
}
