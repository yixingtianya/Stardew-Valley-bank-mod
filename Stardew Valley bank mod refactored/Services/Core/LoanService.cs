using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;
using StardewValley;

namespace BankMod.Services.Core;

/// <summary>
/// Full loan service: issuance, daily interest deduction (Stage 8 dynamic cash collection),
/// interest debt tracking, due-date auto-repayment, principal debt grace period, and forced collection.
/// </summary>
public class LoanService : ILoanService
{
    public IReadOnlyList<LoanRecord> GetLoans(BankAccountData account)
    {
        return account.Loans;
    }

    public LoanRecord? GetLoan(BankAccountData account, CompanyDefinition company)
    {
        return account.Loans.FirstOrDefault(l =>
            l.CompanyName == company.Name ||
            l.CompanyName == company.OriginalName ||
            (!string.IsNullOrEmpty(company.CropCode) && l.CompanyName == company.CropCode));
    }

    public bool IssueLoan(CompanyDefinition company, int amount, int repaymentDays,
        BankAccountData account, ModConfig config, IInterestCalculator interestCalc)
    {
        if (amount <= 0 || repaymentDays is not (7 or 14))
            return false;

        // Stage 8: Bankruptcy blocks borrowing
        if (account.IsInBankruptcy)
            return false;

        // Stage 9: Hungry/Dying dynamic companies block borrowing (value2.txt §5.1)
        if (company.IsDynamic)
        {
            var dyn = account.DynamicCompanies.FirstOrDefault(c =>
                c.CompanyName == company.Name || c.CompanyName == company.OriginalName);
            if (dyn is not null && (dyn.Status == CompanyStatus.Hungry || dyn.Status == CompanyStatus.Dying))
                return false;
        }

        // Check per-company loan limit (exclude transferred loans — they don't consume the receiving company's limit)
        int companyPrincipal = account.Loans.Where(l =>
            (l.CompanyName == company.Name || l.CompanyName == company.OriginalName) && !l.IsTransferred).Sum(l => l.Principal);
        if (companyPrincipal + amount > company.LoanLimit)
            return false;

        // Check global leverage-based credit limit
        int globalLimit = GetGlobalCreditLimit(account, config);
        int totalPrincipal = account.Loans.Sum(l => l.Principal) + amount;
        if (totalPrincipal > globalLimit)
            return false;

        // Calculate effective rate (with weather/luck influence + 14-day discount)
        var ctx = BuildContext(company, config);
        double baseRate = interestCalc.CalculateLoanRate(company, ctx);
        double effectiveRate = baseRate;
        if (repaymentDays == 14)
            effectiveRate = Math.Max(0, baseRate - config.LongTermRateDiscount);

        int today = (int)Game1.stats.DaysPlayed;
        int dueDay = today + repaymentDays;

        // Merge with existing loan if same company + same due day + same period (same-day same-terms borrow)
        var sameDayLoan = account.Loans.FirstOrDefault(l =>
            (l.CompanyName == company.Name || l.CompanyName == company.OriginalName)
            && l.DueDay == dueDay && l.RepaymentPeriodDays == repaymentDays);

        if (sameDayLoan is not null)
        {
            int newTotal = sameDayLoan.Principal + amount;
            sameDayLoan.InterestRate = ((sameDayLoan.InterestRate * sameDayLoan.Principal) + (effectiveRate * amount)) / newTotal;
            sameDayLoan.Principal = newTotal;
        }
        else
        {
            var loan = new LoanRecord
            {
                CompanyName = company.OriginalName,
                Principal = amount,
                InterestRate = effectiveRate,
                RepaymentPeriodDays = repaymentDays,
                DueDay = dueDay,
                LoanStartDay = today,
                IsInDefault = false,
                DefaultDaysRemaining = 0,
                IsInInterestDebt = false,
                OverdueInterest = 0
            };
            account.Loans.Add(loan);
        }

        // Transfer gold to player
        Game1.player.Money += amount;

        return true;
    }

    /// <summary>Get all active loans for a specific company (matches by Name, OriginalName, and CropCode).</summary>
    public IReadOnlyList<LoanRecord> GetCompanyLoans(BankAccountData account, CompanyDefinition company)
    {
        return account.Loans.Where(l =>
            l.CompanyName == company.Name ||
            l.CompanyName == company.OriginalName ||
            (!string.IsNullOrEmpty(company.CropCode) && l.CompanyName == company.CropCode)
        ).ToList();
    }

    /// <summary>Calculate global borrowing limit: min(net_worth × leverage_coeff, hard_cap).</summary>
    private static int GetGlobalCreditLimit(BankAccountData account, ModConfig config)
    {
        int totalDeposits = account.CompanyAccounts.Sum(a => a.DepositBalance);
        int totalLoanPrincipal = account.Loans.Sum(l => l.Principal);
        int netWorth = totalDeposits + Game1.player.Money - totalLoanPrincipal;
        if (netWorth <= 0)
            return 0;

        return (int)(netWorth * config.BorrowingLeverageCoefficient);
    }

    public int RepayLoan(LoanRecord loan, int requestedAmount, BankAccountData account)
    {
        if (requestedAmount <= 0 || (loan.Principal <= 0 && loan.AccumulatedInterest <= 0 && loan.OverdueInterest <= 0))
            return 0;

        // Manual repayment: cash + same-company deposit only (preserves player choice)
        var sameCompany = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == loan.CompanyName);
        int sameDeposit = sameCompany?.DepositBalance ?? 0;
        int available = Game1.player.Money + sameDeposit;

        int totalOwed = loan.Principal + loan.AccumulatedInterest + loan.OverdueInterest;
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

        // Payment order: overdue interest first, then accumulated interest, then principal
        int overduePaid = Math.Min(actualPaid, loan.OverdueInterest);
        loan.OverdueInterest -= overduePaid;
        actualPaid -= overduePaid;

        int interestPaid = Math.Min(actualPaid, loan.AccumulatedInterest);
        loan.AccumulatedInterest -= interestPaid;
        actualPaid -= interestPaid;

        int principalPaid = Math.Min(actualPaid, loan.Principal);
        loan.Principal -= principalPaid;

        // Clear interest debt if overdue is fully paid
        if (loan.OverdueInterest <= 0)
        {
            loan.IsInInterestDebt = false;
            loan.OverdueInterest = 0;
        }

        // If loan fully repaid, remove it and re-check bankruptcy
        if (loan.Principal <= 0 && loan.AccumulatedInterest <= 0 && loan.OverdueInterest <= 0)
        {
            account.Loans.Remove(loan);
            // Exit bankruptcy if no frozen loans remain
            if (!account.Loans.Any(l => l.IsFrozen))
            {
                account.IsInBankruptcy = false;
                account.BankruptcyWarningShown = false;
            }
        }
        else
        {
            // If loan was overdue (in default), partial repayment does NOT clear default or reset due date.
            // The full debt (principal + interest + overdue) must be paid to exit default.
            // For non-defaulted loans, partial early repayment resets the due date (refinancing).
            if (!loan.IsInDefault && !loan.IsFrozen)
            {
                int today = (int)Game1.stats.DaysPlayed;
                loan.DueDay = today + loan.RepaymentPeriodDays;
            }
        }

        return cashUsed + (r > 0 ? 0 : 0); // Return actual paid for caller use
    }

    /// <summary>
    /// Daily tick: interest collection, due-date handling, default progression.
    /// Stage 8: dynamic company interest is deducted from cash daily (8.1-8.2).
    /// Fixed company interest continues to accrue for maturity collection (8.3).
    /// </summary>
    public void DailyTick(BankAccountData account, ModConfig config, IInterestCalculator interestCalc)
    {
        if (account.Loans.Count == 0)
            return;

        int today = (int)Game1.stats.DaysPlayed;
        var loansToCheck = account.Loans.ToList();
        bool anyInterestDebt = false;
        bool anyPrincipalDebt = false;

        foreach (var loan in loansToCheck)
        {
            // Frozen loans: no interest, no due date — skip entirely
            if (loan.IsFrozen)
            {
                anyPrincipalDebt = true; // keeps the warning banner visible
                continue;
            }

            // Resolve company definition: check fixed companies first, then dynamic companies
            var companyDef = config.Companies.FirstOrDefault(c => c.Name == loan.CompanyName);
            bool isDynamic = companyDef?.IsDynamic ?? false;

            if (companyDef is null)
            {
                // Try to find it via active dynamic companies
                var dynCompany = account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == loan.CompanyName);
                if (dynCompany is null) continue;

                isDynamic = true;
                var cropData = CropDataProvider.GetByCode(dynCompany.CropCode);
                double r = cropData?.R ?? 0;
                double depRate = r > 0.20 ? r * 0.5 : r > 0 ? r : 0;
                double loanRate = r > 0.20 ? r * 0.75 : r > 0 ? r * 1.5 : 0;
                companyDef = new CompanyDefinition
                {
                    Name = dynCompany.CompanyName,
                    CropCode = dynCompany.CropCode,
                    IsDynamic = true,
                    DepositInterestRate = depRate,
                    LoanInterestRate = loanRate,
                    LoanLimit = dynCompany.LoanLimit
                };
            }

            // Recalculate effective weather/luck for today's rate
            var ctx = BuildContext(companyDef, config);
            double todayBaseRate = interestCalc.CalculateLoanRate(companyDef, ctx);
            double weatherDelta = todayBaseRate - companyDef.LoanInterestRate;
            double dailyRate = Math.Max(0, loan.InterestRate + weatherDelta);

            // Apply penalty rate if in default (principal debt)
            if (loan.IsInDefault)
                dailyRate += config.PenaltyInterestRate;

            int dailyInterest = (int)(loan.Principal * dailyRate);

            // === Stage 8.1/8.2: Dynamic company — deduct interest from cash daily ===
            if (isDynamic && dailyInterest > 0)
            {
                int collectible = dailyInterest;

                // Also collect overdue interest if simple mode
                if (loan.OverdueInterest > 0 && !account.UseCompoundInterest)
                {
                    collectible += (int)(loan.OverdueInterest * dailyRate);
                }

                if (Game1.player.Money >= collectible)
                {
                    // Cash sufficient: deduct in full
                    Game1.player.Money -= collectible;
                    loan.OverdueInterest = 0;
                    loan.IsInInterestDebt = false;
                }
                else
                {
                    // Cash insufficient: deduct what we can
                    int deducted = Game1.player.Money;
                    Game1.player.Money = 0;
                    int unpaid = collectible - deducted;

                    // Allocate unpaid: interest first, then overdue portion
                    int unpaidBaseInterest = Math.Min(unpaid, dailyInterest);
                    loan.OverdueInterest += unpaidBaseInterest;
                    loan.IsInInterestDebt = true;
                    anyInterestDebt = true;
                }

                // Compound mode: overdue interest rolls into principal
                if (loan.IsInInterestDebt && account.UseCompoundInterest && loan.OverdueInterest > 0)
                {
                    int compoundInterest = (int)(loan.OverdueInterest * dailyRate);
                    loan.OverdueInterest += compoundInterest;
                }
            }
            // === Fixed company: accrue interest (original behavior, collected at maturity) ===
            else if (!isDynamic && dailyInterest > 0)
            {
                loan.AccumulatedInterest += dailyInterest;
            }

            // === Stage 8.3: Due-date handling for all loans ===
            // Use <= to catch loans that slipped past their due day (e.g. player skipped a day)
            bool justEnteredDefault = false;
            if (loan.DueDay <= today && !loan.IsInDefault)
            {
                int totalOwed = loan.Principal + loan.AccumulatedInterest + loan.OverdueInterest;

                if (Game1.player.Money >= totalOwed)
                {
                    // Repay in full from cash
                    Game1.player.Money -= totalOwed;
                    account.Loans.Remove(loan);
                    Game1.chatBox?.addInfoMessage(I18n.Get("ln.1", new { principal = loan.Principal, interest = loan.AccumulatedInterest }));
                    continue;
                }
                else
                {
                    // Not enough cash → enter grace period (principal debt)
                    loan.IsInDefault = true;
                    loan.DefaultDaysRemaining = config.PrincipalDebtGraceDays;
                    justEnteredDefault = true;
                    anyPrincipalDebt = true;
                }
            }

            // Track principal debt
            if (loan.IsInDefault)
                anyPrincipalDebt = true;

            // === Progress default/grace period ===
            if (loan.IsInDefault && !justEnteredDefault)
            {
                loan.DefaultDaysRemaining--;

                if (loan.DefaultDaysRemaining <= 0)
                {
                    // Grace period expired — force deduction from cash + all deposits
                    int totalOwed = loan.Principal + loan.AccumulatedInterest + loan.OverdueInterest;
                    int totalAvailable = Game1.player.Money + account.CompanyAccounts.Sum(a => a.DepositBalance);

                    if (totalAvailable < totalOwed)
                    {
                        // All assets insufficient → freeze loan (no new interest, no due date), enter bankruptcy
                        // Interest amounts preserved — only income garnishment + borrow block apply
                        loan.IsFrozen = true;
                        loan.IsInDefault = false;
                        account.IsInBankruptcy = true;
                        account.BankruptcyWarningShown = false;
                        anyPrincipalDebt = true;
                    }
                    else
                    {
                        int collected = CollectFundsOrdered(account, loan, totalOwed, config);

                        int remaining = collected;
                        int overdueDeducted = Math.Min(remaining, loan.OverdueInterest);
                        loan.OverdueInterest -= overdueDeducted;
                        remaining -= overdueDeducted;

                        int interestDeducted = Math.Min(remaining, loan.AccumulatedInterest);
                        loan.AccumulatedInterest -= interestDeducted;
                        remaining -= interestDeducted;

                        loan.Principal -= remaining;

                        if (loan.OverdueInterest <= 0)
                            loan.IsInInterestDebt = false;

                        if (loan.Principal > 0 || loan.AccumulatedInterest > 0 || loan.OverdueInterest > 0)
                        {
                            loan.DefaultDaysRemaining = config.PrincipalDebtGraceDays;
                            Game1.chatBox?.addInfoMessage(I18n.Get("ln.2", new { amount = loan.Principal + loan.AccumulatedInterest + loan.OverdueInterest }));
                        }
                        else
                        {
                            account.Loans.Remove(loan);
                            Game1.chatBox?.addInfoMessage(I18n.Get("ln.3", new { amount = collected }));
                        }
                    }
                }
            }
        }

        // Update global debt flags
        account.IsInInterestDebt = anyInterestDebt;
        account.IsInPrincipalDebt = anyPrincipalDebt;
    }

    /// <summary>
    /// Collect funds from player for forced loan repayment during grace expiry.
    /// Priority: cash → same-company deposit → other deposits (lowest interest rate first).
    /// Mutates player gold and deposit balances.
    /// </summary>
    private static int CollectFundsOrdered(BankAccountData account, LoanRecord loan, int amountNeeded, ModConfig config)
    {
        int remaining = amountNeeded;

        // 1. Cash on hand
        int cashUsed = Math.Min(Game1.player.Money, remaining);
        Game1.player.Money -= cashUsed;
        remaining -= cashUsed;
        if (remaining <= 0) return amountNeeded;

        // 2. Same-company deposit first
        var sameCompany = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == loan.CompanyName);
        if (sameCompany is not null && sameCompany.DepositBalance > 0)
        {
            int fromSame = Math.Min(sameCompany.DepositBalance, remaining);
            sameCompany.DepositBalance -= fromSame;
            sameCompany.BaseAmount = Math.Max(0, sameCompany.BaseAmount - fromSame);
            remaining -= fromSame;
            if (remaining <= 0) return amountNeeded;
        }

        // 3. Other company deposits — low interest rate accounts first
        var others = account.CompanyAccounts
            .Where(a => a.CompanyName != loan.CompanyName && a.DepositBalance > 0)
            .OrderBy(a => GetDepositRate(a, config))
            .ToList();

        foreach (var ca in others)
        {
            int fromOther = Math.Min(ca.DepositBalance, remaining);
            ca.DepositBalance -= fromOther;
            ca.BaseAmount = Math.Max(0, ca.BaseAmount - fromOther);
            remaining -= fromOther;
            if (remaining <= 0) break;
        }

        return amountNeeded - remaining;
    }

    private static double GetDepositRate(CompanyAccount ca, ModConfig config)
    {
        var company = config.Companies.FirstOrDefault(c => c.Name == ca.CompanyName);
        return company?.DepositInterestRate ?? 0.05;
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
