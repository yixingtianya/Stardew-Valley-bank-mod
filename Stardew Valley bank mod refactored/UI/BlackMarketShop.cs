using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

/// <summary>神秘黑店 - 金币回收商店，任何时间都能打开。</summary>
internal static class BlackMarketShop
{
    /// <summary>打开神秘黑店。</summary>
    public static void Open()
    {
        var stock = BuildStock();
        Game1.activeClickableMenu = new ShopMenu(
            shopId: "BlackMarket",
            itemPriceAndStock: stock,
            currency: 0,
            who: null
        );
    }

    private static Dictionary<ISalable, ItemStockInformation> BuildStock()
    {
        var stock = new Dictionary<ISalable, ItemStockInformation>();

        // === 食物Buff ===
        AddFood(stock, 279, 100_000);  // 魔法糖冰棍
        AddFood(stock, 204, 80_000);   // 幸运午餐
        AddFood(stock, 921, 80_000);   // 墨汁意大利饺
        AddFood(stock, 230, 50_000);   // 红之盛宴
        AddFood(stock, 212, 50_000);   // 鲑鱼晚餐
        AddFood(stock, 253, 30_000);   // 三倍浓缩咖啡
        AddFood(stock, 242, 40_000);   // 海之菜肴
        AddFood(stock, 241, 20_000);   // 救生汉堡
        AddFood(stock, 243, 25_000);   // 矿工特供
        AddFood(stock, 201, 40_000);   // 完美早餐
        AddFood(stock, 226, 40_000);   // 香辣鳗鱼
        AddFood(stock, 235, 30_000);   // 秋日恩赐
        AddFood(stock, 240, 20_000);   // 农夫午餐
        AddFood(stock, 221, 20_000);   // 粉红蛋糕
        AddFood(stock, 907, 50_000);   // 热带咖喱
        AddFood(stock, 873, 50_000);   // 椰林飘香

        // === 战斗消耗品 ===
        AddObject(stock, 74, 100_000);  // 五彩碎片
        AddObject(stock, 773, 50_000);  // 生命药水
        AddObject(stock, 879, 30_000);  // 怪兽香水
        AddObject(stock, 772, 8_000);   // 蒜油

        // === 核心材料 ===
        AddObject(stock, 337, 80_000);  // 铱锭
        AddObject(stock, 787, 50_000);  // 电池组
        AddObject(stock, 386, 50_000);  // 铱矿石
        AddObject(stock, 384, 20_000);  // 黄金矿石
        AddObject(stock, 918, 50_000);  // 顶级生长激素
        AddObject(stock, 919, 50_000);  // 顶级肥料
        AddObject(stock, 920, 50_000);  // 顶级保湿土壤
        AddObject(stock, 909, 30_000);  // 放射性矿石

        // === 树液类 ===
        AddObject(stock, 725, 5_000);   // 橡树树脂
        AddObject(stock, 724, 3_000);   // 枫糖浆
        AddObject(stock, 726, 2_000);   // 松焦油
        AddObjectStr(stock, "MysticSyrup", 8_000);  // 神秘糖浆

        // === 钓鱼消耗品 ===
        AddObject(stock, 908, 50_000);  // 魔法鱼饵
        AddObject(stock, 774, 10_000);  // 万能鱼饵
        AddObject(stock, 856, 30_000);  // 珍稀诱钩

        // === 传送图腾 ===
        AddObject(stock, 261, 30_000);  // 传送图腾：沙漠
        AddObject(stock, 688, 20_000);  // 传送图腾：农场
        AddObject(stock, 689, 20_000);  // 传送图腾：山岭
        AddObject(stock, 690, 25_000);  // 传送图腾：海滩
        // 传送图腾：姜岛 — 需要解锁姜岛才显示
        if (Game1.player.hasOrWillReceiveMail("willyBoatFixed") || Game1.player.mailReceived.Contains("willyBoatFixed"))
            AddObject(stock, 886, 40_000);

        // === 动物产品 ===
        AddObject(stock, 430, 30_000);  // 松露
        AddObject(stock, 432, 50_000);  // 松露油
        AddObject(stock, 424, 8_000);   // 奶酪
        AddObject(stock, 426, 12_000);  // 山羊奶酪
        AddObject(stock, 445, 15_000);  // 鱼籽酱
        AddObject(stock, 447, 10_000);  // 腌鱼籽
        AddObject(stock, 812, 3_000);   // 鱼籽

        // === 姜岛/骷髅洞穴货币 ===
        AddObject(stock, 852, 100_000); // 龙牙
        AddObject(stock, 848, 30_000);  // 火山晶石
        AddObject(stock, 881, 8_000);   // 骨头碎片
        AddObject(stock, 858, 20_000);  // 齐钻

        // === 特殊消耗品 ===
        AddObject(stock, 917, 20_000);  // 齐氏调味料
        AddObject(stock, 872, 100_000); // 仙尘
        AddObjectStr(stock, "MysticTreeSeed", 30_000);  // 神秘树种
        AddObject(stock, 347, 50_000);  // 稀有种子
        AddObject(stock, 499, 80_000);  // 上古种子

        // === 宝石镶嵌 ===
        AddObject(stock, 60, 8_000);    // 绿宝石
        AddObject(stock, 62, 8_000);    // 海蓝宝石
        AddObject(stock, 64, 8_000);    // 红宝石
        AddObject(stock, 66, 8_000);    // 紫水晶
        AddObject(stock, 68, 8_000);    // 黄水晶
        AddObject(stock, 70, 8_000);    // 翡翠
        AddObject(stock, 72, 20_000);   // 钻石

        // === 饮品/酒类 ===
        AddObject(stock, 340, 3_000);   // 蜂蜜
        AddObject(stock, 346, 5_000);   // 啤酒
        AddObject(stock, 303, 5_000);   // 淡啤酒
        AddObject(stock, 348, 6_000);   // 果酒
        AddObject(stock, 614, 4_000);   // 绿茶

        return stock;
    }

    private static void AddObject(Dictionary<ISalable, ItemStockInformation> stock, int id, int price)
    {
        var item = ItemRegistry.Create($"(O){id}");
        stock.Add(item, new ItemStockInformation(price: price, stock: int.MaxValue));
    }

    private static void AddObjectStr(Dictionary<ISalable, ItemStockInformation> stock, string id, int price)
    {
        var item = ItemRegistry.Create($"(O){id}");
        stock.Add(item, new ItemStockInformation(price: price, stock: int.MaxValue));
    }

    private static void AddFood(Dictionary<ISalable, ItemStockInformation> stock, int id, int price)
    {
        // 食物和普通对象都是 (O) 前缀，统一处理
        AddObject(stock, id, price);
    }
}
