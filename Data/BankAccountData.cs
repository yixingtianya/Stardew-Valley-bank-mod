namespace BankMod.Data;

/// <summary>Player bank account data stored in save file, supporting per-company accounts and loans.</summary>
public class BankAccountData
{
    public List<CompanyAccount> CompanyAccounts { get; set; } = new();
    public List<LoanRecord> Loans { get; set; } = new();

    /// <summary>Per-crop shipping bin tracking (Stage 6).</summary>
    public List<CropShipmentData> CropShipments { get; set; } = new();

    /// <summary>Dynamically generated companies (Stage 6).</summary>
    public List<DynamicCompanyData> DynamicCompanies { get; set; } = new();

    /// <summary>Number of dynamic companies spawned in the current season (cap: 2).</summary>
    public int DynamicCompaniesSpawnedThisSeason { get; set; }

    /// <summary>Season name when spawn counter was last reset.</summary>
    public string SpawnCounterSeason { get; set; } = "";

    /// <summary>Per-crop competitor suppression tracking (Stage 7.3).</summary>
    public List<CropSuppressionData> CropSuppressions { get; set; } = new();

    // === Stage 7.3: one-time thank-you letters after first successful fuel suppression ===
    public bool PierreLetterSent { get; set; }
    public bool MorrisLetterSent { get; set; }
    public string PierreThanksLetterText { get; set; } = "";
    public string MorrisThanksLetterText { get; set; } = "";

    // Legacy fields — migrated to Loans on load by LoanService
    public int LoanPrincipal { get; set; }
    public int AccumulatedInterest { get; set; }
}

/// <summary>Tracks competitor suppression state for a crop when sold at shops (Stage 7.3).</summary>
public class CropSuppressionData
{
    public string CropCode { get; set; } = "";
    public int Stacks { get; set; }
    public int RemainingDays { get; set; }
}

/// <summary>Per-company deposit account.</summary>
public class CompanyAccount
{
    public string CompanyName { get; set; } = "";
    public int DepositBalance { get; set; }
    public int BaseAmount { get; set; }
    public int AccumulatedInterest { get; set; }

    // === V3.7: Manual compound interest detection ===
    public int[] RecentPrincipalDeltas { get; set; } = new int[7];
    public int WindowDayIndex { get; set; }
    public bool HasFirstIncrease { get; set; }
    public int LastPositiveDelta { get; set; }
    public int WindowPeak { get; set; }
    public int WindowBreakCount { get; set; }
    public int PenaltyDaysRemaining { get; set; }
    public int PreviousBaseAmount { get; set; }
}

/// <summary>Active loan record for a company. Each company can have one active loan at a time.</summary>
public class LoanRecord
{
    /// <summary>Which company this loan belongs to.</summary>
    public string CompanyName { get; set; } = "";

    /// <summary>Remaining principal (may be less than original if partially repaid).</summary>
    public int Principal { get; set; }

    /// <summary>Unpaid interest accumulated since last repayment or loan start.</summary>
    public int AccumulatedInterest { get; set; }

    /// <summary>Effective daily loan interest rate stored at time of borrowing (after discount).</summary>
    public double InterestRate { get; set; }

    /// <summary>Repayment period in days: 7 or 14.</summary>
    public int RepaymentPeriodDays { get; set; }

    /// <summary>Game daysPlayed value of the repayment due date.</summary>
    public int DueDay { get; set; }

    /// <summary>Game daysPlayed value when the loan was first issued.</summary>
    public int LoanStartDay { get; set; }

    /// <summary>Whether the player missed the due-date payment and is in grace/default.</summary>
    public bool IsInDefault { get; set; }

    /// <summary>Remaining grace days before forced collection. Reset to config value on default entry.</summary>
    public int DefaultDaysRemaining { get; set; }
}
