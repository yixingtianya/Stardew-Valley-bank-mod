using BankMod.Domain;

namespace BankMod.Services.Abstractions;

/// <summary>Provides the current deposit limit for a company, which may vary by status.</summary>
public interface IDepositLimitProvider
{
    /// <summary>Get the effective deposit limit for a company.</summary>
    int GetDepositLimit(CompanyId companyId, CompanyStatus status, int baseLimit);
}
