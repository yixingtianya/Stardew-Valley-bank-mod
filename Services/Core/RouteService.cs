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
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;

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

    public RouteService(ICompanyManager companyManager, ModConfig config, IMonitor monitor)
    {
        _companyManager = companyManager;
        _config = config;
        _monitor = monitor;
    }

    /// <summary>Restore save-specific persisted state.</summary>
    public void LoadFromSave(IModHelper helper)
    {
        _completedRoute = helper.Data.ReadSaveData<string>("bm_route") ?? "";
        _onlineShoppingUnlocked = helper.Data.ReadSaveData<string>("bm_shop") == "1";
        _routeEffectsApplied = helper.Data.ReadSaveData<string>("bm_fx") == "1";
        SeenCutscene = helper.Data.ReadSaveData<string>("bm_cut") == "1";
        // Restore boost flags (normally set by ApplyRouteEffects, but the guard skips on reload)
        PierreBoosted = helper.Data.ReadSaveData<string>("bm_pb") == "1";
        JojaBoosted = helper.Data.ReadSaveData<string>("bm_jb") == "1";
        if (_routeEffectsApplied)
        {
            var cm = _companyManager as CompanyManager;
            if (cm != null)
            {
                cm.PierreSuppressionBoosted = PierreBoosted;
                cm.PierreSuppressionDisabled = JojaBoosted;
                cm.JojaSuppressionBoosted = JojaBoosted;
            }
        }
        // Migration: old saves have fx=True but no boost flags — force re-apply
        if (_routeEffectsApplied && !string.IsNullOrEmpty(_completedRoute) && !PierreBoosted && !JojaBoosted)
        {
            _monitor.Log("[Route] Old save detected (fx without boost flags) — re-applying effects", LogLevel.Warn);
            _routeEffectsApplied = false;
        }
        _monitor.Log($"[Route] LoadFromSave: route={_completedRoute}, shop={_onlineShoppingUnlocked}, fx={_routeEffectsApplied}, cut={SeenCutscene}, pb={PierreBoosted}, jb={JojaBoosted}", LogLevel.Info);
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

    /// <summary>Apply company renames and boost flags. Idempotent.</summary>
    public void ApplyRouteEffects()
    {
        if (_routeEffectsApplied) return;
        _routeEffectsApplied = true;

        _monitor.Log($"[Route] ApplyRouteEffects: route={_completedRoute}, origNames={_originalCompanyNames?.Count ?? 0}", LogLevel.Info);

        // Save and restore original company names
        if (_originalCompanyNames == null)
            _originalCompanyNames = _config.Companies.Select(c => c.Name).ToList();
        else
            for (int i = 0; i < Math.Min(_originalCompanyNames.Count, _config.Companies.Count); i++)
                _config.Companies[i].Name = _originalCompanyNames[i];

        var cm = _companyManager as CompanyManager;
        if (_completedRoute == "Community")
        {
            foreach (var c in _config.Companies)
                if (c.Name.Contains("Joja")) { c.Name = "皮埃尔子公司"; break; }
            PierreBoosted = true;
            if (cm != null) cm.PierreSuppressionBoosted = true;
            _monitor.Log("[Route] CC effects: Joja renamed, Pierre ×2, PierreBoosted=true, cm.PierreSuppressionBoosted=true", LogLevel.Info);
        }
        else if (_completedRoute == "Joja")
        {
            foreach (var c in _config.Companies)
                if (c.Name.Contains("皮埃尔")) { c.Name = "Joja子公司"; break; }
            JojaBoosted = true;
            if (cm != null) { cm.PierreSuppressionDisabled = true; cm.JojaSuppressionBoosted = true; }
            _monitor.Log($"[Route] Joja effects: Pierre renamed, JojaBoosted={JojaBoosted}, cm.PierreSuppressionDisabled={cm?.PierreSuppressionDisabled}, cm.JojaSuppressionBoosted={cm?.JojaSuppressionBoosted}", LogLevel.Info);
        }
    }

    public void UnlockOnlineShopping()
    {
        if (_onlineShoppingUnlocked) return;
        _onlineShoppingUnlocked = true;
        Game1.chatBox?.addInfoMessage("网购功能已解锁！打开手机银行即可使用。");
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
