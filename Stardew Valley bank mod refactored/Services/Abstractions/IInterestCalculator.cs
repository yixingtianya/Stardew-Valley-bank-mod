using BankMod.Data;
using BankMod.Domain;

namespace BankMod.Services.Abstractions;

/// <summary>Calculates daily interest rates for companies based on context.</summary>
public interface IInterestCalculator
{
    /// <summary>Calculate the effective deposit rate for a company today.</summary>
    double CalculateDepositRate(CompanyDefinition company, InterestCalculationContext ctx);

    /// <summary>Calculate the effective loan rate for a company today.</summary>
    double CalculateLoanRate(CompanyDefinition company, InterestCalculationContext ctx);
}
