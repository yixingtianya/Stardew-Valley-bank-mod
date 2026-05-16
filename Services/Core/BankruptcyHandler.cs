using BankMod.Data;
using BankMod.Services.Abstractions;
using StardewValley;

namespace BankMod.Services.Core;

/// <summary>
/// Stage 8.4: Detects bankruptcy conditions and manages bankruptcy state transitions.
/// - Enter bankruptcy: has overdue/interst-debt loans AND cash + deposits < overdue total
/// - During bankruptcy: interest frozen, borrowing blocked, shipping income garnished
/// - Exit bankruptcy: all overdue debt cleared
/// </summary>
public class BankruptcyHandler : IBankruptcyHandler
{
    public void CheckBankruptcy(BankAccountData account, ModConfig config)
    {
        // Bankruptcy is now triggered by LoanService when forced collection fails (loan.IsFrozen = true).
        // This method is called after manual repayment to re-check.
        bool anyFrozen = account.Loans.Any(l => l.IsFrozen);
        if (!anyFrozen && account.IsInBankruptcy)
        {
            account.IsInBankruptcy = false;
            account.BankruptcyWarningShown = false;
        }
    }

    /// <summary>
    /// During bankruptcy, deduct a percentage of income toward debt repayment.
    /// Loan priority: frozen > dynamic fewest days remaining > dynamic highest principal > crop ID.
    /// Within each loan: overdue interest → accumulated interest → principal.
    /// </summary>
    public int ApplyShippingIncomeDeduction(BankAccountData account, ModConfig config, int shippingIncome)
    {
        if (!account.IsInBankruptcy || shippingIncome <= 0)
            return 0;

        int deductAmount = (int)(shippingIncome * config.BankruptcyIncomeDeduction);
        if (deductAmount <= 0)
            return 0;

        int remaining = deductAmount;
        int today = (int)Game1.stats.DaysPlayed;

        // Sort loans by priority
        var orderedLoans = account.Loans
            .OrderBy(l => l.IsFrozen ? 0 : 1)                              // 1. Frozen first
            .ThenBy(l => IsDynamicLoan(account, l) ? (l.DueDay - today) : int.MaxValue) // 2. Fewest days remaining
            .ThenByDescending(l => IsDynamicLoan(account, l) ? l.Principal : 0)          // 3. Highest principal
            .ThenBy(l => GetCropCode(account, l))                           // 4. Crop ID
            .ToList();

        foreach (var loan in orderedLoans)
        {
            if (remaining <= 0) break;

            // Pay overdue interest first
            int payOverdue = Math.Min(loan.OverdueInterest, remaining);
            loan.OverdueInterest -= payOverdue;
            remaining -= payOverdue;
            if (loan.OverdueInterest <= 0)
                loan.IsInInterestDebt = false;

            // Then accumulated interest
            if (remaining > 0)
            {
                int payAccInt = Math.Min(loan.AccumulatedInterest, remaining);
                loan.AccumulatedInterest -= payAccInt;
                remaining -= payAccInt;
            }

            // Then principal
            if (remaining > 0)
            {
                int payPrincipal = Math.Min(loan.Principal, remaining);
                loan.Principal -= payPrincipal;
                remaining -= payPrincipal;
            }
        }

        // Remove fully repaid loans
        account.Loans.RemoveAll(l =>
            l.Principal <= 0 && l.AccumulatedInterest <= 0 && l.OverdueInterest <= 0);

        int actualDeducted = deductAmount - remaining;
        Game1.player.Money -= actualDeducted;

        // Update global debt flags
        account.IsInInterestDebt = account.Loans.Any(l => l.IsInInterestDebt);
        account.IsInPrincipalDebt = account.Loans.Any(l => l.IsInDefault);

        // If no frozen loans remain, clear bankruptcy
        if (!account.Loans.Any(l => l.IsFrozen))
        {
            account.IsInBankruptcy = false;
            account.BankruptcyWarningShown = false;
        }

        return actualDeducted;
    }

    private static bool IsDynamicLoan(BankAccountData account, LoanRecord loan)
    {
        return account.DynamicCompanies.Any(c => c.CompanyName == loan.CompanyName);
    }

    private static string GetCropCode(BankAccountData account, LoanRecord loan)
    {
        return account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == loan.CompanyName)?.CropCode ?? loan.CompanyName;
    }
}
