using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Core;
using StardewModdingAPI;
using StardewValley;

namespace BankMod.Patches;

internal static class FbnNewsGenerator
{
    private static ModServices? _s;
    private static ModConfig? _c;
    private static string _modDir = "";
    private static readonly Random _rng = new();
    private static bool _bundleNewsShown;
    private static int _lastGeneratedDay = -1;
    private static List<string>? _cachedContent;

    public static void Initialize(ModServices services, ModConfig config, string modDir) { _s = services; _c = config; _modDir = modDir; }
    public static void InvalidateCache() { _cachedContent = null; _lastGeneratedDay = -1; }

    public static List<string> Generate()
    {
        // Cache: regenerate only once per day
        int today = Game1.dayOfMonth;
        string season = Game1.currentSeason;
        int cacheKey = Game1.year * 1000 + season.GetHashCode() + today;
        _s?.Monitor.Log($"[FBN] Generate: cacheHit={_cachedContent != null && _lastGeneratedDay == cacheKey}, cachedContentNull={_cachedContent == null}, lastDay={_lastGeneratedDay}, cacheKey={cacheKey}", LogLevel.Info);
        if (_cachedContent != null && _lastGeneratedDay == cacheKey)
        {
            _s?.Monitor.Log($"[FBN] Generate: returning cached content ({_cachedContent.Count} lines)", LogLevel.Info);
            return _cachedContent;
        }
        _lastGeneratedDay = cacheKey;

        var lines = new List<string>();
        if (_s is null || _c is null) { lines.Add(I18n.Get("fbn.1")); return lines; }

        var account = _s.BankAccountService.Load();
        string tomorrowWeather = Game1.weatherForTomorrow ?? "Sun";
        bool luckWeatherEnabled = _c.EnableLuckInfluence || _c.EnableWeatherInfluence;

        lines.Add(I18n.Get("fbn.2"));
        lines.Add(I18n.Get("fbn.3"));

        if (!_bundleNewsShown && TryBundleNews(lines)) { _bundleNewsShown = true; return lines; }
        if (TryFbnEventNews(lines, account)) return lines;
        if (TrySpecialDateNews(lines, today)) return lines;
        if (TrySeasonTransitionNews(lines, account)) return lines;
        if (TryCompanyBirthNews(lines, account, today)) return lines;
        if (TryGlobalEventNews(lines, account, tomorrowWeather)) return lines;

        // Season transition daily snippets (Day 1-3, doesn't block forecast)
        int doy = Game1.dayOfMonth;
        if (doy >= 1 && doy <= 3) AddSeasonDailySnippet(lines, doy);

        if (luckWeatherEnabled)
            GetForecastNews(lines, account, tomorrowWeather);
        else
            GetGeneralProgram(lines, account);

        _cachedContent = lines;
        return lines;
    }

    // ====== B: 献祭 / Joja 路线 ======
    private static bool TryBundleNews(List<string> lines)
    {
        if (Game1.player.hasOrWillReceiveMail("JojaMember"))
        {
            lines.Add(""); lines.Add("");
            lines.Add(I18n.Get("fbn.4"));
            lines.Add(I18n.Get("fbn.5"));
            lines.Add(I18n.Get("fbn.6"));
            return true;
        }
        if (Game1.player.mailReceived.Contains("ccIsComplete"))
        {
            lines.Add(""); lines.Add("");
            lines.Add(I18n.Get("fbn.7"));
            lines.Add(I18n.Get("fbn.8"));
            lines.Add(I18n.Get("fbn.9"));
            return true;
        }
        if (Game1.player.hasOrWillReceiveMail("ccCraftsRoom") || Game1.player.hasOrWillReceiveMail("ccPantry"))
        {
            lines.Add(""); lines.Add("");
            lines.Add(I18n.Get("fbn.10"));
            lines.Add(I18n.Get("fbn.11"));
            return true;
        }
        return false;
    }

    // ====== B2: FBN 真假消息 ======
    private static bool TryFbnEventNews(List<string> lines, BankAccountData account)
    {
        _s?.Monitor.Log($"[FBN] TryFbnEventNews: company={account.FbnEventCompany}, eventDay={account.FbnEventDay}, today={Game1.dayOfMonth}, showOutcome={account.FbnShowOutcome}", LogLevel.Info);
        // Day of event: show crisis news from TV2.txt (both real and fake)
        if (!string.IsNullOrEmpty(account.FbnEventCompany) && account.FbnEventDay == Game1.dayOfMonth && !account.FbnShowOutcome)
        {
            string text = ReadTv2Section("TV2.txt", account.FbnEventCompany);
            _s?.Monitor.Log($"[FBN] ReadTv2Section result: len={text.Length}", LogLevel.Info);
            if (!string.IsNullOrEmpty(text))
            {
                lines.Add(""); lines.Add("");
                // Chunk long text at sentence boundaries, max ~80 chars per page
                var sentences = text.Replace("。", "。|").Replace("？", "？|").Replace("！", "！|").Replace("……", "……|").Split('|');
                var sb = new System.Text.StringBuilder();
                foreach (var s in sentences)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    string chunk = s.Trim();
                    if (sb.Length + chunk.Length > 80 && sb.Length > 0)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear();
                    }
                    sb.Append(chunk);
                }
                if (sb.Length > 0) lines.Add(sb.ToString());
                _s?.Monitor.Log($"[FBN] Crisis news for {account.FbnEventCompany} (real={account.FbnEventIsReal})", LogLevel.Info);
                account.FbnShowOutcome = true; // always show outcome tomorrow
                return true;
            }
        }

        // Day after event: show outcome (both real and fake show 活 unless company actually died)
        if (account.FbnShowOutcome && Game1.dayOfMonth != account.FbnEventDay)
        {
            bool died = !account.DynamicCompanies.Any(c =>
                c.CompanyName == account.FbnEventCompany && c.Status != CompanyStatus.Bankrupt);
            string file = died ? I18n.Get("fbn.12") : I18n.Get("fbn.13");
            string text = ReadTv2Section(file, account.FbnEventCompany);
            if (!string.IsNullOrEmpty(text))
            {
                lines.Add(""); lines.Add("");
                var sentences = text.Replace("。", "。|").Replace("？", "？|").Replace("！", "！|").Replace("……", "……|").Split('|');
                var sb = new System.Text.StringBuilder();
                foreach (var s in sentences)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    string chunk = s.Trim();
                    if (sb.Length + chunk.Length > 80 && sb.Length > 0)
                    { lines.Add(sb.ToString()); sb.Clear(); }
                    sb.Append(chunk);
                }
                if (sb.Length > 0) lines.Add(sb.ToString());
                _s?.Monitor.Log($"[FBN] Outcome news for {account.FbnEventCompany} (died={died}, wasReal={account.FbnEventIsReal})", LogLevel.Info);
            }
            account.FbnEventCompany = "";
            account.FbnShowOutcome = false;
            return true;
        }

        return false;
    }

    private static string ReadTv2Section(string fileName, string companyName)
    {
        try
        {
            string path = Path.Combine(_modDir, fileName);
            if (!File.Exists(path)) return "";
            var allLines = File.ReadAllLines(path);
            bool inSection = false;
            var section = new List<string>();
            foreach (var line in allLines)
            {
                if (line.Contains("###") && line.Contains(companyName))
                {
                    inSection = true;
                    continue;
                }
                if (inSection)
                {
                    if (line.StartsWith("###") || line.StartsWith("---"))
                        break;
                    string t = line.Trim();
                    if (t.StartsWith(">")) t = t[1..].Trim();
                    if (!string.IsNullOrEmpty(t) && !t.StartsWith("##"))
                        section.Add(t);
                }
            }
            return string.Join("\n", section);
        }
        catch { return ""; }
    }

    // ====== 三: 特殊日期 ======
    private static bool TrySpecialDateNews(List<string> lines, int today)
    {
        if (Game1.currentSeason == "winter" && Game1.dayOfMonth == 1)
        {
            lines.Add(""); lines.Add("");
            lines.Add(I18n.Get("fbn.14"));
            lines.Add(I18n.Get("fbn.15"));
            lines.Add(I18n.Get("fbn.16"));
            return true;
        }
        if (Game1.currentSeason == "spring" && Game1.dayOfMonth == 1)
        {
            lines.Add(""); lines.Add("");
            lines.Add(I18n.Get("fbn.17"));
            lines.Add(I18n.Get("fbn.18"));
            return true;
        }
        return false;
    }

    // ====== 季节过渡特别节目 (Day 4 morning, priority: below 三, above 二) ======
    private static bool TrySeasonTransitionNews(List<string> lines, BankAccountData account)
    {
        if (Game1.dayOfMonth != 4) return false;

        var transitioning = account.DynamicCompanies
            .Where(c => c.Status != CompanyStatus.Bankrupt && c.Status != CompanyStatus.New)
            .ToList();

        lines.Add(""); lines.Add("");
        lines.Add(I18n.Get("fbn.19"));

        // TV.txt 一、节目开场（每季固定）
        string season = Game1.currentSeason;
        if (season == "spring")
        {
            lines.Add(I18n.Get("fbn.20"));
            lines.Add(I18n.Get("fbn.21"));
        }
        else if (season == "summer")
        {
            lines.Add(I18n.Get("fbn.22"));
            lines.Add(I18n.Get("fbn.23"));
        }
        else if (season == "fall")
        {
            lines.Add(I18n.Get("fbn.24"));
            lines.Add(I18n.Get("fbn.25"));
        }
        else
        {
            lines.Add(I18n.Get("fbn.26"));
            lines.Add(I18n.Get("fbn.27"));
        }
        lines.Add("");

        if (transitioning.Count == 0)
        {
            lines.Add(I18n.Get("fbn.28"));
            return true;
        }

        lines.Add(I18n.Get("fbn.29"));
        lines.Add("");

        foreach (var dc in transitioning)
        {
            var cd = CropDataProvider.GetByCode(dc.CropCode);
            string name = cd?.DisplayName ?? dc.CropCode;

            if (dc.Status == CompanyStatus.Prosperous)
                lines.Add(I18n.Get("fbn.30"));
            else if (dc.Status == CompanyStatus.Stable)
                lines.Add(I18n.Get("fbn.31"));
            else if (dc.Status == CompanyStatus.Hungry)
                lines.Add(I18n.Get("fbn.32"));
            else if (dc.Status == CompanyStatus.Dying)
                lines.Add(I18n.Get("fbn.33"));
        }

        lines.Add("");
        lines.Add(I18n.Get("fbn.34"));
        return true;
    }

    private static string GetPreviousSeasonName()
    {
        return Game1.currentSeason switch
        {
            "summer" => I18n.Get("fbn.35"), "fall" => I18n.Get("fbn.36"), "winter" => I18n.Get("fbn.37"), _ => I18n.Get("fbn.38")
        };
    }

    /// <summary>Day 1-3 season transition daily snippet (TV.txt 六). Added before forecast, doesn't block.</summary>
    private static void AddSeasonDailySnippet(List<string> lines, int dayOfMonth)
    {
        lines.Add("");
        switch (dayOfMonth)
        {
            case 1:
                string season = Game1.currentSeason;
                if (season == "spring")
                {
                    lines.Add(I18n.Get("fbn.20"));
                    lines.Add(I18n.Get("fbn.21"));
                    lines.Add(I18n.Get("fbn.39"));
                }
                else if (season == "summer")
                {
                    lines.Add(I18n.Get("fbn.22"));
                    lines.Add(I18n.Get("fbn.40"));
                    lines.Add(I18n.Get("fbn.41"));
                }
                else if (season == "fall")
                {
                    lines.Add(I18n.Get("fbn.42"));
                    lines.Add(I18n.Get("fbn.43"));
                    lines.Add(I18n.Get("fbn.44"));
                }
                else
                {
                    lines.Add(I18n.Get("fbn.26"));
                    lines.Add(I18n.Get("fbn.45"));
                    lines.Add(I18n.Get("fbn.46"));
                }
                break;
            case 2:
                lines.Add(I18n.Get("fbn.47"));
                lines.Add(I18n.Get("fbn.48"));
                lines.Add(I18n.Get("fbn.49"));
                break;
            case 3:
                lines.Add(I18n.Get("fbn.50"));
                lines.Add(I18n.Get("fbn.51"));
                lines.Add(I18n.Get("fbn.52"));
                break;
        }
    }

    // ====== 二: 动态公司诞生微讯 ======
    private static bool TryCompanyBirthNews(List<string> lines, BankAccountData account, int today)
    {
        var newborns = account.DynamicCompanies
            .Where(c => c.Status == CompanyStatus.New && c.GenerationDay == today)
            .ToList();
        if (newborns.Count == 0) return false;

        lines.Add(""); lines.Add("");
        lines.Add(I18n.Get("fbn.53"));
        foreach (var nc in newborns)
        {
            var cd = CropDataProvider.GetByCode(nc.CropCode);
            string cropName = cd?.DisplayName ?? nc.CropCode;
            string[] micro = GetBirthMicroNews(cropName);
            if (micro.Length > 0)
            {
                lines.Add(micro[0]);
                if (micro.Length > 1)
                    lines.Add(micro[1]);
            }
            else
            {
                lines.Add(I18n.Get("fbn.54"));
            }
        }
        return true;
    }

    /// <summary>Returns 1-2 micro-news lines for a newly born company. Content from TV.txt §二.</summary>
    private static string[] GetBirthMicroNews(string cropName)
    {
        // Spring
        if (cropName == I18n.Get("fbn.55")) return new[] { I18n.Get("fbn.56"), I18n.Get("fbn.57") };
        if (cropName == I18n.Get("fbn.58")) return new[] { I18n.Get("fbn.59"), I18n.Get("fbn.60") };
        if (cropName == I18n.Get("fbn.61")) return new[] { I18n.Get("fbn.62"), I18n.Get("fbn.63") };
        if (cropName == I18n.Get("fbn.64")) return new[] { I18n.Get("fbn.65"), I18n.Get("fbn.66") };
        if (cropName == I18n.Get("fbn.67")) return new[] { I18n.Get("fbn.68"), I18n.Get("fbn.69") };
        if (cropName == I18n.Get("fbn.70")) return new[] { I18n.Get("fbn.71"), I18n.Get("fbn.72") };
        if (cropName == I18n.Get("fbn.73")) return new[] { I18n.Get("fbn.74"), I18n.Get("fbn.75") };
        if (cropName == I18n.Get("fbn.76")) return new[] { I18n.Get("fbn.77"), I18n.Get("fbn.78") };
        if (cropName == I18n.Get("fbn.79")) return new[] { I18n.Get("fbn.80"), I18n.Get("fbn.81") };
        if (cropName == I18n.Get("fbn.82")) return new[] { I18n.Get("fbn.83"), I18n.Get("fbn.84") };
        if (cropName == I18n.Get("fbn.85")) return new[] { I18n.Get("fbn.86"), I18n.Get("fbn.87") };
        if (cropName == I18n.Get("fbn.88")) return new[] { I18n.Get("fbn.89") };
        // Summer
        if (cropName == I18n.Get("fbn.90")) return new[] { I18n.Get("fbn.91"), I18n.Get("fbn.92") };
        if (cropName == I18n.Get("fbn.93")) return new[] { I18n.Get("fbn.94"), I18n.Get("fbn.95") };
        if (cropName == I18n.Get("fbn.96")) return new[] { I18n.Get("fbn.97"), I18n.Get("fbn.98") };
        if (cropName == I18n.Get("fbn.99")) return new[] { I18n.Get("fbn.100"), I18n.Get("fbn.101") };
        if (cropName == I18n.Get("fbn.102")) return new[] { I18n.Get("fbn.103"), I18n.Get("fbn.104") };
        if (cropName == I18n.Get("fbn.105")) return new[] { I18n.Get("fbn.106"), I18n.Get("fbn.107") };
        if (cropName == I18n.Get("fbn.108")) return new[] { I18n.Get("fbn.109"), I18n.Get("fbn.110") };
        if (cropName == I18n.Get("fbn.111")) return new[] { I18n.Get("fbn.112"), I18n.Get("fbn.113") };
        if (cropName == I18n.Get("fbn.114")) return new[] { I18n.Get("fbn.115") };
        if (cropName == I18n.Get("fbn.116")) return new[] { I18n.Get("fbn.117"), I18n.Get("fbn.118") };
        if (cropName == I18n.Get("fbn.119")) return new[] { I18n.Get("fbn.120") };
        // Fall
        if (cropName == I18n.Get("fbn.121")) return new[] { I18n.Get("fbn.122"), I18n.Get("fbn.123") };
        if (cropName == I18n.Get("fbn.124")) return new[] { I18n.Get("fbn.125"), I18n.Get("fbn.126") };
        if (cropName == I18n.Get("fbn.127")) return new[] { I18n.Get("fbn.128"), I18n.Get("fbn.129") };
        if (cropName == I18n.Get("fbn.130")) return new[] { I18n.Get("fbn.131"), I18n.Get("fbn.132") };
        if (cropName == I18n.Get("fbn.133")) return new[] { I18n.Get("fbn.134"), I18n.Get("fbn.135") };
        if (cropName == I18n.Get("fbn.136")) return new[] { I18n.Get("fbn.137"), I18n.Get("fbn.138") };
        if (cropName == I18n.Get("fbn.139")) return new[] { I18n.Get("fbn.140") };
        if (cropName == I18n.Get("fbn.141")) return new[] { I18n.Get("fbn.142"), I18n.Get("fbn.143") };
        if (cropName == I18n.Get("fbn.144")) return new[] { I18n.Get("fbn.145"), I18n.Get("fbn.146") };
        if (cropName == I18n.Get("fbn.147")) return new[] { I18n.Get("fbn.148"), I18n.Get("fbn.149") };
        if (cropName == I18n.Get("fbn.150")) return new[] { I18n.Get("fbn.151") };
        // Winter
        if (cropName == I18n.Get("fbn.152")) return new[] { I18n.Get("fbn.153"), I18n.Get("fbn.154") };
        // Cross-season
        if (cropName == I18n.Get("fbn.155")) return new[] { I18n.Get("fbn.156"), I18n.Get("fbn.157") };
        if (cropName == I18n.Get("fbn.158")) return new[] { I18n.Get("fbn.159"), I18n.Get("fbn.160") };
        if (cropName == I18n.Get("fbn.161")) return new[] { I18n.Get("fbn.162"), I18n.Get("fbn.163") };
        if (cropName == I18n.Get("fbn.164")) return new[] { I18n.Get("fbn.165"), I18n.Get("fbn.166") };
        return Array.Empty<string>();
    }

    // ====== C: 全局财经事件 (30%) ======
    private static bool TryGlobalEventNews(List<string> lines, BankAccountData account, string weather)
    {
        if (_rng.Next(100) >= 30) return false;
        bool isUp = _rng.Next(2) == 0;
        lines.Add(""); lines.Add("");

        if (isUp)
        {
            lines.Add(I18n.Get("fbn.167"));
            lines.Add(I18n.Get("fbn.168"));
            lines.Add(I18n.Get("fbn.169"));
        }
        else
        {
            lines.Add(I18n.Get("fbn.170"));
            lines.Add(weather == "Rain" || weather == "Storm"
                ? I18n.Get("fbn.171")
                : I18n.Get("fbn.172"));
        }
        return true;
    }

    // ====== A: 每日利率预测 ======
    private static void GetForecastNews(List<string> lines, BankAccountData account, string weather)
    {
        lines.Add(""); lines.Add("");
        _s?.Monitor.Log($"[FBN] Forecast: TomorrowLuck={account.TomorrowLuck:F4}, LuckCoeff={_c!.LuckStrengthCoefficient}, weather={weather}, TomorrowRandoms={account.TomorrowRandoms.Count}", LogLevel.Info);

        // Weather modifier: bad weather → higher rates, good weather → lower rates
        double weatherMod = 0;
        if (_c.EnableWeatherInfluence)
        {
            weatherMod = weather switch
            {
                "Rain" => 0.001,
                "Storm" => 0.002,
                "Snow" => 0.0005,
                "Sun" => -0.0005,
                _ => 0
            };
        }

        lines.Add(I18n.Get("fbn.182"));
        lines.Add(I18n.Get("fbn.183"));

        int shown = 0;
        foreach (var dc in account.DynamicCompanies)
        {
            if (dc.Status == CompanyStatus.Bankrupt) continue;
            if (shown >= 3) break;
            shown++;
            var cd = CropDataProvider.GetByCode(dc.CropCode);
            string name = cd?.DisplayName ?? dc.CropCode;
            // 3-tier base rate (same as actual calculation)
            double baseRate = cd?.R > 0.20 ? cd.R * 0.5 : cd?.R > 0 ? cd.R : 0;

            // Luck flat additive: sign(tomorrowLuck) × coefficient / 100
            double luckFlat = _c.EnableLuckInfluence
                ? Math.Sign(account.TomorrowLuck) * _c.LuckStrengthCoefficient / 100.0
                : 0;

            if (_c.RandomnessMultiplier > 0 && account.TomorrowRandoms.TryGetValue(dc.CompanyName, out double baseRnd))
            {
                // Map random [-1,+1] to slot [0,4], scale by multiplier (5 = ±50%)
                int slot = (int)Math.Clamp((baseRnd + 1.0) / 2.0 * 5, 0, 4);
                double rnd = -1.0 + slot * 0.5;
                double rate = (baseRate + luckFlat) * (1 + rnd * 0.5 * (_c.RandomnessMultiplier / 5.0)) + weatherMod;
                rate = Math.Max(0, rate);
                lines.Add($"  {name}：{rate * 100:F1}%");
            }
            else
            {
                // No randomness: single predicted rate
                double rate = Math.Max(0, baseRate + luckFlat + weatherMod);
                lines.Add($"  {name}：{rate * 100:F2}%");
            }
        }
        if (shown == 0) lines.Add(I18n.Get("fbn.184"));
    }

    // ====== D/E: 通用节目 ======
    private static void GetGeneralProgram(List<string> lines, BankAccountData account)
    {
        lines.Add(""); lines.Add("");
        int pick = _rng.Next(4);
        switch (pick)
        {
            case 0:
                lines.Add(I18n.Get("fbn.185"));
                lines.Add(I18n.Get("fbn.186"));
                lines.Add(I18n.Get("fbn.187"));
                break;
            case 1:
                lines.Add(I18n.Get("fbn.188"));
                lines.Add(I18n.Get("fbn.189"));
                break;
            case 2:
                lines.Add(I18n.Get("fbn.190"));
                lines.Add(I18n.Get("fbn.191"));
                break;
            case 3:
                lines.Add(I18n.Get("fbn.192"));
                lines.Add(I18n.Get("fbn.193"));
                break;
        }
    }

    private static string WeatherToCN(string w) => w switch
    {
        "Rain" => I18n.Get("fbn.194"), "Storm" => I18n.Get("fbn.195"), "Snow" => I18n.Get("fbn.196"), "Wind" => I18n.Get("fbn.197"),
        "Sun" => I18n.Get("fbn.198"), "Festival" => I18n.Get("fbn.199"), _ => I18n.Get("fbn.200")
    };
}
