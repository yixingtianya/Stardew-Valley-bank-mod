using BankMod.Domain;

namespace BankMod.Data;

/// <summary>Runtime data for a dynamically generated company, persisted in save data.</summary>
public class DynamicCompanyData
{
    public string CompanyName { get; set; } = "";
    public string CropCode { get; set; } = "";
    public int GenerationDay { get; set; }
    public int ProtectionEndDay { get; set; }
    public CompanyStatus Status { get; set; } = CompanyStatus.New;
    public int DepositLimit { get; set; }
    public int LoanLimit { get; set; }
    /// <summary>Internal fuel stock in Fuel Points (FP). Divide by 10 for display units.</summary>
    public int FuelStock { get; set; }
    public string? BankruptcySeason { get; set; }
    public int BankruptcyYear { get; set; }
    /// <summary>Accumulated fuel penalty (FP) from external sale/purchase today. Reset daily.</summary>
    public int DailyExternalFuelPenalty { get; set; }

    // === Stage 9: Rescue investment & asset pool tracking ===
    /// <summary>Remaining restructuring days (0 = not restructuring).</summary>
    public int RestructuringDaysRemaining { get; set; }

    /// <summary>Total rescue shares purchased this restructuring cycle (max 7).</summary>
    public int TotalRescueSharesPurchased { get; set; }

    /// <summary>Total rescue investment amount (doubled on revival, cleared on liquidation).</summary>
    public int PendingRescueDeposit { get; set; }

    /// <summary>Amount already withdrawn from the asset pool.</summary>
    public int AssetPoolConsumed { get; set; }
}
