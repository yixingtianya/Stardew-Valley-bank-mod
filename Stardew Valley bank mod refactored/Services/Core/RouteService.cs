using System.Text.Json;
using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;
using StardewModdingAPI;
using StardewValley;

namespace BankMod.Services.Core;

/// <summary>Detects CC vs Joja route completion using stable SMAPI APIs and applies persistent route effects.</summary>
public class RouteService : IRouteService
{
    private readonly ICompanyManager _companyManager;
    private readonly IBankAccountService _accountService;
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;

    // Hardcoded original config company names — never translated, used for matching
    private static readonly string[] DefaultCompanyNames = { "JojaMart", "Pierre's General Store" };

    // State
    private string _completedRoute = "";
    private bool _onlineShoppingUnlocked;
    private bool _routeEffectsApplied;
    private List<string>? _originalCompanyNames;

    public string CompletedRoute => _completedRoute;
    public bool OnlineShoppingUnlocked => _onlineShoppingUnlocked;
    public bool PierreBoosted { get; private set; }
    public bool JojaBoosted { get; private set; }
    public bool SeenCutscene { get; private set; }
    public bool ModEventPlaying { get; set; }

    public RouteService(ICompanyManager companyManager, IBankAccountService accountService, ModConfig config, IMonitor monitor)
    {
        _companyManager = companyManager;
        _accountService = accountService;
        _config = config;
        _monitor = monitor;
    }

    /// <summary>Restore save-specific persisted state.</summary>
    public void LoadFromSave(IModHelper helper)
    {
        // Migrate: ensure OriginalName is populated and reset corrupted display names
        MigrateOriginalNames();
        // Migrate dynamic companies from localized names to stable CropCode identifiers
        MigrateDynamicCompanyNames();

        // Use OriginalName as the stable backup for matching
        _originalCompanyNames = _config.Companies.Select(c => c.OriginalName).ToList();

        _completedRoute = helper.Data.ReadSaveData<string>("bm_route") ?? "";
        _onlineShoppingUnlocked = helper.Data.ReadSaveData<string>("bm_shop") == "1";
        _routeEffectsApplied = helper.Data.ReadSaveData<string>("bm_fx") == "1";
        SeenCutscene = helper.Data.ReadSaveData<string>("bm_cut") == "1";
        PierreBoosted = helper.Data.ReadSaveData<string>("bm_pb") == "1";
        JojaBoosted = helper.Data.ReadSaveData<string>("bm_jb") == "1";

        // Always restore display names from OriginalName (stable English identifiers)
        if (_originalCompanyNames != null)
            for (int i = 0; i < Math.Min(_originalCompanyNames.Count, _config.Companies.Count); i++)
                _config.Companies[i].Name = _config.Companies[i].OriginalName;

        // Migration: fix loan/account records that still use old (possibly Chinese) display names
        // Must run BEFORE route effects are applied, so config names are still original English
        MigrateLoanNames();

        // Self-heal: clear stale debt flags if no loans exist
        var account = _accountService.Load();
        if (account.Loans.Count == 0 && (account.IsInPrincipalDebt || account.IsInInterestDebt))
        {
            account.IsInPrincipalDebt = false;
            account.IsInInterestDebt = false;
            _accountService.Save(account);
            _monitor.Log("[Route] Self-healed stale debt flags (no loans exist)", LogLevel.Warn);
        }

        if (_routeEffectsApplied)
        {
            var cm = _companyManager as CompanyManager;
            if (cm != null)
            {
                cm.PierreSuppressionBoosted = PierreBoosted;
                cm.PierreSuppressionDisabled = JojaBoosted;
                cm.JojaSuppressionBoosted = JojaBoosted;
            }
            // NOTE: Do NOT modify c.Name here — GetDisplayName() handles route-based display names.
            // c.Name must always equal c.OriginalName so that c.Name == ca.CompanyName matching works.
        }
        // Migration: old saves have fx=True but no boost flags — force re-apply
        if (_routeEffectsApplied && !string.IsNullOrEmpty(_completedRoute) && !PierreBoosted && !JojaBoosted)
        {
            _monitor.Log("[Route] Old save detected (fx without boost flags) — re-applying effects", LogLevel.Warn);
            _routeEffectsApplied = false;
        }

        _monitor.Log($"[Route] LoadFromSave: route={_completedRoute}, shop={_onlineShoppingUnlocked}, fx={_routeEffectsApplied}, cut={SeenCutscene}, pb={PierreBoosted}, jb={JojaBoosted}", LogLevel.Info);
    }

    /// <summary>Ensure OriginalName is populated and config names are reset to original English identifiers.</summary>
    private void MigrateOriginalNames()
    {
        bool changed = false;
        for (int i = 0; i < _config.Companies.Count; i++)
        {
            var c = _config.Companies[i];
            // Populate OriginalName from hardcoded defaults if missing
            if (string.IsNullOrEmpty(c.OriginalName))
            {
                c.OriginalName = i < DefaultCompanyNames.Length ? DefaultCompanyNames[i] : c.Name;
                changed = true;
            }
            // Always reset display name to OriginalName (route effects will re-translate)
            if (c.Name != c.OriginalName)
            {
                _monitor.Log($"[Route] Migrate: resetting '{c.Name}' → '{c.OriginalName}'", LogLevel.Info);
                c.Name = c.OriginalName;
                changed = true;
            }
        }
        if (changed)
            _monitor.Log("[Route] Migrated OriginalName/reset display names — config will be saved on next game save", LogLevel.Info);
    }

    /// <summary>Migrate dynamic companies from localized display names to stable CropCode identifiers.</summary>
    private void MigrateDynamicCompanyNames()
    {
        var account = _accountService.Load();
        bool changed = false;

        // Step 1: Build mapping of old CompanyName → CropCode BEFORE changing anything
        var oldNameToCrop = new Dictionary<string, string>();
        foreach (var dc in account.DynamicCompanies)
        {
            if (string.IsNullOrEmpty(dc.CompanyName) || string.IsNullOrEmpty(dc.CropCode))
                continue;
            if (dc.CompanyName != dc.CropCode)
                oldNameToCrop[dc.CompanyName] = dc.CropCode;
        }

        // Step 2: Migrate loan records using the old→new mapping
        foreach (var loan in account.Loans)
        {
            if (oldNameToCrop.TryGetValue(loan.CompanyName, out string? cropCode))
            {
                _monitor.Log($"[Route] Migrate loan: '{loan.CompanyName}' → '{cropCode}'", LogLevel.Info);
                loan.CompanyName = cropCode;
                changed = true;
            }
        }

        // Step 3: Migrate company account records
        foreach (var ca in account.CompanyAccounts)
        {
            if (oldNameToCrop.TryGetValue(ca.CompanyName, out string? cropCode))
            {
                _monitor.Log($"[Route] Migrate account: '{ca.CompanyName}' → '{cropCode}'", LogLevel.Info);
                ca.CompanyName = cropCode;
                changed = true;
            }
        }

        // Step 4: Migrate dynamic company records themselves (AFTER loans/accounts)
        foreach (var dc in account.DynamicCompanies)
        {
            if (string.IsNullOrEmpty(dc.CompanyName) || string.IsNullOrEmpty(dc.CropCode))
                continue;
            if (dc.CompanyName == dc.CropCode)
                continue;
            var cropData = CropDataProvider.GetByCode(dc.CropCode);
            if (cropData != null)
            {
                _monitor.Log($"[Route] Migrate dynamic company: '{dc.CompanyName}' → '{dc.CropCode}' (crop: {cropData.DisplayName})", LogLevel.Info);
                dc.CompanyName = dc.CropCode;
                changed = true;
            }
        }

        if (changed)
        {
            _accountService.Save(account);
            _monitor.Log("[Route] Dynamic company name migration complete", LogLevel.Info);
        }
    }

    // Known translations for fixed companies (hardcoded — no i18n dependency at migration time)
    // Includes common variants (with/without "的") from different mod versions
    // Also includes route-specific subsidiary names that may leak into save data
    private static readonly Dictionary<string, string> KnownTranslations = new()
    {
        ["Joja超市"] = "JojaMart",
        ["皮埃尔杂货店"] = "Pierre's General Store",
        ["皮埃尔的杂货店"] = "Pierre's General Store",
        ["皮埃尔子公司"] = "JojaMart",           // CC route: Joja → Pierre's subsidiary
        ["Joja子公司"] = "Pierre's General Store", // Joja route: Pierre → Joja's subsidiary
        ["Pierre's Subsidiary"] = "JojaMart",       // English variant of CC route rename
        ["Joja Subsidiary"] = "Pierre's General Store", // English variant of Joja route rename
    };

    // Route-specific display names (hardcoded — i18n locale may not be ready at SaveLoaded time)
    // rte.1 = "皮埃尔子公司" (Community route → Joja renamed to Pierre's subsidiary)
    // rte.3 = "Joja子公司" (Joja route → Pierre renamed to Joja's subsidiary)
    private static readonly Dictionary<string, string> RouteDisplayNames = new()
    {
        ["Community_Joja"] = "皮埃尔子公司",
        ["Joja_Pierre"] = "Joja子公司",
    };

    /// <summary>Migrate loan/account records from old display names to OriginalName identifiers.</summary>
    private void MigrateLoanNames()
    {
        var account = _accountService.Load();
        bool changed = false;

        // Build mapping: any known display name → OriginalName
        var nameToOriginal = new Dictionary<string, string>();
        foreach (var c in _config.Companies)
        {
            nameToOriginal[c.OriginalName] = c.OriginalName;
            if (!string.IsNullOrEmpty(c.Name) && c.Name != c.OriginalName)
                nameToOriginal[c.Name] = c.OriginalName;
        }
        // Add hardcoded known translations (Chinese, etc.)
        foreach (var kv in KnownTranslations)
            nameToOriginal[kv.Key] = kv.Value;

        // Add crop display names → crop code mappings (e.g. "青豆公司" → "Green Bean")
        foreach (var crop in CropDataProvider.AllCrops)
        {
            string displayName = crop.DisplayName + I18n.Get("cmp.6");
            if (!nameToOriginal.ContainsKey(displayName))
                nameToOriginal[displayName] = crop.CropCode;
        }

        _monitor.Log($"[Route] MigrateLoanNames: mapping has {nameToOriginal.Count} entries, loan count={account.Loans.Count}", LogLevel.Info);
        foreach (var kv in nameToOriginal)
            _monitor.Log($"[Route]   map: '{kv.Key}' → '{kv.Value}'", LogLevel.Trace);

        // Migrate loan records
        foreach (var loan in account.Loans)
        {
            if (string.IsNullOrEmpty(loan.CompanyName)) continue;
            if (_config.Companies.Any(c => c.OriginalName == loan.CompanyName)) continue;
            if (nameToOriginal.TryGetValue(loan.CompanyName, out string? original))
            {
                _monitor.Log($"[Route] Migrate loan: '{loan.CompanyName}' → '{original}'", LogLevel.Info);
                loan.CompanyName = original;
                changed = true;
            }
            else
            {
                _monitor.Log($"[Route] Migrate loan: '{loan.CompanyName}' — NO MATCH in mapping!", LogLevel.Warn);
            }
        }

        // Migrate company account records
        foreach (var ca in account.CompanyAccounts)
        {
            if (string.IsNullOrEmpty(ca.CompanyName)) continue;
            if (_config.Companies.Any(c => c.OriginalName == ca.CompanyName)) continue;
            if (nameToOriginal.TryGetValue(ca.CompanyName, out string? original))
            {
                _monitor.Log($"[Route] Migrate account: '{ca.CompanyName}' → '{original}'", LogLevel.Info);
                ca.CompanyName = original;
                changed = true;
            }
        }

        // Migrate dynamic company records
        foreach (var dc in account.DynamicCompanies)
        {
            if (string.IsNullOrEmpty(dc.CompanyName)) continue;
            if (dc.CompanyName == dc.CropCode) continue;
            if (nameToOriginal.TryGetValue(dc.CompanyName, out string? original))
            {
                _monitor.Log($"[Route] Migrate dynamic: '{dc.CompanyName}' → '{original}'", LogLevel.Info);
                dc.CompanyName = original;
                changed = true;
            }
        }

        if (changed)
        {
            _accountService.Save(account);
            _monitor.Log("[Route] Loan name migration complete", LogLevel.Info);
        }
        else
        {
            _monitor.Log("[Route] MigrateLoanNames: no changes needed (or no matches found)", LogLevel.Info);
        }
    }

    /// <summary>Persist state to save-specific data.</summary>
    public void SaveToSave(IModHelper helper)
    {
        helper.Data.WriteSaveData("bm_route", _completedRoute);
        helper.Data.WriteSaveData("bm_shop", _onlineShoppingUnlocked ? "1" : "0");
        helper.Data.WriteSaveData("bm_fx", _routeEffectsApplied ? "1" : "0");
        helper.Data.WriteSaveData("bm_cut", SeenCutscene ? "1" : "0");
        helper.Data.WriteSaveData("bm_pb", PierreBoosted ? "1" : "0");
        helper.Data.WriteSaveData("bm_jb", JojaBoosted ? "1" : "0");
    }

    /// <summary>Detect route using eventsSeen (primary) with mail fallback for Joja projects.</summary>
    public void DetectRoute()
    {
        if (!string.IsNullOrEmpty(_completedRoute)) return;

        // CC: use documented vanilla event ID 191393 (Community Center ceremony)
        if (Game1.player.eventsSeen.Contains(MailFlags.Event_CC_Ceremony)
            || Game1.player.hasCompletedCommunityCenter())
        {
            _completedRoute = "Community";
            _monitor.Log("[Route] Detected Community Center route", LogLevel.Info);
            return;
        }

        // Joja: primary check = event 502261 seen (Joja warehouse ceremony)
        if (Game1.player.eventsSeen.Contains(MailFlags.Event_Joja_Warehouse))
        {
            _completedRoute = "Joja";
            _monitor.Log("[Route] Detected Joja route (event seen)", LogLevel.Info);
            return;
        }

        // Joja fallback: Joja member AND all 5 development project mails received
        if (Game1.player.hasOrWillReceiveMail(MailFlags.JojaMember) && HasAllJojaProjects())
        {
            _completedRoute = "Joja";
            _monitor.Log("[Route] Detected Joja route (mail fallback)", LogLevel.Info);
        }
    }

    public void DetectRouteFromEvent(string eventId)
    {
        if (!string.IsNullOrEmpty(_completedRoute)) return;
        if (eventId.Contains(MailFlags.Event_Joja_Warehouse))
        {
            _completedRoute = "Joja";
            _monitor.Log($"[Route] Detected Joja route from event {eventId}", LogLevel.Info);
        }
        else if (eventId.Contains(MailFlags.Event_CC_Ceremony))
        {
            _completedRoute = "Community";
            _monitor.Log($"[Route] Detected CC route from event {eventId}", LogLevel.Info);
        }
    }

    /// <summary>Get the display name for a company (always localized via i18n).</summary>
    public string GetDisplayName(CompanyDefinition company)
    {
        // Dynamic companies: derive display name from crop code
        if (company.IsDynamic && !string.IsNullOrEmpty(company.CropCode))
        {
            var cropData = CropDataProvider.GetByCode(company.CropCode);
            if (cropData != null)
                return cropData.DisplayName + I18n.Get("cmp.6");
        }
        // Route-based renames (defeated company takes on subsidiary name)
        if (_routeEffectsApplied)
        {
            if (_completedRoute == "Community" && company.OriginalName.Contains("Joja"))
                return I18n.Get("rte.1");
            if (_completedRoute == "Joja" && company.OriginalName.Contains("Pierre"))
                return I18n.Get("rte.3");
        }
        // Base localized names for fixed companies
        if (company.OriginalName.Contains("Joja"))
            return I18n.Get("loc.1");
        if (company.OriginalName.Contains("Pierre"))
            return I18n.Get("loc.2");
        return company.Name;
    }

    /// <summary>Apply company renames and boost flags. Idempotent. Name stays as stable identifier for matching.</summary>
    public void ApplyRouteEffects()
    {
        if (_routeEffectsApplied) return;
        _routeEffectsApplied = true;

        _monitor.Log($"[Route] ApplyRouteEffects: route={_completedRoute}", LogLevel.Info);

        // Ensure c.Name always equals c.OriginalName — GetDisplayName() handles route-based display names
        for (int i = 0; i < _config.Companies.Count; i++)
            _config.Companies[i].Name = _config.Companies[i].OriginalName;

        var cm = _companyManager as CompanyManager;
        if (_completedRoute == "Community")
        {
            PierreBoosted = true;
            if (cm != null) cm.PierreSuppressionBoosted = true;
            _monitor.Log("[Route] CC effects: Pierre ×2, Joja display name handled by GetDisplayName()", LogLevel.Info);
        }
        else if (_completedRoute == "Joja")
        {
            JojaBoosted = true;
            if (cm != null) { cm.PierreSuppressionDisabled = true; cm.JojaSuppressionBoosted = true; }
            _monitor.Log($"[Route] Joja effects: JojaBoosted={JojaBoosted}, Pierre display name handled by GetDisplayName()", LogLevel.Info);
        }
    }

    public void UnlockOnlineShopping()
    {
        if (_onlineShoppingUnlocked) return;
        _onlineShoppingUnlocked = true;
        Game1.chatBox?.addInfoMessage(I18n.Get("rte.4"));
        _monitor.Log("[Route] Online shopping unlocked", LogLevel.Info);
    }

    public void MarkCutsceneSeen()
    {
        SeenCutscene = true;
    }

    private static bool HasAllJojaProjects()
    {
        var projects = new[] {
            MailFlags.Joja_Minecart, MailFlags.Joja_Bus, MailFlags.Joja_Bridge,
            MailFlags.Joja_Greenhouse, MailFlags.Joja_FishTank
        };
        foreach (var p in projects)
            if (!Game1.player.hasOrWillReceiveMail(p) && !Game1.player.mailReceived.Contains(p))
                return false;
        return true;
    }
}
