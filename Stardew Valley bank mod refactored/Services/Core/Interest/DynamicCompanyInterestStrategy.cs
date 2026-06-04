using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;

namespace BankMod.Services.Core.Interest;

/// <summary>
/// Dynamic company interest strategy (V3.5 patch5 + Stage 7 fluctuation).
/// Calculates deposit and loan rates based on crop's base daily rate R = equivalentDailyProfit / equivalentCost,
/// then applies the new 3-tier discount rules:
///   R > 20%:     deposit = R x 0.5,   loan = R x 0.75
///   0% < R <= 20%: deposit = R,          loan = R x 1.5
///   R <= 0%:      deposit = 0 (clamped), loan = 0
/// Loan rate hard floor: +1% (0.01).
/// Stage 7 adds consecutive sell bonus/decay and competitor suppression.
/// </summary>
public class DynamicCompanyInterestStrategy : IInterestCalculator
{
    /// <summary>Hard floor for loan interest rates -- never below +1%.</summary>
    public const double LoanRateHardFloor = 0.01;

    public double CalculateDepositRate(CompanyDefinition company, InterestCalculationContext ctx)
    {
        // 3-tier discount rules already applied in GetAllCompanyDefinitions
        double rate = company.DepositInterestRate;

        // Stage 7.1/7.2: consecutive sell bonus or decay
        rate += GetConsecutiveAdjustment(ctx, isDeposit: true);

        // Stage 7.4 step 4: luck flat additive (coefficient = max ±%, sign from DailyLuck)
        if (ctx.LuckEnabled && ctx.DailyLuck != 0)
            rate += Math.Sign(ctx.DailyLuck) * ctx.Config.LuckStrengthCoefficient / 100.0;

        // Stage 7.4 step 4.5: random volatility (multiplicative, pre-generated, scaled by multiplier)
        if (ctx.Config.RandomnessMultiplier > 0 && ctx.PreGeneratedRandom.HasValue)
            rate *= 1 + ctx.PreGeneratedRandom.Value * 0.5 * (ctx.Config.RandomnessMultiplier / 5.0);

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
        // 3-tier discount rules already applied in GetAllCompanyDefinitions
        double rate = company.LoanInterestRate;

        // Stage 7.1/7.2: consecutive sell bonus or decay
        rate += GetConsecutiveAdjustment(ctx, isDeposit: false);

        // Stage 7.4 step 4: luck flat additive (coefficient = max ±%, sign from DailyLuck)
        if (ctx.LuckEnabled && ctx.DailyLuck != 0)
            rate += Math.Sign(ctx.DailyLuck) * ctx.Config.LuckStrengthCoefficient / 100.0;

        // Stage 7.4 step 4.5: random volatility (multiplicative, pre-generated, scaled by multiplier)
        if (ctx.Config.RandomnessMultiplier > 0 && ctx.PreGeneratedRandom.HasValue)
            rate *= 1 + ctx.PreGeneratedRandom.Value * 0.5 * (ctx.Config.RandomnessMultiplier / 5.0);

        // Stage 7.4 step 5: weather bonus
        if (ctx.WeatherEnabled)
            rate += GetWeatherBonus(company, ctx);

        // Stage 7.4 step 6: clamp
        if (!ctx.Config.AllowNegativeInterest)
            rate = Math.Max(0, rate);
        else
            rate = Math.Max(ctx.Config.DepositRateFloor, rate);

        // Hard floor: loan rate never below +1% (V3.5 patch5)
        rate = Math.Max(LoanRateHardFloor, rate);

        return rate;
    }

    /// <summary>
    /// Fallback for companies not found in crop data (e.g. Coffee Bean, fixed companies misrouted here).
    /// Uses config interest rates directly -- no crop-based discount rules applied.
    /// </summary>
    private static double FallbackRate(double configRate, CompanyDefinition company, InterestCalculationContext ctx)
    {
        double rate = configRate;

        rate += GetConsecutiveAdjustment(ctx, isDeposit: true);
        rate -= GetSuppressionDrop(ctx);

        if (ctx.LuckEnabled)
            rate += ctx.DailyLuck * ctx.Config.LuckStrengthCoefficient;

        if (ctx.WeatherEnabled)
            rate += GetWeatherBonus(company, ctx);

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

    private static double GetSuppressionDrop(InterestCalculationContext ctx)
    {
        if (ctx.SuppressionStacks <= 0) return 0;
        return ctx.SuppressionStacks * Math.Abs(ctx.Config.SuppressMaxDropPerDay);
    }

    private static double GetWeatherBonus(CompanyDefinition company, InterestCalculationContext ctx)
    {
        if (ctx.IsLightning) return company.LightningBonus;
        if (ctx.IsRaining) return company.RainBonus;
        if (ctx.IsSnowing) return company.SnowBonus;
        return company.SunBonus;
    }
}
