using BankMod.Data;
using BankMod.Services.Abstractions;
using StardewModdingAPI;
using StardewValley;

namespace BankMod.Services.Core;

/// <summary>Monitors shipping bin sales and tracks per-crop cumulative/consecutive sell data.</summary>
public class ShipmentTrackingService
{
    /// <summary>
    /// Called on DayEnding. Iterates the shipping bin, records cumulative sell counts
    /// and updates per-crop tracking. Also feeds fuel inventory via IFuelService.
    /// </summary>
    public void ProcessShipments(BankAccountData account, IFuelService fuelService, IMonitor monitor)
    {
        var shippingBin = Game1.getFarm().getShippingBin(Game1.player);
        int today = (int)Game1.stats.DaysPlayed;

        monitor.Log($"[ShipmentTracking] Scanning shipping bin: {shippingBin.Count} items found", LogLevel.Debug);

        foreach (var item in shippingBin)
        {
            string? cropCode = NormalizeItemId(item);
            if (cropCode is null)
            {
                monitor.Log($"[ShipmentTracking] Skipped non-Object item: {item.Name} (QID={item.QualifiedItemId})", LogLevel.Debug);
                continue;
            }

            var cropData = CropDataProvider.GetByCode(cropCode);
            if (cropData is null)
            {
                monitor.Log($"[ShipmentTracking] No CropData for: {cropCode} (qty={item.Stack})", LogLevel.Debug);
                continue;
            }

            var tracking = account.CropShipments.FirstOrDefault(s => s.CropCode == cropCode);
            if (tracking is null)
            {
                tracking = new CropShipmentData { CropCode = cropCode };
                account.CropShipments.Add(tracking);
            }

            int quantity = item.Stack;
            tracking.CumulativeSellCount += quantity;
            tracking.LastSellDay = today;

            monitor.Log($"[ShipmentTracking] {cropCode}: +{quantity} (cumulative={tracking.CumulativeSellCount})", LogLevel.Debug);

            // Record fuel points for the shipping bin sale
            int quality = item is StardewValley.Object o ? o.Quality : 0;
            fuelService.RecordShippingBinSale(account, cropCode, quantity, quality);
        }
    }

    /// <summary>
    /// Called on DayStarted. Updates consecutive selling days and decay days for each tracked crop.
    /// If the crop was sold yesterday, increment ConsecutiveSellDays and reset DecayDays.
    /// Otherwise, reset ConsecutiveSellDays and increment DecayDays (if has history).
    /// </summary>
    public void UpdateConsecutiveDays(BankAccountData account)
    {
        int today = (int)Game1.stats.DaysPlayed;
        int yesterday = today - 1;

        foreach (var tracking in account.CropShipments)
        {
            if (tracking.LastSellDay == yesterday)
            {
                tracking.ConsecutiveSellDays += 1;
                tracking.DecayDays = 0;
            }
            else
            {
                tracking.ConsecutiveSellDays = 0;
                if (tracking.CumulativeSellCount > 0)
                    tracking.DecayDays += 1;
            }
        }
    }

    /// <summary>Maps QualifiedItemId (e.g. "(O)192", "(O)SummerSquash") to CropDataProvider code (e.g. "Potato", "Summer Squash").</summary>
    private static readonly Dictionary<string, string> ItemIdToCode = new()
    {
        // Spring
        { "(O)Carrot", "Carrot" },
        { "(O)400", "Strawberry" },
        { "(O)24", "Parsnip" },
        { "(O)188", "Green Bean" },
        { "(O)192", "Potato" },
        { "(O)248", "Garlic" },
        { "(O)271", "Unmilled Rice" },
        { "(O)190", "Cauliflower" },
        { "(O)250", "Kale" },
        { "(O)597", "Blue Jazz" },
        { "(O)252", "Rhubarb" },
        { "(O)591", "Tulip" },
        // Summer
        { "(O)258", "Blueberry" },
        { "(O)304", "Hops" },
        { "(O)260", "Hot Pepper" },
        { "(O)256", "Tomato" },
        { "(O)264", "Radish" },
        { "(O)266", "Red Cabbage" },
        { "(O)254", "Melon" },
        { "(O)SummerSquash", "Summer Squash" },
        { "(O)593", "Summer Spangle" },
        { "(O)268", "Starfruit" },
        { "(O)376", "Poppy" },
        // Fall
        { "(O)284", "Beet" },
        { "(O)272", "Eggplant" },
        { "(O)274", "Artichoke" },
        { "(O)Broccoli", "Broccoli" },
        { "(O)398", "Grape" },
        { "(O)276", "Pumpkin" },
        { "(O)280", "Yam" },
        { "(O)300", "Amaranth" },
        { "(O)278", "Bok Choy" },
        { "(O)282", "Cranberries" },
        { "(O)595", "Fairy Rose" },
        // Winter
        { "(O)Powdermelon", "Powdermelon" },
        // Cross-season
        { "(O)262", "Wheat" },
        { "(O)454", "Ancient Fruit" },
        { "(O)270", "Corn" },
        { "(O)421", "Sunflower" },
    };

    private static string? NormalizeItemId(Item item)
    {
        if (item is not StardewValley.Object) return null;
        return NormalizeItemId(item.QualifiedItemId);
    }

    /// <summary>Public lookup: maps QualifiedItemId to CropDataProvider code. Returns null for non-crop items.</summary>
    public static string? NormalizeItemId(string qualifiedItemId)
    {
        return ItemIdToCode.TryGetValue(qualifiedItemId, out string? code) ? code : null;
    }
}
