using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;
using BankMod.Services.Core;
using BankMod.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Objects;

namespace BankMod;

/// <summary>The mod entry point — thin composition root that wires SMAPI events to domain services.</summary>
internal sealed class BankMod : Mod
{
    /*********
    ** Constants
    *********/
    private const string PhoneReceivedFlag = "bankmod_phone_status";

    // V3.3 联机消息类型常量
    private const string MsgConfigSync = "ConfigSync";
    private const string MsgBankOperation = "BankOperation";
    private const string MsgBankDataSync = "BankDataSync";
    private const string MsgBankSnapshot = "BankSnapshot";

    /*********
    ** Fields
    *********/
    private ModConfig _config = new();
    private ModServices _services = null!;
    private bool _phoneReceivedToday;
    private string? _lastLocationName;
    private string? _lastReadMail;
    private bool _hasReceivedPhoneInSession;
    private int _postSaveStep;
    private string _gmcmWarning = string.Empty;
    private IGenericModConfigMenuApi? _gmcmApi;
#pragma warning disable CS0169
    private bool _lastKnownSeparateWallets; // V3.3 勘误六：分家状态变化检测（阶段十三实现）
#pragma warning restore CS0169
    private Dictionary<string, int>? _preShopInventory; // Stage 7.3 shop sale detection
    private readonly Vector2 _jojaMarkerTile = new(27, 7); // JojaMart supply shelf
    private const string MorrisEventKey = "BankMod.MorrisSampleBasket";
    private bool _waitingForMorrisEventEnd;
    private bool _morrisEventSeen;
    private Vector2? _morrisOriginalPos;

    private const string MorrisEventScript =
        "continue/27 7/farmer 27 7 1 Morris 23 7 1/" +
        "pause 500/fade/viewport 25 3/pause 400/" +
        "jump farmer/pause 800/" +
        "faceDirection farmer 3/pause 300/" +
        "speak Morris \"嘿！别碰那个周转箱，里面的样品弄乱了很麻烦。$2\"/pause 500/" +
        "emote Morris 28/pause 300/" +
        "speak Morris \"不过既然你在这儿……我听说最近镇上都在夸%farm农场的东西。$0#$b#说实话，皮埃尔那个老抠收你的菜给不了几个钱吧？$0\"/pause 500/" +
        "speak Morris \"Joja讲究的是高效和实惠。$1#$b#如果你愿意把最好的当季作物稳定供给我，我可以按总部认可的流程给你一个长期收购价。$1\"/pause 300/" +
        "speak Morris \"只要品质达标，价格绝对比隔壁那家杂货店更让你满意。$1#$b#你少跑腿，我补货，这才是双赢。$1\"/pause 500/" +
        "speak Morris \"好好考虑一下。等你带第一批符合品控单的作物到柜台来，我们就可以正式敲定。$0#$b#现在我得回去看销售报表了……$0\"/end";
    /*********
    ** Public methods
    *********/
    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        I18n.Init(helper.Translation);

        // Load config
        _config = helper.ReadConfig<ModConfig>();
        ValidateAndFixConfigOnStartup(); // 启动时修正非法值
        Monitor.Log($"Config loaded: {_config.Companies.Count} companies", LogLevel.Debug);

        // Compose services (manual DI root)
        _services = new ModServices(helper, _config, Monitor);

        // Register SMAPI events
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.Player.InventoryChanged += OnInventoryChanged;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Content.AssetRequested += OnAssetRequested;

        // V3.3 联机基础架构埋点
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;

        _lastLocationName = null;

        // Console commands
        helper.ConsoleCommands.Add("spawn_phone", I18n.Command_SpawnPhone_Desc(), SpawnPhone);
        helper.ConsoleCommands.Add("reset_phone_flag", "重置手机标志位", ResetPhoneFlag);

        Monitor.Log(I18n.Mod_Loaded(), LogLevel.Info);
    }

    /*********
    ** SMAPI event handlers
    *********/
    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        _gmcmApi = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (_gmcmApi is not null)
        {
            RegisterModConfig(_gmcmApi);
            Monitor.Log("GMCM registered successfully", LogLevel.Info);
        }
        else
        {
            Monitor.Log("GMCM not found - config menu will not be available", LogLevel.Warn);
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null) return;
        if (!Context.IsMainPlayer) return;

        // Remove phones from shipping bin (phones are unsellable by design)
        var shippingBin = Game1.getFarm().getShippingBin(Game1.player);
        for (int i = shippingBin.Count - 1; i >= 0; i--)
        {
            if (shippingBin[i].QualifiedItemId == PhoneItem.QualifiedItemId)
            {
                Monitor.Log("Removed phone from shipping bin (phones are not sellable)", LogLevel.Warn);
                shippingBin.RemoveAt(i);
            }
        }

        var account = _services.BankAccountService.Load();
        _services.ShipmentTracking.ProcessShipments(account, _services.FuelService, Monitor);
        _services.BankAccountService.Save(account);
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(PhoneItem.TextureAssetName))
        {
            e.LoadFrom(() => CreatePhoneSprite(), AssetLoadPriority.Medium);
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
        {
            e.Edit(assets =>
            {
                var data = assets.AsDictionary<string, ObjectData>();
                if (!data.Data.ContainsKey(PhoneItem.ItemId))
                {
                    data.Data[PhoneItem.ItemId] = new ObjectData
                    {
                        Name = "手机",
                        DisplayName = "手机",
                        Price = 0,
                        Type = "Quest",
                        Category = -300,
                        Description = "一部神奇的手机，可以联系银行和其他服务。",
                        Texture = PhoneItem.TextureAssetName,
                        SpriteIndex = 0
                    };
                }
            });
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Data/Events/JojaMart"))
        {
            e.Edit(assets =>
            {
                var data = assets.AsDictionary<string, string>();
                data.Data[MorrisEventKey] = MorrisEventScript;
            });
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Data/mail"))
        {
            e.Edit(assets =>
            {
                var mailData = assets.AsDictionary<string, string>();
                mailData.Data["BankMod_Letter"] = "亲爱的玩家，欢迎来到星露谷银行！^我们已经为您准备了专属手机，随信附上，请查收！";
            });
        }
    }

    private Texture2D CreatePhoneSprite()
    {
        if (Game1.graphics?.GraphicsDevice is not GraphicsDevice gd)
        {
            Monitor.Log("GraphicsDevice not available for phone sprite creation", LogLevel.Warn);
            return new Texture2D(Game1.game1.GraphicsDevice, 16, 16);
        }

        try
        {
            var fullTex = Helper.ModContent.Load<Texture2D>("assets/phone.png");
            RenderTarget2D rt = new(gd, 16, 16);
            gd.SetRenderTarget(rt);
            gd.Clear(Color.Transparent);
            using SpriteBatch sb = new(gd);
            sb.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend);
            sb.Draw(fullTex, new Rectangle(0, 0, 16, 16), Color.White);
            sb.End();
            gd.SetRenderTarget(null);
            Monitor.Log("Phone sprite created: 16x16", LogLevel.Debug);
            return rt;
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to create phone sprite: {ex.Message}", LogLevel.Error);
            return new Texture2D(gd, 16, 16);
        }
    }

    private void RegisterModConfig(IGenericModConfigMenuApi gmcm)
    {
        gmcm.Register(
            ModManifest,
            reset: () => _config = new ModConfig(),
            save: () =>
            {
                if (HasIllegalConfig())
                {
                    _config = Helper.ReadConfig<ModConfig>();
                    _gmcmWarning = "利率设置不合法，保存已拒绝。贷款利率不能低于存款利率，这不是Bug。";
                    _postSaveStep = 1;
                    Monitor.Log("[GMCM] 保存被拒绝：跨公司利率非法", LogLevel.Warn);
                    return;
                }

                Helper.WriteConfig(_config);
                _postSaveStep = 0;
                Monitor.Log("GMCM config saved successfully", LogLevel.Info);
            }
        );

        // ============== 利率计算 ==============
        gmcm.AddSectionTitle(ModManifest, () => "利率计算", () => "存款利率计算核心设置");
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.UseCompoundInterest,
            setValue: val => _config.UseCompoundInterest = val,
            name: () => "复利计算",
            tooltip: () => "启用后利息计入本金产生复利；关闭后按本金计算单利");
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.AllowNegativeInterest,
            setValue: val => _config.AllowNegativeInterest = val,
            name: () => "允许负利率",
            tooltip: () => "启用后利率可低于0%，存款可能缩水");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.DepositRateFloor * 100),
            setValue: val => _config.DepositRateFloor = val / 100f,
            name: () => "存款利率下限 (%/天)",
            tooltip: () => "负利率的最大跌幅。推荐 -2.0%/天，范围 -5.0%~0%",
            min: -5f, max: 0f, interval: 0.1f);

        // ============== 运气影响 ==============
        gmcm.AddSectionTitle(ModManifest, () => "运气影响", () => "每日运气对利率的乘数影响：利率 × (1 + 运气值 × 系数)");
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.EnableLuckInfluence,
            setValue: val => _config.EnableLuckInfluence = val,
            name: () => "启用运气影响",
            tooltip: () => "关闭后每日运气不影响利率");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)_config.LuckStrengthCoefficient,
            setValue: val => _config.LuckStrengthCoefficient = val,
            name: () => "运气强度系数",
            tooltip: () => "推荐 2.5，范围 1.0~5.0。值越大运气对利率影响越显著",
            min: 0f, max: 5f, interval: 0.1f);

        // ============== 天气影响 ==============
        gmcm.AddSectionTitle(ModManifest, () => "天气影响", () => "不同天气对各公司利率的加成");
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.EnableWeatherInfluence,
            setValue: val => _config.EnableWeatherInfluence = val,
            name: () => "启用天气影响",
            tooltip: () => "关闭后天气不影响利率");

        // ============== 固定公司 ==============
        gmcm.AddSectionTitle(ModManifest, () => "固定公司", () => "皮埃尔和 Joja 的基础参数");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.Companies[0].DepositInterestRate * 100),
            setValue: val => _config.Companies[0].DepositInterestRate = val / 100f,
            name: () => "Joja 存款利率 (%/天)", tooltip: () => "不能超过任何公司的贷款利率，否则保存将被拒绝",
            min: 1f, max: 10f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.Companies[0].LoanInterestRate * 100),
            setValue: val => _config.Companies[0].LoanInterestRate = val / 100f,
            name: () => "Joja 贷款利率 (%/天)", tooltip: () => "不能低于任何公司的存款利率，否则保存将被拒绝",
            min: 2f, max: 15f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.Companies[0].DepositLimit,
            setValue: val => _config.Companies[0].DepositLimit = val,
            name: () => "Joja 存款上限 (g)", tooltip: () => "推荐 2,000,000g",
            min: 100000, max: 10000000, interval: 100000);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.Companies[0].LoanLimit,
            setValue: val => _config.Companies[0].LoanLimit = val,
            name: () => "Joja 贷款上限 (g)", tooltip: () => "推荐 300,000g",
            min: 50000, max: 1000000, interval: 10000);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.Companies[1].DepositInterestRate * 100),
            setValue: val => _config.Companies[1].DepositInterestRate = val / 100f,
            name: () => "皮埃尔存款利率 (%/天)", tooltip: () => "不能超过任何公司的贷款利率，否则保存将被拒绝",
            min: 0.5f, max: 8f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.Companies[1].LoanInterestRate * 100),
            setValue: val => _config.Companies[1].LoanInterestRate = val / 100f,
            name: () => "皮埃尔贷款利率 (%/天)", tooltip: () => "不能低于任何公司的存款利率，否则保存将被拒绝",
            min: 1f, max: 12f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.Companies[1].DepositLimit,
            setValue: val => _config.Companies[1].DepositLimit = val,
            name: () => "皮埃尔存款上限 (g)", tooltip: () => "推荐 1,000,000g",
            min: 100000, max: 10000000, interval: 100000);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.Companies[1].LoanLimit,
            setValue: val => _config.Companies[1].LoanLimit = val,
            name: () => "皮埃尔贷款上限 (g)", tooltip: () => "推荐 200,000g",
            min: 50000, max: 1000000, interval: 10000);

        // 跨公司套利保护：仅在非法时显示警告
        gmcm.AddParagraph(ModManifest, () =>
        {
            if (!HasIllegalConfig())
                return string.Empty;
            return "跨公司套利保护：存款利率不能超过任何公司的贷款利率，贷款利率不能低于任何公司的存款利率。当前设置不合法，保存将被拒绝，这不是Bug。";
        });

        // ============== 动态公司 ==============
        gmcm.AddSectionTitle(ModManifest, () => "动态公司", () => "由玩家售卖触发生成的作物关联公司");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.CompanySpawnRequiredSellCount,
            setValue: val => _config.CompanySpawnRequiredSellCount = val,
            name: () => "生成所需累计出售数量", tooltip: () => "推荐 50 个，范围 10~500",
            min: 10, max: 500, interval: 10);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.CompanySellTrackingWindowDays,
            setValue: val => _config.CompanySellTrackingWindowDays = val,
            name: () => "连续售卖判定窗口 (天)", tooltip: () => "推荐 7 天，范围 3~14",
            min: 3, max: 14, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.DepositLoanCoefficient,
            setValue: val => _config.DepositLoanCoefficient = val,
            name: () => "存贷款系数", tooltip: () => "存款上限 = 作物成本 × 存贷款系数\n贷款上限基准 = 存款上限 × 1.0\n有效贷款上限 = 基准 × 状态乘数（繁荣/稳定×1.0, 饥饿×0.6, 濒死×0.3）",
            min: 1000, max: 10000, interval: 500);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.LoanRateHardCap * 100),
            setValue: val => _config.LoanRateHardCap = val / 100f,
            name: () => "贷款日利率硬上限 (%/天)", tooltip: () => "推荐 15%，范围 10%~20%",
            min: 10f, max: 20f, interval: 1f);

        // ============== 连续售卖与打压 ==============
        gmcm.AddSectionTitle(ModManifest, () => "连续售卖与打压", () => "连续售卖加成和竞争打压的利率影响");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.ConsecutiveSellDepositBonusPerDay * 100),
            setValue: val => _config.ConsecutiveSellDepositBonusPerDay = val / 100f,
            name: () => "连续售卖日加成 (%/天·存款)", tooltip: () => "推荐 +0.5%/天",
            min: 0f, max: 1f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.ConsecutiveSellDepositBonusCap * 100),
            setValue: val => _config.ConsecutiveSellDepositBonusCap = val / 100f,
            name: () => "连续售卖加成上限 (%·存款)", tooltip: () => "推荐 +5.0%",
            min: 0f, max: 10f, interval: 0.5f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.ConsecutiveSellLoanBonusPerDay * 100),
            setValue: val => _config.ConsecutiveSellLoanBonusPerDay = val / 100f,
            name: () => "连续售卖日加成 (%/天·贷款)", tooltip: () => "推荐 +1.0%/天",
            min: 0f, max: 2f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.ConsecutiveSellLoanBonusCap * 100),
            setValue: val => _config.ConsecutiveSellLoanBonusCap = val / 100f,
            name: () => "连续售卖加成上限 (%·贷款)", tooltip: () => "推荐 +10.0%",
            min: 0f, max: 20f, interval: 1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.SuppressMaxDropPerDay * 100),
            setValue: val => _config.SuppressMaxDropPerDay = val / 100f,
            name: () => "打压最大跌幅 (%/天)", tooltip: () => "推荐 -2.0%",
            min: -5f, max: 0f, interval: 0.5f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.SuppressEffectDurationDays,
            setValue: val => _config.SuppressEffectDurationDays = val,
            name: () => "打压效果持续天数", tooltip: () => "推荐 5 天，范围 1~14",
            min: 1, max: 14, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.SuppressMaxStacks,
            setValue: val => _config.SuppressMaxStacks = val,
            name: () => "打压最大叠层数", tooltip: () => "推荐 3 层，范围 1~5",
            min: 1, max: 5, interval: 1);

        // ============== 贷款与债务 ==============
        gmcm.AddSectionTitle(ModManifest, () => "贷款与债务", () => "还款周期、宽限期、罚息设置");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.LongTermRateDiscount * 100),
            setValue: val => _config.LongTermRateDiscount = val / 100f,
            name: () => "14天周期利率折扣 (%/天)", tooltip: () => "推荐 0.5%",
            min: 0f, max: 2f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.PrincipalDebtGraceDays,
            setValue: val => _config.PrincipalDebtGraceDays = val,
            name: () => "本金欠债宽限期 (天)", tooltip: () => "推荐 1 天，范围 0~3",
            min: 0, max: 3, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.PenaltyInterestRate * 100),
            setValue: val => _config.PenaltyInterestRate = val / 100f,
            name: () => "本金欠债罚息幅度 (%)", tooltip: () => "推荐 +20%，范围 0%~50%",
            min: 0f, max: 50f, interval: 5f);

        // ============== 破产保护 ==============
        gmcm.AddSectionTitle(ModManifest, () => "破产保护", () => "破产判定条件和收入扣除");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.BankruptcyIncomeDeduction * 100),
            setValue: val => _config.BankruptcyIncomeDeduction = val / 100f,
            name: () => "破产收入扣除比例 (%)", tooltip: () => "推荐 50%",
            min: 20, max: 80, interval: 5);

        // ============== 债券交易 ==============
        gmcm.AddSectionTitle(ModManifest, () => "债券交易（倒闭机制）", () => "公司濒临破产的条件和债务转移参数");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.BankruptcyNoTradeDays,
            setValue: val => _config.BankruptcyNoTradeDays = val,
            name: () => "倒闭：无交易天数", tooltip: () => "推荐 14 天",
            min: 7, max: 28, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.BankruptcyRateNegativeDays,
            setValue: val => _config.BankruptcyRateNegativeDays = val,
            name: () => "倒闭：利率负值天数", tooltip: () => "推荐 5 天",
            min: 3, max: 14, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.BankruptcyDepositThreshold,
            setValue: val => _config.BankruptcyDepositThreshold = val,
            name: () => "倒闭：存款枯竭阈值 (g)", tooltip: () => "推荐 5,000g",
            min: 0, max: 50000, interval: 1000);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.PreBankruptcyGraceDays,
            setValue: val => _config.PreBankruptcyGraceDays = val,
            name: () => "濒临破产宽限期 (天)", tooltip: () => "推荐 3 天",
            min: 1, max: 7, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.DebtTransferRateDiscount * 100),
            setValue: val => _config.DebtTransferRateDiscount = val / 100f,
            name: () => "债务转移利率折扣 (%)", tooltip: () => "推荐 50%",
            min: 30, max: 70, interval: 5);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.DebtTransferNewRepaymentDays,
            setValue: val => _config.DebtTransferNewRepaymentDays = val,
            name: () => "债务转移新还款周期 (天)", tooltip: () => "推荐 28 天",
            min: 14, max: 56, interval: 7);

        // ============== 借款上限 ==============
        gmcm.AddSectionTitle(ModManifest, () => "借款上限（全局）", () => "借款上限 = min(净资产×杠杆系数, 硬封顶)");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.BorrowingLeverageCoefficient * 100),
            setValue: val => _config.BorrowingLeverageCoefficient = val / 100f,
            name: () => "借款杠杆系数 (%)", tooltip: () => "推荐 200%",
            min: 100, max: 500, interval: 50);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.BorrowingHardCap,
            setValue: val => _config.BorrowingHardCap = val,
            name: () => "借款硬封顶 (g)", tooltip: () => "推荐 500,000g",
            min: 100000, max: 2000000, interval: 100000);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.FixedCompanyQuotaRatio * 100),
            setValue: val => _config.FixedCompanyQuotaRatio = val / 100f,
            name: () => "固定公司额度占比 (%)", tooltip: () => "推荐 60%",
            min: 50, max: 80, interval: 5);

        // ============== 多人模式 ==============
        gmcm.AddSectionTitle(ModManifest, () => "多人模式", () => "多人联机下的转账设置");
        gmcm.AddTextOption(ModManifest,
            getValue: () => _config.TransferFeeMode,
            setValue: val => _config.TransferFeeMode = val,
            name: () => "转账手续费模式",
            tooltip: () => "百分比：按转账金额百分比收费；固定：按固定金额收费",
            allowedValues: new[] { "percentage", "fixed" });
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.TransferFeeValue * 100),
            setValue: val => _config.TransferFeeValue = val / 100f,
            name: () => "转账手续费",
            tooltip: () => "百分比模式推荐 1%；固定模式推荐 0~200g",
            min: 0f, max: 5f, interval: 0.1f);

        // ============== 显示设置 ==============
        gmcm.AddSectionTitle(ModManifest, () => "显示设置", () => "利率显示精度和通知开关");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.RateDisplayPrecision,
            setValue: val => _config.RateDisplayPrecision = val,
            name: () => "利率显示精度 (小数位)", tooltip: () => "推荐 2 位，范围 1~4",
            min: 1, max: 4, interval: 1);
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.ShowMorningInterestNotification,
            setValue: val => _config.ShowMorningInterestNotification = val,
            name: () => "晨间利息通知", tooltip: () => "每天早晨显示各公司有效利率变化");
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.ShowCompanyDangerWarning,
            setValue: val => _config.ShowCompanyDangerWarning = val,
            name: () => "公司濒危预警", tooltip: () => "动态公司濒临破产时显示警告");

        // ============== 作物利率表（子页面） ==============
        gmcm.AddPageLink(ModManifest, "CropRatesTable",
            text: () => "📊 查看作物利率表（按R分区）",
            tooltip: () => "V3.5 patch5 终版数据，咖啡豆已排除，按基础日利率R三区展示");

        gmcm.AddPage(ModManifest, "CropRatesTable",
            pageTitle: () => "作物利率与燃料数据总表");

        // Build crop rate text dynamically from authoritative data source
        static string FormatCropLine(CropDataRecord c)
        {
            double dep = c.R > 0.20 ? c.R * 0.5 : c.R > 0 ? c.R : 0;
            double loan = c.R > 0.20 ? c.R * 0.75 : c.R > 0 ? c.R * 1.5 : 0;
            if (loan < 0.01) loan = 0.01; // hard floor
            return $"{c.CropCode} ({c.DisplayName}) | R={c.R * 100:F2}% | 存款 {dep * 100:F2}% | 贷款 {loan * 100:F2}% | 日耗={c.DBase:F1} | 燃料上限={c.Smax:F0}";
        }

        var allCrops = CropDataProvider.AllCrops;

        // Tier 1: R > 20%
        var tier1 = allCrops.Where(c => c.R > 0.20).OrderByDescending(c => c.R).ToList();
        gmcm.AddSectionTitle(ModManifest, () => $"R > 20% — 高回报区（{tier1.Count} 种）",
            () => "存款=R×0.5, 贷款=R×0.75");
        foreach (var c in tier1)
            gmcm.AddParagraph(ModManifest, () => FormatCropLine(c));

        // Tier 2: 10% < R ≤ 20%
        var tier2 = allCrops.Where(c => c.R > 0.10 && c.R <= 0.20).OrderByDescending(c => c.R).ToList();
        gmcm.AddSectionTitle(ModManifest, () => $"10% < R ≤ 20% — 中等回报区（{tier2.Count} 种）",
            () => "存款=R（不打折）, 贷款=R×1.5");
        foreach (var c in tier2)
            gmcm.AddParagraph(ModManifest, () => FormatCropLine(c));

        // Tier 3: 0% < R ≤ 10%
        var tier3 = allCrops.Where(c => c.R > 0 && c.R <= 0.10).OrderByDescending(c => c.R).ToList();
        gmcm.AddSectionTitle(ModManifest, () => $"0% < R ≤ 10% — 低回报区（{tier3.Count} 种）",
            () => "存款=R（不打折）, 贷款=R×1.5");
        foreach (var c in tier3)
            gmcm.AddParagraph(ModManifest, () => FormatCropLine(c));

        // Tier 4: R ≤ 0%
        var tier4 = allCrops.Where(c => c.R <= 0).OrderBy(c => c.R).ToList();
        gmcm.AddSectionTitle(ModManifest, () => $"R ≤ 0% — 赔钱作物区（{tier4.Count} 种）",
            () => "存款钳位0%, 贷款钳位0%（实现中贷款≥+1%）");
        if (tier4.Count > 0)
            foreach (var c in tier4)
                gmcm.AddParagraph(ModManifest, () => FormatCropLine(c));
        else
            gmcm.AddParagraph(ModManifest, () => "（无）");

        // Summary footer
        gmcm.AddParagraph(ModManifest, () =>
            $"共 {allCrops.Count} 种作物 | 数据源: Crops values.txt (V3.5 patch5) | 咖啡豆已排除 | 贷款利率硬下限 +1%");
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return;

        _phoneReceivedToday = false;
        _lastReadMail = null;
        _morrisEventSeen = Helper.Data.ReadSaveData<string>("bankmod_morris_event_seen") == "1";
        string? phoneFlag = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);
        _hasReceivedPhoneInSession = (phoneFlag == "1");
        Monitor.Log("Save loaded, BankMod ready", LogLevel.Info);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return;

        // V3.3 客机跳过日结算（所有金融逻辑由主机执行后广播）
        if (!Context.IsMainPlayer)
        {
            Monitor.Log("Skipping day-started logic (not main player)");
            return;
        }

        Monitor.Log("New day started", LogLevel.Info);

        string? phoneFlag = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);
        if (phoneFlag != "1")
        {
            bool hasReceivedPhoneBefore = _hasReceivedPhoneInSession ||
                (Game1.player.mailReceived?.Contains("BankMod_Letter") == true);
            if (hasReceivedPhoneBefore && !Game1.player.mailbox.Contains("BankMod_Letter"))
            {
                Game1.player.mailbox.Add("BankMod_Letter");
                _lastReadMail = null; // 新信件入队，重置读取检测状态
                Monitor.Log("Phone lost previously, sending new claim letter to mailbox", LogLevel.Info);
            }
        }

        // Delegate daily settlement to CompanyManager
        _services.CompanyManager.OnDayStarted();
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        // SafeDialogueBox dismissed → flag re-open for next tick (avoids re-entrancy)
        if (_postSaveStep == 3 && e.OldMenu is SafeDialogueBox && e.NewMenu is null)
        {
            _postSaveStep = 4;
            Monitor.Log("[GMCM] 警告已关闭，将在下一帧重新打开GMCM", LogLevel.Info);
            return;
        }

        // Stage 7.3: shop sale detection — snapshot inventory when shop opens
        if (e.NewMenu is StardewValley.Menus.ShopMenu && Context.IsMainPlayer)
        {
            _preShopInventory = Game1.player.Items
                .Where(item => item is StardewValley.Object)
                .GroupBy(item => item.QualifiedItemId)
                .ToDictionary(g => g.Key, g => g.Sum(item => item.Stack));
            return;
        }
        // Shop closed — diff inventory to detect sold items
        if (e.OldMenu is StardewValley.Menus.ShopMenu && _preShopInventory != null && Context.IsMainPlayer)
        {
            var account = _services.BankAccountService.Load();
            bool changed = false;

            foreach (var kvp in _preShopInventory)
            {
                int postCount = Game1.player.Items
                    .Where(item => item is StardewValley.Object && item.QualifiedItemId == kvp.Key)
                    .Sum(item => item.Stack);
                int sold = kvp.Value - postCount;
                if (sold > 0)
                {
                    string? cropCode = ShipmentTrackingService.NormalizeItemId(kvp.Key);
                    if (cropCode != null)
                    {
                        _services.CompanyManager.ApplySuppression(account, cropCode, sold);
                        _services.FuelService.RecordExternalSale(cropCode, sold);
                        changed = true;
                        var cs = account.CropSuppressions.First(s => s.CropCode == cropCode);
                        Monitor.Log($"[Stage7] Suppression: {cropCode} x{sold} sold -> stacks={cs.Stacks} days={cs.RemainingDays}", LogLevel.Debug);
                    }
                }
            }

            if (changed)
                _services.BankAccountService.Save(account);

            _preShopInventory = null;
            return;
        }

        if (!Context.IsWorldReady || Game1.player is null)
            return;

        if (e.OldMenu is MailboxDialog mailboxDialog)
        {
            Monitor.Log("Mailbox dialog closed", LogLevel.Debug);
            if (mailboxDialog.PhoneReceived)
                GivePhoneToPlayer();
        }
    }

    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return;

        // Skip phone check when inventory/menu is open — moving items around isn't discarding
        if (Game1.activeClickableMenu is not null)
            return;

        string? flagValue = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);
        if (flagValue != "1") return;

        if (!PlayerHasPhone())
        {
            Helper.Data.WriteSaveData(PhoneReceivedFlag, "0");
            _phoneReceivedToday = false;
            _lastReadMail = null; // 重置以便下次来信时能重新检测
            Monitor.Log("Phone was lost/discarded, status reset to 0", LogLevel.Info);
        }
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (Game1.currentLocation?.Name != "JojaMart" || Game1.activeClickableMenu is not null)
            return;

        // Draw floating "!" marker above the supply box tile
        var worldPos = new Vector2(_jojaMarkerTile.X * 64 + 32, _jojaMarkerTile.Y * 64);
        var screenPos = Game1.GlobalToLocal(worldPos);
        float bounce = (float)Math.Sin(Game1.currentGameTime.TotalGameTime.TotalSeconds * 3) * 5;
        var drawPos = new Vector2(screenPos.X, screenPos.Y - 48 + bounce);

        // Pulsing golden exclamation mark
        float alpha = 0.6f + (float)Math.Sin(Game1.currentGameTime.TotalGameTime.TotalSeconds * 4) * 0.3f;
        e.SpriteBatch.Draw(Game1.mouseCursors, drawPos,
            new Rectangle(120, 192, 8, 8), Color.Gold * alpha,
            0f, Vector2.Zero, 4f, Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 1f);

        // Shadow/glow circle below
        e.SpriteBatch.Draw(Game1.mouseCursors,
            new Vector2(drawPos.X - 8, drawPos.Y + 28),
            new Rectangle(268, 470, 16, 16), Color.White * 0.3f,
            0f, Vector2.Zero, 2f, Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 1f);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady) return;
        if (e.Button is not (SButton.MouseRight or SButton.ControllerA)) return;
        if (Game1.activeClickableMenu is not null) return;

        // JojaMart supply box click → play Morris event → open supply menu after
        if (Game1.currentLocation?.Name == "JojaMart")
        {
            var cursorTile = e.Cursor.Tile;
            if (Math.Abs(cursorTile.X - _jojaMarkerTile.X) < 1.5f &&
                Math.Abs(cursorTile.Y - _jojaMarkerTile.Y) < 1.5f)
            {
                Helper.Input.Suppress(e.Button);
                if (_morrisEventSeen)
                {
                    Game1.activeClickableMenu = new JojaSupplyMenu(_services, Helper);
                    return;
                }
                try
                {
                    // Hide real Morris via private isInvisible field (reflection)
                    var realMorris = Game1.currentLocation?.characters
                        .OfType<NPC>()
                        .FirstOrDefault(c => c.Name == "Morris");
                    if (realMorris != null)
                    {
                        Helper.Reflection.GetField<bool>(realMorris, "isInvisible").SetValue(true);
                        _morrisOriginalPos = realMorris.Position;
                    }

                    var evt = new Event(MorrisEventScript, Game1.player);
                    Game1.currentLocation.startEvent(evt);
                    _waitingForMorrisEventEnd = true;
                }
                catch (Exception ex)
                {
                    _morrisOriginalPos = null;
                    Monitor.Log($"[JojaMarker] Event start failed: {ex.Message}, opening menu directly", LogLevel.Warn);
                    Game1.activeClickableMenu = new JojaSupplyMenu(_services, Helper);
                }
                return;
            }
        }

        if (Game1.player.CurrentItem?.QualifiedItemId != PhoneItem.QualifiedItemId) return;

        Helper.Input.Suppress(e.Button);
        Game1.activeClickableMenu = new BankChoiceMenu(_services, _config, Helper);
        Monitor.Log("Showing bank choice dialog", LogLevel.Debug);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // Three-step deferred GMCM warning — must run even when Context.IsWorldReady is false
        if (_postSaveStep == 1)
        {
            if (Game1.activeClickableMenu is not null)
            {
                Game1.activeClickableMenu = null;
                Monitor.Log("[GMCM] 已主动关闭GMCM菜单", LogLevel.Info);
            }
            _postSaveStep = 2;
            return;
        }
        else if (_postSaveStep == 2)
        {
            Game1.activeClickableMenu = new SafeDialogueBox(_gmcmWarning);
            _postSaveStep = 3;
            Monitor.Log("[GMCM] 已展示警告对话框，等待玩家关闭", LogLevel.Info);
            return;
        }
        else if (_postSaveStep == 4)
        {
            _postSaveStep = 0;
            _gmcmApi?.OpenModMenu(ModManifest);
            Monitor.Log("[GMCM] 已重新打开GMCM", LogLevel.Info);
            return;
        }

        // Morris event ended → open Joja supply menu
        if (_waitingForMorrisEventEnd && Game1.CurrentEvent == null && Game1.activeClickableMenu == null)
        {
            _waitingForMorrisEventEnd = false;
            _morrisEventSeen = true;
            Helper.Data.WriteSaveData("bankmod_morris_event_seen", "1");

            // Restore map Morris visibility
            if (_morrisOriginalPos.HasValue)
            {
                var realMorris = Game1.currentLocation?.characters
                    .OfType<NPC>()
                    .FirstOrDefault(c => c.Name == "Morris");
                if (realMorris != null)
                {
                    Helper.Reflection.GetField<bool>(realMorris, "isInvisible").SetValue(false);
                }
                _morrisOriginalPos = null;
            }

            Game1.activeClickableMenu = new JojaSupplyMenu(_services, Helper);
            Monitor.Log("[JojaMarker] Event ended, opening supply menu", LogLevel.Debug);
            return;
        }

        if (!Context.IsWorldReady || Game1.player is null)
            return;

        string? currentLocation = Game1.player.currentLocation?.Name;
        if (currentLocation == "Farm" && _lastLocationName != "Farm")
        {
            Monitor.Log("Player entered Farm - sending bank letter", LogLevel.Info);
            string? phoneFlag = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);
            if (phoneFlag != "1")
            {
                if (!Game1.player.mailbox.Contains("BankMod_Letter"))
                {
                    Game1.player.mailbox.Add("BankMod_Letter");
                    _lastReadMail = null; // 新信件入队，重置读取检测状态
                    Monitor.Log("Bank letter sent to mailbox", LogLevel.Info);
                }
            }
        }
        _lastLocationName = currentLocation;

        if (_lastReadMail != "BankMod_Letter" && !Game1.player.mailbox.Contains("BankMod_Letter"))
        {
            string? phoneFlag = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);
            if (phoneFlag != "1" && !_phoneReceivedToday)
            {
                Monitor.Log("Bank letter read - directly giving phone to player", LogLevel.Info);
                GivePhoneToPlayer();
                _lastReadMail = "BankMod_Letter";
            }
        }
    }

    /*********
    ** V3.3 联机事件处理器（阶段一埋点，当前仅日志输出）
    *********/
    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        Monitor.Log($"[联机] 客户端已连接: {e.Peer.PlayerID} (主机={Context.IsMainPlayer})", LogLevel.Info);
        // TODO 阶段十三：主机序列化 ModConfig → SendMessage(MsgConfigSync)
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        Monitor.Log($"[联机] 收到消息类型: {e.Type} 来自: {e.FromPlayerID}", LogLevel.Debug);
        // TODO 阶段十三：根据 e.Type 分发处理 ConfigSync / BankOperation / BankDataSync / BankSnapshot
    }

    /*********
    ** Config validation helpers（仅校验 Joja + 皮埃尔 两个固定公司，跨公司规则）
    *********/

    /// <summary>启动时校验 config.json：非法则自动修正并写盘。</summary>
    private void ValidateAndFixConfigOnStartup()
    {
        if (_config.Companies.Count < 2) return;
        if (HasIllegalConfig())
        {
            double maxDep = Math.Max(_config.Companies[0].DepositInterestRate, _config.Companies[1].DepositInterestRate);
            foreach (var c in _config.Companies)
                if (c.LoanInterestRate < maxDep) c.LoanInterestRate = maxDep;
            Helper.WriteConfig(_config);
            Monitor.Log("[配置警告] config.json 利率非法，已自动修正并写盘。", LogLevel.Alert);
        }
    }

    /// <summary>跨公司校验固定公司（Joja + 皮埃尔）：每笔贷款利率必须 &ge; 所有存款利率。</summary>
    private bool HasIllegalConfig()
    {
        if (_config.Companies.Count < 2) return false;
        var joja = _config.Companies[0];
        var pierre = _config.Companies[1];

        if (joja.LoanInterestRate < joja.DepositInterestRate) return true;
        if (joja.LoanInterestRate < pierre.DepositInterestRate) return true;
        if (pierre.LoanInterestRate < joja.DepositInterestRate) return true;
        if (pierre.LoanInterestRate < pierre.DepositInterestRate) return true;
        return false;
    }

    /*********
    ** Phone management
    *********/
    private void GivePhoneToPlayer()
    {
        if (Game1.player is null || _phoneReceivedToday)
            return;

        var phone = ItemRegistry.Create(PhoneItem.QualifiedItemId);
        if (Game1.player.addItemToInventoryBool(phone))
        {
            _phoneReceivedToday = true;
            _hasReceivedPhoneInSession = true;
            Helper.Data.WriteSaveData(PhoneReceivedFlag, "1");
            Game1.chatBox?.addInfoMessage(I18n.Chat_PhoneReceived());
            Monitor.Log("Phone given to player, status set to 1", LogLevel.Info);
        }
        else
        {
            Monitor.Log("Player inventory full, cannot give phone", LogLevel.Warn);
            Game1.chatBox?.addErrorMessage(I18n.Command_Error_InventoryFull());
        }
    }

    private static bool PlayerHasPhone()
    {
        if (Game1.player is null) return false;
        foreach (Item item in Game1.player.Items)
        {
            if (item?.QualifiedItemId == PhoneItem.QualifiedItemId)
                return true;
        }
        return false;
    }

    /*********
    ** Console commands
    *********/
    private void SpawnPhone(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Game1.chatBox?.addErrorMessage(I18n.Command_Error_NoWorld());
            return;
        }

        try
        {
            var phone = ItemRegistry.Create(PhoneItem.QualifiedItemId);
            if (Game1.player.addItemToInventoryBool(phone))
            {
                Game1.chatBox?.addInfoMessage(I18n.Command_SpawnPhone_Success());
                Helper.Data.WriteSaveData(PhoneReceivedFlag, "1");
            }
            else
                Game1.chatBox?.addErrorMessage(I18n.Command_Error_InventoryFull());
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to spawn phone: {ex.Message}", LogLevel.Error);
            Game1.chatBox?.addErrorMessage("Spawn phone failed!");
        }
    }

    private void ResetPhoneFlag(string command, string[] args)
    {
        Helper.Data.WriteSaveData(PhoneReceivedFlag, "0");
        _phoneReceivedToday = false;
        Game1.chatBox?.addInfoMessage("手机标志位已重置为0");
        Monitor.Log("Phone status reset to 0", LogLevel.Info);
    }

    /// <summary>DialogueBox subclass with a titleInPosition field to satisfy GMCM's reflection check. GMCM reads it as bool, not Point.</summary>
    private sealed class SafeDialogueBox : StardewValley.Menus.DialogueBox
    {
#pragma warning disable CS0649
        public bool titleInPosition;
#pragma warning restore CS0649

        public SafeDialogueBox(string dialogue) : base(dialogue) { }
    }

}
