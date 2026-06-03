namespace BankMod.Services.Abstractions;

/// <summary>计划中（P4）：季节初果实购买机制。
/// 每个季节前三天，固定公司出售溢价当季果实，购买后加速动态公司诞生。</summary>
public interface ISeasonalFruitMarket
{
    /// <summary>是否处于季节初果实时段。</summary>
    bool IsInFruitWindow();

    /// <summary>获取当前可购买的果实列表及价格。</summary>
    IReadOnlyList<(string cropId, int price, int quantity)> GetAvailableFruits();
}
