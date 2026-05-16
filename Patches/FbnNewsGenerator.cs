using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Core;
using StardewValley;

namespace BankMod.Patches;

internal static class FbnNewsGenerator
{
    private static ModServices? _s;
    private static ModConfig? _c;
    private static readonly Random _rng = new();

    public static void Initialize(ModServices services, ModConfig config) { _s = services; _c = config; }

    public static List<string> Generate()
    {
        var lines = new List<string>();
        if (_s is null || _c is null) { lines.Add("FBN 初始化中..."); return lines; }

        var account = _s.BankAccountService.Load();
        int today = (int)Game1.stats.DaysPlayed;
        string tomorrowWeather = Game1.weatherForTomorrow ?? "Sun";
        bool luckWeatherEnabled = _c.EnableLuckInfluence || _c.EnableWeatherInfluence;

        lines.Add("=== FBN · 芬吉尔共和国商业财经频道 ===");
        lines.Add("主播：巴德·坦纳顿");

        if (TryBundleNews(lines)) return lines;
        if (TrySpecialDateNews(lines, today)) return lines;
        if (TryCompanyBirthNews(lines, account, today)) return lines;
        if (TryGlobalEventNews(lines, account, tomorrowWeather)) return lines;

        if (luckWeatherEnabled)
            GetForecastNews(lines, account, tomorrowWeather);
        else
            GetGeneralProgram(lines, account);

        return lines;
    }

    // ====== B: 献祭 / Joja 路线 ======
    private static bool TryBundleNews(List<string> lines)
    {
        if (Game1.player.hasOrWillReceiveMail("JojaMember"))
        {
            lines.Add(""); lines.Add("");
            lines.Add("Joja集团今日宣布上调金融服务利率。发言人表示：'这是Joja会员体系");
            lines.Add("带来的运营红利正反哺金融服务。这是Joja生态的胜利。'");
            lines.Add("我们采访了不愿透露姓名的镇民，对方只说了三个字：'信他个……咳。'");
            return true;
        }
        if (Game1.player.hasOrWillReceiveMail("ccIsComplete") || Game1.player.mailReceived.Contains("ccIsComplete"))
        {
            lines.Add(""); lines.Add("");
            lines.Add("社区中心全部献祭包完成。刘易斯镇长激动地宣布：'鹈鹕镇赢了！'");
            lines.Add("皮埃尔在杂货店挂出新招牌：'传统农业，可信赖的金融服务。'");
            lines.Add("Joja今天没有发表评论。他们也没有人接受我们的采访。");
            return true;
        }
        if (Game1.player.hasOrWillReceiveMail("ccCraftsRoom") || Game1.player.hasOrWillReceiveMail("ccPantry"))
        {
            lines.Add(""); lines.Add("");
            lines.Add("社区中心传来消息：森林精灵对最近的献祭表示满意。");
            lines.Add("刘易斯镇长：'鹈鹕镇传统价值回归的第一步，也是经济上的一步。'");
            return true;
        }
        return false;
    }

    // ====== 三: 特殊日期 ======
    private static bool TrySpecialDateNews(List<string> lines, int today)
    {
        if (Game1.currentSeason == "winter" && Game1.dayOfMonth == 1)
        {
            lines.Add(""); lines.Add("");
            lines.Add("镇长刘易斯发表年度经济演讲：'今年鹈鹕镇的金融市场走出了自己的节奏。");
            lines.Add("动态公司新增了一些，也倒闭了一些。但留下的，都是强者。'");
            lines.Add("Marnie站起来鼓掌，比所有人都用力。");
            return true;
        }
        if (Game1.currentSeason == "spring" && Game1.dayOfMonth == 1)
        {
            lines.Add(""); lines.Add("");
            lines.Add("新的一年，新的开始在鹈鹕镇。巴德·坦纳顿祝所有农场主：");
            lines.Add("'愿你的作物丰收，愿你的利率永远向上，愿你的贷款按时还清。'");
            return true;
        }
        return false;
    }

    // ====== 二: 动态公司诞生微讯 ======
    private static bool TryCompanyBirthNews(List<string> lines, BankAccountData account, int today)
    {
        var newborns = account.DynamicCompanies
            .Where(c => c.Status == CompanyStatus.New && c.GenerationDay == today)
            .ToList();
        if (newborns.Count == 0) return false;

        lines.Add(""); lines.Add("");
        lines.Add("--- 新公司诞生 ---");
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
                lines.Add($"欢迎 {nc.CompanyName} 加入鹈鹕镇金融市场！");
            }
        }
        return true;
    }

    /// <summary>Returns 1-2 micro-news lines for a newly born company. Content from TV.txt §二.</summary>
    private static string[] GetBirthMicroNews(string cropName)
    {
        return cropName switch
        {
            // Spring
            "胡萝卜" => new[] {
                "胡萝卜公司今早发布季报：生长快、利率高，是春季农民的'第一桶金'。CEO表示：'我们不做最贵的种子，只做最快看到利息的账户。'——分析师提醒：快进快出，别等夏天才想起胡萝卜。",
                "胡萝卜公司的存款利率高达16.67%，但燃料需求也不低。刘易斯镇长评论：'种胡萝卜像快钱，爽是爽，但别指望它养你一辈子。'"
            },
            "草莓" => new[] {
                "草莓公司发言人穿着粉色西装亮相：'我们的利率虽被打了对折，但依然站在春季顶端。甜蜜，但不廉价。'——观众席上，阿比盖尔小声说：'草莓确实甜，但我不喜欢打折。'",
                "草莓公司提醒农场主：连续收获模式下，别只盯着单次收益。'种一季草莓，像存一笔定期。别提前支取，除非你想让利息变绿。'"
            },
            "防风草" => new[] {
                "防风草公司发布声明：'我们不打折，因为我们够基础。'——巴德评论：'确实，连利率都懒得动。'玛妮路过：'但猪猪们喜欢防风草。'",
                "防风草公司利率稳定在18.75%，适合不想折腾的农场主。皮埃尔说：'这是入门级金融产品，就像我的入门级种子。'"
            },
            "青豆" => new[] {
                "青豆公司CEO站在爬架上接受采访：'我们长高，利率也稳。17.46%不打折，这就是垂直发展的魅力。'——克林特：'我喜欢垂直的东西。'场面一度尴尬。",
                "青豆公司强调：'连续收获，不是连续收割利息。'——分析师：'但如果你不出货，它们确实会停止生长。'"
            },
            "土豆" => new[] {
                "土豆公司今日发布多产系数修正报告：'平均1.2倍产量，不是运气，是科学。'——巴德：'科学地多赚土豆，也科学地多还贷款。'",
                "土豆公司利率15.33%，燃料需求不高。潘姆评论：'土豆实在，不像某些水果，贵得要死还跌价。'"
            },
            "大蒜" => new[] {
                "大蒜公司：'我们的利率12.5%，不够性感，但能驱赶吸血鬼般的通胀。'——塞巴斯蒂安：'这比喻太老土了。不过大蒜确实有用。'",
                "大蒜公司春季最后一批出货提醒：'别等到夏天才想起我们。利率不会发芽。'"
            },
            "未碾米" => new[] {
                "未碾米公司今日未发表评论。因为他们的利率为负。巴德：'这是鹈鹕镇唯一一家存钱还会亏的公司。建议你别存，直接种别的。'",
                "未碾米公司发言人终于开口：'我们… 我们还在打磨产品。'乔治：'打磨米？你们该打磨的是利率。'"
            },
            "花椰菜" => new[] {
                "花椰菜公司：'大器晚成，9.9%的利率，配得上9天的等待。'——巴德：'也配得上3.3的日耗。适合土地少的佛系玩家。'",
                "花椰菜公司提醒农场主：'别因为长得像大脑，就做蠢事。比如把存款全放在我们这。'"
            },
            "羽衣甘蓝" => new[] {
                "羽衣甘蓝公司：'健康、稳定、不打折。'——莉亚：'像我的沙拉。也像我的存款账户。'",
                "羽衣甘蓝利率9.52%，燃料需求6.7。适合不想折腾但希望有点收益的农场主。玛妮：'猪猪们不吃这个，但我吃。'"
            },
            "蓝爵" => new[] {
                "蓝爵公司：'我们不是花，是利率。'——但巴德查了数据：'9.52%，和羽衣甘蓝一样。你们是商量好的？'",
                "蓝爵公司强调美观与收益并存。艾芙琳：'种一片蓝爵，看着心情好，账户也涨一点。'"
            },
            "大黄" => new[] {
                "大黄公司：'酸得够味，利率不高但稳。'——格斯：'用来做酒不错。用来存钱？也行吧。'",
                "大黄公司燃料需求极低（3.3），适合不想喂太多作物的懒人农场主。'少种一点，照样有利息。'"
            },
            "郁金香" => new[] {
                "郁金香公司：'我们是春天的颜色，也是春天的利率。8.33%，不惊艳，但从不缺席。'——巴德：'就像电视节目里的广告。'"
            },
            // Summer
            "蓝莓" => new[] {
                "蓝莓公司CEO穿着蓝色西装宣布：'我们的利率虽然打了对折，但20.31%依然是夏季的蓝筹股。'——巴德：'蓝筹？是蓝莓筹吧。'",
                "蓝莓公司燃料需求高（日耗20），但产能也高。刘易斯：'种蓝莓像养一群猪，吃得快，拉得多，但利润也大。'"
            },
            "啤酒花" => new[] {
                "啤酒花公司今天在星之果实餐吧开香槟——哦不，开啤酒。CEO微醺：'18.06%的存款利率，足够你买醉。贷款利率27.08%，足够你清醒。'",
                "啤酒花公司日耗30，是燃料需求之王。玛妮：'种啤酒花？你得有足够的地。不然公司比你醉得快。'"
            },
            "辣椒" => new[] {
                "辣椒公司：'我们的利率热辣辣，14.58%。贷款利率21.88%，别被辣哭了。'——巴德：'适合敢玩火的人。'",
                "辣椒公司强调连续收获的乐趣。山姆：'种辣椒像打鼓，节奏对了就一直有。'"
            },
            "番茄" => new[] {
                "番茄公司：'12.5%的存款利率，不是最高，但最亲民。'——巴德：'就像番茄本身，哪里都能种，哪里都能卖。'",
                "番茄公司提醒农场主：'别因为连续收获就忘了还贷款。番茄红了，账单也红了。'"
            },
            "萝卜" => new[] {
                "萝卜公司：'10.42%的存款利率，适合快进快出的农场主。'——巴德：'毕竟萝卜长得快，利息也长得快？不一定。'",
                "萝卜公司日耗6.7，中规中矩。克林特：'我喜欢萝卜。不废话。'"
            },
            "红叶卷心菜" => new[] {
                "红叶卷心菜公司：'17.78%不打折，我们是夏季的中流砥柱。'——巴德：'也是唯一一个名字带颜色却没什么人关注的作物。'",
                "红叶卷心菜公司燃料需求5.0，适合中等规模农场。皮埃尔：'种子不便宜，但值得。'"
            },
            "甜瓜" => new[] {
                "甜瓜公司：'我们大、圆、甜，利率17.71%。'——巴德：'也重、占地、生长慢。'刘易斯：'但一个甜瓜顶三个防风草。'",
                "甜瓜公司提醒农场主：'别因为长得大就以为利息也大。种甜瓜是马拉松，不是短跑。'"
            },
            "夏季西葫芦" => new[] {
                "金皮西葫芦公司（夏季版）：'16.67%的存款利率，不打折。'——巴德：'但你不是春季作物吗？'公司：'夏天也有我们的位置。'",
                "金皮西葫芦公司燃料需求6.7，适合过渡期种植。玛妮：'猪猪们喜欢西葫芦。虽然它们什么都喜欢。'"
            },
            "夏季亮片" => new[] {
                "夏季亮片公司：'我们是花，不是菜，但利率10.00%，比某些菜还稳。'——艾芙琳：'种一片亮片，像存一笔小钱，看着开心。'"
            },
            "杨桃" => new[] {
                "杨桃公司：'我们贵，但我们值。6.73%的存款利率，配上3.6的日耗，适合精打细算的农场主。'——巴德：'也适合贷款买种子的勇士。'",
                "杨桃公司贷款利率10.10%，不算高。但分析师警告：'种子贵，别种一半没钱了。'"
            },
            "罂粟" => new[] {
                "罂粟公司：'5.71%的利率，不高，但美丽。'——巴德：'美丽不能当饭吃，但能当花卖。'哈维：'作为医生，我建议别种太多。'"
            },
            // Fall
            "甜菜" => new[] {
                "甜菜公司：'66.67%的基础R，33.33%的存款利率，这是秋季的王炸。'——巴德：'也是燃料需求最低的高R作物。甜菜，甜过你的贷款。'",
                "甜菜公司CEO：'我们不是糖，我们是钱。'——潘姆：'听起来不错。但我更想要可乐。'"
            },
            "茄子" => new[] {
                "茄子公司：'56%的基础R，28%的存款利率，贷款利率42%。'——巴德：'借钱种茄子？你得卖多少茄子才还清？'",
                "茄子公司强调连续收获。玛妮：'茄子紫得像猪猪的鼻子。但猪不种地。'"
            },
            "洋蓟" => new[] {
                "洋蓟公司：'54.17%的R，27.08%的存款利率。我们贵，但我们值。'——巴德：'也难种。适合老手。'",
                "洋蓟公司燃料需求5.0，不算高。克林特：'我喜欢洋蓟。但我不说为什么。'"
            },
            "西兰花" => new[] {
                "西兰花公司：'45.83%的R，22.92%的存款利率。我们是秋季的健康选择。'——巴德：'也是燃料需求较高的健康选择。'",
                "西兰花公司提醒农场主：'别因为名字像树，就把钱存成木头。'"
            },
            "葡萄" => new[] {
                "葡萄公司：'39.68%的R，19.84%的存款利率。我们爬藤，利率也爬。'——格斯：'用来酿酒更好。用来存钱也行。'",
                "葡萄公司连续收获模式适合懒人。塞巴斯蒂安：'种一次，收一季。像存款，存一次，利一季。'"
            },
            "南瓜" => new[] {
                "南瓜公司：'16.92%的存款利率，不打折。我们是万圣节的主角，也是秋季的中坚。'——巴德：'也是占地大户。一个南瓜占一格，别贪心。'",
                "南瓜公司燃料需求极低（3.3）。刘易斯：'种南瓜像存定期，稳，但慢。'"
            },
            "山药" => new[] {
                "山药公司：'16.67%的存款利率，和南瓜差不多，但我们更土。'——巴德：'土不是缺点，是特点。'玛妮：'猪猪们喜欢山药。它们喜欢一切圆的。'"
            },
            "苋菜" => new[] {
                "苋菜公司：'16.33%的存款利率，适合不喜欢折腾的农场主。'——巴德：'也适合喜欢紫色的。'",
                "苋菜公司燃料需求6.7，中规中矩。艾芙琳：'种苋菜像织毛衣，慢慢来，总有收获。'"
            },
            "白菜" => new[] {
                "白菜公司：'15.00%的存款利率，不高不低，但我们的燃料需求11.7。'——巴德：'适合大地主。小农场主别碰。'",
                "白菜公司强调连续收获。山姆：'种白菜像打游戏，一直按一直有。'"
            },
            "蔓越莓" => new[] {
                "蔓越莓公司：'8.50%的存款利率，不高，但我们是秋季的产量之王。'——巴德：'12.0的Y值，日耗20，仓库上限120。种蔓越莓，种到地老天荒。'",
                "蔓越莓公司CEO：'我们不追求高利率，我们追求稳定。'——巴德：'稳定地吃你的地，稳定地出货。'"
            },
            "仙子玫瑰" => new[] {
                "仙子玫瑰公司：'3.75%的存款利率，我们是花，不是菜，别指望暴富。'——艾芙琳：'但好看。种一片，心情好。'巴德：'心情好不算利息。'"
            },
            // Winter
            "霜瓜" => new[] {
                "霜瓜公司：'14.29%的存款利率，不打折。我们是冬天唯一的选择。'——巴德：'也是唯一一个名字带霜但利率不冷的。'",
                "霜瓜公司燃料需求6.7，适合冬季无聊种地的人。玛妮：'冬天猪猪们不出门，但霜瓜可以。'"
            },
            // Cross-season
            "小麦" => new[] {
                "小麦公司：'夏秋两季，37.5%的R，18.75%的存款利率。我们是面包的起点，也是利率的中等生。'——巴德：'适合不想换作物的懒人。'",
                "小麦公司燃料需求11.7，连续收获。皮埃尔：'种小麦像印钱，慢，但稳。'"
            },
            "上古水果" => new[] {
                "上古水果公司：'19.70%的存款利率，接近20%不打折线。我们是时间的果实，也是耐心的回报。'——巴德：'也是84天9收的数学题。种一次，吃一年。'",
                "上古水果公司CEO：'我们的等效支出只有282g，别被550g的售价吓到。'——巴德：'种子制造机才是真神。'"
            },
            "玉米" => new[] {
                "玉米公司：'2.14%的存款利率，不高，但我们是夏秋跨季的填充物。'——巴德：'适合不想动脑的人。'",
                "玉米公司贷款利率3.21%，硬下限1%之上。潘姆：'玉米？我只吃爆米花。'"
            },
            "向日葵" => new[] {
                "向日葵公司：'我们的R是负的，存款利率0%，贷款利率也0%。'——巴德：'但这不意味着免费借钱。硬下限1%，别想白嫖。'",
                "向日葵公司发言人：'我们… 我们主要是好看。'艾芙琳：'是的，好看。别存钱就行。'"
            },
            _ => Array.Empty<string>()
        };
    }

    // ====== C: 全局财经事件 (30%) ======
    private static bool TryGlobalEventNews(List<string> lines, BankAccountData account, string weather)
    {
        if (_rng.Next(100) >= 30) return false;
        bool isUp = _rng.Next(2) == 0;
        lines.Add(""); lines.Add("");

        if (isUp)
        {
            lines.Add("今日鹈鹕镇金融市场迎来意外涨幅。多家农业公司利率上调。");
            lines.Add("北部的气象站发来确认：昨天运气确实不错。");
            lines.Add("农场主们，这是大自然给你们的股息。");
        }
        else
        {
            lines.Add("市场情绪低迷。今日多家公司利率大幅回调。");
            lines.Add(weather == "Rain" || weather == "Storm"
                ? "分析人士归因于坏运气和暴雨。建议农场主们先去喝一杯格斯的热咖啡。"
                : "分析人士归因于市场自然波动。建议农场主们别急着看账户。");
        }
        return true;
    }

    // ====== A: 每日利率预测 ======
    private static void GetForecastNews(List<string> lines, BankAccountData account, string weather)
    {
        lines.Add(""); lines.Add("");
        double predictedLuck = _rng.NextDouble() * 0.2 - 0.1;
        string luckWord = predictedLuck > 0.05 ? "好运" : predictedLuck < -0.05 ? "坏运" : "中性";
        string luckIcon = predictedLuck > 0.05 ? "☀" : predictedLuck < -0.05 ? "☂" : "☁";

        string weatherCN = WeatherToCN(weather);
        string weatherImpact = weather switch
        {
            "Rain" => "皮埃尔存款利率或受益，Joja 承压",
            "Storm" => "高波动日，贷款利率可能大幅上升",
            "Snow" => "适合稳健持有，利率波动有限",
            "Wind" => "市场方向不明，建议观望",
            "Sun" => "Joja 存款利率可能微涨",
            _ => "利率波动预计温和"
        };

        lines.Add($"--- 明日利率预测 ---");
        lines.Add($"运气预测：{luckWord} {luckIcon}  |  明日{weatherCN}，{weatherImpact}");

        int shown = 0;
        foreach (var dc in account.DynamicCompanies)
        {
            if (dc.Status == CompanyStatus.Bankrupt) continue;
            if (shown >= 3) break;
            shown++;
            double predictedChange = (_rng.NextDouble() - 0.4) * 0.03;
            string arrow = predictedChange > 0.005 ? "▲" : predictedChange < -0.005 ? "▼" : "→";
            var cd = CropDataProvider.GetByCode(dc.CropCode);
            string name = cd?.DisplayName ?? dc.CropCode;
            lines.Add($"  {dc.CompanyName}：{arrow} {Math.Abs(predictedChange * 100):F2}%");
        }
        if (shown == 0) lines.Add("今日无活跃动态公司，市场平静。");
    }

    // ====== D/E: 通用节目 ======
    private static void GetGeneralProgram(List<string> lines, BankAccountData account)
    {
        lines.Add(""); lines.Add("");
        int pick = _rng.Next(4);
        switch (pick)
        {
            case 0:
                lines.Add("刘易斯镇长发表讲话：'鹈鹕镇的农业经济，依托我们优秀的农场主，");
                lines.Add("和稳健的金融服务体系，正在步入一个新的时代。'");
                lines.Add("他说这话时身后站着一排猪，Marnie在镜头外偷偷挥手。");
                break;
            case 1:
                lines.Add("Marnie接受采访：'猪猪们很努力，每天都在给农场主惊喜。'");
                lines.Add("'金融？我把钱都存在皮埃尔，Joja的莫里斯盯着我看的样子让我不自在。'");
                break;
            case 2:
                lines.Add("威利坐在码头边：'钓鱼和投资有点像——你得等。'");
                lines.Add("'但钓鱼至少你能吃掉钓上来的东西。贷款？我更相信大海。'");
                break;
            case 3:
                lines.Add("格斯展示今日特调：'农场主们辛苦了。来喝一杯吧。'");
                lines.Add("'存款涨一个百分点，不如我的啤酒涨一个指节。当然，我不是理财顾问。'");
                break;
        }
    }

    private static string WeatherToCN(string w) => w switch
    {
        "Rain" => "雨天", "Storm" => "暴风雨", "Snow" => "雪天", "Wind" => "大风天",
        "Sun" => "晴天", "Festival" => "节日", _ => "多云"
    };
}
