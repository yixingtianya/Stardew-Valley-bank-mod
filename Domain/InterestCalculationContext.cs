using BankMod.Data;

namespace BankMod.Domain;

/// <summary>Contextual data needed for interest rate calculation.</summary>
public class InterestCalculationContext
{
    public CompanyDefinition Company { get; init; } = null!;
    public double DailyLuck { get; init; }
    public bool IsLightning { get; init; }
    public bool IsRaining { get; init; }
    public bool IsSnowing { get; init; }
    public ModConfig Config { get; init; } = null!;

    /// <summary>Whether weather influence is enabled and applicable.</summary>
    public bool WeatherEnabled => Config.EnableWeatherInfluence;

    /// <summary>Whether luck influence is enabled and applicable.</summary>
    public bool LuckEnabled => Config.EnableLuckInfluence;

    /// <summary>Consecutive days the crop has been sold via shipping bin (Stage 7.1).</summary>
    public int ConsecutiveSellDays { get; init; }

    /// <summary>Active competitor suppression stacks (Stage 7.3).</summary>
    public int SuppressionStacks { get; init; }

    /// <summary>Whether the crop has ever been sold via shipping bin. Gates Stage 7.2 decay.</summary>
    public bool HasSoldHistory { get; init; }

    /// <summary>Consecutive days without selling (Stage 7.2 cumulative decay).</summary>
    public int DecayDays { get; init; }

    /// <summary>Whether this account is under V3.7 manual-compound penalty (rate × 0.10).</summary>
    public bool IsPenaltyPeriod { get; init; }
}
