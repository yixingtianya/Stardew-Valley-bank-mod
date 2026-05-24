namespace BankMod.Messages;

/// <summary>Config sync: host sends to clients on connect.</summary>
[Serializable]
public class ConfigSyncMessage
{
    public double JojaDepositRate { get; set; }
    public double JojaLoanRate { get; set; }
    public double PierreDepositRate { get; set; }
    public double PierreLoanRate { get; set; }
    public bool EnableLuckInfluence { get; set; }
    public bool EnableWeatherInfluence { get; set; }
    public bool UseCompoundInterest { get; set; }
    public int CompanySpawnRequiredSellCount { get; set; }
    public double BankruptcyIncomeDeduction { get; set; }
}

/// <summary>Financial operation request from client to host.</summary>
[Serializable]
public class BankOperationRequest
{
    public string Operation { get; set; } = ""; // Deposit, Withdraw, Borrow7, Borrow14, Repay, Transfer
    public string CompanyName { get; set; } = "";
    public int Amount { get; set; }
    public long SenderId { get; set; }
    public string? TargetPlayer { get; set; } // for transfers
}

/// <summary>Data sync broadcast from host to all clients after an operation.</summary>
[Serializable]
public class BankDataSync
{
    public long SenderId { get; set; }
    public string CompanyName { get; set; } = "";
    public int NewDepositBalance { get; set; }
    public int NewLoanPrincipal { get; set; }
}

/// <summary>Daily snapshot broadcast from host after OnDayStarted.</summary>
[Serializable]
public class BankSnapshot
{
    public List<CompanySnapshot> Companies { get; set; } = new();
    public bool IsInBankruptcy { get; set; }
    public int PlayerMoney { get; set; }
}

[Serializable]
public class CompanySnapshot
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public double DepositRate { get; set; }
    public double LoanRate { get; set; }
    public int DepositBalance { get; set; }
    public int LoanPrincipal { get; set; }
}
