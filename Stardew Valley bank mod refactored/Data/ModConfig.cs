namespace BankMod.Data;

/// <summary>Mod configuration read from config.json — all parameters aligned with value.txt V1.0.</summary>
public class ModConfig
{
    /*********
    ** 固定公司
    *********/
    public List<CompanyDefinition> Companies { get; set; } = new()
    {
        new()
        {
            OriginalName = "JojaMart",
            Name = "JojaMart",
            DepositInterestRate = 0.025,
            DepositLimit = 2000000,
            LoanInterestRate = 0.04,
            LoanLimit = 300000,
            SunBonus = 0.0001,
            RainBonus = -0.0001,
            SnowBonus = 0.0001,
            LightningBonus = 0.0
        },
        new()
        {
            OriginalName = "Pierre's General Store",
            Name = "Pierre's General Store",
            DepositInterestRate = 0.015,
            DepositLimit = 1000000,
            LoanInterestRate = 0.025,
            LoanLimit = 200000,
            SunBonus = 0.0,
            RainBonus = 0.0002,
            SnowBonus = -0.0001,
            LightningBonus = -0.0005
        }
    };

    /*********
    ** 利率计算选项
    *********/
    public bool UseCompoundInterest { get; set; } = false;
    public int CompoundDurationDays { get; set; } = 14; // 10-21
    public bool AllowNegativeInterest { get; set; } = false;
    public double DepositRateFloor { get; set; } = -0.02;

    /*********
    ** 运气影响
    *********/
    public bool EnableLuckInfluence { get; set; } = true;
    public double LuckStrengthCoefficient { get; set; } = 2.5;

    /*********
    ** 天气影响
    *********/
    public bool EnableWeatherInfluence { get; set; } = true;

    /*********
    ** 动态公司
    *********/
    public int CompanySpawnRequiredSellCount { get; set; } = 50;
    public int CompanySellTrackingWindowDays { get; set; } = 7;
    public double LoanRateHardCap { get; set; } = 0.15;
    public int DepositLoanCoefficient { get; set; } = 1000;

    /*********
    ** 连续售卖加成
    *********/
    public double ConsecutiveSellDepositBonusPerDay { get; set; } = 0.005;
    public double ConsecutiveSellDepositBonusCap { get; set; } = 0.05;
    public double ConsecutiveSellLoanBonusPerDay { get; set; } = 0.01;
    public double ConsecutiveSellLoanBonusCap { get; set; } = 0.10;
    public double SellDecayDepositPerDay { get; set; } = 0.005;
    public double SellDecayLoanPerDay { get; set; } = 0.01;

    /*********
    ** 竞争打压
    *********/
    public double SuppressMaxDropPerDay { get; set; } = -0.02;
    public int SuppressEffectDurationDays { get; set; } = 5;
    public int SuppressMaxStacks { get; set; } = 3;

    /*********
    ** 贷款与还款
    *********/
    public double LongTermRateDiscount { get; set; } = 0.005;
    public int PrincipalDebtGraceDays { get; set; } = 1;
    public double PenaltyInterestRate { get; set; } = 0.20;

    /*********
    ** 破产与欠债
    *********/
    public double BankruptcyIncomeDeduction { get; set; } = 0.50;

    /*********
    ** 债券交易（阶段九）
    *********/
    public double DebtTransferRateDiscount { get; set; } = 0.50;
    public int DebtTransferNewRepaymentDays { get; set; } = 28;

    /*********
    ** 公司倒闭与清算（阶段九）
    *********/
    public double ProsperousAnnualRisk { get; set; } = 0.30; // 15%/22%/30%/40%/50%
    public bool DisableNaturalBankruptcy { get; set; } = false; // casual mode
    public int ProsperousLoanLimitMult { get; set; } = 10;
    public int StableLoanLimitMult { get; set; } = 9;
    public int HungryLoanLimitMult { get; set; } = 6;
    public int DyingLoanLimitMult { get; set; } = 3;
    public double ProsperousReturnRate { get; set; } = 0.80;
    public double StableReturnRate { get; set; } = 0.50;
    public double HungryReturnRate { get; set; } = 0.0;
    public double DyingReturnRate { get; set; } = 0.0;
    public double RescueInvestmentCapRatio { get; set; } = 0.50;

    /*********
    ** 借款上限
    *********/
    public double BorrowingLeverageCoefficient { get; set; } = 2.0;
    public double FixedCompanyQuotaRatio { get; set; } = 0.60;

    /*********
    ** 多人模式
    *********/
    public string TransferFeeMode { get; set; } = "percentage";
    public double TransferFeeValue { get; set; } = 0.01;

    /*********
    ** 网购与运费（阶段十四）
    *********/
    public bool EnableDeliveryFee { get; set; } = true;
    public double DeliveryFeePercentage { get; set; } = 0.05;
    public int DeliveryFeeFlat { get; set; } = 0;
    public List<string> OnlineShopList { get; set; } = new() { "SeedShop", "Blacksmith", "Carpenter", "Joja", "IslandTrade", "DesertTrade", "Sandy", "QiGemShop" };

    /*********
    ** 显示设置
    *********/
    public int RateDisplayPrecision { get; set; } = 2;
    public bool ShowRateChangeNotification { get; set; } = true;
    public bool ShowMorningInterestNotification { get; set; } = true;
    public bool ShowCompanyDangerWarning { get; set; } = true;

    /*********
    ** 移动端触摸输入
    *********/
    public bool EnableTouchOverlay { get; set; } = true;
    public int TouchButtonSize { get; set; } = 64;
    public string TouchOverlayPosition { get; set; } = "Auto";
    public string OpenBankKey { get; set; } = "MouseRight";

    /// <summary>Deep-clone the config for snapshot/backup purposes.</summary>
    public ModConfig Clone()
    {
        return new ModConfig
        {
            Companies = Companies.Select(c => c.Clone()).ToList(),
            UseCompoundInterest = UseCompoundInterest,
            CompoundDurationDays = CompoundDurationDays,
            AllowNegativeInterest = AllowNegativeInterest,
            DepositRateFloor = DepositRateFloor,
            EnableLuckInfluence = EnableLuckInfluence,
            LuckStrengthCoefficient = LuckStrengthCoefficient,
            EnableWeatherInfluence = EnableWeatherInfluence,
            CompanySpawnRequiredSellCount = CompanySpawnRequiredSellCount,
            CompanySellTrackingWindowDays = CompanySellTrackingWindowDays,
            LoanRateHardCap = LoanRateHardCap,
            DepositLoanCoefficient = DepositLoanCoefficient,
            ConsecutiveSellDepositBonusPerDay = ConsecutiveSellDepositBonusPerDay,
            ConsecutiveSellDepositBonusCap = ConsecutiveSellDepositBonusCap,
            ConsecutiveSellLoanBonusPerDay = ConsecutiveSellLoanBonusPerDay,
            ConsecutiveSellLoanBonusCap = ConsecutiveSellLoanBonusCap,
            SellDecayDepositPerDay = SellDecayDepositPerDay,
            SellDecayLoanPerDay = SellDecayLoanPerDay,
            SuppressMaxDropPerDay = SuppressMaxDropPerDay,
            SuppressEffectDurationDays = SuppressEffectDurationDays,
            SuppressMaxStacks = SuppressMaxStacks,
            LongTermRateDiscount = LongTermRateDiscount,
            PrincipalDebtGraceDays = PrincipalDebtGraceDays,
            PenaltyInterestRate = PenaltyInterestRate,
            BankruptcyIncomeDeduction = BankruptcyIncomeDeduction,
            DebtTransferRateDiscount = DebtTransferRateDiscount,
            DebtTransferNewRepaymentDays = DebtTransferNewRepaymentDays,
            ProsperousLoanLimitMult = ProsperousLoanLimitMult,
            StableLoanLimitMult = StableLoanLimitMult,
            HungryLoanLimitMult = HungryLoanLimitMult,
            DyingLoanLimitMult = DyingLoanLimitMult,
            ProsperousReturnRate = ProsperousReturnRate,
            StableReturnRate = StableReturnRate,
            HungryReturnRate = HungryReturnRate,
            DyingReturnRate = DyingReturnRate,
            RescueInvestmentCapRatio = RescueInvestmentCapRatio,
            BorrowingLeverageCoefficient = BorrowingLeverageCoefficient,
            FixedCompanyQuotaRatio = FixedCompanyQuotaRatio,
            TransferFeeMode = TransferFeeMode,
            TransferFeeValue = TransferFeeValue,
            EnableDeliveryFee = EnableDeliveryFee,
            DeliveryFeePercentage = DeliveryFeePercentage,
            DeliveryFeeFlat = DeliveryFeeFlat,
            OnlineShopList = new List<string>(OnlineShopList),
            RateDisplayPrecision = RateDisplayPrecision,
            ShowRateChangeNotification = ShowRateChangeNotification,
            ShowMorningInterestNotification = ShowMorningInterestNotification,
            ShowCompanyDangerWarning = ShowCompanyDangerWarning,
            EnableTouchOverlay = EnableTouchOverlay,
            TouchButtonSize = TouchButtonSize,
            TouchOverlayPosition = TouchOverlayPosition,
            OpenBankKey = OpenBankKey
        };
    }
}

/// <summary>Definition of a bank company with interest rates, limits, and per-company weather effects.</summary>
public class CompanyDefinition
{
    /// <summary>Stable internal identifier (always the original config name, e.g. "JojaMart", "Pierre's General Store"). Never translated.</summary>
    public string OriginalName { get; set; } = "";

    /// <summary>Display name shown in UI (may be translated via i18n).</summary>
    public string Name { get; set; } = "";
    public double DepositInterestRate { get; set; }
    public double LoanInterestRate { get; set; }
    public int DepositLimit { get; set; }
    public int LoanLimit { get; set; }
    public double RainBonus { get; set; }
    public double SunBonus { get; set; }
    public double SnowBonus { get; set; }
    public double LightningBonus { get; set; }

    /// <summary>Crop code for CropDataProvider lookup (e.g. "Potato"). Null for fixed companies.</summary>
    public string? CropCode { get; set; }

    /// <summary>Whether this is a dynamically generated company.</summary>
    public bool IsDynamic { get; set; }

    public CompanyDefinition Clone()
    {
        return new CompanyDefinition
        {
            OriginalName = OriginalName,
            Name = Name,
            DepositInterestRate = DepositInterestRate,
            LoanInterestRate = LoanInterestRate,
            DepositLimit = DepositLimit,
            LoanLimit = LoanLimit,
            RainBonus = RainBonus,
            SunBonus = SunBonus,
            SnowBonus = SnowBonus,
            LightningBonus = LightningBonus,
            CropCode = CropCode,
            IsDynamic = IsDynamic
        };
    }
}
