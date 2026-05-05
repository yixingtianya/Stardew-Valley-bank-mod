namespace BankMod.Data;

/// <summary>Per-crop shipping bin tracking, persisted in save data.</summary>
public class CropShipmentData
{
    public string CropCode { get; set; } = "";
    public int CumulativeSellCount { get; set; }
    public int LastSellDay { get; set; }
    public int ConsecutiveSellDays { get; set; }
    /// <summary>Consecutive days without selling (for cumulative decay). Reset on sale.</summary>
    public int DecayDays { get; set; }
}
