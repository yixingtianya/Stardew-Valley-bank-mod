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

    // === Stage 8: Debt & Bankruptcy tracking ===
    /// <summary>Whether the player is in the bankruptcy protection state.</summary>
    public bool IsInBankruptcy { get; set; }

    /// <summary>Whether the first-time bankruptcy warning popup has been shown this cycle.</summary>
    public bool BankruptcyWarningShown { get; set; }

    /// <summary>Whether the player is in principal-debt state (fixed company loan maturity missed).</summary>
    public bool IsInPrincipalDebt { get; set; }

    /// <summary>Whether the player is in interest-debt state (dynamic company daily interest unpaid).</summary>
    public bool IsInInterestDebt { get; set; }

    // Stage 15: deposit compound interest (per-save toggle)
    public bool UseCompoundInterest { get; set; } = false;
    public int CompoundDurationDays { get; set; } = 14;
    /// <summary>Migration version: 0 = not yet initialized from config, 1 = initialized.</summary>
    public int CompoundConfigVersion { get; set; }
    public string CompoundActiveCompany { get; set; } = "";
    public int CompoundDaysRemaining { get; set; }
    public int CompoundCooldownDays { get; set; }

    // Stage 15: FBN true/false bankruptcy news
    public string FbnSeason { get; set; } = "";
    public int FbnLastEventDay { get; set; }
    public bool FbnTrueUsed { get; set; }
    public int FbnFalseUsed { get; set; }
    public int FbnFalseQuota { get; set; }
    public string FbnTempBoostCompany { get; set; } = "";
    public string FbnEventCompany { get; set; } = "";
    public bool FbnEventIsReal { get; set; }
    public bool FbnEventCompanyDied { get; set; }
    public int FbnEventDay { get; set; }
    public bool FbnShowOutcome { get; set; }

    // Stage 15 NPC dialogue tracking
    public List<string> DebtNpcSpoken { get; set; } = new();
    public List<string> BankruptcyNpcGifted { get; set; } = new();
    public List<string> CongratsNpcSpoken { get; set; } = new();
    public bool DebtClearedCongratsShown { get; set; }

    // Legacy fields — migrated to Loans on load by LoanService
    public int LoanPrincipal { get; set; }
    public int AccumulatedInterest { get; set; }

    // === Season interest log ===
    /// <summary>Season key when the interest log was last reset (e.g. "Spring_Year1").</summary>
    public string InterestLogSeason { get; set; } = "";
    /// <summary>Per-day interest records for the current season, used by the interest log viewer.</summary>
    public List<DailyInterestRecord> SeasonInterestLog { get; set; } = new();
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
    /// <summary>AccumulatedInterest value at end of previous day's processing.
    /// Used to detect AccInt decreases (interest → principal conversion).</summary>
    public int PreviousAccumulatedInterest { get; set; }
    /// <summary>Whether AccumulatedInterest decreased at any point during the current 7-day window.
    /// Manual compound interest requires interest to be withdrawn and re-deposited as principal,
    /// which causes AccInt to decrease. Pure farming deposits never decrease AccInt.</summary>
    public bool AccIntDecreasedInWindow { get; set; }
}

/// <summary>A single day's interest record for one company.</summary>
public class DailyInterestRecord
{
    /// <summary>Game day number (DaysPlayed).</summary>
    public int Day { get; set; }
    /// <summary>Season day (1-28).</summary>
    public int SeasonDay { get; set; }
    /// <summary>Company name (stable English identifier).</summary>
    public string CompanyName { get; set; } = "";
    /// <summary>Effective deposit interest rate on this day.</summary>
    public double Rate { get; set; }
    /// <summary>Interest earned on this day.</summary>
    public int Interest { get; set; }
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

    /// <summary>Overdue daily interest that couldn't be deducted from player cash (Stage 8 interest debt).</summary>
    public int OverdueInterest { get; set; }

    /// <summary>Whether this loan is in interest-debt state (daily interest deduction failed).</summary>
    public bool IsInInterestDebt { get; set; }

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

    /// <summary>Whether this loan is frozen (bankrupt — no interest, no due date, infinite term).</summary>
    public bool IsFrozen { get; set; }

    /// <summary>Whether this loan was transferred from another company (doesn't count against the receiving company's loan limit).</summary>
    public bool IsTransferred { get; set; }
}
