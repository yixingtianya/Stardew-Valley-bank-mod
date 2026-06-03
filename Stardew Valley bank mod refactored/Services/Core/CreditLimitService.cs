using BankMod.Services.Abstractions;

namespace BankMod.Services.Core;

/// <summary>计划中（P2）：信用额度服务桩实现。</summary>
public class CreditLimitService : ICreditLimitService
{
    public int GetTotalCreditLimit()
    {
        // 计划中：min(净资产 × 杠杆系数, 硬封顶)
        return 500000;
    }

    public int GetCompanyCreditLimit(string companyName)
    {
        // 计划中：按固定公司额度占比分配
        return 200000;
    }
}
