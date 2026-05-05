using BankMod.Data;

namespace BankMod.Services.Abstractions;

/// <summary>Manages loan issuance, daily interest accrual, due-date repayment, and default handling.</summary>
public interface ILoanService
{
    /// <summary>Get all active loans for the player.</summary>
    IReadOnlyList<LoanRecord> GetLoans(BankAccountData account);

    /// <summary>Get the active loan for a specific company, or null if none.</summary>
    LoanRecord? GetLoan(BankAccountData account, string companyName);

    /// <summary>
    /// Issue a new loan or top up an existing one for the given company.
    /// A repayment period (7 or 14 days) is chosen at borrowing time.
    /// Returns true on success; false if the amount would exceed the company's loan limit.
    /// </summary>
    bool IssueLoan(CompanyDefinition company, int amount, int repaymentDays,
        BankAccountData account, ModConfig config, IInterestCalculator interestCalc);

    /// <summary>
    /// Make a repayment toward a loan. Funds are applied in order:
    /// accumulated interest first, then principal.
    /// Returns the amount actually repaid (player gold deducted).
    /// </summary>
    int RepayLoan(LoanRecord loan, int requestedAmount, BankAccountData account);

    /// <summary>
    /// Daily tick run each morning:
    /// 1. Accrue daily interest on all active loans (principal × daily rate → accumulated interest)
    /// 2. Check if any loan's due date is today → attempt auto-repayment from player cash
    /// 3. Handle default/grace period progression for overdue loans
    /// </summary>
    void DailyTick(BankAccountData account, ModConfig config, IInterestCalculator interestCalc);
}
