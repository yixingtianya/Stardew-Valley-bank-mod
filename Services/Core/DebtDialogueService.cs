using BankMod.Data;
using StardewValley;

namespace BankMod.Services.Core;

public class DebtDialogueService
{
    private readonly Random _rng = new();

    /// <summary>Check whether dialogue should be injected without modifying state.</summary>
    public bool ShouldInjectDialogue(string npcName, BankAccountData account)
    {
        var npc = Game1.getCharacterFromName(npcName, mustBeVillager: false);
        if (npc == null) return false;
        bool isPartner = Game1.player.spouse == npcName || Game1.player.isRoommate(npcName);
        if (account.IsInBankruptcy)
        {
            if (isPartner)
            {
                if (account.BankruptcyNpcGifted.Contains($"bankrupt_{npcName}_spouse")) return false;
                return SpouseLines.GetBankrupt(npcName) != null;
            }
            int hearts = Game1.player.getFriendshipHeartLevelForNPC(npcName);
            int tier = hearts < 4 ? 0 : hearts < 8 ? 1 : 2;
            string key = $"bankrupt_{npcName}_{tier}";
            if (account.BankruptcyNpcGifted.Contains(key)) return false;
            for (int t = tier + 1; t <= 2; t++)
                if (account.BankruptcyNpcGifted.Contains($"bankrupt_{npcName}_{t}")) return false;
            return BankruptLines.Get(npcName, hearts) != null;
        }
        if (account.IsInInterestDebt || account.IsInPrincipalDebt)
        {
            if (isPartner)
            {
                string key = $"debt_{npcName}_{Game1.dayOfMonth}";
                if (account.DebtNpcSpoken.Contains(key)) return false;
                return SpouseLines.GetDebt(npcName) != null;
            }
            if (_rng.Next(100) >= 15) return false;
            string key2 = $"debt_{npcName}_{Game1.dayOfMonth}";
            if (account.DebtNpcSpoken.Contains(key2)) return false;
            return DebtLines.Get(npcName, Game1.player.getFriendshipHeartLevelForNPC(npcName)) != null;
        }
        return false;
    }

    public bool TryInjectDialogue(string npcName, BankAccountData account, ModConfig config)
    {
        var npc = Game1.getCharacterFromName(npcName, mustBeVillager: false);
        if (npc == null) return false;
        if (account.IsInBankruptcy)
            return TryBankruptcyDialogue(npc, account);
        if (account.IsInInterestDebt || account.IsInPrincipalDebt)
            return TryDebtDialogue(npc, account);
        return false;
    }

    private bool TryDebtDialogue(NPC npc, BankAccountData account)
    {
        bool isPartner = Game1.player.spouse == npc.Name || Game1.player.isRoommate(npc.Name);
        if (isPartner)
        {
            string key = $"debt_{npc.Name}_{Game1.dayOfMonth}";
            if (account.DebtNpcSpoken.Contains(key)) return false;
            account.DebtNpcSpoken.Add(key);
            string? text = SpouseLines.GetDebt(npc.Name);
            if (text == null) return false;
            npc.setNewDialogue(new StardewValley.Dialogue(npc, null, text));
            Game1.drawDialogue(npc);
            return true;
        }
        if (_rng.Next(100) >= 15) return false;
        string key2 = $"debt_{npc.Name}_{Game1.dayOfMonth}";
        if (account.DebtNpcSpoken.Contains(key2)) return false;
        account.DebtNpcSpoken.Add(key2);
        string? text2 = DebtLines.Get(npc.Name, Game1.player.getFriendshipHeartLevelForNPC(npc.Name));
        if (text2 == null) return false;
        npc.setNewDialogue(new StardewValley.Dialogue(npc, null, text2));
        Game1.drawDialogue(npc);
        return true;
    }

    private bool TryBankruptcyDialogue(NPC npc, BankAccountData account)
    {
        bool isPartner = Game1.player.spouse == npc.Name || Game1.player.isRoommate(npc.Name);
        if (isPartner)
        {
            string key = $"bankrupt_{npc.Name}_spouse";
            if (account.BankruptcyNpcGifted.Contains(key)) return false;
            var result = SpouseLines.GetBankrupt(npc.Name);
            if (result == null) return false;
            account.BankruptcyNpcGifted.Add(key);
            var (text, itemId, count, giftName) = result.Value;
            npc.setNewDialogue(new StardewValley.Dialogue(npc, null, text));
            Game1.drawDialogue(npc);
            if (npc.Name == "Shane")
            {
                GiveItem(206, 1, I18n.Get("dialogue.1"));
                GiveItem(346, 1, I18n.Get("dialogue.2"));
            }
            else
            {
                GiveItem(itemId, count, giftName);
            }
            return true;
        }
        int hearts = Game1.player.getFriendshipHeartLevelForNPC(npc.Name);
        int tier = hearts < 4 ? 0 : hearts < 8 ? 1 : 2;
        string key2 = $"bankrupt_{npc.Name}_{tier}";
        if (account.BankruptcyNpcGifted.Contains(key2)) return false;
        for (int t = tier + 1; t <= 2; t++)
            if (account.BankruptcyNpcGifted.Contains($"bankrupt_{npc.Name}_{t}")) return false;
        account.BankruptcyNpcGifted.Add(key2);
        string? text2 = BankruptLines.Get(npc.Name, hearts);
        if (text2 == null) return false;
        npc.setNewDialogue(new StardewValley.Dialogue(npc, null, text2));
        Game1.drawDialogue(npc);
        if (hearts >= 8) GiveGift(npc.Name);
        return true;
    }

    /// <summary>Check if the player should get a congrats line from this NPC today. Does not modify state.</summary>
    public string? GetCongratsLine(string npcName, BankAccountData account)
    {
        if (!account.DebtClearedCongratsShown) return null;
        if (account.CongratsNpcSpoken.Contains(npcName)) return null;
        bool isPartner = Game1.player.spouse == npcName || Game1.player.isRoommate(npcName);
        if (isPartner)
            return SpouseLines.GetCongrats(npcName);
        int hearts = Game1.player.getFriendshipHeartLevelForNPC(npcName);
        return CongratsLines.Get(npcName, hearts);
    }

    /// <summary>Mark congrats line as spoken for this NPC.</summary>
    public void MarkCongratsSpoken(string npcName, BankAccountData account)
    {
        if (!account.CongratsNpcSpoken.Contains(npcName))
            account.CongratsNpcSpoken.Add(npcName);
    }

    public void TryCongratulate(BankAccountData account)
    {
        if (account.DebtClearedCongratsShown) return;
        account.DebtClearedCongratsShown = true;
        Game1.chatBox?.addInfoMessage(I18n.Get("dialogue.3"));
    }

    private static void GiveGift(string npcName)
    {
        switch (npcName)
        {
            case "Lewis": Game1.player.Money += 500; Game1.chatBox?.addInfoMessage(I18n.Get("dialogue.4")); return;
            case "Marnie": GiveItem(176, 6, I18n.Get("dialogue.5")); return;
            case "Robin": GiveItem(388, 20, I18n.Get("dialogue.6")); return;
            case "Pierre": GiveItem(770, 5, I18n.Get("dialogue.7")); return;
            case "Willy": GiveItem(685, 10, I18n.Get("dialogue.8")); return;
            case "Gus": GiveItem(196, 1, I18n.Get("dialogue.9")); return;
            case "Emily": GiveItem(428, 1, I18n.Get("dialogue.10")); return;
            case "Haley": GiveItem(421, 1, I18n.Get("fbn.164")); return;
            case "Leah": GiveItem(196, 1, I18n.Get("dialogue.9")); return;
            case "Pam": GiveItem(346, 1, I18n.Get("dialogue.2")); return;
            case "Shane": GiveItem(206, 1, I18n.Get("dialogue.1")); return;
            case "Harvey": GiveItem(349, 1, I18n.Get("dialogue.11")); return;
            case "Maru": GiveItem(787, 1, I18n.Get("dialogue.12")); return;
            case "Sebastian": GiveItem(769, 5, I18n.Get("dialogue.13")); return;
            case "Abigail": GiveItem(66, 1, I18n.Get("dialogue.14")); return;
            case "Sam": GiveItem(155, 1, I18n.Get("dialogue.15")); return;
            case "Elliott": GiveItem(444, 1, I18n.Get("dialogue.16")); return;
            case "George": GiveItem(378, 5, I18n.Get("dialogue.17")); return;
            case "Evelyn": GiveItem(223, 3, I18n.Get("dialogue.18")); return;
            case "Clint": GiveItem(335, 3, I18n.Get("dialogue.19")); return;
            case "Wizard": GiveItem(797, 1, I18n.Get("dialogue.20")); return;
            case "Demetrius": GiveItem(369, 5, I18n.Get("dialogue.21")); return;
            case "Penny": GiveItem(770, 1, I18n.Get("dialogue.7")); return;
            case "Jas": GiveItem(330, 2, I18n.Get("dialogue.22")); return;
            case "Vincent": GiveItem(340, 1, I18n.Get("dialogue.23")); return;
            case "Sandy": GiveItem(802, 3, I18n.Get("dialogue.24")); return;
            case "Marlon": GiveItem(773, 1, I18n.Get("dialogue.25")); return;
            case "Governor": GiveItem(369, 10, I18n.Get("dialogue.21")); return;
        }
    }

    private static void GiveItem(int itemId, int count, string name)
    {
        var item = new StardewValley.Object(itemId.ToString(), count);
        if (item != null && Game1.player.addItemToInventoryBool(item))
            Game1.chatBox?.addInfoMessage(I18n.Get("dialogue.26"));
    }
}

internal static class DebtLines
{
    public static string? Get(string npcName, int hearts)
    {
        var tier = hearts < 4 ? 0 : hearts < 8 ? 1 : 2; // 0=low, 1=mid, 2=high
        return npcName switch
        {
            "Pierre" => tier switch { 0 => I18n.Get("dialogue.27"), 1 => I18n.Get("dialogue.28"), _ => I18n.Get("dialogue.29") },
            "Caroline" => tier switch { 0 => I18n.Get("dialogue.30"), 1 => I18n.Get("dialogue.31"), _ => I18n.Get("dialogue.32") },
            "Lewis" => tier switch { 0 => I18n.Get("dialogue.33"), 1 => I18n.Get("dialogue.34"), _ => I18n.Get("dialogue.35") },
            "Marnie" => tier switch { 0 => I18n.Get("dialogue.36"), 1 => I18n.Get("dialogue.37"), _ => I18n.Get("dialogue.38") },
            "Robin" => tier switch { 0 => I18n.Get("dialogue.39"), 1 => I18n.Get("dialogue.40"), _ => I18n.Get("dialogue.41") },
            "George" => tier switch { 0 => I18n.Get("dialogue.42"), 1 => I18n.Get("dialogue.43"), _ => I18n.Get("dialogue.44") },
            "Evelyn" => tier switch { 0 => I18n.Get("dialogue.45"), 1 => I18n.Get("dialogue.46"), _ => I18n.Get("dialogue.47") },
            "Gus" => tier switch { 0 => I18n.Get("dialogue.48"), 1 => I18n.Get("dialogue.49"), _ => I18n.Get("dialogue.50") },
            "Pam" => tier switch { 0 => I18n.Get("dialogue.51"), 1 => I18n.Get("dialogue.52"), _ => I18n.Get("dialogue.53") },
            "Shane" => tier switch { 0 => I18n.Get("dialogue.54"), 1 => I18n.Get("dialogue.55"), _ => I18n.Get("dialogue.56") },
            "Leah" => tier switch { 0 => I18n.Get("dialogue.57"), 1 => I18n.Get("dialogue.58"), _ => I18n.Get("dialogue.59") },
            "Elliott" => tier switch { 0 => I18n.Get("dialogue.60"), 1 => I18n.Get("dialogue.61"), _ => I18n.Get("dialogue.62") },
            "Harvey" => tier switch { 0 => I18n.Get("dialogue.63"), 1 => I18n.Get("dialogue.64"), _ => I18n.Get("dialogue.65") },
            "Sebastian" => tier switch { 0 => I18n.Get("dialogue.66"), 1 => I18n.Get("dialogue.67"), _ => I18n.Get("dialogue.68") },
            "Abigail" => tier switch { 0 => I18n.Get("dialogue.69"), 1 => I18n.Get("dialogue.70"), _ => I18n.Get("dialogue.71") },
            "Sam" => tier switch { 0 => I18n.Get("dialogue.72"), 1 => I18n.Get("dialogue.73"), _ => I18n.Get("dialogue.74") },
            "Maru" => tier switch { 0 => I18n.Get("dialogue.75"), 1 => I18n.Get("dialogue.76"), _ => I18n.Get("dialogue.77") },
            "Clint" => tier switch { 0 => I18n.Get("dialogue.78"), 1 => I18n.Get("dialogue.79"), _ => I18n.Get("dialogue.80") },
            "Willy" => tier switch { 0 => I18n.Get("dialogue.81"), 1 => I18n.Get("dialogue.82"), _ => I18n.Get("dialogue.83") },
            "Wizard" => tier switch { 0 => I18n.Get("dialogue.84"), 1 => I18n.Get("dialogue.85"), _ => I18n.Get("dialogue.86") },
            "Krobus" => tier switch { 0 => I18n.Get("dialogue.87"), 1 => I18n.Get("dialogue.88"), _ => I18n.Get("dialogue.89") },
            "Dwarf" => tier switch { 0 => I18n.Get("dialogue.90"), 1 => I18n.Get("dialogue.91"), _ => I18n.Get("dialogue.92") },
            "Demetrius" => tier switch { 0 => I18n.Get("dialogue.93"), 1 => I18n.Get("dialogue.94"), _ => I18n.Get("dialogue.95") },
            "Penny" => tier switch { 0 => I18n.Get("dialogue.96"), 1 => I18n.Get("dialogue.97"), _ => I18n.Get("dialogue.98") },
            "Jas" => tier switch { 0 => I18n.Get("dialogue.99"), 1 => I18n.Get("dialogue.100"), _ => I18n.Get("dialogue.101") },
            "Vincent" => tier switch { 0 => I18n.Get("dialogue.102"), 1 => I18n.Get("dialogue.103"), _ => I18n.Get("dialogue.104") },
            "Sandy" => tier switch { 0 => I18n.Get("dialogue.105"), 1 => I18n.Get("dialogue.106"), _ => I18n.Get("dialogue.107") },
            "Marlon" => tier switch { 0 => I18n.Get("dialogue.108"), 1 => I18n.Get("dialogue.109"), _ => I18n.Get("dialogue.110") },
            "Governor" => tier switch { 0 => I18n.Get("dialogue.111"), 1 => I18n.Get("dialogue.112"), _ => I18n.Get("dialogue.113") },
            _ => null
        };
    }
}

internal static class BankruptLines
{
    public static string? Get(string npcName, int hearts)
    {
        var tier = hearts < 4 ? 0 : hearts < 8 ? 1 : 2;
        return npcName switch
        {
            "Lewis" => tier switch { 0 => I18n.Get("dialogue.114"), 1 => I18n.Get("dialogue.115"), _ => I18n.Get("dialogue.116") },
            "Marnie" => tier switch { 0 => I18n.Get("dialogue.117"), 1 => I18n.Get("dialogue.118"), _ => I18n.Get("dialogue.119") },
            "Robin" => tier switch { 0 => I18n.Get("dialogue.120"), 1 => I18n.Get("dialogue.121"), _ => I18n.Get("dialogue.122") },
            "Pierre" => tier switch { 0 => I18n.Get("dialogue.123"), 1 => I18n.Get("dialogue.124"), _ => I18n.Get("dialogue.125") },
            "Willy" => tier switch { 0 => I18n.Get("dialogue.126"), 1 => I18n.Get("dialogue.127"), _ => I18n.Get("dialogue.128") },
            "Gus" => tier switch { 0 => I18n.Get("dialogue.129"), 1 => I18n.Get("dialogue.130"), _ => I18n.Get("dialogue.131") },
            "Emily" => tier switch { 0 => I18n.Get("dialogue.132"), 1 => I18n.Get("dialogue.133"), _ => I18n.Get("dialogue.134") },
            "Haley" => tier switch { 0 => I18n.Get("dialogue.135"), 1 => I18n.Get("dialogue.136"), _ => I18n.Get("dialogue.137") },
            "Leah" => tier switch { 0 => I18n.Get("dialogue.138"), 1 => I18n.Get("dialogue.139"), _ => I18n.Get("dialogue.140") },
            "Pam" => tier switch { 0 => I18n.Get("dialogue.141"), 1 => I18n.Get("dialogue.142"), _ => I18n.Get("dialogue.143") },
            "Shane" => tier switch { 0 => I18n.Get("dialogue.144"), 1 => I18n.Get("dialogue.145"), _ => I18n.Get("dialogue.146") },
            "Harvey" => tier switch { 0 => I18n.Get("dialogue.147"), 1 => I18n.Get("dialogue.148"), _ => I18n.Get("dialogue.149") },
            "Maru" => tier switch { 0 => I18n.Get("dialogue.150"), 1 => I18n.Get("dialogue.151"), _ => I18n.Get("dialogue.152") },
            "Sebastian" => tier switch { 0 => I18n.Get("dialogue.153"), 1 => I18n.Get("dialogue.154"), _ => I18n.Get("dialogue.155") },
            "Abigail" => tier switch { 0 => I18n.Get("dialogue.156"), 1 => I18n.Get("dialogue.157"), _ => I18n.Get("dialogue.158") },
            "Sam" => tier switch { 0 => I18n.Get("dialogue.159"), 1 => I18n.Get("dialogue.160"), _ => I18n.Get("dialogue.161") },
            "Elliott" => tier switch { 0 => I18n.Get("dialogue.162"), 1 => I18n.Get("dialogue.163"), _ => I18n.Get("dialogue.164") },
            "George" => tier switch { 0 => I18n.Get("dialogue.165"), 1 => I18n.Get("dialogue.166"), _ => I18n.Get("dialogue.167") },
            "Evelyn" => tier switch { 0 => I18n.Get("dialogue.168"), 1 => I18n.Get("dialogue.169"), _ => I18n.Get("dialogue.170") },
            "Clint" => tier switch { 0 => I18n.Get("dialogue.171"), 1 => I18n.Get("dialogue.172"), _ => I18n.Get("dialogue.173") },
            "Wizard" => tier switch { 0 => I18n.Get("dialogue.174"), 1 => I18n.Get("dialogue.175"), _ => I18n.Get("dialogue.176") },
            "Demetrius" => tier switch { 0 => I18n.Get("dialogue.177"), 1 => I18n.Get("dialogue.178"), _ => I18n.Get("dialogue.179") },
            "Penny" => tier switch { 0 => I18n.Get("dialogue.180"), 1 => I18n.Get("dialogue.181"), _ => I18n.Get("dialogue.182") },
            "Jas" => tier switch { 0 => I18n.Get("dialogue.183"), 1 => I18n.Get("dialogue.184"), _ => I18n.Get("dialogue.185") },
            "Vincent" => tier switch { 0 => I18n.Get("dialogue.186"), 1 => I18n.Get("dialogue.187"), _ => I18n.Get("dialogue.188") },
            "Sandy" => tier switch { 0 => I18n.Get("dialogue.189"), 1 => I18n.Get("dialogue.190"), _ => I18n.Get("dialogue.191") },
            "Marlon" => tier switch { 0 => I18n.Get("dialogue.192"), 1 => I18n.Get("dialogue.193"), _ => I18n.Get("dialogue.194") },
            "Governor" => tier switch { 0 => I18n.Get("dialogue.195"), 1 => I18n.Get("dialogue.196"), _ => I18n.Get("dialogue.197") },
            _ => null
        };
    }
}

internal static class CongratsLines
{
    public static string? Get(string npcName, int hearts)
    {
        var tier = hearts < 4 ? 0 : hearts < 8 ? 1 : 2;
        return npcName switch
        {
            "Pierre" => tier switch { 0 => I18n.Get("dialogue.198"), 1 => I18n.Get("dialogue.199"), _ => I18n.Get("dialogue.200") },
            "Lewis" => tier switch { 0 => I18n.Get("dialogue.201"), 1 => I18n.Get("dialogue.202"), _ => I18n.Get("dialogue.203") },
            _ => hearts >= 8 ? I18n.Get("dialogue.204") : null
        };
    }
}

internal static class SpouseLines
{
    public static string? GetDebt(string npcName)
    {
        return npcName switch
        {
            "Abigail" => I18n.Get("dialogue.205"),
            "Elliott" => I18n.Get("dialogue.206"),
            "Harvey" => I18n.Get("dialogue.207"),
            "Haley" => I18n.Get("dialogue.208"),
            "Leah" => I18n.Get("dialogue.209"),
            "Maru" => I18n.Get("dialogue.210"),
            "Penny" => I18n.Get("dialogue.211"),
            "Sam" => I18n.Get("dialogue.212"),
            "Sebastian" => I18n.Get("dialogue.213"),
            "Shane" => I18n.Get("dialogue.214"),
            "Krobus" => I18n.Get("dialogue.215"),
            _ => null
        };
    }

    public static (string text, int itemId, int count, string giftName)? GetBankrupt(string npcName)
    {
        return npcName switch
        {
            "Abigail" => ((I18n.Get("dialogue.216"), 66, 2, I18n.Get("dialogue.14"))),
            "Elliott" => ((I18n.Get("dialogue.217"), 444, 2, I18n.Get("dialogue.16"))),
            "Harvey" => ((I18n.Get("dialogue.218"), 349, 3, I18n.Get("dialogue.11"))),
            "Haley" => ((I18n.Get("dialogue.219"), 421, 2, I18n.Get("fbn.164"))),
            "Leah" => ((I18n.Get("dialogue.220"), 196, 2, I18n.Get("dialogue.9"))),
            "Maru" => ((I18n.Get("dialogue.221"), 787, 2, I18n.Get("dialogue.12"))),
            "Penny" => ((I18n.Get("dialogue.222"), 770, 1, I18n.Get("dialogue.7"))),
            "Sam" => ((I18n.Get("dialogue.223"), 155, 2, I18n.Get("dialogue.15"))),
            "Sebastian" => ((I18n.Get("dialogue.224"), 769, 10, I18n.Get("dialogue.13"))),
            "Shane" => ((I18n.Get("dialogue.225"), 206, 1, I18n.Get("dialogue.1"))),
            "Krobus" => ((I18n.Get("dialogue.226"), 305, 5, I18n.Get("dialogue.227"))),
            _ => null
        };
    }

    public static string? GetCongrats(string npcName)
    {
        return npcName switch
        {
            "Abigail" => I18n.Get("dialogue.228"),
            "Elliott" => I18n.Get("dialogue.229"),
            "Harvey" => I18n.Get("dialogue.230"),
            "Haley" => I18n.Get("dialogue.231"),
            "Leah" => I18n.Get("dialogue.232"),
            "Maru" => I18n.Get("dialogue.233"),
            "Penny" => I18n.Get("dialogue.234"),
            "Sam" => I18n.Get("dialogue.235"),
            "Sebastian" => I18n.Get("dialogue.236"),
            "Shane" => I18n.Get("dialogue.237"),
            "Krobus" => I18n.Get("dialogue.238"),
            _ => null
        };
    }
}