namespace BankMod.Domain;

/// <summary>Immutable crop data record sourced from Crops values.txt — the single source of truth for dynamic company interest and fuel parameters.</summary>
public class CropDataRecord
{
    /// <summary>In-game crop ID code (e.g. "Blueberry", "Ancient Fruit").</summary>
    public string CropCode { get; init; } = "";

    /// <summary>i18n key for localized display name (e.g. "fbn.147").</summary>
    public string NameKey { get; init; } = "";

    /// <summary>Localized display name (resolved at runtime via I18n).</summary>
    public string DisplayName => I18n.Get(NameKey);

    /// <summary>Whether this crop's seeds can be purchased from shops.</summary>
    public bool IsPurchasable { get; init; } = true;

    /// <summary>Base daily interest rate R = equivalentDailyProfit / equivalentCost.</summary>
    public double R { get; init; }

    /// <summary>Monthly yield per plot (units/month/plant).</summary>
    public double Y { get; init; }

    /// <summary>Base daily fuel consumption D_base = (5/3) × Y.</summary>
    public double DBase { get; init; }

    /// <summary>Warehouse capacity Smax = Y × 10.</summary>
    public double Smax { get; init; }

    /// <summary>Equivalent seed cost (seed purchase price, or sell price ÷ 2 for non-purchasable crops).</summary>
    public double EquivalentCost { get; init; }

    /// <summary>Season(s) this crop grows in (e.g. "Spring", "Summer・Fall").</summary>
    public string Season { get; init; } = "";
}
