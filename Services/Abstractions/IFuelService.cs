using BankMod.Data;
using BankMod.Domain;

namespace BankMod.Services.Abstractions;

/// <summary>Manages company fuel inventory and satisfaction rate calculation.
/// Internal fuel stock uses Fuel Points (FP): 1 standard crop = 10 FP.</summary>
public interface IFuelService
{
    /// <summary>Update daily fuel inventory on the given account — subtract daily consumption for all companies.</summary>
    void UpdateDailyInventory(BankAccountData account);

    /// <summary>Get the fuel satisfaction rate for a company.</summary>
    double GetSatisfactionRate(CompanyId companyId);

    /// <summary>Record a shipping bin sale — adds fuel to the matching dynamic company on the given account.</summary>
    void RecordShippingBinSale(BankAccountData account, string cropCode, int quantity, int quality);

    /// <summary>Record an external purchase (buying from shops) — reduces fuel (5 FP per item), capped at 20% fuel/day.</summary>
    void RecordExternalPurchase(BankAccountData account, string cropCode, int quantity);

    /// <summary>Record an external sale (selling to shops) — reduces fuel (5 FP per item), capped at 20% fuel/day.
    /// Returns actual fuel points deducted (0 = suppressed by daily cap or no matching company).</summary>
    int RecordExternalSale(BankAccountData account, string cropCode, int quantity);

    /// <summary>Get fuel stock in display units (FP / 10).</summary>
    double GetDisplayFuelStock(CompanyId companyId);
}
