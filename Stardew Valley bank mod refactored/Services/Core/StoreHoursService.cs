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
    private readonly IRouteService _routeService;
    private Dictionary<string, StoreHours>? _hours;
    private bool _loaded;

    // Maps shopId → time.txt store name keywords
    private static readonly Dictionary<string, string> ShopNameMap = new()
    {
        ["SeedShop"] = I18n.Get("rte.2"),
        ["Carpenter"] = I18n.Get("str.1"),
        ["Blacksmith"] = I18n.Get("str.2"),
        ["Joja"] = "Joja",
        ["FishShop"] = I18n.Get("str.3"),
        ["DesertTrade"] = I18n.Get("str.4"),
        ["Sandy"] = I18n.Get("str.4"),
        ["IslandTrade"] = I18n.Get("str.5"),
    };

    // Chinese day-of-week → index (0=Mon..6=Sun)
    private static readonly Dictionary<string, int> DayOfWeekMap = new()
    {
        [I18n.Get("str.6")] = 0, [I18n.Get("str.7")] = 1, [I18n.Get("str.8")] = 2, [I18n.Get("str.9")] = 3, [I18n.Get("str.10")] = 4, [I18n.Get("str.11")] = 5, [I18n.Get("str.12")] = 6,
        [I18n.Get("str.13")] = 0, [I18n.Get("str.14")] = 1, [I18n.Get("str.15")] = 2, [I18n.Get("str.16")] = 3, [I18n.Get("str.17")] = 4, [I18n.Get("str.18")] = 5, [I18n.Get("str.19")] = 6,
    };

    // Chinese time → 24h int
    private static int ParseTime(string chineseTime)
    {
        // "上午9:00" → 900, "下午9:00" → 2100, "凌晨12:00" → 0, "全天24小时" → -1
        if (chineseTime.Contains(I18n.Get("str.20"))) return -1;

        bool isPM = chineseTime.Contains(I18n.Get("str.21")) || chineseTime.Contains(I18n.Get("str.22")) || chineseTime.Contains(I18n.Get("str.23"));
        string num = chineseTime.Replace(I18n.Get("str.24"), "").Replace(I18n.Get("str.21"), "").Replace(I18n.Get("str.22"), "").Replace(I18n.Get("str.23"), "").Replace(":", "").Trim();
        if (!int.TryParse(num, out int t)) return -1;
        // "凌晨12:00" = 0:00 = 0
        if (chineseTime.Contains(I18n.Get("str.23")) && t >= 1200) t -= 1200;
        if (isPM && t < 1200 && !chineseTime.Contains(I18n.Get("str.23"))) t += 1200;
        return t;
    }

    public StoreHoursService(IModHelper helper, IMonitor monitor, IRouteService routeService)
    {
        _helper = helper;
        _monitor = monitor;
        _routeService = routeService;
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
            if (!hasBus) return I18n.Get("str.25");
        }
        if (shopId == "IslandTrade")
        {
            bool hasBoat = Game1.player.hasOrWillReceiveMail(MailFlags.WillyBoatFixed)
                || Game1.player.mailReceived.Contains(MailFlags.WillyBoatFixed);
            if (!hasBoat) return I18n.Get("str.26");
        }
        if (shopId == "Sandy")
        {
            // Sandy's Oasis: 9:00-23:00, closed Tue. Bus check same as DesertTrade.
            bool hasBus = Game1.player.hasOrWillReceiveMail(MailFlags.CC_Vault)
                || Game1.player.mailReceived.Contains(MailFlags.CC_Vault)
                || Game1.player.hasOrWillReceiveMail(MailFlags.Joja_Bus)
                || Game1.player.mailReceived.Contains(MailFlags.Joja_Bus);
            if (!hasBus) return I18n.Get("str.25");
        }
        if (shopId == "QiGemShop")
        {
            bool hasWalnuts = Game1.netWorldState.Value.GoldenWalnutsFound >= 100;
            if (!hasWalnuts) return I18n.Get("str.27");
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
        // CC route completed → Pierre opens every day (vanilla SDV behavior)
        if (h.ClosedDays.Contains(dow))
        {
            if (shopId == "SeedShop" && _routeService.CompletedRoute == "Community")
            {
                _monitor.Log("[StoreHours] Pierre: CC route completed, ignoring Wednesday closure", LogLevel.Trace);
            }
            else
            {
                return h.ClosedDayLabel;
            }
        }
        // Seasonal close (e.g. summer 26 for Pierre)
        if (h.SeasonCloses.TryGetValue(season, out int closedDay) && day == closedDay)
            return I18n.Get("str.28", new { day });
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
                if (timeStr.Contains(I18n.Get("str.20")))
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
                        h.ClosedDayLabel = label + I18n.Get("str.29");
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
                    if (sid == "SeedShop" && notes != null && notes.Contains(I18n.Get("str.30")))
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
        "spring" => I18n.Get("str.31"), "summer" => I18n.Get("str.32"), "fall" => I18n.Get("str.33"), "winter" => I18n.Get("str.34"), _ => season
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
