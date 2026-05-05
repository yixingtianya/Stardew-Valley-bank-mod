using BankMod.Domain;
using BankMod.Services.Abstractions;

namespace BankMod.Services.Core;

/// <summary>Provides deposit limits. Currently delegates to the base config value;
/// V3.1 dynamic modifiers (status-dependent, multi-factor correction) are planned.</summary>
public class DepositLimitProvider : IDepositLimitProvider
{
    public int GetDepositLimit(CompanyId companyId, CompanyStatus status, int baseLimit)
    {
        // 计划中：根据公司状态应用动态修正因子
        return baseLimit;
    }
}
