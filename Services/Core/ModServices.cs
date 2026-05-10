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
                "皮埃尔杂货店 的祝贺信^发件人：皮埃尔^主题：感谢你，我的朋友！^^"
                + "亲爱的 @，^^"
                + "我真不知道该如何感谢你！你最近卖给我的这批 " + cropDisplay + "，质量和价格都太棒了。^^"
                + "靠着你这批货，我稍微调整了一下售价，就成功把顾客从 " + competitor + " 那儿吸引了过来。你没看到，他们店里的老主顾都开始往我这儿跑了。^^"
                + "我听说为了留住最后几个客户，" + competitor + " 正咬着牙用比成本还低的价格在抛售存货。啧啧，这简直是在烧自己的\"燃料\"来续命。^^^"
                + "这，就是商业。^^"
                + "再次感谢你的鼎力相助，你永远是杂货店最尊贵的朋友！^^"
                + "——皮埃尔";
            account.PierreLetterSent = true;
            PendingPierreMail = true;
            Monitor.Log($"[MailDebug] Pierre letter: saved, pending delivery flag set", LogLevel.Info);
            Monitor.Log($"[Stage7] Pierre thanks letter queued: {cropDisplay}", LogLevel.Info);
        }
        else
        {
            account.MorrisThanksLetterText =
                "Joja超市 的祝贺信^发件人：Joja 客户关系部（莫里斯）^主题：高效合作，共赢未来！^^"
                + "尊敬的 @，合伙人：^^"
                + "您近期向 Joja 超市供应的 " + cropDisplay + "，已为我们创造了显著的竞争优势。^^"
                + "通过将您的优质供货纳入我们的价格策略，Joja 超市已成功夺取 " + competitor + " 约 34.7% 的核心客群。直接导致了对方启动\"紧急客户挽留计划\"——也就是不计成本地降价抛售，其燃料储备正在迅速枯竭。^^^"
                + "这在 Joja 内部被称为\"战略性燃料消耗\"。您的存在，让我们的胜利更加轻而易举。^^"
                + "请继续向我们供货。更大的市场份额，与您分享。^^"
                + "效率至真。^^"
                + "——Joja Mart · 莫里斯";
            account.MorrisLetterSent = true;
            PendingMorrisMail = true;
            Monitor.Log($"[MailDebug] Morris letter: saved, pending delivery flag set", LogLevel.Info);
            Monitor.Log($"[JojaSupply] Morris thanks letter queued: {cropDisplay}", LogLevel.Info);
        }

        return true;
    }
}
