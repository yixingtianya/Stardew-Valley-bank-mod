using BankMod.Services.Abstractions;

namespace BankMod.Services.Core;

/// <summary>计划中（P4）：季节果实市场桩实现。</summary>
public class SeasonalFruitMarket : ISeasonalFruitMarket
{
    public bool IsInFruitWindow()
    {
        // 计划中：季节前三天返回 true
        return false;
    }

    public IReadOnlyList<(string cropId, int price, int quantity)> GetAvailableFruits()
    {
        // 计划中：返回当季可购买的溢价果实
        return Array.Empty<(string, int, int)>();
    }
}
