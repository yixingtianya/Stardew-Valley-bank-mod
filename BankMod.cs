using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;
using BankMod.Services.Core;
using BankMod.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using System.IO;
using StardewValley;
using StardewValley.GameData.Objects;
using StardewValley.Menus;

namespace BankMod;

/// <summary>The mod entry point — thin composition root that wires SMAPI events to domain services.</summary>
internal sealed class BankMod : Mod
{
    /*********
    ** Constants
    *********/
    private const string PhoneReceivedFlag = MailFlags.Save_PhoneReceived;

    // V3.3 联机消息类型常量
    private const string MsgConfigSync = "ConfigSync";
    private const string MsgBankOperation = "BankOperation";
    private const string MsgBankDataSync = "BankDataSync";
    private const string MsgBankSnapshot = "BankSnapshot";
    private const string MsgAccountMigration = "AccountMigration";

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
    private bool _standaloneMode; // 客机独立运行（房主未装mod）
    private bool _configSyncReceived = false; // 是否收到过ConfigSync
    private int _standaloneCheckTicks; // 独立模式检测计时器
    private bool _shopIsJoja; // 记录当前商店是否为 Joja
    private int _lastMoneySnapshot;
    private int _bankruptGarnishCooldown;
    private bool _bankruptExitMessageShown;
    private bool _pendingPierreMail;
    private bool _pendingMorrisMail;
    private readonly Vector2 _jojaMarkerTile = new(27, 7); // JojaMart supply shelf
    private const string MorrisEventKey = "BankMod.MorrisSampleBasket";
    private bool _waitingForMorrisEventEnd;
    private bool _morrisEventSeen;
    private bool _shopIntercepted;
    private bool _shopSkipped;
    private string _pendingShopId = "";
    private Vector2? _morrisOriginalPos;

    private const string MorrisEventScript =
        "continue/27 7/farmer 27 7 1 Morris 22 7 1/" +
        "pause 500/fade/viewport 26 7/pause 400/" +
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
        helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.Player.InventoryChanged += OnInventoryChanged;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Content.AssetRequested += OnAssetRequested;

        // V3.3 联机基础架构埋点
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;

        // Stage 13: Wire remote operation callback
        _services.SendRemoteOperation = (op, company, amount, target) =>
        {
            var req = new Messages.BankOperationRequest
            {
                Operation = op, CompanyName = company, Amount = amount,
                SenderId = Game1.player.UniqueMultiplayerID, TargetPlayer = target
            };
            Helper.Multiplayer.SendMessage(req, MsgBankOperation, null, null);
            Monitor.Log($"[联机] 发送操作请求: {op} {company} {amount}", LogLevel.Debug);
        };

        // Stage 10: TV financial channel Harmony patches
        var tvHarmony = new HarmonyLib.Harmony("bankmod.tv");
        tvHarmony.PatchAll(typeof(Patches.TVPatch).Assembly);
        Patches.FbnNewsGenerator.Initialize(_services, _config);
        Monitor.Log("[TV] FBN channel Harmony patches applied", LogLevel.Debug);

        // Console command: debug season shop
        helper.ConsoleCommands.Add("season_shop_debug", "查看当前季节可采购的农产品筛选过程", (cmd, args) =>
        {
            if (!Context.IsWorldReady) { Monitor.Log("需要加载存档。", LogLevel.Warn); return; }
            string season = Game1.currentSeason;
            string seasonKey = season switch { "spring" => "Spring", "summer" => "Summer", "fall" => "Fall", "winter" => "Winter", _ => season };
            Monitor.Log($"当前季节: {season}, 第{Game1.dayOfMonth}天", LogLevel.Info);

            var bundleCrops = new HashSet<string> { "Parsnip", "Green Bean", "Cauliflower", "Potato", "Blueberry", "Melon", "Hot Pepper", "Tomato", "Corn", "Eggplant", "Pumpkin", "Yam", "Wheat", "Poppy", "Sunflower" };

            var all = CropDataProvider.AllCrops;
            Monitor.Log($"全部作物: {all.Count} 种", LogLevel.Info);

            foreach (var cd in all)
            {
                bool inSeason = cd.Season.Contains(seasonKey, StringComparison.OrdinalIgnoreCase);
                bool hasR = cd.R > 0;
                bool isPurchase = cd.IsPurchasable;
                bool notBundle = !bundleCrops.Contains(cd.CropCode);

                if (!inSeason) continue;
                Monitor.Log($"  {cd.CropCode}({cd.DisplayName}): 当季={inSeason} R={cd.R:F2}({hasR}) 可购={isPurchase} 非献祭={notBundle}", LogLevel.Info);
            }
        });

        // Console command: set company fuel
        helper.ConsoleCommands.Add("set_fuel", "设置指定动态公司的燃料点数\n用法: set_fuel <公司名或作物名> <燃料点(FP)>\n例: set_fuel 土豆 500", (cmd, args) =>
        {
            if (args.Length < 2) { Monitor.Log("用法: set_fuel <公司名> <燃料FP>", LogLevel.Info); return; }
            if (!Context.IsWorldReady) { Monitor.Log("需要加载存档后再用。", LogLevel.Warn); return; }

            string name = args[0];
            if (!int.TryParse(args[1], out int fuel)) { Monitor.Log("燃料值必须为整数", LogLevel.Warn); return; }

            var account = _services.BankAccountService.Load();
            var dc = account.DynamicCompanies.FirstOrDefault(
                c => c.CompanyName.Contains(name, StringComparison.OrdinalIgnoreCase)
                  || c.CropCode.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (dc is null) { Monitor.Log($"未找到公司: {name}", LogLevel.Warn); return; }

            dc.FuelStock = fuel;
            _services.BankAccountService.Save(account);
            Monitor.Log($"{dc.CompanyName} 燃料设为 {fuel} FP", LogLevel.Info);
        });

        _lastLocationName = null;

        // Console commands
        helper.ConsoleCommands.Add("spawn_phone", I18n.Command_SpawnPhone_Desc(), SpawnPhone);
        helper.ConsoleCommands.Add("reset_phone_flag", "重置手机标志位", ResetPhoneFlag);
        helper.ConsoleCommands.Add("bank_mail", "向信箱直接投递银行信件: bank_mail <pierre|morris>\n用于测试中文邮件显示", TestBankMail);

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

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        _services.RouteService.SaveToSave(Helper);
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null) return;
        if (!Context.IsMainPlayer && !_standaloneMode) return;

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

        // Snapshot money before overnight shipping income is added
        _lastMoneySnapshot = Game1.player.Money;
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(PhoneItem.TextureAssetName))
        {
            e.LoadFrom(() => CreatePhoneSprite(), AssetLoadPriority.Medium);
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Mods/BankMod/FBNScreen"))
        {
            e.LoadFrom(() =>
            {
                string path = Path.Combine(Helper.DirectoryPath, "assets", "TV3.png");
                return File.Exists(path) ? Texture2D.FromFile(Game1.graphics.GraphicsDevice, path) : null;
            }, AssetLoadPriority.Medium);
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
                // BankMod_Letter 始终注册（欢迎信，内容固定）
                mailData.Data[MailFlags.BankMod_Letter] = "亲爱的玩家, 欢迎来到星露谷银行!^我们已经为您准备了专属手机, 随信附上, 请查收!";


                // Pierre/Morris 信件直接注册中文内容，由原版 LetterViewerMenu 渲染
                // SMAPI 中文版已替换 Game1.smallFont 为支持中文的字体
                if (!mailData.Data.ContainsKey(MailFlags.BankMod_PierreThanks))
                    mailData.Data[MailFlags.BankMod_PierreThanks] = "占位^Pierre信件";
                if (!mailData.Data.ContainsKey(MailFlags.BankMod_MorrisThanks))
                    mailData.Data[MailFlags.BankMod_MorrisThanks] = "占位^Morris信件";

                // 如果存档中有真实信件内容，用真实内容覆盖占位
                BankAccountData? acc = null;
                try { acc = _services.BankAccountService.Load(); } catch { }
                if (acc is not null)
                {
                    Monitor.Log($"[AssetRequested] Data/mail loading: PierreText='{(acc.PierreThanksLetterText?.Length > 0 ? acc.PierreThanksLetterText.Substring(0, Math.Min(30, acc.PierreThanksLetterText.Length)) + "..." : "null")}', MorrisText='{(acc.MorrisThanksLetterText?.Length > 0 ? acc.MorrisThanksLetterText.Substring(0, Math.Min(30, acc.MorrisThanksLetterText.Length)) + "..." : "null")}'", LogLevel.Info);
                    if (!string.IsNullOrEmpty(acc.PierreThanksLetterText))
                        mailData.Data[MailFlags.BankMod_PierreThanks] = NormalizeMailText(acc.PierreThanksLetterText);
                    if (!string.IsNullOrEmpty(acc.MorrisThanksLetterText))
                        mailData.Data[MailFlags.BankMod_MorrisThanks] = NormalizeMailText(acc.MorrisThanksLetterText);
                }
                else
                {
                    Monitor.Log($"[AssetRequested] Data/mail loading: acc is null", LogLevel.Warn);
                }
            });
        }

        // Register custom Junimo sprites under both simple name (for temporaryAnimatedSprite)
        // and Characters/<name> (for addTemporaryActor)
        foreach (var name in JunimoSprites)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(name) || e.NameWithoutLocale.IsEquivalentTo($"Characters/{name}"))
            {
                var n = name; // capture for closure
                e.LoadFrom(() =>
                {
                    string p = Path.Combine(Helper.DirectoryPath, "assets", $"{n}.png");
                    return File.Exists(p) ? Texture2D.FromFile(Game1.graphics.GraphicsDevice, p) : null;
                }, AssetLoadPriority.Medium);
            }
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

    /// <summary>Custom Junimo sprites registered as Characters/znm* for addTemporaryActor event commands.</summary>
    private static readonly string[] JunimoSprites =
    {
        "znm", "znm1", "znm2",
        "znmf", "znm1f", "znm2f",
        "znmh", "znm1h", "znm2h",
        "znmz", "znm1z", "znm2z",
    };

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

        // ============== 债券交易（阶段九） ==============
        gmcm.AddSectionTitle(ModManifest, () => "债券交易与清算（阶段九）", () => "公司燃料耗尽倒闭时的债券转移和阶梯清算参数");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.DebtTransferRateDiscount * 100),
            setValue: val => _config.DebtTransferRateDiscount = val / 100f,
            name: () => "债务转移利率折扣 (%)", tooltip: () => "推荐 50%",
            min: 10, max: 90, interval: 5);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.DebtTransferNewRepaymentDays,
            setValue: val => _config.DebtTransferNewRepaymentDays = val,
            name: () => "债务转移新还款周期 (天)", tooltip: () => "推荐 28 天",
            min: 14, max: 56, interval: 7);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.ProsperousReturnRate * 100),
            setValue: val => _config.ProsperousReturnRate = val / 100f,
            name: () => "清算返还：繁荣期 (%)", tooltip: () => "推荐 80%",
            min: 0, max: 100, interval: 5);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.StableReturnRate * 100),
            setValue: val => _config.StableReturnRate = val / 100f,
            name: () => "清算返还：稳定期 (%)（≤繁荣期）", tooltip: () => "推荐 50%",
            min: 0, max: 100, interval: 5);
        gmcm.AddSectionTitle(ModManifest, () => "贷款上限乘数（阶段九）", () => "值÷10=实际乘数。默认：繁荣10(×1.0) 稳定9(×0.9) 饥饿6(×0.6) 濒死3(×0.3)");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.ProsperousLoanLimitMult,
            setValue: val => _config.ProsperousLoanLimitMult = val,
            name: () => "繁荣期贷款上限乘数", tooltip: () => "默认 10（×1.0）",
            min: 1, max: 20, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.StableLoanLimitMult,
            setValue: val => _config.StableLoanLimitMult = val,
            name: () => "稳定期贷款上限乘数", tooltip: () => "默认 9（×0.9）",
            min: 1, max: 20, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.HungryLoanLimitMult,
            setValue: val => _config.HungryLoanLimitMult = val,
            name: () => "饥饿期贷款上限乘数", tooltip: () => "默认 6（×0.6）",
            min: 1, max: 20, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.DyingLoanLimitMult,
            setValue: val => _config.DyingLoanLimitMult = val,
            name: () => "濒死期贷款上限乘数", tooltip: () => "默认 3（×0.3）",
            min: 1, max: 20, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.RescueInvestmentCapRatio * 100),
            setValue: val => _config.RescueInvestmentCapRatio = val / 100f,
            name: () => "入股救市上限（贷款上限×比例）", tooltip: () => "推荐 50%",
            min: 10, max: 100, interval: 5);

        // ============== 借款上限 ==============
        gmcm.AddSectionTitle(ModManifest, () => "借款上限（全局）", () => "借款上限 = min(净资产×杠杆系数, 硬封顶)");
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.BorrowingLeverageCoefficient * 100),
            setValue: val => _config.BorrowingLeverageCoefficient = val / 100f,
            name: () => "借款杠杆系数 (%)", tooltip: () => "推荐 200%",
            min: 100, max: 500, interval: 50);
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
        _lastMoneySnapshot = Game1.player?.Money ?? 0;

        // Stage 14: Route check
        _services.RouteService.LoadFromSave(Helper);
        _services.RouteService.DetectRoute();
        if (!string.IsNullOrEmpty(_services.RouteService.CompletedRoute))
        {
            _services.RouteService.ApplyRouteEffects();
            _services.RouteService.SaveToSave(Helper);
            if (_services.RouteService.CompletedRoute == "Joja"
                && !_services.RouteService.SeenCutscene
                && !_services.RouteService.OnlineShoppingUnlocked)
                _oldSavePending = true;
            else if (_services.RouteService.CompletedRoute == "Community")
            {
                bool hasMovieTheater = Game1.player.hasOrWillReceiveMail(MailFlags.CC_MovieTheater) || Game1.player.mailReceived.Contains(MailFlags.CC_MovieTheater);
                if (hasMovieTheater)
                {
                    _services.RouteService.MarkCutsceneSeen();
                    _services.RouteService.UnlockOnlineShopping();
                    _services.RouteService.SaveToSave(Helper);
                    Monitor.Log("[Stage14] CC old save: Movie Theater, skipping event", LogLevel.Info);
                }
            }
        }
        _morrisEventSeen = Helper.Data.ReadSaveData<string>(MailFlags.Save_MorrisEventSeen) == "1";
        string? phoneFlag = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);
        _hasReceivedPhoneInSession = (phoneFlag == "1");

        // Invalidate Data/mail cache if letters were queued in a previous session
        var acc = _services.BankAccountService.Load();
        if (!string.IsNullOrEmpty(acc.PierreThanksLetterText) || !string.IsNullOrEmpty(acc.MorrisThanksLetterText))
            Helper.GameContent.InvalidateCache("Data/mail");

        Monitor.Log("Save loaded, BankMod ready", LogLevel.Info);
    }

    private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        // Stage 13: Standalone mode detection for guests (once per second)
        if (!Context.IsMainPlayer && !_configSyncReceived && _standaloneCheckTicks > 0)
        {
            _standaloneCheckTicks--;
            if (_standaloneCheckTicks <= 0)
            {
                _standaloneMode = true;
                _services.StandaloneMode = true;
                Monitor.Log("[MP] ConfigSync timeout (5s) - host does not have mod. Entering standalone mode.", LogLevel.Warn);
                Game1.chatBox?.addInfoMessage("[BankMod] 检测到房主未安装此Mod，将以独立模式运行。");
            }
        }
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return;

        // V3.3 客机跳过日结算（除非在独立模式）
        if (!Context.IsMainPlayer && !_standaloneMode)
        {
            Monitor.Log("Skipping day-started logic (not main player, not standalone)");
            return;
        }
        int preStandaloneGold = Game1.player.Money;
        if (_standaloneMode)
            Monitor.Log("[MP] Running day-started in standalone mode", LogLevel.Info);

        Monitor.Log("New day started", LogLevel.Info);

        // Stage 8: Check overnight money gain (shipping income) for bankruptcy deduction
        var account = _services.BankAccountService.Load();
        if (account.IsInBankruptcy && Game1.player.Money > _lastMoneySnapshot)
        {
            int overnightGain = Game1.player.Money - _lastMoneySnapshot;
            int actualDeducted = _services.BankruptcyHandler.ApplyShippingIncomeDeduction(account, _config, overnightGain);
            if (actualDeducted > 0)
            {
                _services.BankAccountService.Save(account);
                Game1.chatBox?.addInfoMessage($"破产偿债：过夜出货收入 {overnightGain:N0}g 扣除 {actualDeducted:N0}g（{_config.BankruptcyIncomeDeduction * 100:F0}%）");
                Monitor.Log($"[Bankruptcy] Overnight: deducted {actualDeducted}g from {overnightGain}g shipping income", LogLevel.Info);
            }
        }

        DumpMailDebugInfo();

        string? phoneFlag = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);

        // Phone lost detection: player picked up phone (seen in inventory) but
        // it's gone now and was never put in a chest → truly discarded
        if (phoneFlag == "1" && _phoneSeenInInventory && !_phonePutInChest && !PlayerHasPhone())
        {
            Helper.Data.WriteSaveData(PhoneReceivedFlag, "0");
            phoneFlag = "0";
            _phoneReceivedToday = false;
            _lastReadMail = null;
            Monitor.Log("Phone confirmed lost (seen then missing, not in chest) — status reset to 0", LogLevel.Warn);
        }
        _phoneSeenInInventory = false;
        _phonePutInChest = false;

        if (phoneFlag != "1")
        {
            bool hasReceivedPhoneBefore = _hasReceivedPhoneInSession ||
                (Game1.player.mailReceived?.Contains(MailFlags.BankMod_Letter) == true);
            if (hasReceivedPhoneBefore && !Game1.player.mailbox.Contains(MailFlags.BankMod_Letter))
            {
                Game1.player.mailbox.Add(MailFlags.BankMod_Letter);
                _lastReadMail = null; // 新信件入队，重置读取检测状态
                Monitor.Log("Phone lost previously, sending new claim letter to mailbox", LogLevel.Info);
            }
        }

        // Delegate daily settlement to CompanyManager
        _services.CompanyManager.OnDayStarted();

        // Snapshot money for bankruptcy garnishment tracking
        if (_standaloneMode && Game1.player.Money != preStandaloneGold)
            Monitor.Log($"[MP] Standalone gold change: {preStandaloneGold} -> {Game1.player.Money} (delta={Game1.player.Money - preStandaloneGold})", LogLevel.Warn);
        _lastMoneySnapshot = Game1.player.Money;

        // Stage 13: Broadcast daily snapshot to all clients
        if (Context.IsMainPlayer && Game1.IsMultiplayer)
        {
            var snapAccount = _services.BankAccountService.Load();
            var snap = new Messages.BankSnapshot
            {
                IsInBankruptcy = snapAccount.IsInBankruptcy,
                PlayerMoney = Game1.player.Money,
                Companies = new List<Messages.CompanySnapshot>()
            };
            var allCompanies = _services.CompanyManager.GetAllCompanyDefinitions(snapAccount);
            foreach (var c in allCompanies)
            {
                var ca = snapAccount.CompanyAccounts.FirstOrDefault(a => a.CompanyName == c.Name);
                snap.Companies.Add(new Messages.CompanySnapshot
                {
                    Name = c.Name,
                    Status = account.DynamicCompanies.FirstOrDefault(d => d.CompanyName == c.Name)?.Status.ToString() ?? "Active",
                    DepositRate = c.DepositInterestRate,
                    LoanRate = c.LoanInterestRate,
                    DepositBalance = ca?.DepositBalance ?? 0,
                    LoanPrincipal = account.Loans.Where(l => l.CompanyName == c.Name).Sum(l => l.Principal)
                });
            }
            Helper.Multiplayer.SendMessage(snap, MsgBankSnapshot, null, null);
            Monitor.Log("[联机] 快照已广播", LogLevel.Debug);
        }
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

        // Stage 11 + 7.3: shop handling
        if (e.NewMenu is StardewValley.Menus.ShopMenu shopMenu && Context.IsMainPlayer)
        {
            string shopId = shopMenu.ShopId ?? "";
            int day = Game1.dayOfMonth;

            // Stage 7: Intercept Joja/Pierre shop → ask purpose
            bool isJojaOrPierre = shopId.Contains("Joja") || Game1.currentLocation?.Name == "JojaMart";
            bool isPierre = shopId == "SeedShop" || Game1.currentLocation?.Name == "SeedShop";
            bool isJoja = shopId.Contains("Joja") || Game1.currentLocation?.Name == "JojaMart";
            // CC route: Joja loses suppression; Joja route: Pierre loses suppression
            bool suppressDisabled = (isPierre && _services.JojaBoosted) || (isJoja && _services.PierreBoosted);
            if (isJojaOrPierre && !_shopIntercepted && !_shopSkipped)
            {
                _shopIntercepted = true;
                _pendingShopId = shopId;
                Game1.activeClickableMenu = null;
                var responses = new List<Response> { new("Shop", "购买商品") };
                if (!suppressDisabled)
                    responses.Add(new("Supply", "出售农产品（打压竞争对手）"));
                Game1.currentLocation.createQuestionDialogue(
                    "欢迎光临！请问您需要什么服务？",
                    responses.ToArray(),
                    (_, answer) =>
                    {
                        _shopIntercepted = false;
                        if (answer == "Supply")
                        {
                            Game1.activeClickableMenu = new JojaSupplyMenu(_services, Helper);
                        }
                        else
                        {
                            _shopSkipped = true;
                            Game1.drawObjectDialogue("好的，请稍等");
                        }
                    }
                );
                return;
            }

            // Reset interception flags when shop opens normally
            _shopIntercepted = false;
            _shopSkipped = false;

            // Stage 11: inject seasonal crops (days 1-3, Joja/Pierre)
            if (day >= 1 && day <= 3 && (shopId == "SeedShop" || shopId.Contains("Joja")))
                InjectSeasonalCrops(shopMenu, shopId);

            // Stage 7.3: snapshot inventory for sale detection
            _preShopInventory = Game1.player.Items
                .Where(item => item is StardewValley.Object)
                .GroupBy(item => item.QualifiedItemId)
                .ToDictionary(g => g.Key, g => g.Sum(item => item.Stack));

            // 判断是否为 Joja 商店（通过 ShopId 或当前所在位置）
            shopId = shopMenu.ShopId ?? "";
            _shopIsJoja = shopId.Contains("Joja", StringComparison.OrdinalIgnoreCase)
                || Game1.currentLocation?.Name == "JojaMart";
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
                        // Route check: CC → Joja blocked; Joja → Pierre blocked
                        bool blocked = (_shopIsJoja && _services.PierreBoosted) || (!_shopIsJoja && _services.JojaBoosted);
                        Monitor.Log($"[Stage7] Sale: shopIsJoja={_shopIsJoja}, PierreBoosted={_services.PierreBoosted}, JojaBoosted={_services.JojaBoosted}, blocked={blocked}", LogLevel.Info);
                        if (blocked)
                        {
                            Monitor.Log($"[Stage7] Sale BLOCKED — route effect suppresses this shop", LogLevel.Info);
                        }
                        else
                        {
                            int penalty = _services.FuelService.RecordExternalSale(account, cropCode, sold);
                            changed = true;
                            Monitor.Log($"[Stage7] External sale at {(_shopIsJoja ? "Joja" : "Pierre")}: {cropCode} x{sold} -> {penalty} FP penalty", LogLevel.Debug);

                            if (penalty > 0)
                            {
                                var company = account.DynamicCompanies.FirstOrDefault(c => c.CropCode == cropCode);
                                string competitor = company?.CompanyName ?? cropCode;
                                string cropDisplay = CropDataProvider.GetByCode(cropCode)?.DisplayName ?? cropCode;

                                bool hasBankLetter = _hasReceivedPhoneInSession ||
                                    (Game1.player?.mailReceived?.Contains(MailFlags.BankMod_Letter) == true);

                                string letterType = _shopIsJoja ? "morris" : "pierre";
                                if (_services.TriggerThanksLetter(account, letterType, cropCode, cropDisplay, competitor, hasBankLetter))
                                    UpdateMailCache(account);
                            }
                        }
                    }
                }
            }

            // Stage 11: detect seasonal crop purchases and apply fuel penalty
            int shopDay = Game1.dayOfMonth;
            if (shopDay >= 1 && shopDay <= 3 && (_shopIsJoja || _preShopInventory != null))
            {
                string shopKey = _shopIsJoja ? "Joja" : "Pierre";
                foreach (var item in Game1.player.Items)
                {
                    if (item is not StardewValley.Object obj) continue;
                    int preCount = 0;
                    if (_preShopInventory.TryGetValue(obj.QualifiedItemId, out var cnt))
                        preCount = cnt;
                    int bought = obj.Stack - preCount;
                    if (bought <= 0) continue;

                    string? cropCode = ShipmentTrackingService.NormalizeItemId(obj.QualifiedItemId);
                    if (cropCode is null) continue;

                    // Track daily purchase
                    if (!SeasonShopSold.ContainsKey(shopKey))
                        SeasonShopSold[shopKey] = new Dictionary<string, int>();
                    if (!SeasonShopSold[shopKey].ContainsKey(cropCode))
                        SeasonShopSold[shopKey][cropCode] = 0;
                    SeasonShopSold[shopKey][cropCode] += bought;

                    // Apply fuel penalty
                    var company = account.DynamicCompanies.FirstOrDefault(c => c.CropCode == cropCode);
                    if (company is not null)
                    {
                        company.FuelStock -= bought * 5;
                        if (company.FuelStock < 0) company.FuelStock = 0;
                        changed = true;
                    }
                }
            }

            if (changed)
                _services.BankAccountService.Save(account);

            _preShopInventory = null;
            return;
        }

        // 调试：记录所有菜单变化
        Monitor.Log($"[MailDebug] MenuChanged: NewMenu={e.NewMenu?.GetType().Name ?? "null"}", LogLevel.Info);

        if (e.NewMenu is LetterViewerMenu lvm)
        {
            Monitor.Log($"[MailDebug] LetterViewerMenu opened, mailTitle='{lvm.mailTitle}'", LogLevel.Info);

            // 当 BankMod 感谢信打开时，Data/mail 缓存可能还是占位符
            // 直接从存档读取真实内容，通过反射替换 LetterViewerMenu 内部的信件文本
            string title = lvm.mailTitle ?? "";
            if (title is MailFlags.BankMod_PierreThanks or MailFlags.BankMod_MorrisThanks)
            {
                try
                {
                    var acc = _services.BankAccountService.Load();
                    string? realText = title == MailFlags.BankMod_PierreThanks
                        ? acc.PierreThanksLetterText
                        : acc.MorrisThanksLetterText;

                    if (!string.IsNullOrEmpty(realText))
                    {
                        // 反射获取 LetterViewerMenu 的 mailMessage 字段并替换
                        var mailMsgField = typeof(LetterViewerMenu).GetField("mailMessage",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        if (mailMsgField != null)
                        {
                            // ^ 是星露谷信件换行符，^^ 是空行，^^^ 是自定义分页标记
                            // 先按 ^^^ 分页，再在每个页面内保留 ^ 和 ^^ 供游戏渲染引擎处理
                            string normalized = NormalizeMailText(realText);
                            string[] pages = normalized.Split(new[] { "^^^" }, StringSplitOptions.RemoveEmptyEntries);
                            mailMsgField.SetValue(lvm, new List<string>(pages));
                            Monitor.Log($"[MailDebug] Replaced {title} content via reflection ({pages.Length} pages)", LogLevel.Info);
                        }
                        else
                        {
                            Monitor.Log($"[MailDebug] Could not find mailMessage field on LetterViewerMenu", LogLevel.Warn);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[MailDebug] Failed to replace mail content: {ex.Message}", LogLevel.Warn);
                }
            }
        }

        if (e.OldMenu is MailboxDialog mailboxDialog)
        {
            Monitor.Log("Mailbox dialog closed", LogLevel.Debug);
            if (mailboxDialog.PhoneReceived)
                GivePhoneToPlayer();
        }
    }

    private bool _phoneSeenInInventory;
    private bool _phonePutInChest;

    // Stage 14: Route & cutscene
    private bool _lastEventActive;
    private bool _oldSavePending;

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!Context.IsWorldReady || _services.RouteService.SeenCutscene) return;
        if (e.NewLocation.Name != "AbandonedJojaMart") return;
        if (!Game1.player.hasCompletedCommunityCenter()) return;

        _services.RouteService.DetectRoute();
        _services.RouteService.ApplyRouteEffects();

        bool hasMovieTheater = Game1.player.hasOrWillReceiveMail(MailFlags.CC_MovieTheater) || Game1.player.mailReceived.Contains(MailFlags.CC_MovieTheater);
        if (hasMovieTheater)
        {
            _services.RouteService.MarkCutsceneSeen();
            _services.RouteService.UnlockOnlineShopping();
            return;
        }

        _services.RouteService.MarkCutsceneSeen();
        _services.EventScriptService.StartRouteEvent("献祭.txt", false);
    }

    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null) return;

        // Track: phone entered player inventory (from ground, mail, or cursor)
        if (e.Added.Any(item => item.QualifiedItemId == PhoneItem.QualifiedItemId))
        {
            _phoneSeenInInventory = true;
            Monitor.Log("[Phone] Phone entered inventory — _phoneSeenInInventory = true", LogLevel.Debug);
        }

        // Track: phone moved from inventory into a chest
        if (e.Removed.Any(item => item.QualifiedItemId == PhoneItem.QualifiedItemId)
            && Game1.activeClickableMenu is StardewValley.Menus.ItemGrabMenu)
        {
            _phonePutInChest = true;
            Monitor.Log("[Phone] Phone moved to chest — _phonePutInChest = true", LogLevel.Debug);
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

    // Stage 11: daily seasonal purchase tracker
    private static readonly Dictionary<string, Dictionary<string, int>> SeasonShopSold = new();
    private static string _seasonShopDayKey = "";

    private static void ResetSeasonShopDaily()
    {
        string today = $"{Game1.year}_{Game1.currentSeason}_{Game1.dayOfMonth}";
        if (_seasonShopDayKey != today)
        {
            SeasonShopSold.Clear();
            _seasonShopDayKey = today;
        }
    }

    /// <summary>Stage 11: Inject seasonal crops as limited-stock items into Joja/Pierre shops.</summary>
    private void InjectSeasonalCrops(StardewValley.Menus.ShopMenu shop, string shopId)
    {
        ResetSeasonShopDaily();
        string shopKey = shopId.Contains("Joja") ? "Joja" : "Pierre";

        // Route effects
        if ((shopKey == "Pierre" && _services.JojaBoosted) || (shopKey == "Joja" && _services.PierreBoosted)) return;
        int dailyLimit = (shopKey == "Pierre" && _services.PierreBoosted) || (shopKey == "Joja" && _services.JojaBoosted) ? 20 : 10;

        var bundleCrops = new HashSet<string> { "Parsnip", "Green Bean", "Cauliflower", "Potato", "Blueberry", "Melon", "Hot Pepper", "Tomato", "Corn", "Eggplant", "Pumpkin", "Yam", "Wheat", "Poppy", "Sunflower" };
        bool bundleUnlocked = Game1.player.hasOrWillReceiveMail(MailFlags.CC_Pantry) || Game1.player.hasOrWillReceiveMail(MailFlags.JojaMember);
        bool islandUnlocked = Game1.player.hasOrWillReceiveMail(MailFlags.WillyBoatFixed);

        string seasonKey = Game1.currentSeason switch { "spring" => "Spring", "summer" => "Summer", "fall" => "Fall", "winter" => "Winter", _ => Game1.currentSeason };
        int added = 0;

        foreach (var cd in CropDataProvider.AllCrops)
        {
            if (cd.R <= 0) continue;
            if (!cd.IsPurchasable) continue;
            if (!cd.Season.Contains(seasonKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (bundleCrops.Contains(cd.CropCode) && !bundleUnlocked) continue;
            if (cd.CropCode == "Starfruit" && !Game1.player.hasOrWillReceiveMail(MailFlags.CC_Vault)) continue;
            if ((cd.CropCode == "Banana" || cd.CropCode == "Mango" || cd.CropCode == "Pineapple" || cd.CropCode == "Taro") && !islandUnlocked) continue;

            int objId = cd.CropCode switch
            {
                "Carrot" => 78, "Strawberry" => 400, "Parsnip" => 24, "Green Bean" => 188, "Potato" => 192,
                "Garlic" => 248, "Unmilled Rice" => 271, "Cauliflower" => 190, "Kale" => 250,
                "Blue Jazz" => 597, "Rhubarb" => 252, "Tulip" => 591,
                "Blueberry" => 258, "Hops" => 304, "Hot Pepper" => 260, "Tomato" => 256, "Radish" => 264,
                "Red Cabbage" => 266, "Melon" => 254, "Summer Spangle" => 593, "Starfruit" => 268,
                "Beet" => 284, "Eggplant" => 272, "Artichoke" => 274, "Grape" => 398,
                "Pumpkin" => 276, "Yam" => 280, "Amaranth" => 300, "Bok Choy" => 278,
                "Cranberries" => 282, "Fairy Rose" => 595, "Powdermelon" => 0,
                "Wheat" => 262, "Ancient Fruit" => 454, "Corn" => 270, "Sunflower" => 421,
                "Summer Squash" => 0, "Broccoli" => 0, "Poppy" => 376,
                _ => 0
            };
            if (objId <= 0) continue;

            var item = ItemRegistry.Create($"(O){objId}");
            int basePrice = (item as StardewValley.Object)?.sellToStorePrice() ?? 0;
            if (basePrice <= 0) continue;

            int alreadySold = 0;
            if (SeasonShopSold.TryGetValue(shopKey, out var sd) && sd.TryGetValue(cd.CropCode, out var s))
                alreadySold = s;
            int remaining = dailyLimit - alreadySold;
            if (remaining <= 0) continue;

            int price = basePrice * 2;
            shop.itemPriceAndStock.Add(item, new StardewValley.ItemStockInformation(price, remaining));
            shop.forSale.Add(item);
            added++;
        }

        Monitor.Log($"[SeasonShop] Added {added} seasonal items to {shopKey} shop", LogLevel.Debug);
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

                    if (Game1.currentLocation is null) return;
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

        if (Game1.player?.CurrentItem?.QualifiedItemId != PhoneItem.QualifiedItemId) return;

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
            Helper.Data.WriteSaveData(MailFlags.Save_MorrisEventSeen, "1");

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
                if (!Game1.player.mailbox.Contains(MailFlags.BankMod_Letter))
                {
                    Game1.player.mailbox.Add(MailFlags.BankMod_Letter);
                    _lastReadMail = null; // 新信件入队，重置读取检测状态
                    Monitor.Log("Bank letter sent to mailbox", LogLevel.Info);
                }
            }
        }
        _lastLocationName = currentLocation;

        // Pierre/Morris 信件投递：独立于农场进入检测，pending 标志一设置就投递
        // Data/mail 中始终有占位条目，游戏一定能创建 LetterViewerMenu
        if (_pendingPierreMail || _services.PendingPierreMail)
        {
            _pendingPierreMail = false;
            _services.PendingPierreMail = false;
            if (!Game1.player.mailbox.Contains(MailFlags.BankMod_PierreThanks))
            {
                Game1.player.mailbox.Add(MailFlags.BankMod_PierreThanks);
                Monitor.Log("[MailDebug] Pierre mail added to mailbox", LogLevel.Info);
            }
        }
        if (_pendingMorrisMail || _services.PendingMorrisMail)
        {
            _pendingMorrisMail = false;
            _services.PendingMorrisMail = false;
            if (!Game1.player.mailbox.Contains(MailFlags.BankMod_MorrisThanks))
            {
                Game1.player.mailbox.Add(MailFlags.BankMod_MorrisThanks);
                Monitor.Log("[MailDebug] Morris mail added to mailbox", LogLevel.Info);
            }
        }

        if (_lastReadMail != MailFlags.BankMod_Letter && !Game1.player.mailbox.Contains(MailFlags.BankMod_Letter))
        {
            string? phoneFlag = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);
            if (phoneFlag != "1" && !_phoneReceivedToday)
            {
                Monitor.Log("Bank letter read - directly giving phone to player", LogLevel.Info);
                GivePhoneToPlayer();
                _lastReadMail = MailFlags.BankMod_Letter;
            }
        }

        // Stage 14: Event transition detection
        bool eventActive = Game1.CurrentEvent is not null;

        // Event-based route detection (Joja 502261 / CC 191393)
        if (eventActive && !_lastEventActive)
        {
            _services.RouteService.DetectRouteFromEvent(Game1.CurrentEvent.id);
            if (!string.IsNullOrEmpty(_services.RouteService.CompletedRoute))
                _services.RouteService.ApplyRouteEffects();
        }
        _lastEventActive = eventActive;

        // Show dialog after trigger (event-end / old save — Joja only; CC via OnWarped)
        if (!eventActive && !_services.RouteService.SeenCutscene && Game1.activeClickableMenu is null
            && (_oldSavePending || _services.RouteService.CompletedRoute == "Joja"))
        {
            _oldSavePending = false;
            _services.RouteService.MarkCutsceneSeen();
            _services.RouteService.SaveToSave(Helper);
            string file = _services.RouteService.CompletedRoute == "Joja" ? "joja.txt" : "献祭.txt";
            _services.EventScriptService.StartRouteEvent(file, _services.RouteService.CompletedRoute == "Joja");
        }

        // Mod event ended → unlock online shopping
        if (_services.RouteService.ModEventPlaying && !eventActive)
        {
            _services.RouteService.ModEventPlaying = false;
            _services.RouteService.UnlockOnlineShopping();
            _services.RouteService.SaveToSave(Helper);
        }

        // Stage 8 bankruptcy: garnish non-withdrawal money gains + banner
        var account = _services.BankAccountService.Load();
        if (account.IsInBankruptcy)
        {
            BankruptBanner.Show();
            if (!_bankruptExitMessageShown) _bankruptExitMessageShown = true; // reset exit message flag
        }
        else
        {
            BankruptBanner.Hide();
            if (_bankruptExitMessageShown)
            {
                _bankruptExitMessageShown = false;
                Game1.chatBox?.addInfoMessage("恭喜，享受你的春天吧！");
            }
        }

        if (account.IsInBankruptcy && Game1.player.Money > _lastMoneySnapshot)
        {
            int increase = Game1.player.Money - _lastMoneySnapshot;
            if (_services.ExemptNextMoneyIncrease)
            {
                _services.ExemptNextMoneyIncrease = false;
            }
            else
            {
                int actualDeducted = _services.BankruptcyHandler.ApplyShippingIncomeDeduction(account, _config, increase);
                if (actualDeducted > 0)
                {
                    _services.BankAccountService.Save(account);
                    if (_bankruptGarnishCooldown <= 0)
                    {
                        Game1.chatBox?.addInfoMessage($"破产偿债：收入 {increase:N0}g 扣除 {actualDeducted:N0}g（{_config.BankruptcyIncomeDeduction * 100:F0}%）");
                        _bankruptGarnishCooldown = 60; // ~1 second cooldown to avoid spam
                    }
                }
            }
        }
        if (_bankruptGarnishCooldown > 0)
            _bankruptGarnishCooldown--;
        _lastMoneySnapshot = Game1.player.Money;

    }

    /*********
    ** V3.3 联机事件处理器（阶段一埋点，当前仅日志输出）
    *********/
    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        Monitor.Log($"[MP] PeerConnected: ID={e.Peer.PlayerID} IsHost={e.Peer.IsHost} IsMainPlayer={Context.IsMainPlayer}", LogLevel.Info);
        if (Context.IsMainPlayer)
        {
            var syncMsg = new Messages.ConfigSyncMessage
            {
                JojaDepositRate = _config.Companies[0].DepositInterestRate,
                JojaLoanRate = _config.Companies[0].LoanInterestRate,
                PierreDepositRate = _config.Companies[1].DepositInterestRate,
                PierreLoanRate = _config.Companies[1].LoanInterestRate,
                EnableLuckInfluence = _config.EnableLuckInfluence,
                EnableWeatherInfluence = _config.EnableWeatherInfluence,
                UseCompoundInterest = _config.UseCompoundInterest,
                CompanySpawnRequiredSellCount = _config.CompanySpawnRequiredSellCount,
                BankruptcyIncomeDeduction = _config.BankruptcyIncomeDeduction
            };
            Helper.Multiplayer.SendMessage(syncMsg, MsgConfigSync, null, new[] { e.Peer.PlayerID });
            Monitor.Log($"[MP] ConfigSync sent to {e.Peer.PlayerID}: JojaDep={syncMsg.JojaDepositRate} PierreDep={syncMsg.PierreDepositRate}", LogLevel.Info);
        }
        else if (!_configSyncReceived)
        {
            // Guest: start 5-second timer to detect if host has the mod
            _standaloneCheckTicks = 5; // 5 seconds
            Monitor.Log("[MP] Guest waiting 5s for ConfigSync...", LogLevel.Info);
        }
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        Monitor.Log($"[MP] MessageReceived: Type={e.Type} From={e.FromPlayerID}", LogLevel.Debug);

        switch (e.Type)
        {
            case MsgConfigSync:
                if (!Context.IsMainPlayer && e.ReadAs<Messages.ConfigSyncMessage>() is { } cfg)
                {
                    _configSyncReceived = true;
                    _config.Companies[0].DepositInterestRate = cfg.JojaDepositRate;
                    _config.Companies[0].LoanInterestRate = cfg.JojaLoanRate;
                    _config.Companies[1].DepositInterestRate = cfg.PierreDepositRate;
                    _config.Companies[1].LoanInterestRate = cfg.PierreLoanRate;
                    _config.EnableLuckInfluence = cfg.EnableLuckInfluence;
                    _config.EnableWeatherInfluence = cfg.EnableWeatherInfluence;
                    _config.UseCompoundInterest = cfg.UseCompoundInterest;
                    _config.CompanySpawnRequiredSellCount = cfg.CompanySpawnRequiredSellCount;
                    _config.BankruptcyIncomeDeduction = cfg.BankruptcyIncomeDeduction;
                    Monitor.Log($"[MP] ConfigSync applied: JojaDep={cfg.JojaDepositRate} PierreDep={cfg.PierreDepositRate} Luck={cfg.EnableLuckInfluence} Weather={cfg.EnableWeatherInfluence}", LogLevel.Info);

                    // If we were in standalone mode, migrate our data to the host
                    if (_standaloneMode)
                    {
                        Monitor.Log("[MP] Migrating from standalone to networked mode — sending account data to host", LogLevel.Warn);
                        var migAccount = _services.BankAccountService.Load();
                        Helper.Multiplayer.SendMessage(migAccount, MsgAccountMigration, null, null);
                        _standaloneMode = false;
                        _services.StandaloneMode = false;
                        Game1.chatBox?.addInfoMessage("[BankMod] 检测到房主已安装Mod，已将本地数据同步至主机。");
                    }
                }
                else
                {
                    Monitor.Log($"[MP] ConfigSync ignored (IsMain={Context.IsMainPlayer})", LogLevel.Debug);
                }
                break;

            case MsgBankOperation:
                if (Context.IsMainPlayer && e.ReadAs<Messages.BankOperationRequest>() is { } req)
                {
                    Monitor.Log($"[MP] BankOperation received: Op={req.Operation} Co={req.CompanyName} Amt={req.Amount} Sender={req.SenderId} Target={req.TargetPlayer ?? "N/A"}", LogLevel.Info);
                    ProcessRemoteOperation(req);
                }
                else
                {
                    Monitor.Log($"[MP] BankOperation ignored (IsMain={Context.IsMainPlayer}, ReadOk={e.ReadAs<Messages.BankOperationRequest>() is not null})", LogLevel.Debug);
                }
                break;

            case MsgBankDataSync:
                if (!Context.IsMainPlayer && e.ReadAs<Messages.BankDataSync>() is { } sync)
                {
                    var account = _services.BankAccountService.Load();
                    var ca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == sync.CompanyName);
                    if (ca is not null)
                    {
                        int oldBal = ca.DepositBalance;
                        ca.DepositBalance = sync.NewDepositBalance;
                        ca.BaseAmount = sync.NewDepositBalance;
                        Monitor.Log($"[MP] BankDataSync applied: {sync.CompanyName} {oldBal}->{sync.NewDepositBalance} Loan={sync.NewLoanPrincipal}", LogLevel.Info);
                    }
                    else
                    {
                        Monitor.Log($"[MP] BankDataSync: company {sync.CompanyName} not found in account", LogLevel.Warn);
                    }
                    _services.BankAccountService.Save(account);
                }
                else
                {
                    Monitor.Log($"[MP] BankDataSync ignored (IsMain={Context.IsMainPlayer})", LogLevel.Debug);
                }
                break;

            case MsgAccountMigration:
                if (Context.IsMainPlayer && e.ReadAs<Data.BankAccountData>() is { } guestData)
                {
                    var hostAccount = _services.BankAccountService.Load();
                    Monitor.Log($"[MP] AccountMigration: merging guest data (guest={guestData.CompanyAccounts.Count}accts/{guestData.Loans.Count}loans host={hostAccount.CompanyAccounts.Count}accts/{hostAccount.Loans.Count}loans)", LogLevel.Warn);

                    // Additive merge: deposit/loan principal/FP accumulate; status recalculated from merged FP later
                    foreach (var gca in guestData.CompanyAccounts)
                    {
                        var hca = hostAccount.CompanyAccounts.FirstOrDefault(a => a.CompanyName == gca.CompanyName);
                        if (hca is not null)
                        {
                            hca.DepositBalance += gca.DepositBalance;
                            hca.BaseAmount += gca.BaseAmount;
                            hca.AccumulatedInterest += gca.AccumulatedInterest;
                            Monitor.Log($"[MP]   Merged {gca.CompanyName}: +{gca.DepositBalance} deposit +{gca.AccumulatedInterest} interest", LogLevel.Debug);
                        }
                        else
                        {
                            hostAccount.CompanyAccounts.Add(gca);
                        }
                    }
                    foreach (var gl in guestData.Loans)
                    {
                        var hl = hostAccount.Loans.FirstOrDefault(l => l.CompanyName == gl.CompanyName && l.DueDay == gl.DueDay);
                        if (hl is not null)
                        {
                            hl.Principal += gl.Principal;
                            hl.AccumulatedInterest += gl.AccumulatedInterest;
                            hl.OverdueInterest += gl.OverdueInterest;
                            Monitor.Log($"[MP]   Merged loan {gl.CompanyName}: +{gl.Principal} principal", LogLevel.Debug);
                        }
                        else
                        {
                            hostAccount.Loans.Add(gl);
                        }
                    }
                    foreach (var gdc in guestData.DynamicCompanies)
                    {
                        var hdc = hostAccount.DynamicCompanies.FirstOrDefault(c => c.CompanyName == gdc.CompanyName);
                        if (hdc is not null)
                        {
                            hdc.FuelStock += gdc.FuelStock;
                            Monitor.Log($"[MP]   Merged {gdc.CompanyName} fuel: +{gdc.FuelStock} FP", LogLevel.Debug);
                        }
                        else
                        {
                            hostAccount.DynamicCompanies.Add(gdc);
                        }
                    }
                    if (!hostAccount.IsInBankruptcy && guestData.IsInBankruptcy)
                        hostAccount.IsInBankruptcy = true;

                    _services.BankAccountService.Save(hostAccount);
                    Game1.chatBox?.addInfoMessage("[BankMod] 已接收客机独立运行期间的数据并合并。");
                    Monitor.Log($"[MP] AccountMigration complete: {hostAccount.CompanyAccounts.Count}accts/{hostAccount.Loans.Count}loans", LogLevel.Info);
                }
                break;

            case MsgBankSnapshot:
                if (!Context.IsMainPlayer && e.ReadAs<Messages.BankSnapshot>() is { } snap)
                {
                    var account = _services.BankAccountService.Load();
                    account.IsInBankruptcy = snap.IsInBankruptcy;
                    Monitor.Log($"[MP] Snapshot received: {snap.Companies.Count} companies, Bankrupt={snap.IsInBankruptcy}, HostMoney={snap.PlayerMoney}", LogLevel.Info);
                    foreach (var cs in snap.Companies)
                    {
                        var ca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == cs.Name);
                        if (ca is not null)
                        {
                            int oldBal = ca.DepositBalance;
                            ca.DepositBalance = cs.DepositBalance;
                            ca.BaseAmount = cs.DepositBalance;
                            Monitor.Log($"[MP]   Snapshot {cs.Name}: Bal {oldBal}->{cs.DepositBalance} Loan={cs.LoanPrincipal} Status={cs.Status}", LogLevel.Debug);
                        }
                    }
                    _services.BankAccountService.Save(account);
                }
                else
                {
                    Monitor.Log($"[MP] Snapshot ignored (IsMain={Context.IsMainPlayer})", LogLevel.Debug);
                }
                break;
        }
    }

    private readonly Dictionary<long, bool> _operationLocks = new();

    private void ProcessRemoteOperation(Messages.BankOperationRequest req)
    {
        if (_operationLocks.ContainsKey(req.SenderId))
        {
            Monitor.Log($"[联机] 操作被锁: {req.SenderId}", LogLevel.Warn);
            return;
        }
        _operationLocks[req.SenderId] = true;

        try
        {
            var account = _services.BankAccountService.Load();
            var company = _config.Companies.FirstOrDefault(c => c.Name == req.CompanyName);
            if (company is null) company = _services.CompanyManager.GetAllCompanyDefinitions(account)
                .FirstOrDefault(c => c.Name == req.CompanyName);
            if (company is null) return;

            var ca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == req.CompanyName);

            switch (req.Operation)
            {
                case "Deposit":
                    if (ca is not null && Game1.player.Money >= req.Amount)
                    {
                        Game1.player.Money -= req.Amount;
                        ca.DepositBalance += req.Amount;
                        ca.BaseAmount += req.Amount;
                    }
                    break;
                case "Withdraw":
                    if (ca is not null && ca.DepositBalance >= req.Amount)
                    {
                        ca.DepositBalance -= req.Amount;
                        ca.BaseAmount = Math.Max(0, ca.BaseAmount - req.Amount);
                        Game1.player.Money += req.Amount;
                    }
                    break;
                case "Borrow7":
                case "Borrow14":
                    int days = req.Operation == "Borrow7" ? 7 : 14;
                    _services.LoanService.IssueLoan(company, req.Amount, days, account, _config, _services.FixedInterestCalculator);
                    break;
                case "Repay":
                    var loan = _services.LoanService.GetLoan(account, req.CompanyName);
                    if (loan is not null) _services.LoanService.RepayLoan(loan, req.Amount, account);
                    break;
                case "Transfer":
                    var target = Game1.getAllFarmers().FirstOrDefault(f => f.Name == req.TargetPlayer);
                    if (target is not null && Game1.player.Money >= req.Amount)
                    {
                        Game1.player.Money -= req.Amount;
                        target.Money += req.Amount;
                    }
                    break;
            }

            _services.BankAccountService.Save(account);

            // Broadcast updated state
            var sync = new Messages.BankDataSync
            {
                SenderId = req.SenderId,
                CompanyName = req.CompanyName,
                NewDepositBalance = ca?.DepositBalance ?? 0,
                NewLoanPrincipal = account.Loans.Where(l => l.CompanyName == req.CompanyName).Sum(l => l.Principal)
            };
            Helper.Multiplayer.SendMessage(sync, MsgBankDataSync, null, null);
        }
        finally
        {
            _operationLocks.Remove(req.SenderId);
        }
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
            _services.HasReceivedPhoneInSession = true;
            Helper.Data.WriteSaveData(PhoneReceivedFlag, "1");
            // 标记银行欢迎信为已接收，确保后续 JojaSupplyMenu 等处的 mailReceived 检查通过
            if (!Game1.player.mailReceived.Contains(MailFlags.BankMod_Letter))
                Game1.player.mailReceived.Add(MailFlags.BankMod_Letter);
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

    public static string NormalizeMailTextPublic(string text) => NormalizeMailText(text);

    private static string NormalizeMailText(string text)
    {
        return text
            .Replace("：", ": ")
            .Replace("，", ", ")
            .Replace("。", ". ")
            .Replace("！", "! ")
            .Replace("？", "? ")
            .Replace("（", "(")
            .Replace("）", ")")
            .Replace("——", "--")
            .Replace("  ", " ");
    }

    /// <summary>
    /// 直接更新内存中 Data/mail 的信件内容，绕过 SMAPI AssetRequested 缓存问题。
    /// 在 TriggerThanksLetter 成功后调用，确保 LetterViewerMenu 能读到真实内容。
    /// </summary>
    private void UpdateMailCache(BankAccountData account)
    {
        try
        {
            var mailData = Helper.GameContent.Load<Dictionary<string, string>>("Data/mail");
            if (!string.IsNullOrEmpty(account.PierreThanksLetterText))
            {
                mailData[MailFlags.BankMod_PierreThanks] = NormalizeMailText(account.PierreThanksLetterText);
                Monitor.Log("[MailCache] Updated BankMod.PierreThanks in memory", LogLevel.Info);
            }
            if (!string.IsNullOrEmpty(account.MorrisThanksLetterText))
            {
                mailData[MailFlags.BankMod_MorrisThanks] = NormalizeMailText(account.MorrisThanksLetterText);
                Monitor.Log("[MailCache] Updated BankMod.MorrisThanks in memory", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"[MailCache] Failed to update mail cache: {ex.Message}", LogLevel.Warn);
        }
    }

    private void DumpMailDebugInfo()
    {
        var player = Game1.player;
        if (player is null) return;

        var mailbox = player.mailbox;
        var mailForTomorrow = player.mailForTomorrow;
        Monitor.Log($"[MailDebug] --- Day Start Mail Dump ---", LogLevel.Info);
        Monitor.Log($"[MailDebug] mailbox count={mailbox?.Count ?? -1}: [{string.Join(", ", mailbox ?? new())}]", LogLevel.Info);
        Monitor.Log($"[MailDebug] mailForTomorrow count={mailForTomorrow?.Count ?? -1}: [{string.Join(", ", mailForTomorrow ?? new())}]", LogLevel.Info);

        try
        {
            var mailData = Helper.GameContent.Load<Dictionary<string, string>>("Data/mail");
            Monitor.Log($"[MailDebug] Data/mail has BankMod_Letter={mailData.ContainsKey(MailFlags.BankMod_Letter)}, BankMod.PierreThanks={mailData.ContainsKey(MailFlags.BankMod_PierreThanks)}, BankMod.MorrisThanks={mailData.ContainsKey(MailFlags.BankMod_MorrisThanks)}", LogLevel.Info);
            if (mailData.TryGetValue(MailFlags.BankMod_PierreThanks, out var pt))
                Monitor.Log($"[MailDebug] BankMod.PierreThanks text len={pt?.Length ?? -1}", LogLevel.Info);
            if (mailData.TryGetValue(MailFlags.BankMod_MorrisThanks, out var mt))
                Monitor.Log($"[MailDebug] BankMod.MorrisThanks text len={mt?.Length ?? -1}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"[MailDebug] Error reading Data/mail: {ex.Message}", LogLevel.Error);
        }
    }

    private void ResetPhoneFlag(string command, string[] args)
    {
        Helper.Data.WriteSaveData(PhoneReceivedFlag, "0");
        _phoneReceivedToday = false;
        Game1.chatBox?.addInfoMessage("手机标志位已重置为0");
        Monitor.Log("Phone status reset to 0", LogLevel.Info);
    }

    private void TestBankMail(string command, string[] args)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            Monitor.Log("[bank_mail] 游戏尚未就绪", LogLevel.Warn);
            return;
        }

        string type = args.Length > 0 ? args[0].ToLower() : "";

        // 检测银行欢迎信是否已发送（以银行欢迎信为标志）
        // 控制台命令测试时：允许新存档直接测试，游戏内触发仍需检查
        bool hasBankLetter = _hasReceivedPhoneInSession || 
            (Game1.player.mailReceived?.Contains(MailFlags.BankMod_Letter) == true);
        
        // 控制台命令总是允许执行（方便测试），但会记录日志
        if (!hasBankLetter)
        {
            Monitor.Log("[bank_mail] 新存档检测到，跳过银行欢迎信检查（仅控制台测试）", LogLevel.Info);
            // 强制设置标志以便测试
            _hasReceivedPhoneInSession = true;
        }

        switch (type)
        {
            case "pierre":
            {
                // 检测是否已发送过
                var accP = _services.BankAccountService.Load();
                if (accP.PierreLetterSent)
                {
                    Monitor.Log("[bank_mail] Pierre 信已发送过，跳过", LogLevel.Info);
                    Game1.chatBox?.addInfoMessage("Pierre 信已发送过，请重新开存档测试");
                    return;
                }
                
                // 使用统一的触发方法设置信件内容
                _services.TriggerThanksLetter(accP, "pierre", "test_crop", "测试作物", "Joja超市", true);
                _services.BankAccountService.Save(accP);
                UpdateMailCache(accP);
                Monitor.Log("[bank_mail] Pierre mail queued for display", LogLevel.Info);
                Game1.chatBox?.addInfoMessage("Pierre 信已触发，将在下一帧显示");
                break;
            }

            case "morris":
            {
                // 检测是否已发送过
                var accM = _services.BankAccountService.Load();
                if (accM.MorrisLetterSent)
                {
                    Monitor.Log("[bank_mail] Morris 信已发送过，跳过", LogLevel.Info);
                    Game1.chatBox?.addInfoMessage("Morris 信已发送过，请重新开存档测试");
                    return;
                }
                
                // 使用统一的触发方法设置信件内容
                _services.TriggerThanksLetter(accM, "morris", "test_crop", "测试作物", "皮埃尔杂货店", true);
                _services.BankAccountService.Save(accM);
                UpdateMailCache(accM);
                Monitor.Log("[bank_mail] Morris mail queued for display", LogLevel.Info);
                Game1.chatBox?.addInfoMessage("Morris 信已触发，将在下一帧显示");
                break;
            }

            default:
                Game1.chatBox?.addInfoMessage("用法: bank_mail <pierre|morris>");
                return;
        }
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
