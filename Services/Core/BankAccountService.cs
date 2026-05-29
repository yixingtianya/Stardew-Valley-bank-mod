using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;

namespace BankMod.Services.Core;

/// <summary>Manages bank account CRUD operations. Thin wrapper over save-data persistence
/// with an in-memory cache to avoid repeated JSON deserialization per tick.</summary>
public class BankAccountService : IBankAccountService
{
    private readonly StardewModdingAPI.IModHelper _helper;
    private BankAccountData? _cache;

    public BankAccountService(StardewModdingAPI.IModHelper helper)
    {
        _helper = helper;
    }

    public BankAccountData Load()
    {
        if (_cache != null) return _cache;

        var data = _helper.Data.ReadSaveData<BankAccountData>("bankmod_account_data") ?? new BankAccountData();

        // Migrate legacy top-level loan fields to per-company LoanRecord
        if (data.LoanPrincipal > 0 || data.AccumulatedInterest > 0)
        {
            if (data.Loans.Count == 0)
            {
                data.Loans.Add(new LoanRecord
                {
                    CompanyName = I18n.Get("mod.191"),
                    Principal = data.LoanPrincipal,
                    AccumulatedInterest = data.AccumulatedInterest,
                    InterestRate = 0.04,
                    RepaymentPeriodDays = 7,
                    DueDay = (int)StardewValley.Game1.stats.DaysPlayed + 7,
                    LoanStartDay = (int)StardewValley.Game1.stats.DaysPlayed
                });
            }
            data.LoanPrincipal = 0;
            data.AccumulatedInterest = 0;
        }

        _cache = data;
        return data;
    }

    public void InvalidateCache()
    {
        _cache = null;
    }

    public void Save(BankAccountData data)
    {
        _cache = data;
        _helper.Data.WriteSaveData("bankmod_account_data", data);
    }

    public CompanyAccount GetOrCreateAccount(BankAccountData data, string companyName, string originalName)
    {
        var ca = data.CompanyAccounts.FirstOrDefault(a =>
            a.CompanyName == companyName || a.CompanyName == originalName);
        if (ca is null)
        {
            ca = new CompanyAccount { CompanyName = originalName };
            data.CompanyAccounts.Add(ca);
        }
        if (ca.BaseAmount == 0 && ca.DepositBalance > 0)
            ca.BaseAmount = ca.DepositBalance - ca.AccumulatedInterest;
        return ca;
    }
}
