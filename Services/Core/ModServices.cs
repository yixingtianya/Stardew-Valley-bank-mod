using StardewModdingAPI;

namespace BankMod.Services.Core;

/// <summary>Holds all mod service references for constructor injection into UI and other consumers.
/// Acts as a manual DI container (SMAPI mods don't use standard .NET DI).</summary>
public class ModServices
{
    public Abstractions.ICompanyManager CompanyManager { get; }
    public Abstractions.IInterestCalculator FixedInterestCalculator { get; }
    public Abstractions.IInterestCalculator DynamicInterestCalculator { get; }
    // Backward-compat alias: fixed company strategy is the default
    public Abstractions.IInterestCalculator InterestCalculator => FixedInterestCalculator;
    public Abstractions.IDepositLimitProvider DepositLimitProvider { get; }
    public Abstractions.IBankAccountService BankAccountService { get; }
    public Abstractions.IFuelService FuelService { get; }
    public Abstractions.ILoanService LoanService { get; }
    public Abstractions.IBankruptcyHandler BankruptcyHandler { get; }
    public Abstractions.ICreditLimitService CreditLimitService { get; }
    public Abstractions.IMessageScheduler MessageScheduler { get; }
    public Abstractions.ISeasonalFruitMarket SeasonalFruitMarket { get; }
    public ShipmentTrackingService ShipmentTracking { get; }
    public IMonitor Monitor { get; }

    public ModServices(IModHelper helper, Data.ModConfig config, IMonitor monitor)
    {
        Monitor = monitor;
        BankAccountService = new BankAccountService(helper);

        var fixedInterestCalc = new Interest.FixedCompanyInterestStrategy();
        var dynamicInterestCalc = new Interest.DynamicCompanyInterestStrategy();
        FixedInterestCalculator = fixedInterestCalc;
        DynamicInterestCalculator = dynamicInterestCalc;

        DepositLimitProvider = new DepositLimitProvider();
        LoanService = new LoanService();
        ShipmentTracking = new ShipmentTrackingService();
        FuelService = new FuelService(BankAccountService);

        CompanyManager = new CompanyManager(
            fixedInterestCalc, dynamicInterestCalc,
            BankAccountService, LoanService,
            ShipmentTracking, FuelService,
            config, monitor);

        BankruptcyHandler = new BankruptcyHandler();
        CreditLimitService = new CreditLimitService();
        MessageScheduler = new MessageScheduler();
        SeasonalFruitMarket = new SeasonalFruitMarket();
    }
}
