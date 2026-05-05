using BankMod.Domain;

namespace BankMod.Services.Core;

/// <summary>
/// Provides crop data lookup from the authoritative Crops values.txt dataset (V3.5 patch5).
/// All crop interest rates, fuel parameters Y/D_base/Smax are sourced from this provider.
/// Coffee Bean is explicitly excluded.
/// </summary>
public static class CropDataProvider
{
    /// <summary>Crop codes that must never generate a dynamic company.</summary>
    public const string ExcludedCoffeeBean = "Coffee Bean";

    /// <summary>All crops eligible for dynamic company generation (Coffee Bean excluded).</summary>
    public static readonly IReadOnlyList<CropDataRecord> AllCrops;

    /// <summary>Lookup by crop code.</summary>
    private static readonly Dictionary<string, CropDataRecord> _byCode;

    static CropDataProvider()
    {
        var crops = new List<CropDataRecord>
        {
            // === Spring ===
            new() { CropCode = "Carrot",       DisplayName = "胡萝卜",     IsPurchasable = false, R = 0.3333, Y = 9.0,  DBase = 15.0, Smax = 90,  Season = "Spring", EquivalentCost = 17.5 },
            new() { CropCode = "Strawberry",   DisplayName = "草莓",       IsPurchasable = true,  R = 0.2583, Y = 6.0,  DBase = 10.0, Smax = 60,  Season = "Spring", EquivalentCost = 100 },
            new() { CropCode = "Parsnip",      DisplayName = "防风草",     IsPurchasable = true,  R = 0.1875, Y = 7.0,  DBase = 11.7, Smax = 70,  Season = "Spring", EquivalentCost = 20 },
            new() { CropCode = "Green Bean",   DisplayName = "青豆",       IsPurchasable = true,  R = 0.1746, Y = 7.0,  DBase = 11.7, Smax = 70,  Season = "Spring", EquivalentCost = 60 },
            new() { CropCode = "Potato",       DisplayName = "土豆",       IsPurchasable = true,  R = 0.1533, Y = 5.6,  DBase = 9.3,  Smax = 56,  Season = "Spring", EquivalentCost = 50 },
            new() { CropCode = "Garlic",       DisplayName = "大蒜",       IsPurchasable = true,  R = 0.1250, Y = 7.0,  DBase = 11.7, Smax = 70,  Season = "Spring", EquivalentCost = 40 },
            new() { CropCode = "Cauliflower",  DisplayName = "花椰菜",     IsPurchasable = true,  R = 0.0990, Y = 2.0,  DBase = 3.3,  Smax = 20,  Season = "Spring", EquivalentCost = 80 },
            new() { CropCode = "Kale",         DisplayName = "羽衣甘蓝",   IsPurchasable = true,  R = 0.0952, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Spring", EquivalentCost = 70 },
            new() { CropCode = "Blue Jazz",    DisplayName = "蓝爵",       IsPurchasable = true,  R = 0.0952, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Spring", EquivalentCost = 30 },
            new() { CropCode = "Rhubarb",      DisplayName = "大黄",       IsPurchasable = true,  R = 0.0923, Y = 2.0,  DBase = 3.3,  Smax = 20,  Season = "Spring", EquivalentCost = 100 },
            new() { CropCode = "Tulip",        DisplayName = "郁金香",     IsPurchasable = true,  R = 0.0833, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Spring", EquivalentCost = 20 },
            new() { CropCode = "Unmilled Rice",DisplayName = "未碾米",     IsPurchasable = true,  R = -0.0417,Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Spring", EquivalentCost = 40 },

            // === Summer ===
            new() { CropCode = "Blueberry",    DisplayName = "蓝莓",       IsPurchasable = true,  R = 0.4063, Y = 12.0, DBase = 20.0, Smax = 120, Season = "Summer", EquivalentCost = 80 },
            new() { CropCode = "Hops",         DisplayName = "啤酒花",     IsPurchasable = true,  R = 0.3611, Y = 18.0, DBase = 30.0, Smax = 180, Season = "Summer", EquivalentCost = 60 },
            new() { CropCode = "Hot Pepper",   DisplayName = "辣椒",       IsPurchasable = true,  R = 0.2917, Y = 8.0,  DBase = 13.3, Smax = 80,  Season = "Summer", EquivalentCost = 40 },
            new() { CropCode = "Tomato",       DisplayName = "番茄",       IsPurchasable = true,  R = 0.2500, Y = 5.0,  DBase = 8.3,  Smax = 50,  Season = "Summer", EquivalentCost = 50 },
            new() { CropCode = "Radish",       DisplayName = "萝卜",       IsPurchasable = true,  R = 0.2083, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Summer", EquivalentCost = 40 },
            new() { CropCode = "Red Cabbage",  DisplayName = "红叶卷心菜", IsPurchasable = true,  R = 0.1778, Y = 3.0,  DBase = 5.0,  Smax = 30,  Season = "Summer", EquivalentCost = 100 },
            new() { CropCode = "Melon",        DisplayName = "甜瓜",       IsPurchasable = true,  R = 0.1771, Y = 2.0,  DBase = 3.3,  Smax = 20,  Season = "Summer", EquivalentCost = 80 },
            new() { CropCode = "Summer Squash",DisplayName = "金皮西葫芦", IsPurchasable = false, R = 0.1667, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Summer", EquivalentCost = 22.5 },
            new() { CropCode = "Summer Spangle",DisplayName= "夏季亮片",   IsPurchasable = true,  R = 0.1000, Y = 3.0,  DBase = 5.0,  Smax = 30,  Season = "Summer", EquivalentCost = 50 },
            new() { CropCode = "Starfruit",    DisplayName = "杨桃",       IsPurchasable = true,  R = 0.0673, Y = 2.15, DBase = 3.6,  Smax = 22,  Season = "Summer", EquivalentCost = 400 },
            new() { CropCode = "Poppy",        DisplayName = "罂粟",       IsPurchasable = true,  R = 0.0571, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Summer", EquivalentCost = 100 },

            // === Fall ===
            new() { CropCode = "Beet",         DisplayName = "甜菜",       IsPurchasable = true,  R = 0.6667, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Fall", EquivalentCost = 20 },
            new() { CropCode = "Eggplant",     DisplayName = "茄子",       IsPurchasable = true,  R = 0.5600, Y = 5.0,  DBase = 8.3,  Smax = 50,  Season = "Fall", EquivalentCost = 20 },
            new() { CropCode = "Artichoke",    DisplayName = "洋蓟",       IsPurchasable = true,  R = 0.5417, Y = 3.0,  DBase = 5.0,  Smax = 30,  Season = "Fall", EquivalentCost = 30 },
            new() { CropCode = "Broccoli",     DisplayName = "西兰花",     IsPurchasable = false, R = 0.4583, Y = 6.0,  DBase = 10.0, Smax = 60,  Season = "Fall", EquivalentCost = 35 },
            new() { CropCode = "Grape",        DisplayName = "葡萄",       IsPurchasable = true,  R = 0.3968, Y = 7.0,  DBase = 11.7, Smax = 70,  Season = "Fall", EquivalentCost = 60 },
            new() { CropCode = "Pumpkin",      DisplayName = "南瓜",       IsPurchasable = true,  R = 0.1692, Y = 2.0,  DBase = 3.3,  Smax = 20,  Season = "Fall", EquivalentCost = 100 },
            new() { CropCode = "Yam",          DisplayName = "山药",       IsPurchasable = true,  R = 0.1667, Y = 2.0,  DBase = 3.3,  Smax = 20,  Season = "Fall", EquivalentCost = 60 },
            new() { CropCode = "Amaranth",     DisplayName = "苋菜",       IsPurchasable = true,  R = 0.1633, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Fall", EquivalentCost = 70 },
            new() { CropCode = "Bok Choy",     DisplayName = "白菜",       IsPurchasable = true,  R = 0.1500, Y = 7.0,  DBase = 11.7, Smax = 70,  Season = "Fall", EquivalentCost = 50 },
            new() { CropCode = "Cranberries",  DisplayName = "蔓越莓",     IsPurchasable = true,  R = 0.0850, Y = 12.0, DBase = 20.0, Smax = 120, Season = "Fall", EquivalentCost = 240 },
            new() { CropCode = "Fairy Rose",   DisplayName = "仙子玫瑰",   IsPurchasable = true,  R = 0.0375, Y = 2.0,  DBase = 3.3,  Smax = 20,  Season = "Fall", EquivalentCost = 200 },

            // === Winter ===
            new() { CropCode = "Powdermelon",  DisplayName = "霜瓜",       IsPurchasable = false, R = 0.1429, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Winter", EquivalentCost = 30 },

            // === Cross-season ===
            new() { CropCode = "Wheat",        DisplayName = "小麦",       IsPurchasable = true,  R = 0.3750, Y = 7.0,  DBase = 11.7, Smax = 70,  Season = "Summer・Fall", EquivalentCost = 10 },
            new() { CropCode = "Ancient Fruit",DisplayName = "上古水果",   IsPurchasable = false, R = 0.1970, Y = 3.0,  DBase = 5.0,  Smax = 30,  Season = "Spring・Summer・Fall", EquivalentCost = 275 },
            new() { CropCode = "Corn",         DisplayName = "玉米",       IsPurchasable = true,  R = 0.0214, Y = 4.0,  DBase = 6.7,  Smax = 40,  Season = "Summer・Fall", EquivalentCost = 150 },
            new() { CropCode = "Sunflower",    DisplayName = "向日葵",     IsPurchasable = true,  R = -0.0750,Y = 2.0,  DBase = 3.3,  Smax = 20,  Season = "Summer・Fall", EquivalentCost = 200 },
        };

        AllCrops = crops.AsReadOnly();
        _byCode = crops.ToDictionary(c => c.CropCode, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Get crop data by in-game crop code. Returns null if not found or Coffee Bean.</summary>
    public static CropDataRecord? GetByCode(string cropCode)
    {
        if (string.Equals(cropCode, ExcludedCoffeeBean, StringComparison.OrdinalIgnoreCase))
            return null;
        return _byCode.TryGetValue(cropCode, out var record) ? record : null;
    }

    /// <summary>Check whether a crop should be excluded from dynamic company generation.</summary>
    public static bool IsExcluded(string cropCode) =>
        string.Equals(cropCode, ExcludedCoffeeBean, StringComparison.OrdinalIgnoreCase);

    /// <summary>Get all crop codes that are eligible for company generation this season.</summary>
    public static IEnumerable<CropDataRecord> GetCropsForSeason(string season)
    {
        return AllCrops.Where(c => c.Season.Contains(season, StringComparison.OrdinalIgnoreCase));
    }
}
