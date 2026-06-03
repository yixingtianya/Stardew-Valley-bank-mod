using BankMod.Data;
using StardewModdingAPI;
using StardewValley;

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

    // Mail bypass: directly show ChineseMailMenu without going through vanilla mailbox
    public bool PendingPierreMail { get; set; }
    public bool PendingMorrisMail { get; set; }

    // 当前会话是否已收到银行欢迎信（GivePhoneToPlayer 设置）
    public bool HasReceivedPhoneInSession { get; set; }

    // Stage 8 bankruptcy: exempt next money increase from garnishment (bank withdrawal)
    public bool ExemptNextMoneyIncrease { get; set; }

    // Stage 13: multiplayer remote operation callback
    public Action<string, string, int, string?>? SendRemoteOperation { get; set; }
    public bool StandaloneMode { get; set; }

    // Stage 14: route flags — forwarded to RouteService
    public string CompletedRoute => RouteService.CompletedRoute;
    public bool OnlineShoppingUnlocked => RouteService.OnlineShoppingUnlocked;
    public bool PierreBoosted => RouteService.PierreBoosted;
    public bool JojaBoosted => RouteService.JojaBoosted;

    // Stage 14: new services
    public Abstractions.IRouteService RouteService { get; }
    public Abstractions.IEventScriptService EventScriptService { get; }
    public Abstractions.IStoreHoursService StoreHoursService { get; }
    public Data.ModConfig Config { get; }

    public ModServices(IModHelper helper, Data.ModConfig config, IMonitor monitor)
    {
        Monitor = monitor;
        Config = config;
        BankAccountService = new BankAccountService(helper);

        var fixedInterestCalc = new Interest.FixedCompanyInterestStrategy();
        var dynamicInterestCalc = new Interest.DynamicCompanyInterestStrategy();
        FixedInterestCalculator = fixedInterestCalc;
        DynamicInterestCalculator = dynamicInterestCalc;

        DepositLimitProvider = new DepositLimitProvider();
        LoanService = new LoanService();
        ShipmentTracking = new ShipmentTrackingService();
        FuelService = new FuelService(BankAccountService);
        BankruptcyHandler = new BankruptcyHandler();

        CompanyManager = new CompanyManager(
            fixedInterestCalc, dynamicInterestCalc,
            BankAccountService, LoanService,
            BankruptcyHandler,
            ShipmentTracking, FuelService,
            config, monitor);

        CreditLimitService = new CreditLimitService();
        MessageScheduler = new MessageScheduler();
        SeasonalFruitMarket = new SeasonalFruitMarket();
        RouteService = new RouteService(CompanyManager, BankAccountService, config, monitor);
        // Wire display name resolver so CompanyManager uses localized names
        var cm = CompanyManager as CompanyManager;
        if (cm != null) cm.DisplayNameResolver = RouteService.GetDisplayName;
        EventScriptService = new EventScriptService(helper, RouteService, monitor);
        StoreHoursService = new StoreHoursService(helper, monitor, RouteService);
    }

    /// <summary>
    /// 统一的感谢信触发方法 - Pierre 和 Morris 信都用这个方法
    /// 除了触发源不同，逻辑完全一致
    /// 注意：不自行 Load/Save account，由调用方统一管理持久化，避免覆盖问题
    /// </summary>
    /// <param name="account">银行账户数据（由调用方 Load 和 Save）</param>
    /// <param name="letterType">"pierre" 或 "morris"</param>
    /// <param name="cropCode">作物代码</param>
    /// <param name="cropDisplay">作物显示名</param>
    /// <param name="competitor">竞争对手名称</param>
    /// <param name="hasBankLetter">银行欢迎信是否已接收（以银行欢迎信为标志）</param>
    public bool TriggerThanksLetter(BankAccountData account, string letterType, string cropCode, string cropDisplay, string competitor, bool hasBankLetter)
    {
        bool alreadySent = letterType == "pierre" ? account.PierreLetterSent : account.MorrisLetterSent;

        if (alreadySent)
        {
            Monitor.Log($"[MailDebug] {letterType} letter already sent, skipping", LogLevel.Info);
            return false;
        }

        // 检测银行欢迎信是否已发送（以银行欢迎信为标志）
        if (!hasBankLetter)
        {
            Monitor.Log($"[MailDebug] BankMod_Letter not received yet, cannot trigger {letterType} letter", LogLevel.Warn);
            return false;
        }

        if (letterType == "pierre")
        {
            account.PierreThanksLetterText =
                I18n.Get("svc.1")
                + I18n.Get("svc.2")
                + I18n.Get("svc.3") + cropDisplay + I18n.Get("svc.4")
                + I18n.Get("svc.5") + competitor + I18n.Get("svc.6")
                + I18n.Get("svc.7") + competitor + I18n.Get("svc.8")
                + I18n.Get("svc.9")
                + I18n.Get("svc.10")
                + I18n.Get("svc.11");
            account.PierreLetterSent = true;
            PendingPierreMail = true;
            Monitor.Log($"[MailDebug] Pierre letter: saved, pending delivery flag set", LogLevel.Info);
            Monitor.Log($"[Stage7] Pierre thanks letter queued: {cropDisplay}", LogLevel.Info);
        }
        else
        {
            account.MorrisThanksLetterText =
                I18n.Get("svc.12")
                + I18n.Get("svc.13")
                + I18n.Get("svc.14") + cropDisplay + I18n.Get("svc.15")
                + I18n.Get("svc.16") + competitor + I18n.Get("svc.17")
                + I18n.Get("svc.18")
                + I18n.Get("svc.19")
                + I18n.Get("svc.20")
                + I18n.Get("svc.21");
            account.MorrisLetterSent = true;
            PendingMorrisMail = true;
            Monitor.Log($"[MailDebug] Morris letter: saved, pending delivery flag set", LogLevel.Info);
            Monitor.Log($"[JojaSupply] Morris thanks letter queued: {cropDisplay}", LogLevel.Info);
        }

        return true;
    }
}
