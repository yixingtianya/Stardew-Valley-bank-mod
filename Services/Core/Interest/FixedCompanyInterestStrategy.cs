using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;

namespace BankMod.Services.Core.Interest;

/// <summary>Fixed-company interest strategy: base rate + weather bonus × luck multiplier.
/// Extracted from BankMod.GetEffectiveDepositRate().</summary>
public class FixedCompanyInterestStrategy : IInterestCalculator
{
    public double CalculateDepositRate(CompanyDefinition company, InterestCalculationContext ctx)
    {
        double rate = company.DepositInterestRate;

        // Stage 7.1/7.2: consecutive sell bonus or decay
        rate += GetConsecutiveAdjustment(ctx, isDeposit: true);

        // Stage 7.4 step 4: luck multiplier
        if (ctx.LuckEnabled)
            rate *= 1 + ctx.DailyLuck * ctx.Config.LuckStrengthCoefficient;

        // Stage 7.4 step 5: weather bonus
        if (ctx.WeatherEnabled)
            rate += GetWeatherBonus(company, ctx);

        // Stage 7.4 step 6: clamp
        if (!ctx.Config.AllowNegativeInterest)
            rate = Math.Max(0, rate);
        else
            rate = Math.Max(ctx.Config.DepositRateFloor, rate);

        // V3.7: manual compound interest penalty
        if (ctx.IsPenaltyPeriod)
            rate *= 0.10;

        return rate;
    }

    public double CalculateLoanRate(CompanyDefinition company, InterestCalculationContext ctx)
    {
        double rate = company.LoanInterestRate;

        // Stage 7.1/7.2: consecutive sell bonus or decay
        rate += GetConsecutiveAdjustment(ctx, isDeposit: false);

        // Stage 7.4 step 4: luck multiplier
        if (ctx.LuckEnabled)
            rate *= 1 + ctx.DailyLuck * ctx.Config.LuckStrengthCoefficient;

        // Stage 7.4 step 5: weather bonus
        if (ctx.WeatherEnabled)
            rate += GetWeatherBonus(company, ctx);

        // Stage 7.4 step 6: clamp
        if (!ctx.Config.AllowNegativeInterest)
            rate = Math.Max(0, rate);
        else
            rate = Math.Max(ctx.Config.DepositRateFloor, rate);

        return rate;
    }

    private static double GetConsecutiveAdjustment(InterestCalculationContext ctx, bool isDeposit)
    {
        if (ctx.ConsecutiveSellDays > 0)
        {
            double bonusPerDay = isDeposit
                ? ctx.Config.ConsecutiveSellDepositBonusPerDay
                : ctx.Config.ConsecutiveSellLoanBonusPerDay;
            double cap = isDeposit
                ? ctx.Config.ConsecutiveSellDepositBonusCap
                : ctx.Config.ConsecutiveSellLoanBonusCap;
            return Math.Min(ctx.ConsecutiveSellDays * bonusPerDay, cap);
        }
        // Stage 7.2: cumulative decay — only if the crop has been sold before
        if (ctx.HasSoldHistory && ctx.DecayDays > 0)
        {
            double decayPerDay = isDeposit
                ? ctx.Config.SellDecayDepositPerDay
                : ctx.Config.SellDecayLoanPerDay;
            double cap = isDeposit
                ? ctx.Config.ConsecutiveSellDepositBonusCap
                : ctx.Config.ConsecutiveSellLoanBonusCap;
            return -Math.Min(ctx.DecayDays * decayPerDay, cap);
        }
        return 0;
    }

    private static double GetWeatherBonus(CompanyDefinition company, InterestCalculationContext ctx)
    {
        if (ctx.IsLightning) return company.LightningBonus;
        if (ctx.IsRaining) return company.RainBonus;
        if (ctx.IsSnowing) return company.SnowBonus;
        return company.SunBonus;
    }
}
