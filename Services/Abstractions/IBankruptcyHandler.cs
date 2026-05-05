using BankMod.Domain;

namespace BankMod.Services.Abstractions;

/// <summary>计划中（P2）：检测破产条件、执行清算和强制划扣。</summary>
public interface IBankruptcyHandler
{
    /// <summary>检查所有公司是否满足破产条件。</summary>
    void Check();

    /// <summary>强制执行破产清算。</summary>
    void Liquidate(CompanyId companyId);
}
