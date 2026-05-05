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
}
