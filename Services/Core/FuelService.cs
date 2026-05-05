using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;

namespace BankMod.Services.Core;

/// <summary>Fuel inventory service: tracks per-company fuel stock in Fuel Points (FP),
/// calculates satisfaction rates, and handles daily consumption.</summary>
public class FuelService : IFuelService
{
    private readonly IBankAccountService _accountService;

    // V3.3 Fuel constants: 1 standard crop = 10 FP internally, display = FP / 10
    private const int FPPerCrop = 10;
    private const int QualityIridium = 4;
    private const int QualityGold = 2;
    private const int IridiumBonus = 10;
    private const int GoldBonus = 5;
    private const int ExternalPenalty = 5;

    public FuelService(IBankAccountService accountService)
    {
        _accountService = accountService;
    }

    public void RecordShippingBinSale(BankAccountData account, string cropCode, int quantity, int quality)
    {
        var company = account.DynamicCompanies.FirstOrDefault(c => c.CropCode == cropCode);
        if (company is null) return;

        var cropData = CropDataProvider.GetByCode(cropCode);
        if (cropData is null) return;

        int fpGain = quantity * FPPerCrop;
        if (quality == QualityIridium) fpGain += quantity * IridiumBonus;
        else if (quality == QualityGold) fpGain += quantity * GoldBonus;

        company.FuelStock += fpGain;

        int smaxFP = (int)(cropData.Smax * FPPerCrop);
        if (company.FuelStock > smaxFP)
            company.FuelStock = smaxFP;
    }

    public void RecordExternalPurchase(string cropCode, int quantity)
    {
        AdjustFuelStock(cropCode, -quantity * ExternalPenalty);
    }

    public void RecordExternalSale(string cropCode, int quantity)
    {
        AdjustFuelStock(cropCode, -quantity * ExternalPenalty);
    }

    public void UpdateDailyInventory(BankAccountData account)
    {
        foreach (var company in account.DynamicCompanies)
        {
            if (company.Status == CompanyStatus.Bankrupt) continue;

            var cropData = CropDataProvider.GetByCode(company.CropCode);
            if (cropData is null) continue;

            double statusCoefficient = company.Status switch
            {
                CompanyStatus.Prosperous => 1.0,
                CompanyStatus.Stable => 0.8,
                CompanyStatus.New => 0.8,
                CompanyStatus.Hungry => 0.5,
                CompanyStatus.Dying => 0.3,
                CompanyStatus.Protection => 0.3,
                _ => 0.8
            };

            int dailyConsumption = (int)(cropData.DBase * statusCoefficient * FPPerCrop);
            company.FuelStock -= dailyConsumption;
            if (company.FuelStock < 0) company.FuelStock = 0;
        }
    }

    public double GetSatisfactionRate(CompanyId companyId)
    {
        var account = _accountService.Load();
        var company = account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == companyId.Value);
        if (company is null) return 1.0;

        var cropData = CropDataProvider.GetByCode(company.CropCode);
        if (cropData is null) return 1.0;

        double demand = cropData.DBase * FPPerCrop;
        if (demand <= 0) return 1.0;

        return company.FuelStock / demand;
    }

    public double GetDisplayFuelStock(CompanyId companyId)
    {
        var account = _accountService.Load();
        var company = account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == companyId.Value);
        return company is null ? 0 : company.FuelStock / 10.0;
    }

    private void AdjustFuelStock(string cropCode, int delta)
    {
        var account = _accountService.Load();
        var company = account.DynamicCompanies.FirstOrDefault(c => c.CropCode == cropCode);
        if (company is null) return;

        company.FuelStock += delta;
        if (company.FuelStock < 0) company.FuelStock = 0;

        _accountService.Save(account);
    }
}
