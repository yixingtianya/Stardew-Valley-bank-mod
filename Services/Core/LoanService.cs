using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;
using StardewValley;

namespace BankMod.Services.Core;

/// <summary>
/// Full loan service: issuance, daily interest accrual, due-date auto-repayment, and default handling.
/// </summary>
public class LoanService : ILoanService
{
    public IReadOnlyList<LoanRecord> GetLoans(BankAccountData account)
    {
        return account.Loans;
    }

    public LoanRecord? GetLoan(BankAccountData account, string companyName)
    {
        return account.Loans.FirstOrDefault(l => l.CompanyName == companyName);
    }

    public bool IssueLoan(CompanyDefinition company, int amount, int repaymentDays,
        BankAccountData account, ModConfig config, IInterestCalculator interestCalc)
    {
        if (amount <= 0 || repaymentDays is not (7 or 14))
            return false;

        var existing = GetLoan(account, company.Name);

        // Check per-company loan limit
        int currentPrincipal = existing?.Principal ?? 0;
        if (currentPrincipal + amount > company.LoanLimit)
            return false;

        // Check global leverage-based credit limit
        int globalLimit = GetGlobalCreditLimit(account, config);
        int totalLoans = account.Loans.Sum(l => l.Principal) + amount;
        if (totalLoans > globalLimit)
            return false;

        // Calculate effective rate (with weather/luck influence + 14-day discount)
        var ctx = BuildContext(company, config);
        double baseRate = interestCalc.CalculateLoanRate(company, ctx);
        double effectiveRate = baseRate;
        if (repaymentDays == 14)
            effectiveRate = Math.Max(0, baseRate - config.LongTermRateDiscount);

        int today = (int)Game1.stats.DaysPlayed;

        if (existing is not null)
        {
            // Top-up: increase principal, recalculate effective rate as weighted average
            int newTotal = existing.Principal + amount;
            existing.InterestRate = ((existing.InterestRate * existing.Principal) + (effectiveRate * amount)) / newTotal;
            existing.Principal = newTotal;
        }
        else
        {
            existing = new LoanRecord
            {
                CompanyName = company.Name,
                Principal = amount,
                InterestRate = effectiveRate,
                RepaymentPeriodDays = repaymentDays,
                DueDay = today + repaymentDays,
                LoanStartDay = today,
                IsInDefault = false,
                DefaultDaysRemaining = 0
            };
            account.Loans.Add(existing);
        }

        // Transfer gold to player
        Game1.player.Money += amount;

        return true;
    }

    /// <summary>Calculate global borrowing limit: min(net_worth × leverage_coeff, hard_cap).</summary>
    private static int GetGlobalCreditLimit(BankAccountData account, ModConfig config)
    {
        int totalDeposits = account.CompanyAccounts.Sum(a => a.DepositBalance);
        int totalLoanPrincipal = account.Loans.Sum(l => l.Principal);
        int netWorth = totalDeposits + Game1.player.Money - totalLoanPrincipal;
        if (netWorth <= 0)
            return 0;

        int leverageLimit = (int)(netWorth * config.BorrowingLeverageCoefficient);
        return Math.Min(leverageLimit, config.BorrowingHardCap);
    }

    public int RepayLoan(LoanRecord loan, int requestedAmount, BankAccountData account)
    {
        if (requestedAmount <= 0 || (loan.Principal <= 0 && loan.AccumulatedInterest <= 0))
            return 0;

        // Manual repayment: cash + same-company deposit only (preserves player choice)
        var sameCompany = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == loan.CompanyName);
        int sameDeposit = sameCompany?.DepositBalance ?? 0;
        int available = Game1.player.Money + sameDeposit;

        int totalOwed = loan.Principal + loan.AccumulatedInterest;
        int actualPaid = Math.Min(requestedAmount, Math.Min(available, totalOwed));

        if (actualPaid <= 0)
            return 0;

        // Collect: cash first, then same-company deposit
        int r = actualPaid;
        int cashUsed = Math.Min(Game1.player.Money, r);
        Game1.player.Money -= cashUsed;
        r -= cashUsed;
        if (r > 0 && sameCompany is not null)
        {
            int fromDeposit = Math.Min(sameCompany.DepositBalance, r);
            sameCompany.DepositBalance -= fromDeposit;
            sameCompany.BaseAmount = Math.Max(0, sameCompany.BaseAmount - fromDeposit);
        }

        // Payment order: accumulated interest first, then principal
        int interestPaid = Math.Min(actualPaid, loan.AccumulatedInterest);
        loan.AccumulatedInterest -= interestPaid;
        int principalPaid = Math.Min(actualPaid - interestPaid, loan.Principal);
        loan.Principal -= principalPaid;

        // If loan fully repaid, remove it
        if (loan.Principal <= 0 && loan.AccumulatedInterest <= 0)
        {
            account.Loans.Remove(loan);
        }
        else
        {
            // Clear default status if enough was paid
            if (loan.IsInDefault)
            {
                loan.IsInDefault = false;
                loan.DefaultDaysRemaining = 0;
            }

            // Reset due date: extend by the original repayment period from today
            int today = (int)Game1.stats.DaysPlayed;
            loan.DueDay = today + loan.RepaymentPeriodDays;
        }

        return actualPaid;
    }

    public void DailyTick(BankAccountData account, ModConfig config, IInterestCalculator interestCalc)
    {
        if (account.Loans.Count == 0)
            return;

        int today = (int)Game1.stats.DaysPlayed;
        var loansToCheck = account.Loans.ToList(); // copy for safe iteration

        foreach (var loan in loansToCheck)
        {
            var company = config.Companies.FirstOrDefault(c => c.Name == loan.CompanyName);
            if (company is null) continue;

            // === Step 1: Accrue daily interest ===
            if (loan.Principal > 0)
            {
                // Recalculate effective weather/luck for today's rate
                var ctx = BuildContext(company, config);
                double todayRate = interestCalc.CalculateLoanRate(company, ctx);
                double weatherDelta = todayRate - company.LoanInterestRate;
                double dailyRate = Math.Max(0, loan.InterestRate + weatherDelta);

                // Apply penalty rate if in default
                if (loan.IsInDefault)
                    dailyRate += config.PenaltyInterestRate;

                int interest = (int)(loan.Principal * dailyRate);
                if (interest > 0)
                {
                    loan.AccumulatedInterest += interest;
                }
            }

            // === Step 2: Check due date (cash only — player must actively manage repayments) ===
            bool justEnteredDefault = false;
            if (loan.DueDay == today && !loan.IsInDefault)
            {
                int totalOwed = loan.Principal + loan.AccumulatedInterest;

                if (Game1.player.Money >= totalOwed)
                {
                    // Repay in full from cash only
                    Game1.player.Money -= totalOwed;
                    account.Loans.Remove(loan);
                    continue; // loan removed, skip Step 3
                }
                else
                {
                    // Not enough cash → enter grace period
                    // Player can still manually repay (RepayLoan uses cash + deposits)
                    loan.IsInDefault = true;
                    loan.DefaultDaysRemaining = config.PrincipalDebtGraceDays;
                    justEnteredDefault = true;
                }
            }

            // === Step 3: Progress default/grace period ===
            // Skip on the day default was entered — countdown starts tomorrow
            if (loan.IsInDefault && !justEnteredDefault)
            {
                loan.DefaultDaysRemaining--;

                if (loan.DefaultDaysRemaining <= 0)
                {
                    // Grace period expired — force deduction from cash + all deposits
                    int totalOwed = loan.Principal + loan.AccumulatedInterest;
                    int available = GetTotalAvailableFunds(account);

                    if (available > 0)
                    {
                        int deducted = Math.Min(available, totalOwed);
                        CollectFunds(account, loan.CompanyName, deducted);

                        // Apply deduction: interest first, then principal
                        int interestDeducted = Math.Min(deducted, loan.AccumulatedInterest);
                        loan.AccumulatedInterest -= interestDeducted;
                        loan.Principal -= (deducted - interestDeducted);
                    }

                    // Reset grace counter for next cycle if still has debt
                    if (loan.Principal > 0 || loan.AccumulatedInterest > 0)
                    {
                        loan.DefaultDaysRemaining = config.PrincipalDebtGraceDays;
                    }
                    else
                    {
                        account.Loans.Remove(loan);
                    }
                }
            }
        }
    }

    /// <summary>Total funds available for repayment: cash + all company deposits.</summary>
    private static int GetTotalAvailableFunds(BankAccountData account)
    {
        return Game1.player.Money + account.CompanyAccounts.Sum(a => a.DepositBalance);
    }

    /// <summary>
    /// Collect funds from player for loan repayment.
    /// Priority: cash on hand → same-company deposit → other company deposits.
    /// Mutates player gold and deposit balances.
    /// </summary>
    private static void CollectFunds(BankAccountData account, string companyName, int amountNeeded)
    {
        int remaining = amountNeeded;

        // 1. Cash on hand
        int cashUsed = Math.Min(Game1.player.Money, remaining);
        Game1.player.Money -= cashUsed;
        remaining -= cashUsed;
        if (remaining <= 0) return;

        // 2. Same-company deposit first
        var sameCompany = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == companyName);
        if (sameCompany is not null && sameCompany.DepositBalance > 0)
        {
            int fromSame = Math.Min(sameCompany.DepositBalance, remaining);
            sameCompany.DepositBalance -= fromSame;
            sameCompany.BaseAmount = Math.Max(0, sameCompany.BaseAmount - fromSame);
            remaining -= fromSame;
            if (remaining <= 0) return;
        }

        // 3. Other company deposits
        foreach (var ca in account.CompanyAccounts)
        {
            if (ca.CompanyName == companyName || ca.DepositBalance <= 0) continue;
            int fromOther = Math.Min(ca.DepositBalance, remaining);
            ca.DepositBalance -= fromOther;
            ca.BaseAmount = Math.Max(0, ca.BaseAmount - fromOther);
            remaining -= fromOther;
            if (remaining <= 0) break;
        }
    }

    private static InterestCalculationContext BuildContext(CompanyDefinition company, ModConfig config)
    {
        return new InterestCalculationContext
        {
            Company = company,
            DailyLuck = Game1.player.DailyLuck,
            IsLightning = Game1.isLightning,
            IsRaining = Game1.isRaining,
            IsSnowing = Game1.isSnowing,
            Config = config
        };
    }
}
