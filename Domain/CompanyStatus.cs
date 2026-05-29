namespace BankMod.Domain;

/// <summary>Company operational status derived from fuel satisfaction rate.</summary>
public enum CompanyStatus
{
    /// <summary>Fuel satisfaction > 150%. Highest rates, highest bankruptcy risk.</summary>
    Prosperous,

    /// <summary>Fuel satisfaction 80%-150%. Moderate rates, lowest bankruptcy risk.</summary>
    Stable,

    /// <summary>Fuel satisfaction 30%-80%. Reduced rates, rising risk.</summary>
    Hungry,

    /// <summary>Fuel satisfaction < 30%. Near-zero rates, bankruptcy imminent.</summary>
    Dying,

    /// <summary>First time entering Dying — 7-day protection period.</summary>
    Protection,

    /// <summary>Company has been liquidated.</summary>
    Bankrupt,

    /// <summary>Newly spawned, 14-day immunity.</summary>
    New,

    /// <summary>Compound interest is in cooldown (112 days).</summary>
    CompoundCooldown
}
