using BankMod.Data;
using BankMod.Domain;

namespace BankMod.Services.Abstractions;

/// <summary>Manages company lifecycle: creation, status transitions, and daily settlement.</summary>
public interface ICompanyManager
{
    /// <summary>Execute daily settlement for all companies (interest, status checks, company generation).</summary>
    void OnDayStarted();

    /// <summary>Get the current status of a company.</summary>
    CompanyStatus GetStatus(CompanyId companyId);

    /// <summary>Get all currently active (non-bankrupt) companies.</summary>
    IReadOnlyList<CompanyId> GetActiveCompanies();

    /// <summary>Get unified CompanyDefinition list (fixed from config + dynamic from save data).</summary>
    List<CompanyDefinition> GetAllCompanyDefinitions(BankAccountData account);

    /// <summary>Calculate max withdrawal considering asset pool (V3.3.1).</summary>
    int GetMaxWithdrawal(BankAccountData account, CompanyDefinition company);

    /// <summary>Apply competitor suppression when crops are sold at shops (Stage 7.3).</summary>
    void ApplySuppression(BankAccountData account, string cropCode, int quantity);

    /// <summary>Stage 9: Rescue investment during pre-bankruptcy grace period. Returns true on success.</summary>
    bool RescueInvest(BankAccountData account, string companyName, int amount);

    /// <summary>Pre-generate tomorrow's luck and per-company random values for FBN forecast.</summary>
    void PreGenerateTomorrowValues(BankAccountData account);

    /// <summary>Generate today's per-company random values and rotate tomorrow's into today's.</summary>
    void GenerateTodayValues(BankAccountData account);
}
