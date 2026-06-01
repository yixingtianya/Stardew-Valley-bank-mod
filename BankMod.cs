using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;
using HarmonyLib;
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
    private bool _lastBankruptcyState;
    private readonly Services.Core.DebtDialogueService _debtDialogue = new();
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

    private static string? _morrisEventScript;
    private static string MorrisEventScript => _morrisEventScript ??= // continue/27 7/farmer 27 7 1 Morris 22 7 1/
        "continue/27 7/farmer 27 7 1 Morris 22 7 1/" +
        "pause 500/fade/viewport 26 7/pause 400/" +
        "jump farmer/pause 800/" +
        "faceDirection farmer 3/pause 300/" +
        I18n.Get("mod.2") +
        "emote Morris 28/pause 300/" +
        I18n.Get("mod.3") +
        I18n.Get("mod.4") +
        I18n.Get("mod.5") +
        I18n.Get("mod.6");
    /*********
    ** Public methods
    *********/
    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        I18n.Init(helper.Translation);
        // Debug: show current locale and sample translation
        var locale = helper.Translation.Locale;
        var sampleKey = "mod.36";
        var sampleVal = I18n.Get(sampleKey);
        Monitor.Log($"[i18n] Locale='{locale}' (empty=English) | {sampleKey}='{sampleVal}'", LogLevel.Debug);

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
        helper.Events.Content.LocaleChanged += OnLocaleChanged;

        // Stage 15: NPC dialogue patch
        Harmony harmony = new("bankmod.npcdialogue");
        harmony.PatchAll(typeof(Patches.NpcDialoguePatch).Assembly);
        Patches.NpcDialoguePatch.Initialize(_debtDialogue, _config, () => _services.BankAccountService.Load());

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
            Monitor.Log(I18n.Get("mod.7"), LogLevel.Debug);
        };

        // Stage 10: TV financial channel Harmony patches
        var tvHarmony = new HarmonyLib.Harmony("bankmod.tv");
        tvHarmony.PatchAll(typeof(Patches.TVPatch).Assembly);
        Patches.FbnNewsGenerator.Initialize(_services, _config, Helper.DirectoryPath);
        Monitor.Log("[TV] FBN channel Harmony patches applied", LogLevel.Debug);

        // Console command: debug season shop
        helper.ConsoleCommands.Add("season_shop_debug", I18n.Get("mod.8"), (cmd, args) =>
        {
            if (!Context.IsWorldReady) { Monitor.Log(I18n.Get("mod.9"), LogLevel.Warn); return; }
            string season = Game1.currentSeason;
            string seasonKey = season switch { "spring" => "Spring", "summer" => "Summer", "fall" => "Fall", "winter" => "Winter", _ => season };
            Monitor.Log(I18n.Get("mod.10", new { day = Game1.dayOfMonth }), LogLevel.Info);

            var bundleCrops = new HashSet<string> { "Parsnip", "Green Bean", "Cauliflower", "Potato", "Blueberry", "Melon", "Hot Pepper", "Tomato", "Corn", "Eggplant", "Pumpkin", "Yam", "Wheat", "Poppy", "Sunflower" };

            var all = CropDataProvider.AllCrops;
            Monitor.Log(I18n.Get("mod.11", new { count = CropDataProvider.AllCrops.Count }), LogLevel.Info);

            foreach (var cd in all)
            {
                bool inSeason = cd.Season.Contains(seasonKey, StringComparison.OrdinalIgnoreCase);
                bool hasR = cd.R > 0;
                bool isPurchase = cd.IsPurchasable;
                bool notBundle = !bundleCrops.Contains(cd.CropCode);

                if (!inSeason) continue;
                Monitor.Log(I18n.Get("mod.12"), LogLevel.Info);
            }
        });

        // Console command: set company fuel
        helper.ConsoleCommands.Add("set_fuel", I18n.Get("mod.13"), (cmd, args) =>
        {
            if (args.Length < 2) { Monitor.Log(I18n.Get("mod.14"), LogLevel.Info); return; }
            if (!Context.IsWorldReady) { Monitor.Log(I18n.Get("mod.15"), LogLevel.Warn); return; }

            string name = args[0];
            if (!int.TryParse(args[1], out int fuel)) { Monitor.Log(I18n.Get("mod.16"), LogLevel.Warn); return; }

            var account = _services.BankAccountService.Load();
            var dc = account.DynamicCompanies.FirstOrDefault(
                c => c.CompanyName.Contains(name, StringComparison.OrdinalIgnoreCase)
                  || c.CropCode.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (dc is null) { Monitor.Log(I18n.Get("mod.17"), LogLevel.Warn); return; }

            dc.FuelStock = fuel;
            _services.BankAccountService.Save(account);
            Monitor.Log(I18n.Get("mod.18", new { fuel = fuel }), LogLevel.Info);
        });

        _lastLocationName = null;

        // Console commands
        helper.ConsoleCommands.Add("refresh_gmcm", "Refresh GMCM config menu labels for current language", (cmd, args) =>
        {
            RefreshGmcm();
            Monitor.Log("GMCM refresh complete. Close and reopen the config menu if it is open.", LogLevel.Info);
        });
        helper.ConsoleCommands.Add("spawn_phone", I18n.Command_SpawnPhone_Desc(), SpawnPhone);
        helper.ConsoleCommands.Add("debt_test", I18n.Get("mod.19"), (cmd, args) =>
        {
            if (args.Length < 2) { Monitor.Log(I18n.Get("mod.20"), LogLevel.Info); return; }
            string npcName = args[0];
            int hearts = int.TryParse(args[1], out int h) ? Math.Clamp(h, 0, 10) : 0;
            var account = _services.BankAccountService.Load();
            // Force bankruptcy for testing + clear tracking
            account.IsInBankruptcy = true;
            account.BankruptcyNpcGifted.Clear();
            account.DebtNpcSpoken.Clear();
            var npc = Game1.getCharacterFromName(npcName, mustBeVillager: false);
            if (npc != null)
            {
                Monitor.Log($"[DebtTest] Before: hearts={Game1.player.getFriendshipHeartLevelForNPC(npcName)}, target={hearts}", LogLevel.Info);
                // Directly set friendship data
                if (!Game1.player.friendshipData.ContainsKey(npcName))
                    Game1.player.friendshipData[npcName] = new Friendship(hearts * 250);
                else
                    Game1.player.friendshipData[npcName].Points = hearts * 250;
                Monitor.Log($"[DebtTest] After: hearts={Game1.player.getFriendshipHeartLevelForNPC(npcName)}", LogLevel.Info);
                _debtDialogue.TryInjectDialogue(npcName, account, _config);
            }
            _services.BankAccountService.Save(account);
            Monitor.Log($"[DebtTest] Simulated {npcName} dialogue at {hearts} hearts (bankruptcy mode)", LogLevel.Info);
        });
        helper.ConsoleCommands.Add("fbn_test", I18n.Get("mod.21"), (cmd, args) =>
        {
            bool isReal = args.Length > 0 && args[0] == "real";
            string? target = args.Length > 1 ? args[1] : null;
            var account = _services.BankAccountService.Load();
            var companies = account.DynamicCompanies.Where(c => c.Status != CompanyStatus.Bankrupt).ToList();
            if (companies.Count == 0) { Monitor.Log(I18n.Get("mod.22"), LogLevel.Warn); return; }
            var company = target != null
                ? companies.FirstOrDefault(c => c.CompanyName.Contains(target)) ?? companies[Random.Shared.Next(companies.Count)]
                : companies[Random.Shared.Next(companies.Count)];
            account.FbnEventCompany = company.CompanyName;
            account.FbnEventIsReal = isReal;
            account.FbnEventDay = Game1.dayOfMonth;
            account.FbnShowOutcome = false;
            if (isReal) account.FbnTempBoostCompany = company.CompanyName;
            _services.BankAccountService.Save(account); // Save() updates cache too
            Patches.FbnNewsGenerator.InvalidateCache();  // clear TV cache only
            Monitor.Log($"[FBN] {I18n.Get("mod.5")} {(isReal ? I18n.Get("mod.23") : I18n.Get("mod.24"))} {I18n.Get("mod.25")}: {company.CompanyName}, day={account.FbnEventDay}", LogLevel.Info);
        });
        helper.ConsoleCommands.Add("reset_phone_flag", I18n.Get("mod.25"), ResetPhoneFlag);
        helper.ConsoleCommands.Add("bank_mail", I18n.Get("mod.26"), TestBankMail);
        helper.ConsoleCommands.Add("i18n_audit", "Audit i18n translations: check for missing keys, mixed-language text, and garbled characters", (cmd, args) => RunI18nAudit());
        helper.ConsoleCommands.Add("company_debug", "Dump all company names with hex values and locale info", (cmd, args) => RunCompanyDebug());
        helper.ConsoleCommands.Add("layout_audit", "Audit BankMenu UI layout: measure all text widths vs container sizes, write report to layout_audit.txt", (cmd, args) => RunLayoutAudit());
        helper.ConsoleCommands.Add("loan_debug", "Dump raw loan/account/company data for debugging display name mismatches", (cmd, args) => RunLoanDebug());

        Monitor.Log(I18n.Get("mod.loaded", new { version = ModManifest.Version.ToString() }), LogLevel.Info);
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

    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e)
    {
        // Diagnose: compare I18n static ref vs fresh Helper.Translation
        var viaStatic = I18n.Get("mod.36");
        var viaFresh = Helper.Translation.Get("mod.36").ToString();
        Monitor.Log($"[i18n] Locale changed: '{e.OldLocale}' -> '{e.NewLocale}'", LogLevel.Info);
        Monitor.Log($"[i18n]   via I18n.Get:     '{viaStatic}'", LogLevel.Info);
        Monitor.Log($"[i18n]   via fresh Helper: '{viaFresh}'", LogLevel.Info);
        if (viaStatic != viaFresh)
            Monitor.Log("[i18n]   *** MISMATCH detected — I18n.Translations is stale! ***", LogLevel.Warn);

        // Refresh the static reference
        I18n.Init(Helper.Translation);

        RefreshGmcm();

        // Verify after refresh
        var afterRefresh = I18n.Get("mod.36");
        Monitor.Log($"[i18n]   after I18n.Init refresh: '{afterRefresh}'", LogLevel.Info);
    }

    private void RefreshGmcm()
    {
        if (_gmcmApi is null) return;
        try
        {
            _gmcmApi.Unregister(ModManifest);
            _gmcmApi.Register(ModManifest,
                reset: () => _config = new ModConfig(),
                save: () =>
                {
                    if (HasIllegalConfig())
                    {
                        _config = Helper.ReadConfig<ModConfig>();
                        _gmcmWarning = I18n.Get("mod.32");
                        _postSaveStep = 1;
                        Monitor.Log(I18n.Get("mod.33"), LogLevel.Warn);
                        return;
                    }
                    Helper.WriteConfig(_config);
                    _postSaveStep = 0;
                    Monitor.Log("GMCM config saved successfully", LogLevel.Info);
                }
            );
            RegisterModConfigOptions(_gmcmApi);
            Monitor.Log("[i18n] GMCM labels refreshed for new locale.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"[i18n] Failed to refresh GMCM: {ex.Message}", LogLevel.Error);
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
                        Name = "BankMod.Phone",
                        DisplayName = "[LocalizedText Strings\\Objects:BankMod.Phone]",
                        Price = 0,
                        Type = "Quest",
                        Category = -300,
                        Description = "[LocalizedText Strings\\Objects:BankMod.Phone_Desc]",
                        Texture = PhoneItem.TextureAssetName,
                        SpriteIndex = 0
                    };
                }
            });
        }

        // Inject localized phone name/description via Strings/Objects
        if (e.NameWithoutLocale.IsEquivalentTo("Strings/Objects"))
        {
            e.Edit(assets =>
            {
                var data = assets.AsDictionary<string, string>();
                data.Data["BankMod.Phone"] = I18n.Phone_Name();
                data.Data["BankMod.Phone_Desc"] = I18n.Phone_Description();
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
                mailData.Data[MailFlags.BankMod_Letter] = I18n.Get("mod.29");


                // Pierre/Morris 信件直接注册中文内容，由原版 LetterViewerMenu 渲染
                // SMAPI 中文版已替换 Game1.smallFont 为支持中文的字体
                if (!mailData.Data.ContainsKey(MailFlags.BankMod_PierreThanks))
                    mailData.Data[MailFlags.BankMod_PierreThanks] = I18n.Get("mod.30");
                if (!mailData.Data.ContainsKey(MailFlags.BankMod_MorrisThanks))
                    mailData.Data[MailFlags.BankMod_MorrisThanks] = I18n.Get("mod.31");

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
                    _gmcmWarning = I18n.Get("mod.32");
                    _postSaveStep = 1;
                    Monitor.Log(I18n.Get("mod.33"), LogLevel.Warn);
                    return;
                }

                Helper.WriteConfig(_config);
                _postSaveStep = 0;
                Monitor.Log("GMCM config saved successfully", LogLevel.Info);
            }
        );

        RegisterModConfigOptions(gmcm);
    }

    private void RegisterModConfigOptions(IGenericModConfigMenuApi gmcm)
    {
        // ============== 利率计算 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.34"), () => I18n.Get("mod.35"));
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.UseCompoundInterest,
            setValue: val => _config.UseCompoundInterest = val,
            name: () => I18n.Get("mod.36"),
            tooltip: () => I18n.Get("mod.37"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.CompoundDurationDays,
            setValue: val => _config.CompoundDurationDays = val,
            name: () => I18n.Get("mod.38"),
            tooltip: () => I18n.Get("mod.39"),
            min: 10, max: 21, interval: 1);
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.AllowNegativeInterest,
            setValue: val => _config.AllowNegativeInterest = val,
            name: () => I18n.Get("mod.40"),
            tooltip: () => I18n.Get("mod.41"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.DepositRateFloor * 100),
            setValue: val => _config.DepositRateFloor = val / 100f,
            name: () => I18n.Get("mod.42"),
            tooltip: () => I18n.Get("mod.43"),
            min: -5f, max: 0f, interval: 0.1f);

        // ============== 运气影响 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.44"), () => I18n.Get("mod.45"));
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.EnableLuckInfluence,
            setValue: val => _config.EnableLuckInfluence = val,
            name: () => I18n.Get("mod.46"),
            tooltip: () => I18n.Get("mod.47"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)_config.LuckStrengthCoefficient,
            setValue: val => _config.LuckStrengthCoefficient = val,
            name: () => I18n.Get("mod.48"),
            tooltip: () => I18n.Get("mod.49"),
            min: 0f, max: 5f, interval: 0.1f);

        // ============== 天气影响 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.50"), () => I18n.Get("mod.51"));
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.EnableWeatherInfluence,
            setValue: val => _config.EnableWeatherInfluence = val,
            name: () => I18n.Get("mod.52"),
            tooltip: () => I18n.Get("mod.53"));

        // ============== 固定公司 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.54"), () => I18n.Get("mod.55"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.Companies[0].DepositInterestRate * 100),
            setValue: val => _config.Companies[0].DepositInterestRate = val / 100f,
            name: () => I18n.Get("mod.56"), tooltip: () => I18n.Get("mod.57"),
            min: 1f, max: 10f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.Companies[0].LoanInterestRate * 100),
            setValue: val => _config.Companies[0].LoanInterestRate = val / 100f,
            name: () => I18n.Get("mod.58"), tooltip: () => I18n.Get("mod.59"),
            min: 2f, max: 15f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.Companies[0].DepositLimit,
            setValue: val => _config.Companies[0].DepositLimit = val,
            name: () => I18n.Get("mod.60"), tooltip: () => I18n.Get("mod.61"),
            min: 100000, max: 10000000, interval: 100000);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.Companies[0].LoanLimit,
            setValue: val => _config.Companies[0].LoanLimit = val,
            name: () => I18n.Get("mod.62"), tooltip: () => I18n.Get("mod.63"),
            min: 50000, max: 1000000, interval: 10000);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.Companies[1].DepositInterestRate * 100),
            setValue: val => _config.Companies[1].DepositInterestRate = val / 100f,
            name: () => I18n.Get("mod.64"), tooltip: () => I18n.Get("mod.57"),
            min: 0.5f, max: 8f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.Companies[1].LoanInterestRate * 100),
            setValue: val => _config.Companies[1].LoanInterestRate = val / 100f,
            name: () => I18n.Get("mod.65"), tooltip: () => I18n.Get("mod.59"),
            min: 1f, max: 12f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.Companies[1].DepositLimit,
            setValue: val => _config.Companies[1].DepositLimit = val,
            name: () => I18n.Get("mod.66"), tooltip: () => I18n.Get("mod.67"),
            min: 100000, max: 10000000, interval: 100000);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.Companies[1].LoanLimit,
            setValue: val => _config.Companies[1].LoanLimit = val,
            name: () => I18n.Get("mod.68"), tooltip: () => I18n.Get("mod.69"),
            min: 50000, max: 1000000, interval: 10000);

        // 跨公司套利保护：仅在非法时显示警告
        gmcm.AddParagraph(ModManifest, () =>
        {
            if (!HasIllegalConfig())
                return string.Empty;
            return I18n.Get("mod.70");
        });

        // ============== 动态公司 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.71"), () => I18n.Get("mod.72"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.CompanySpawnRequiredSellCount,
            setValue: val => _config.CompanySpawnRequiredSellCount = val,
            name: () => I18n.Get("mod.73"), tooltip: () => I18n.Get("mod.74"),
            min: 10, max: 500, interval: 10);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.CompanySellTrackingWindowDays,
            setValue: val => _config.CompanySellTrackingWindowDays = val,
            name: () => I18n.Get("mod.75"), tooltip: () => I18n.Get("mod.76"),
            min: 3, max: 14, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.DepositLoanCoefficient,
            setValue: val => _config.DepositLoanCoefficient = val,
            name: () => I18n.Get("mod.77"), tooltip: () => I18n.Get("mod.78"),
            min: 1000, max: 10000, interval: 500);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.LoanRateHardCap * 100),
            setValue: val => _config.LoanRateHardCap = val / 100f,
            name: () => I18n.Get("mod.79"), tooltip: () => I18n.Get("mod.80"),
            min: 10f, max: 20f, interval: 1f);

        // ============== 连续售卖与打压 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.81"), () => I18n.Get("mod.82"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.ConsecutiveSellDepositBonusPerDay * 100),
            setValue: val => _config.ConsecutiveSellDepositBonusPerDay = val / 100f,
            name: () => I18n.Get("mod.83"), tooltip: () => I18n.Get("mod.84"),
            min: 0f, max: 1f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.ConsecutiveSellDepositBonusCap * 100),
            setValue: val => _config.ConsecutiveSellDepositBonusCap = val / 100f,
            name: () => I18n.Get("mod.85"), tooltip: () => I18n.Get("mod.86"),
            min: 0f, max: 10f, interval: 0.5f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.ConsecutiveSellLoanBonusPerDay * 100),
            setValue: val => _config.ConsecutiveSellLoanBonusPerDay = val / 100f,
            name: () => I18n.Get("mod.87"), tooltip: () => I18n.Get("mod.88"),
            min: 0f, max: 2f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.ConsecutiveSellLoanBonusCap * 100),
            setValue: val => _config.ConsecutiveSellLoanBonusCap = val / 100f,
            name: () => I18n.Get("mod.89"), tooltip: () => I18n.Get("mod.90"),
            min: 0f, max: 20f, interval: 1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.SuppressMaxDropPerDay * 100),
            setValue: val => _config.SuppressMaxDropPerDay = val / 100f,
            name: () => I18n.Get("mod.91"), tooltip: () => I18n.Get("mod.92"),
            min: -5f, max: 0f, interval: 0.5f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.SuppressEffectDurationDays,
            setValue: val => _config.SuppressEffectDurationDays = val,
            name: () => I18n.Get("mod.93"), tooltip: () => I18n.Get("mod.94"),
            min: 1, max: 14, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.SuppressMaxStacks,
            setValue: val => _config.SuppressMaxStacks = val,
            name: () => I18n.Get("mod.95"), tooltip: () => I18n.Get("mod.96"),
            min: 1, max: 5, interval: 1);

        // ============== 贷款与债务 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.97"), () => I18n.Get("mod.98"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.LongTermRateDiscount * 100),
            setValue: val => _config.LongTermRateDiscount = val / 100f,
            name: () => I18n.Get("mod.99"), tooltip: () => I18n.Get("mod.100"),
            min: 0f, max: 2f, interval: 0.1f);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.PrincipalDebtGraceDays,
            setValue: val => _config.PrincipalDebtGraceDays = val,
            name: () => I18n.Get("mod.101"), tooltip: () => I18n.Get("mod.102"),
            min: 0, max: 3, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.PenaltyInterestRate * 100),
            setValue: val => _config.PenaltyInterestRate = val / 100f,
            name: () => I18n.Get("mod.103"), tooltip: () => I18n.Get("mod.104"),
            min: 0f, max: 50f, interval: 5f);

        // ============== 破产保护 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.105"), () => I18n.Get("mod.106"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.BankruptcyIncomeDeduction * 100),
            setValue: val => _config.BankruptcyIncomeDeduction = val / 100f,
            name: () => I18n.Get("mod.107"), tooltip: () => I18n.Get("mod.108"),
            min: 20, max: 80, interval: 5);

        // ============== 自然倒闭风险（阶段十五） ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.109"), () => I18n.Get("mod.110"));
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.DisableNaturalBankruptcy,
            setValue: val => _config.DisableNaturalBankruptcy = val,
            name: () => I18n.Get("mod.111"),
            tooltip: () => I18n.Get("mod.112"));
        gmcm.AddTextOption(ModManifest,
            getValue: () => (_config.ProsperousAnnualRisk * 100).ToString("F0") + "%",
            setValue: val =>
            {
                if (double.TryParse(val.TrimEnd('%'), out double p))
                    _config.ProsperousAnnualRisk = Math.Clamp(p / 100.0, 0.05, 1.0);
            },
            name: () => I18n.Get("mod.113"),
            tooltip: () => I18n.Get("mod.114"),
            allowedValues: new[] { "5%", "15%", "22%", "30%", "40%", "50%" });

        // ============== 债券交易（阶段九） ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.115"), () => I18n.Get("mod.116"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.DebtTransferRateDiscount * 100),
            setValue: val => _config.DebtTransferRateDiscount = val / 100f,
            name: () => I18n.Get("mod.117"), tooltip: () => I18n.Get("mod.108"),
            min: 10, max: 90, interval: 5);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.DebtTransferNewRepaymentDays,
            setValue: val => _config.DebtTransferNewRepaymentDays = val,
            name: () => I18n.Get("mod.118"), tooltip: () => I18n.Get("mod.119"),
            min: 14, max: 56, interval: 7);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.ProsperousReturnRate * 100),
            setValue: val => _config.ProsperousReturnRate = val / 100f,
            name: () => I18n.Get("mod.120"), tooltip: () => I18n.Get("mod.121"),
            min: 0, max: 100, interval: 5);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.StableReturnRate * 100),
            setValue: val => _config.StableReturnRate = val / 100f,
            name: () => I18n.Get("mod.122"), tooltip: () => I18n.Get("mod.108"),
            min: 0, max: 100, interval: 5);
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.123"), () => I18n.Get("mod.124"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.ProsperousLoanLimitMult,
            setValue: val => _config.ProsperousLoanLimitMult = val,
            name: () => I18n.Get("mod.125"), tooltip: () => I18n.Get("mod.126"),
            min: 1, max: 20, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.StableLoanLimitMult,
            setValue: val => _config.StableLoanLimitMult = val,
            name: () => I18n.Get("mod.127"), tooltip: () => I18n.Get("mod.128"),
            min: 1, max: 20, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.HungryLoanLimitMult,
            setValue: val => _config.HungryLoanLimitMult = val,
            name: () => I18n.Get("mod.129"), tooltip: () => I18n.Get("mod.130"),
            min: 1, max: 20, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.DyingLoanLimitMult,
            setValue: val => _config.DyingLoanLimitMult = val,
            name: () => I18n.Get("mod.131"), tooltip: () => I18n.Get("mod.132"),
            min: 1, max: 20, interval: 1);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.RescueInvestmentCapRatio * 100),
            setValue: val => _config.RescueInvestmentCapRatio = val / 100f,
            name: () => I18n.Get("mod.133"), tooltip: () => I18n.Get("mod.108"),
            min: 10, max: 100, interval: 5);

        // ============== 借款上限 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.134"), () => I18n.Get("mod.135"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.BorrowingLeverageCoefficient * 100),
            setValue: val => _config.BorrowingLeverageCoefficient = val / 100f,
            name: () => I18n.Get("mod.136"), tooltip: () => I18n.Get("mod.137"),
            min: 100, max: 500, interval: 50);
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (int)(_config.FixedCompanyQuotaRatio * 100),
            setValue: val => _config.FixedCompanyQuotaRatio = val / 100f,
            name: () => I18n.Get("mod.138"), tooltip: () => I18n.Get("mod.139"),
            min: 50, max: 80, interval: 5);

        // ============== 多人模式 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.140"), () => I18n.Get("mod.141"));
        gmcm.AddTextOption(ModManifest,
            getValue: () => _config.TransferFeeMode,
            setValue: val => _config.TransferFeeMode = val,
            name: () => I18n.Get("mod.142"),
            tooltip: () => I18n.Get("mod.143"),
            allowedValues: new[] { "percentage", "fixed" });
        gmcm.AddNumberOption(ModManifest,
            getValue: () => (float)(_config.TransferFeeValue * 100),
            setValue: val => _config.TransferFeeValue = val / 100f,
            name: () => I18n.Get("mod.144"),
            tooltip: () => I18n.Get("mod.145"),
            min: 0f, max: 5f, interval: 0.1f);

        // ============== 显示设置 ==============
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.146"), () => I18n.Get("mod.147"));
        gmcm.AddNumberOption(ModManifest,
            getValue: () => _config.RateDisplayPrecision,
            setValue: val => _config.RateDisplayPrecision = val,
            name: () => I18n.Get("mod.148"), tooltip: () => I18n.Get("mod.149"),
            min: 1, max: 4, interval: 1);
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.ShowMorningInterestNotification,
            setValue: val => _config.ShowMorningInterestNotification = val,
            name: () => I18n.Get("mod.150"), tooltip: () => I18n.Get("mod.151"));
        gmcm.AddBoolOption(ModManifest,
            getValue: () => _config.ShowCompanyDangerWarning,
            setValue: val => _config.ShowCompanyDangerWarning = val,
            name: () => I18n.Get("mod.152"), tooltip: () => I18n.Get("mod.153"));

        // ============== 作物利率表（子页面） ==============
        gmcm.AddPageLink(ModManifest, "CropRatesTable",
            text: () => I18n.Get("mod.154"),
            tooltip: () => I18n.Get("mod.155"));

        gmcm.AddPage(ModManifest, "CropRatesTable",
            pageTitle: () => I18n.Get("mod.156"));

        // Build crop rate text dynamically from authoritative data source
        static string FormatCropLine(CropDataRecord c)
        {
            double dep = c.R > 0.20 ? c.R * 0.5 : c.R > 0 ? c.R : 0;
            double loan = c.R > 0.20 ? c.R * 0.75 : c.R > 0 ? c.R * 1.5 : 0;
            if (loan < 0.01) loan = 0.01; // hard floor
            return I18n.Get("mod.157", new { name = c.DisplayName, dep = (dep * 100).ToString("F2"), loan = (loan * 100).ToString("F2"), fuel = c.DBase.ToString("F1"), cap = c.Smax.ToString("F0") });
        }

        var allCrops = CropDataProvider.AllCrops;

        // Tier 1: R > 20%
        var tier1 = allCrops.Where(c => c.R > 0.20).OrderByDescending(c => c.R).ToList();
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.158", new { count = tier1.Count }),
            () => I18n.Get("mod.159"));
        foreach (var c in tier1)
            gmcm.AddParagraph(ModManifest, () => FormatCropLine(c));

        // Tier 2: 10% < R ≤ 20%
        var tier2 = allCrops.Where(c => c.R > 0.10 && c.R <= 0.20).OrderByDescending(c => c.R).ToList();
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.160", new { count = tier2.Count }),
            () => I18n.Get("mod.161"));
        foreach (var c in tier2)
            gmcm.AddParagraph(ModManifest, () => FormatCropLine(c));

        // Tier 3: 0% < R ≤ 10%
        var tier3 = allCrops.Where(c => c.R > 0 && c.R <= 0.10).OrderByDescending(c => c.R).ToList();
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.162", new { count = tier3.Count }),
            () => I18n.Get("mod.161"));
        foreach (var c in tier3)
            gmcm.AddParagraph(ModManifest, () => FormatCropLine(c));

        // Tier 4: R ≤ 0%
        var tier4 = allCrops.Where(c => c.R <= 0).OrderBy(c => c.R).ToList();
        gmcm.AddSectionTitle(ModManifest, () => I18n.Get("mod.163", new { count = tier4.Count }),
            () => I18n.Get("mod.164"));
        if (tier4.Count > 0)
            foreach (var c in tier4)
                gmcm.AddParagraph(ModManifest, () => FormatCropLine(c));
        else
            gmcm.AddParagraph(ModManifest, () => I18n.Get("mod.165"));

        // Summary footer
        gmcm.AddParagraph(ModManifest, () =>
            I18n.Get("mod.166", new { total = allCrops.Count, source = "V3.5 patch5", floor = "1%" }));
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return;

        _phoneReceivedToday = false;
        _lastReadMail = null;
        _lastMoneySnapshot = Game1.player?.Money ?? 0;

        // Reset for new save: invalidate cache first, then load fresh
        _services.BankAccountService.InvalidateCache();
        var startAccount = _services.BankAccountService.Load();
        _lastBankruptcyState = startAccount.IsInBankruptcy;

        // Ensure online shop list has all required entries (migration for old configs)
        foreach (var sid in new[] { "DesertTrade", "Sandy", "QiGemShop" })
            if (!_config.OnlineShopList.Contains(sid))
                _config.OnlineShopList.Add(sid);

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
                Game1.chatBox?.addInfoMessage(I18n.Get("mod.167"));
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
                Game1.chatBox?.addInfoMessage(I18n.Get("mod.168", new { amount = actualDeducted }));
                Monitor.Log($"[Bankruptcy] Overnight: deducted {actualDeducted}g from {overnightGain}g shipping income", LogLevel.Info);
            }
        }

        DumpMailDebugInfo();

        string? phoneFlag = Helper.Data.ReadSaveData<string>(PhoneReceivedFlag);

        // Phone lost detection: phoneFlag=="1" means player received phone before;
        // if phone is gone and not stored in a chest → truly discarded → resend letter
        bool phoneInChest = Helper.Data.ReadSaveData<string>("BankMod_PhoneInChest") == "1";
        if (phoneFlag == "1" && !phoneInChest && !PlayerHasPhone())
        {
            Helper.Data.WriteSaveData(PhoneReceivedFlag, "0");
            phoneFlag = "0";
            _phoneReceivedToday = false;
            _lastReadMail = null;
            Monitor.Log("Phone confirmed lost (not in inventory, not in chest) — status reset to 0", LogLevel.Warn);
        }

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
                Companies = new List<Messages.CompanySnapshot>(),
                InterestLogSeason = snapAccount.InterestLogSeason,
                SeasonInterestLog = snapAccount.SeasonInterestLog
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
                    LoanPrincipal = account.Loans.Where(l => l.CompanyName == c.Name).Sum(l => l.Principal),
                    AccumulatedInterest = ca?.AccumulatedInterest ?? 0,
                    BaseAmount = ca?.BaseAmount ?? 0
                });
            }
            Helper.Multiplayer.SendMessage(snap, MsgBankSnapshot, null, null);
            Monitor.Log($"[MP] Snapshot broadcast: {snap.Companies.Count} companies, {snap.SeasonInterestLog.Count} interest log records", LogLevel.Debug);
        }
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        // SafeDialogueBox dismissed → flag re-open for next tick (avoids re-entrancy)
        if (_postSaveStep == 3 && e.OldMenu is SafeDialogueBox && e.NewMenu is null)
        {
            _postSaveStep = 4;
            Monitor.Log(I18n.Get("mod.170"), LogLevel.Info);
            return;
        }

        // Stage 11 + 7.3: shop handling
        if (e.NewMenu is StardewValley.Menus.ShopMenu shopMenu && Context.IsMainPlayer)
        {
            string shopId = shopMenu.ShopId ?? "";
            int day = Game1.dayOfMonth;

            // Stage 7: Intercept Joja/Pierre shop → ask purpose
            // Intercept if route completed OR Morris supply event has been seen (Joja only)
            if (string.IsNullOrEmpty(_services.CompletedRoute) && !_morrisEventSeen) return;

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
                var responses = new List<Response> { new("Shop", I18n.Get("mod.171")) };
                if (!suppressDisabled)
                    responses.Add(new("Supply", I18n.Get("mod.172")));
                Game1.currentLocation.createQuestionDialogue(
                    I18n.Get("mod.173"),
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
                            Game1.drawObjectDialogue(I18n.Get("mod.174"));
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
                                string competitor = CropDataProvider.GetByCode(cropCode)?.DisplayName ?? cropCode;
                                string cropDisplay = competitor;

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
        _services.EventScriptService.StartRouteEvent(I18n.Get("mod.175"), false);
    }

    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null) return;

        // Track: phone entered player inventory (from ground, mail, or cursor)
        if (e.Added.Any(item => item.QualifiedItemId == PhoneItem.QualifiedItemId))
        {
            // Phone taken into inventory → clear chest flag
            Helper.Data.WriteSaveData("BankMod_PhoneInChest", "0");
            Monitor.Log("[Phone] Phone entered inventory — chest flag cleared", LogLevel.Debug);
        }

        // Track: phone moved from inventory into a chest
        if (e.Removed.Any(item => item.QualifiedItemId == PhoneItem.QualifiedItemId)
            && Game1.activeClickableMenu is StardewValley.Menus.ItemGrabMenu)
        {
            // Phone stored in chest → set persistent flag so loss detection won't trigger
            Helper.Data.WriteSaveData("BankMod_PhoneInChest", "1");
            Monitor.Log("[Phone] Phone moved to chest — chest flag set", LogLevel.Debug);
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
                Monitor.Log(I18n.Get("mod.176"), LogLevel.Info);
            }
            _postSaveStep = 2;
            return;
        }
        else if (_postSaveStep == 2)
        {
            Game1.activeClickableMenu = new SafeDialogueBox(_gmcmWarning);
            _postSaveStep = 3;
            Monitor.Log(I18n.Get("mod.177"), LogLevel.Info);
            return;
        }
        else if (_postSaveStep == 4)
        {
            _postSaveStep = 0;
            _gmcmApi?.OpenModMenu(ModManifest);
            Monitor.Log(I18n.Get("mod.178"), LogLevel.Info);
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
            string file = _services.RouteService.CompletedRoute == "Joja" ? "joja.txt" : I18n.Get("mod.175");
            _services.EventScriptService.StartRouteEvent(file, _services.RouteService.CompletedRoute == "Joja");
        }

        // Mod event ended → unlock online shopping
        if (_services.RouteService.ModEventPlaying && !eventActive)
        {
            _services.RouteService.ModEventPlaying = false;
            _services.RouteService.UnlockOnlineShopping();
            _services.RouteService.SaveToSave(Helper);
        }

        // Stage 8 bankruptcy: garnish + banner (state-change detection — no cross-save pollution)
        var account = _services.BankAccountService.Load();
        if (account.IsInBankruptcy)
            BankruptBanner.Show();
        else
            BankruptBanner.Hide();

        if (_lastBankruptcyState && !account.IsInBankruptcy)
        {
            Game1.chatBox?.addInfoMessage(I18n.Get("mod.179"));
            _debtDialogue.TryCongratulate(account);
            _services.BankAccountService.Save(account);
        }
        // Clear NPC tracking on bankruptcy entry
        if (!_lastBankruptcyState && account.IsInBankruptcy)
        {
            account.BankruptcyNpcGifted.Clear();
            _services.BankAccountService.Save(account);
        }
        _lastBankruptcyState = account.IsInBankruptcy;

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
                        Game1.chatBox?.addInfoMessage(I18n.Get("mod.180", new { amount = actualDeducted }));
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
                        Game1.chatBox?.addInfoMessage(I18n.Get("mod.181"));
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
                        ca.BaseAmount = sync.NewBaseAmount;
                        ca.AccumulatedInterest = sync.NewAccumulatedInterest;
                        Monitor.Log($"[MP] BankDataSync applied: {sync.CompanyName} {oldBal}->{sync.NewDepositBalance} Base={sync.NewBaseAmount} AccInt={sync.NewAccumulatedInterest} Loan={sync.NewLoanPrincipal}", LogLevel.Info);
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
                    Game1.chatBox?.addInfoMessage(I18n.Get("mod.182"));
                    Monitor.Log($"[MP] AccountMigration complete: {hostAccount.CompanyAccounts.Count}accts/{hostAccount.Loans.Count}loans", LogLevel.Info);
                }
                break;

            case MsgBankSnapshot:
                if (!Context.IsMainPlayer && e.ReadAs<Messages.BankSnapshot>() is { } snap)
                {
                    var account = _services.BankAccountService.Load();
                    account.IsInBankruptcy = snap.IsInBankruptcy;
                    // Sync interest log data
                    account.InterestLogSeason = snap.InterestLogSeason;
                    account.SeasonInterestLog = snap.SeasonInterestLog;
                    Monitor.Log($"[MP] Snapshot received: {snap.Companies.Count} companies, Bankrupt={snap.IsInBankruptcy}, HostMoney={snap.PlayerMoney}, InterestLog={snap.SeasonInterestLog.Count} records", LogLevel.Info);
                    foreach (var cs in snap.Companies)
                    {
                        var ca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == cs.Name);
                        if (ca is not null)
                        {
                            int oldBal = ca.DepositBalance;
                            ca.DepositBalance = cs.DepositBalance;
                            ca.BaseAmount = cs.BaseAmount;
                            ca.AccumulatedInterest = cs.AccumulatedInterest;
                            Monitor.Log($"[MP]   Snapshot {cs.Name}: Bal {oldBal}->{cs.DepositBalance} Base={cs.BaseAmount} AccInt={cs.AccumulatedInterest} Loan={cs.LoanPrincipal} Status={cs.Status}", LogLevel.Debug);
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
            Monitor.Log(I18n.Get("mod.183"), LogLevel.Warn);
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
                    var loan = _services.LoanService.GetLoan(account, company);
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
                NewLoanPrincipal = account.Loans.Where(l => l.CompanyName == req.CompanyName).Sum(l => l.Principal),
                NewAccumulatedInterest = ca?.AccumulatedInterest ?? 0,
                NewBaseAmount = ca?.BaseAmount ?? 0
            };
            Helper.Multiplayer.SendMessage(sync, MsgBankDataSync, null, null);
            Monitor.Log($"[MP] BankDataSync broadcast: {req.CompanyName} Bal={sync.NewDepositBalance} Base={sync.NewBaseAmount} AccInt={sync.NewAccumulatedInterest}", LogLevel.Debug);
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
            Monitor.Log(I18n.Get("mod.184"), LogLevel.Alert);
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
        Game1.chatBox?.addInfoMessage(I18n.Get("mod.185"));
        Monitor.Log("Phone status reset to 0", LogLevel.Info);
    }

    private void TestBankMail(string command, string[] args)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            Monitor.Log(I18n.Get("mod.186"), LogLevel.Warn);
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
            Monitor.Log(I18n.Get("mod.187"), LogLevel.Info);
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
                    Monitor.Log(I18n.Get("mod.188"), LogLevel.Info);
                    Game1.chatBox?.addInfoMessage(I18n.Get("mod.189"));
                    return;
                }
                
                // 使用统一的触发方法设置信件内容
                _services.TriggerThanksLetter(accP, "pierre", "test_crop", I18n.Get("mod.190"), I18n.Get("mod.191"), true);
                _services.BankAccountService.Save(accP);
                UpdateMailCache(accP);
                Monitor.Log("[bank_mail] Pierre mail queued for display", LogLevel.Info);
                Game1.chatBox?.addInfoMessage(I18n.Get("mod.192"));
                break;
            }

            case "morris":
            {
                // 检测是否已发送过
                var accM = _services.BankAccountService.Load();
                if (accM.MorrisLetterSent)
                {
                    Monitor.Log(I18n.Get("mod.193"), LogLevel.Info);
                    Game1.chatBox?.addInfoMessage(I18n.Get("mod.194"));
                    return;
                }
                
                // 使用统一的触发方法设置信件内容
                _services.TriggerThanksLetter(accM, "morris", "test_crop", I18n.Get("mod.190"), I18n.Get("mod.195"), true);
                _services.BankAccountService.Save(accM);
                UpdateMailCache(accM);
                Monitor.Log("[bank_mail] Morris mail queued for display", LogLevel.Info);
                Game1.chatBox?.addInfoMessage(I18n.Get("mod.196"));
                break;
            }

            default:
                Game1.chatBox?.addInfoMessage(I18n.Get("mod.197"));
                return;
        }
    }

    /// <summary>Run a comprehensive i18n audit: check for missing keys, mixed-language text, and garbled characters.</summary>
    private void RunI18nAudit()
    {
        string i18nDir = Path.Combine(Helper.DirectoryPath, "i18n");
        Monitor.Log("=== i18n Audit ===", LogLevel.Info);

        // 1. Parse both JSON files
        var defaultKeys = ParseI18nJson(Path.Combine(i18nDir, "default.json"));
        var zhKeys = ParseI18nJson(Path.Combine(i18nDir, "zh.json"));

        if (defaultKeys is null || zhKeys is null)
        {
            Monitor.Log("[AUDIT] Failed to parse i18n JSON files!", LogLevel.Error);
            return;
        }

        Monitor.Log($"[AUDIT] default.json: {defaultKeys.Count} keys, zh.json: {zhKeys.Count} keys", LogLevel.Info);

        // 2. Scan source code for all I18n.Get("key") references
        // Try multiple possible source locations (deployed mod won't have .cs files)
        var sourceKeys = new HashSet<string>();
        var searchDirs = new List<string> { Helper.DirectoryPath };
        // Also try going up from mod dir to find project source
        string? parent = Path.GetDirectoryName(Helper.DirectoryPath);
        for (int i = 0; i < 4 && parent != null; i++)
        {
            if (Directory.GetFiles(parent, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
            {
                searchDirs.Add(parent);
                break;
            }
            parent = Path.GetDirectoryName(parent);
        }

        int csFilesScanned = 0;
        foreach (string dir in searchDirs)
        {
            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                csFilesScanned++;
                string content = File.ReadAllText(file);
                var getMatches = System.Text.RegularExpressions.Regex.Matches(content, @"I18n\.Get\(""([^""]+)""");
                for (int mi = 0; mi < getMatches.Count; mi++)
                    sourceKeys.Add(getMatches[mi].Groups[1].Value);
                var directMatches = System.Text.RegularExpressions.Regex.Matches(content, @"(?:Translations[\!\?]\.)?Get\(""([^""]+)""");
                for (int mi = 0; mi < directMatches.Count; mi++)
                    sourceKeys.Add(directMatches[mi].Groups[1].Value);
            }
        }
        Monitor.Log($"[AUDIT] Scanned {csFilesScanned} .cs files, found {sourceKeys.Count} unique i18n keys", LogLevel.Info);

        int issues = 0;

        // 3. Check: keys used in code missing from JSON files
        if (sourceKeys.Count > 0)
        {
            foreach (var key in sourceKeys.OrderBy(k => k))
            {
                if (!defaultKeys.ContainsKey(key))
                {
                    Monitor.Log($"[MISSING EN] Key '{key}' used in code but NOT in default.json", LogLevel.Error);
                    issues++;
                }
                if (!zhKeys.ContainsKey(key))
                {
                    Monitor.Log($"[MISSING ZH] Key '{key}' used in code but NOT in zh.json", LogLevel.Error);
                    issues++;
                }
            }

            // 4. Check: keys in JSON but not used in source code (informational only)
            var unusedInCode = defaultKeys.Keys.Except(sourceKeys).ToList();
            if (unusedInCode.Count > 0)
            {
                Monitor.Log($"[INFO] {unusedInCode.Count} keys in default.json not found in source (may be used dynamically)", LogLevel.Debug);
            }
        }
        else
        {
            Monitor.Log("[AUDIT] Source files not found — skipping code-to-JSON cross-check", LogLevel.Info);
        }

        // 5. Check: keys in one file but not the other
        var onlyInDefault = defaultKeys.Keys.Except(zhKeys.Keys).ToList();
        var onlyInZh = zhKeys.Keys.Except(defaultKeys.Keys).ToList();
        if (onlyInDefault.Count > 0)
        {
            foreach (var key in onlyInDefault.OrderBy(k => k))
            {
                Monitor.Log($"[ONLY EN] Key '{key}' exists in default.json but NOT in zh.json", LogLevel.Warn);
                issues++;
            }
        }
        if (onlyInZh.Count > 0)
        {
            foreach (var key in onlyInZh.OrderBy(k => k))
            {
                Monitor.Log($"[ONLY ZH] Key '{key}' exists in zh.json but NOT in default.json", LogLevel.Warn);
                issues++;
            }
        }

        // 6. Check: empty values
        foreach (var kvp in defaultKeys)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                Monitor.Log($"[EMPTY EN] Key '{kvp.Key}' has empty value in default.json", LogLevel.Warn);
                issues++;
            }
        }
        foreach (var kvp in zhKeys)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                Monitor.Log($"[EMPTY ZH] Key '{kvp.Key}' has empty value in zh.json", LogLevel.Warn);
                issues++;
            }
        }

        // 7. Check: mixed-language in zh.json (Chinese text with embedded Latin words that look garbled)
        // Only flag truly suspicious cases — game/brand names are expected
        var allowedLatin = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Brand / proper nouns
            "Joja", "JojaMart", "Joja Mart", "Morris", "Pierre", "Fbn",
            // NPC names
            "Marnie", "Abigail", "Linus",
            // Game / mod terms
            "GMCM", "FP", "HQ", "QC", "SMAPI", "NPC", "Boss", "Basic",
            // Acronyms & abbreviations
            "CEO", "V3", "V35",
            // SMAPI dialogue command & code tokens
            "speak", "isReal",
            // Common in translations (file extensions, math, user-facing English)
            "Bug", "min", "txt", "json", "Daily", "Mart",
            // Code patterns (console commands, placeholders, tokens)
            "FBN", "set", "fuel", "debt", "test", "fbn", "bank", "mail",
            "real", "fake", "mod", "config", "BankMod", "Compound",
            "Patch", "TV2", "TV3", "Patches",
            // Multi-letter tokens in template strings
            "count", "total", "source", "floor", "name", "dep", "loan", "cap",
            "version", "farm",
            // Author names
            "yixingtianya"
        };
        var mixedZhWarnings = 0;
        foreach (var kvp in zhKeys)
        {
            string val = kvp.Value;
            bool hasCjk = System.Text.RegularExpressions.Regex.IsMatch(val, @"[一-鿿]");
            bool hasLatin = System.Text.RegularExpressions.Regex.IsMatch(val, @"[a-zA-Z]{2,}");
            if (!hasCjk || !hasLatin) continue;

            var latinMatches = System.Text.RegularExpressions.Regex.Matches(val, @"[a-zA-Z]{3,}");
            foreach (System.Text.RegularExpressions.Match lm in latinMatches)
            {
                if (!allowedLatin.Contains(lm.Value))
                {
                    Monitor.Log($"[MIXED ZH] Key '{kvp.Key}' has unexpected Latin '{lm.Value}': {Truncate(val, 70)}", LogLevel.Warn);
                    mixedZhWarnings++;
                    break; // one warning per key
                }
            }
        }
        if (mixedZhWarnings == 0)
            Monitor.Log("[AUDIT] No suspicious mixed-language text in zh.json", LogLevel.Info);

        // 8. Check: Chinese characters in default.json (should be English-only)
        int cnInEn = 0;
        foreach (var kvp in defaultKeys)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(kvp.Value, @"[一-鿿]"))
            {
                Monitor.Log($"[CHINESE IN EN] Key '{kvp.Key}': {Truncate(kvp.Value, 80)}", LogLevel.Error);
                issues++;
                cnInEn++;
            }
        }
        if (cnInEn == 0)
            Monitor.Log("[AUDIT] No Chinese characters found in default.json", LogLevel.Info);

        // 9. Check: characters that may not render in Stardew Valley bitmap font
        var fontWarnings = new System.Text.StringBuilder();
        foreach (var kvp in defaultKeys)
        {
            foreach (char c in kvp.Value)
            {
                if (c > 0x7E && c < 0xA0) continue;
                if (c > 0xFF && c != '—' && c != '–' && c != '‘' && c != '’'
                    && c != '“' && c != '”' && c != '…' && c != '°'
                    && c != '≤' && c != '≥' && c != '⚠')
                {
                    fontWarnings.Append(c);
                }
            }
            if (fontWarnings.Length > 0)
            {
                var unique = new string(fontWarnings.ToString().Distinct().ToArray());
                Monitor.Log($"[FONT EN] Key '{kvp.Key}': non-standard chars [{unique}]: {Truncate(kvp.Value, 60)}", LogLevel.Debug);
                fontWarnings.Clear();
            }
        }

        // Summary
        Monitor.Log("=== Audit Complete ===", LogLevel.Info);
        Monitor.Log($"Total issues found: {issues}", LogLevel.Info);
        Monitor.Log($"Keys in default.json (EN): {defaultKeys.Count}", LogLevel.Info);
        Monitor.Log($"Keys in zh.json (ZH): {zhKeys.Count}", LogLevel.Info);
        Monitor.Log($"Keys in source code: {sourceKeys.Count}", LogLevel.Info);
        if (sourceKeys.Count == 0)
            Monitor.Log("Tip: Run this command in dev mode with source files present for full code cross-check.", LogLevel.Info);
    }

    /// <summary>Dump all company names with hex values and locale info for debugging garbled text.</summary>
    private void RunCompanyDebug()
    {
        Monitor.Log("=== Company Debug ===", LogLevel.Info);
        Monitor.Log($"Locale: '{LocalizedContentManager.CurrentLanguageString}'", LogLevel.Info);

        var account = _services.BankAccountService.Load();

        // Config companies
        Monitor.Log($"--- Config Companies ({_config.Companies.Count}) ---", LogLevel.Info);
        for (int i = 0; i < _config.Companies.Count; i++)
        {
            var c = _config.Companies[i];
            Monitor.Log($"  [{i}] Name='{c.Name}' Hex={HexDump(c.Name)}", LogLevel.Info);
        }

        // Dynamic companies
        Monitor.Log($"--- Dynamic Companies ({account.DynamicCompanies.Count}) ---", LogLevel.Info);
        for (int i = 0; i < account.DynamicCompanies.Count; i++)
        {
            var dc = account.DynamicCompanies[i];
            Monitor.Log($"  [{i}] Name='{dc.CompanyName}' Crop={dc.CropCode} Status={dc.Status} Hex={HexDump(dc.CompanyName)}", LogLevel.Info);
        }

        // Loans
        Monitor.Log($"--- Loans ({account.Loans.Count}) ---", LogLevel.Info);
        for (int i = 0; i < account.Loans.Count; i++)
        {
            var l = account.Loans[i];
            Monitor.Log($"  [{i}] Company='{l.CompanyName}' Principal={l.Principal} Hex={HexDump(l.CompanyName)}", LogLevel.Info);
        }

        // Company accounts
        Monitor.Log($"--- Company Accounts ({account.CompanyAccounts.Count}) ---", LogLevel.Info);
        for (int i = 0; i < account.CompanyAccounts.Count; i++)
        {
            var ca = account.CompanyAccounts[i];
            Monitor.Log($"  [{i}] Name='{ca.CompanyName}' Hex={HexDump(ca.CompanyName)}", LogLevel.Info);
        }

        // Key i18n values
        Monitor.Log($"--- Key i18n Translations ---", LogLevel.Info);
        Monitor.Log($"  rte.1 (Pierre's Subsidiary): '{I18n.Get("rte.1")}'", LogLevel.Info);
        Monitor.Log($"  rte.2 (Pierre):              '{I18n.Get("rte.2")}'", LogLevel.Info);
        Monitor.Log($"  rte.3 (Joja Subsidiary):     '{I18n.Get("rte.3")}'", LogLevel.Info);
        Monitor.Log($"  cmp.6 (Company):             '{I18n.Get("cmp.6")}'", LogLevel.Info);

        // RouteService state
        Monitor.Log($"--- Route State ---", LogLevel.Info);
        Monitor.Log($"  CompletedRoute: '{_services.RouteService.CompletedRoute}'", LogLevel.Info);
        Monitor.Log($"  PierreBoosted: {_services.PierreBoosted}", LogLevel.Info);
        Monitor.Log($"  JojaBoosted: {_services.JojaBoosted}", LogLevel.Info);

        Monitor.Log("=== End Debug ===", LogLevel.Info);
    }

    private static string HexDump(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s)
            sb.Append($"U+{(int)c:X4} ");
        return sb.ToString().Trim();
    }

    /// <summary>Parse a simple i18n JSON file into a dictionary. Handles escaped quotes and backslashes.</summary>
    private static Dictionary<string, string>? ParseI18nJson(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            var result = new Dictionary<string, string>();

            // Match key-value pairs: "key": "value"
            // Value can contain escaped characters like \\\" and \\
            var pattern = System.Text.RegularExpressions.Regex.Replace(
                json,
                @"""((?:[^""\\]|\\.)*)""\s*:\s*""((?:[^""\\]|\\.)*)""",
                match =>
                {
                    string key = match.Groups[1].Value;
                    string val = match.Groups[2].Value
                        .Replace("\\n", "\n")
                        .Replace("\\t", "\t")
                        .Replace("\\\\", "\\")
                        .Replace("\\\"", "\"");
                    result[key] = val;
                    return "";
                });
            return result;
        }
        catch (Exception ex)
        {
            // Log error but return null
            System.Diagnostics.Debug.WriteLine($"Failed to parse {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Truncate a string to maxLen with ellipsis.</summary>
    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    /// <summary>Dump raw loan/account/company data for debugging name mismatches.</summary>
    private void RunLoanDebug()
    {
        if (!Context.IsWorldReady) { Monitor.Log("loan_debug: must be in-game with a save loaded", LogLevel.Warn); return; }

        var lines = new List<string>();
        var account = _services.BankAccountService.Load();
        var companies = _services.CompanyManager.GetAllCompanyDefinitions(account);

        lines.Add("=== LOAN DEBUG REPORT ===");
        lines.Add($"Locale: {Helper.Translation.Locale}");
        lines.Add($"IsInBankruptcy: {account.IsInBankruptcy}");
        lines.Add($"IsInPrincipalDebt: {account.IsInPrincipalDebt}");
        lines.Add($"IsInInterestDebt: {account.IsInInterestDebt}");
        lines.Add($"Total loans: {account.Loans.Count}");
        lines.Add($"Total company accounts: {account.CompanyAccounts.Count}");
        lines.Add($"Total dynamic companies: {account.DynamicCompanies.Count}");
        lines.Add("");

        // Dynamic companies raw data
        lines.Add("--- Dynamic Companies (raw save data) ---");
        foreach (var dc in account.DynamicCompanies)
        {
            var cropData = CropDataProvider.GetByCode(dc.CropCode);
            string derivedDisplay = cropData != null ? cropData.DisplayName + I18n.Get("cmp.6") : "(unknown)";
            lines.Add($"  CompanyName=\"{dc.CompanyName}\" | CropCode=\"{dc.CropCode}\" | Status={dc.Status}");
            lines.Add($"    → derived display: \"{derivedDisplay}\" | name==cropCode: {dc.CompanyName == dc.CropCode}");
        }
        lines.Add("");

        // Company definitions (what UI sees)
        lines.Add("--- Company Definitions (what UI uses) ---");
        foreach (var c in companies)
        {
            string display = _services.RouteService.GetDisplayName(c);
            lines.Add($"  Name=\"{c.Name}\" | OriginalName=\"{c.OriginalName}\" | CropCode=\"{c.CropCode}\" | IsDynamic={c.IsDynamic}");
            lines.Add($"    → DisplayName=\"{display}\"");
        }
        lines.Add("");

        // Fixed companies from config (raw)
        lines.Add("--- Fixed Companies from Config (raw _config.Companies) ---");
        foreach (var c in _config.Companies)
        {
            lines.Add($"  Name=\"{c.Name}\" | OriginalName=\"{c.OriginalName}\"");
        }
        lines.Add("");

        // Check GetCompanyLoans for each company definition
        lines.Add("--- GetCompanyLoans results (simulating UI lookup) ---");
        foreach (var c in companies)
        {
            var loans = _services.LoanService.GetCompanyLoans(account, c);
            lines.Add($"  {c.Name} (IsDynamic={c.IsDynamic}): {loans.Count} loan(s)");
            foreach (var l in loans)
                lines.Add($"    Principal={l.Principal} | Frozen={l.IsFrozen} | Default={l.IsInDefault}");
        }
        lines.Add("");

        // Loan records
        lines.Add("--- Loan Records ---");
        if (account.Loans.Count == 0)
        {
            lines.Add("  (no loan records)");
        }
        // Hardcoded known translations for migration check
        var knownTranslations = new Dictionary<string, string>
        {
            ["Joja超市"] = "JojaMart",
            ["皮埃尔杂货店"] = "Pierre's General Store",
        };
        foreach (var l in account.Loans)
        {
            lines.Add($"  CompanyName=\"{l.CompanyName}\" | Principal={l.Principal} | Interest={l.AccumulatedInterest} | Overdue={l.OverdueInterest}");
            lines.Add($"    IsFrozen={l.IsFrozen} | IsInDefault={l.IsInDefault} | IsInInterestDebt={l.IsInInterestDebt} | DueDay={l.DueDay}");
            // Try to match against company definitions
            var matched = companies.FirstOrDefault(c => c.Name == l.CompanyName);
            var matchedCrop = companies.FirstOrDefault(c => c.CropCode == l.CompanyName);
            var matchedKnown = knownTranslations.TryGetValue(l.CompanyName, out var knownOrig) ? knownOrig : "NONE";
            lines.Add($"    match by Name: {(matched != null ? matched.Name : "NONE")} | match by CropCode: {(matchedCrop != null ? matchedCrop.Name : "NONE")} | KnownTranslation→{matchedKnown}");
        }
        lines.Add("");

        // Company accounts
        lines.Add("--- Company Accounts ---");
        if (account.CompanyAccounts.Count == 0)
        {
            lines.Add("  (no company accounts)");
        }
        foreach (var ca in account.CompanyAccounts)
        {
            lines.Add($"  CompanyName=\"{ca.CompanyName}\" | Deposit={ca.DepositBalance} | Interest={ca.AccumulatedInterest}");
            var matched = companies.FirstOrDefault(c => c.Name == ca.CompanyName);
            var matchedCrop = companies.FirstOrDefault(c => c.CropCode == ca.CompanyName);
            lines.Add($"    match by Name: {(matched != null ? matched.Name : "NONE")} | match by CropCode: {(matchedCrop != null ? matchedCrop.Name : "NONE")}");
        }
        lines.Add("");

        // Bankruptcy diagnosis
        lines.Add("--- Bankruptcy Diagnosis ---");
        if (account.IsInBankruptcy)
        {
            lines.Add("  Player IS in bankruptcy. Checking what loans exist...");
            if (account.Loans.Count == 0)
                lines.Add("  ⚠ NO LOAN RECORDS but IsInBankruptcy=true — possible data mismatch!");
            else
            {
                foreach (var l in account.Loans)
                {
                    bool matched = companies.Any(c => c.Name == l.CompanyName || c.CropCode == l.CompanyName);
                    lines.Add($"  Loan for \"{l.CompanyName}\" → matched in UI: {matched}");
                }
            }
        }
        else
        {
            lines.Add("  Player is NOT in bankruptcy.");
        }

        string reportPath = Path.Combine(Helper.DirectoryPath, "loan_debug.txt");
        File.WriteAllLines(reportPath, lines);
        Monitor.Log($"[LoanDebug] Written to {reportPath} ({lines.Count} lines)", LogLevel.Info);
    }

    /// <summary>Audit BankMenu UI layout: measure every i18n text width vs its container, flag overflows, write report to layout_audit.txt.</summary>
    private void RunLayoutAudit()
    {
        if (!Context.IsWorldReady) { Monitor.Log("layout_audit: must be in-game with a save loaded", LogLevel.Warn); return; }

        var account = _services.BankAccountService.Load();
        var lines = new List<string>();
        int overflowCount = 0;

        lines.Add("=== BankMod UI Layout Audit ===");
        lines.Add($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"Locale: {Helper.Translation.Locale}");
        lines.Add($"Window: 720 x 820 (BankMenu constant)");
        lines.Add($"SmallFont: 'A' = {Game1.smallFont.MeasureString("A").X:F1}px wide, {Game1.smallFont.MeasureString("A").Y:F1}px tall");
        lines.Add("");

        // ---- Section 1: Warning banner ----
        lines.Add("--- Section 1: Warning Banner (max width = 680px) ---");
        int maxWarnWidth = 680;
        foreach (var key in new[] { "uib.1", "uib.2", "uib.3" })
        {
            string text = I18n.Get(key);
            float w = Game1.smallFont.MeasureString(text).X;
            int wrapLines = (int)Math.Ceiling(w / maxWarnWidth);
            bool ov = w > maxWarnWidth; if (ov) overflowCount++;
            lines.Add($"  [{key}] \"{text}\" → {w:F1}px, wraps={wrapLines} {(ov ? "OVERFLOW" : "OK")}");
        }
        lines.Add("");

        // ---- Section 2: Company tabs ----
        lines.Add("--- Section 2: Company Tabs (tabH=30, padding=24) ---");
        var companies = _services.CompanyManager.GetAllCompanyDefinitions(account);
        int maxTabArea = 660;
        foreach (var c in companies)
        {
            string name = _services.RouteService.GetDisplayName(c);
            float tw = Game1.smallFont.MeasureString(name).X;
            int tabW = (int)tw + 24;
            bool wide = tabW > maxTabArea / 3; if (wide) overflowCount++;
            lines.Add($"  [{c.OriginalName}] \"{name}\" → text={tw:F1}px, tab={tabW}px {(wide ? "WIDE" : "OK")}");
        }
        lines.Add("");

        // ---- Section 3: Info lines (max 640px) ----
        lines.Add("--- Section 3: Info Lines (max width = 640px, lineH = 26px) ---");
        int maxInfo = 640;
        var infoKeys = new[] {
            "uib.4","uib.5","uib.6","uib.7","uib.8","uib.9","uib.10","uib.11","uib.12",
            "uib.13","uib.14","uib.15","uib.16","uib.17","uib.18","uib.19","uib.20","uib.21","uib.22","uib.23",
            "uib.24","uib.25","uib.26","uib.27","uib.28","uib.29","uib.30","uib.31","uib.32","uib.33","uib.34",
            "uib.35","uib.36","uib.37","uib.38","uib.39","uib.40","uib.41","uib.42","uib.43","uib.44"
        };
        foreach (var key in infoKeys)
        {
            string text = I18n.Get(key);
            string sample = text
                .Replace("{days}", "7").Replace("{daysLeft}", "5").Replace("{dayOfSeason}", "12")
                .Replace("{units}", "50.0").Replace("{baseUnit}", "Crop")
                .Replace("{rate}", "5.00%").Replace("{interest}", "150")
                .Replace("{count}", "3").Replace("{overdueDays}", "5")
                .Replace("{remainingDays}", "9").Replace("{graceDays}", "2")
                .Replace("{principal}", "5000");
            float w = Game1.smallFont.MeasureString(sample).X;
            bool ov = w > maxInfo; if (ov) overflowCount++;
            lines.Add($"  [{key}] \"{sample}\" → {w:F1}px {(ov ? "OVERFLOW" : "OK")}");
        }
        lines.Add("");

        // ---- Section 4: Buttons (btnW=150) ----
        lines.Add("--- Section 4: Buttons (btnW=150, btnH=40) ---");
        var btnKeys = new[] {
            "uib.45","uib.46","uib.47","uib.48","uib.37","uib.49","uib.50","uib.51",
            "uib.52","uib.53","uib.54","uib.55","uib.56"
        };
        foreach (var key in btnKeys)
        {
            string text = I18n.Get(key);
            string sample = text.Replace("{days}", "7");
            float w = Game1.smallFont.MeasureString(sample).X;
            bool ov = w > 138; if (ov) overflowCount++;
            lines.Add($"  [{key}] \"{sample}\" → {w:F1}px {(ov ? "OVERFLOW (text>button)" : "OK")}");
        }
        // Row2 combos
        lines.Add("  Row2 combos (max window=720):");
        int[] comboSizes = { 2*150+10, 3*150+20, 4*150+30, 5*150+40 };
        string[] comboNames = { "2btn", "3btn", "4btn", "5btn" };
        for (int i = 0; i < comboSizes.Length; i++)
        {
            bool fit = comboSizes[i] <= 720; if (!fit) overflowCount++;
            lines.Add($"    {comboNames[i]}: {comboSizes[i]}px {(fit ? "FITS" : "OVERFLOW")}");
        }
        lines.Add("");

        // ---- Section 5: Remaining {0} placeholders ----
        lines.Add("--- Section 5: Remaining {0} placeholders ---");
        var placeholderKeys = new[] {
            "mod.10","mod.11","mod.18","mod.168","mod.180",
            "cmp.7","cmp.8","cmp.9","cmp.14","cmp.15","cmp.19","cmp.21","cmp.24","cmp.25",
            "ln.1","ln.2","ln.3","str.28",
            "uib.24","uib.28","uib.29","uib.31","uib.32","uib.35",
            "uib.38","uib.39","uib.40","uib.41","uib.52","uib.53",
            "uib.58","uib.61","uib.64","uib.66","uib.67",
            "uib.72","uib.73","uib.75","uib.76","uib.78",
            "uib.90","uib.91","uib.94","uib.95","uib.96","uib.97","uib.98","uib.100","uib.102",
            "uij.3","uij.6","uij.7","uir.2","uir.3","uir.4"
        };
        int phCount = 0;
        foreach (var key in placeholderKeys)
        {
            string val = I18n.Get(key);
            if (val.Contains("{0}") || val.Contains("{1}") || val.Contains("{2}"))
            {
                lines.Add($"  [{key}] HAS NUMBERED PLACEHOLDER: \"{val}\"");
                phCount++;
            }
        }
        if (phCount == 0) lines.Add("  All clean - no numbered placeholders found");
        lines.Add("");

        lines.Add("=== SUMMARY ===");
        lines.Add($"Overflows: {overflowCount} | Numbered placeholders: {phCount}");
        lines.Add($"Window=720x820, InfoW=640, BtnW=150, TabH=30, LineH=26");

        string reportPath = Path.Combine(Helper.DirectoryPath, "layout_audit.txt");
        File.WriteAllLines(reportPath, lines);
        Monitor.Log($"[LayoutAudit] Written to {reportPath} ({lines.Count} lines, {overflowCount} overflows, {phCount} placeholders)", LogLevel.Info);
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
