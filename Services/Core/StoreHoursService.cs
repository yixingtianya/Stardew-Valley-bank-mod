using BankMod.Domain;
using BankMod.Services.Abstractions;
using StardewModdingAPI;
using StardewValley;

namespace BankMod.Services.Core;

/// <summary>Checks SDV shop availability for online shopping using time.txt as the authoritative source.</summary>
public class StoreHoursService : IStoreHoursService
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private Dictionary<string, StoreHours>? _hours;
    private bool _loaded;

    // Maps shopId → time.txt store name keywords
    private static readonly Dictionary<string, string> ShopNameMap = new()
    {
        ["SeedShop"] = "皮埃尔",
        ["Carpenter"] = "木匠",
        ["Blacksmith"] = "铁匠",
        ["Joja"] = "Joja",
        ["FishShop"] = "鱼店",
        ["DesertTrade"] = "绿洲",
        ["Sandy"] = "绿洲",
        ["IslandTrade"] = "旅队",
    };

    // Chinese day-of-week → index (0=Mon..6=Sun)
    private static readonly Dictionary<string, int> DayOfWeekMap = new()
    {
        ["周一"] = 0, ["周二"] = 1, ["周三"] = 2, ["周四"] = 3, ["周五"] = 4, ["周六"] = 5, ["周日"] = 6,
        ["星期一"] = 0, ["星期二"] = 1, ["星期三"] = 2, ["星期四"] = 3, ["星期五"] = 4, ["星期六"] = 5, ["星期日"] = 6,
    };

    // Chinese time → 24h int
    private static int ParseTime(string chineseTime)
    {
        // "上午9:00" → 900, "下午9:00" → 2100, "凌晨12:00" → 0, "全天24小时" → -1
        if (chineseTime.Contains("全天")) return -1;

        bool isPM = chineseTime.Contains("下午") || chineseTime.Contains("晚上") || chineseTime.Contains("凌晨");
        string num = chineseTime.Replace("上午", "").Replace("下午", "").Replace("晚上", "").Replace("凌晨", "").Replace(":", "").Trim();
        if (!int.TryParse(num, out int t)) return -1;
        // "凌晨12:00" = 0:00 = 0
        if (chineseTime.Contains("凌晨") && t >= 1200) t -= 1200;
        if (isPM && t < 1200 && !chineseTime.Contains("凌晨")) t += 1200;
        return t;
    }

    public StoreHoursService(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public bool IsShopOpen(string shopId)
    {
        return GetClosedReason(shopId) == null;
    }

    public string? GetClosedReason(string shopId)
    {
        // Transport locks
        if (shopId == "DesertTrade")
        {
            bool hasBus = Game1.player.hasOrWillReceiveMail(MailFlags.CC_Vault)
                || Game1.player.mailReceived.Contains(MailFlags.CC_Vault)
                || Game1.player.hasOrWillReceiveMail(MailFlags.Joja_Bus)
                || Game1.player.mailReceived.Contains(MailFlags.Joja_Bus);
            if (!hasBus) return "公交未修复";
        }
        if (shopId == "IslandTrade")
        {
            bool hasBoat = Game1.player.hasOrWillReceiveMail(MailFlags.WillyBoatFixed)
                || Game1.player.mailReceived.Contains(MailFlags.WillyBoatFixed);
            if (!hasBoat) return "姜岛未解锁";
        }
        if (shopId == "Sandy")
        {
            // Sandy's Oasis: 9:00-23:00, closed Tue. Bus check same as DesertTrade.
            bool hasBus = Game1.player.hasOrWillReceiveMail(MailFlags.CC_Vault)
                || Game1.player.mailReceived.Contains(MailFlags.CC_Vault)
                || Game1.player.hasOrWillReceiveMail(MailFlags.Joja_Bus)
                || Game1.player.mailReceived.Contains(MailFlags.Joja_Bus);
            if (!hasBus) return "公交未修复";
        }
        if (shopId == "QiGemShop")
        {
            bool hasWalnuts = Game1.netWorldState.Value.GoldenWalnutsFound >= 100;
            if (!hasWalnuts) return "需要100个金色核桃";
        }

        LoadIfNeeded();
        if (_hours == null) return null; // can't check, allow

        if (!_hours.TryGetValue(shopId, out var h) || h.IsAlwaysOpen)
            return null;

        int time = Game1.timeOfDay;
        int dow = (Game1.dayOfMonth - 1) % 7;
        string season = Game1.currentSeason;
        int day = Game1.dayOfMonth;

        // Closed day check
        if (h.ClosedDays.Contains(dow)) return h.ClosedDayLabel;
        // Seasonal close (e.g. summer 26 for Pierre)
        if (h.SeasonCloses.TryGetValue(season, out int closedDay) && day == closedDay)
            return $"{GetSeasonLabel(season)}{closedDay}日休息";
        // Time check
        if (h.OpenTime >= 0 && (time < h.OpenTime || time >= h.CloseTime))
            return $"{h.OpenTime:D4}-{h.CloseTime:D4}";

        return null;
    }

    private void LoadIfNeeded()
    {
        if (_loaded) return;
        _loaded = true;
        _hours = new Dictionary<string, StoreHours>();

        string path = Path.Combine(_helper.DirectoryPath, "time.txt");
        if (!File.Exists(path))
            path = Path.Combine(_helper.DirectoryPath, "assets", "time.txt");
        if (!File.Exists(path))
        {
            _monitor.Log("[StoreHours] time.txt not found, allowing all shops", LogLevel.Warn);
            _hours = null;
            return;
        }

        try
        {
            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                // Find ALL matching shop IDs for this keyword
                var matchedIds = new List<string>();
                foreach (var (sid, keyword) in ShopNameMap)
                {
                    if (line.Contains(keyword))
                        matchedIds.Add(sid);
                }
                if (matchedIds.Count == 0) continue;

                var parts = line.Split('\t');
                if (parts.Length < 3) continue;

                string timeStr = parts[1].Trim();
                string dayStr = parts[2].Trim();

                var h = new StoreHours();
                if (timeStr.Contains("全天"))
                {
                    h.IsAlwaysOpen = true;
                }
                else
                {
                    var times = timeStr.Split('–', '-');
                    if (times.Length >= 2)
                    {
                        h.OpenTime = ParseTime(times[0].Trim());
                        h.CloseTime = ParseTime(times[1].Trim());
                    }
                }

                foreach (var (label, idx) in DayOfWeekMap)
                {
                    if (dayStr.Contains(label))
                    {
                        h.ClosedDays.Add(idx);
                        h.ClosedDayLabel = label + "休息";
                        break;
                    }
                }
                if (dayStr == "无" || dayStr == "—" || string.IsNullOrEmpty(dayStr))
                    h.ClosedDays.Clear();

                string? notes = parts.Length > 3 ? parts[3] : null;

                foreach (var sid in matchedIds)
                {
                    var hh = new StoreHours
                    {
                        OpenTime = h.OpenTime, CloseTime = h.CloseTime,
                        IsAlwaysOpen = h.IsAlwaysOpen,
                        ClosedDays = new HashSet<int>(h.ClosedDays),
                        ClosedDayLabel = h.ClosedDayLabel,
                        SeasonCloses = new Dictionary<string, int>(h.SeasonCloses)
                    };
                    if (sid == "SeedShop" && notes != null && notes.Contains("夏季26日"))
                        hh.SeasonCloses["summer"] = 26;
                    _hours[sid] = hh;
                }
            }
            _monitor.Log($"[StoreHours] Loaded hours for {_hours.Count} shops from time.txt", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[StoreHours] Failed to parse time.txt: {ex.Message}", LogLevel.Warn);
            _hours = null;
        }
    }

    private static string GetSeasonLabel(string season) => season switch
    {
        "spring" => "春", "summer" => "夏", "fall" => "秋", "winter" => "冬", _ => season
    };

    private sealed class StoreHours
    {
        public int OpenTime = 600;
        public int CloseTime = 2400;
        public bool IsAlwaysOpen;
        public HashSet<int> ClosedDays = new();
        public string ClosedDayLabel = "";
        public Dictionary<string, int> SeasonCloses = new(); // season → day
    }
}
