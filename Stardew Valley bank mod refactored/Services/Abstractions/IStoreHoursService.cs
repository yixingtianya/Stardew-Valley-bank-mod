namespace BankMod.Services.Abstractions;

/// <summary>Checks SDV shop availability using Data/Shops and GameStateQuery.</summary>
public interface IStoreHoursService
{
    /// <summary>Check if a shop is currently open. Returns true if any owner's Condition passes.</summary>
    bool IsShopOpen(string shopId);

    /// <summary>Get a human-readable reason why the shop is closed, or null if it's open.</summary>
    string? GetClosedReason(string shopId);
}
