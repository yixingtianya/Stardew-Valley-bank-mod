using BankMod.Domain;
using BankMod.Services.Abstractions;

namespace BankMod.Services.Core;

/// <summary>计划中（P2）：破产处理桩实现。</summary>
public class BankruptcyHandler : IBankruptcyHandler
{
    public void Check()
    {
        // 计划中：检测所有公司是否满足破产条件
    }

    public void Liquidate(CompanyId companyId)
    {
        // 计划中：清算公司资产，清零存款，标记倒闭
    }
}
