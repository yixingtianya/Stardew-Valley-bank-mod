using StardewModdingAPI;

namespace BankMod.Services.Abstractions;

/// <summary>Detects CC vs Joja route completion using stable SMAPI APIs, applies persistent route effects.</summary>
public interface IRouteService
{
    string CompletedRoute { get; }
    bool OnlineShoppingUnlocked { get; }
    bool PierreBoosted { get; }
    bool JojaBoosted { get; }
    bool SeenCutscene { get; }
    bool ModEventPlaying { get; set; }

    /// <summary>Detect route using eventsSeen (primary) with mail flag fallback. Idempotent.</summary>
    void DetectRoute();

    /// <summary>Detect route from a currently-playing vanilla event ID (for real-time detection).</summary>
    void DetectRouteFromEvent(string eventId);

    /// <summary>Apply company renames and boost flags. Idempotent via _routeEffectsApplied guard.</summary>
    void ApplyRouteEffects();

    /// <summary>Get the localized display name for a company (handles route renames).</summary>
    string GetDisplayName(Data.CompanyDefinition company);

    /// <summary>Mark online shopping as unlocked (post-route cutscene).</summary>
    void UnlockOnlineShopping();

    /// <summary>Mark cutscene as seen for this playthrough.</summary>
    void MarkCutsceneSeen();

    /// <summary>Restore persisted state from save data.</summary>
    void LoadFromSave(IModHelper helper);

    /// <summary>Persist state to save data.</summary>
    void SaveToSave(IModHelper helper);
}
