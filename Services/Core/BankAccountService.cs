using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;

namespace BankMod.Services.Core;

/// <summary>Manages bank account CRUD operations. Thin wrapper over save-data persistence.</summary>
public class BankAccountService : IBankAccountService
{
    private readonly StardewModdingAPI.IModHelper _helper;

    public BankAccountService(StardewModdingAPI.IModHelper helper)
    {
        _helper = helper;
    }

    public BankAccountData Load()
    {
        var data = _helper.Data.ReadSaveData<BankAccountData>("bankmod_account_data") ?? new BankAccountData();

        // Migrate legacy top-level loan fields to per-company LoanRecord
        if (data.LoanPrincipal > 0 || data.AccumulatedInterest > 0)
        {
            // Find a target company: prefer first company in config, or create a generic one
            if (data.Loans.Count == 0)
            {
                data.Loans.Add(new LoanRecord
                {
                    CompanyName = "Joja超市",
                    Principal = data.LoanPrincipal,
                    AccumulatedInterest = data.AccumulatedInterest,
                    InterestRate = 0.04, // default Joja rate
                    RepaymentPeriodDays = 7,
                    DueDay = (int)StardewValley.Game1.stats.DaysPlayed + 7,
                    LoanStartDay = (int)StardewValley.Game1.stats.DaysPlayed
                });
            }
            data.LoanPrincipal = 0;
            data.AccumulatedInterest = 0;
        }

        return data;
    }

    public void Save(BankAccountData data)
    {
        _helper.Data.WriteSaveData("bankmod_account_data", data);
    }

    public CompanyAccount GetOrCreateAccount(BankAccountData data, string companyName)
    {
        var ca = data.CompanyAccounts.FirstOrDefault(a => a.CompanyName == companyName);
        if (ca is null)
        {
            ca = new CompanyAccount { CompanyName = companyName };
            data.CompanyAccounts.Add(ca);
        }
        if (ca.BaseAmount == 0 && ca.DepositBalance > 0)
            ca.BaseAmount = ca.DepositBalance - ca.AccumulatedInterest;
        return ca;
    }
}
