using BankMod.Data;
using BankMod.Services.Core;
using HarmonyLib;
using StardewValley;

namespace BankMod.Patches;

[HarmonyPatch]
internal static class NpcDialoguePatch
{
    private static DebtDialogueService? _service;
    private static ModConfig? _config;
    private static Func<BankAccountData>? _getAccount;

    public static void Initialize(DebtDialogueService service, ModConfig config, Func<BankAccountData> getAccount)
    {
        _service = service;
        _config = config;
        _getAccount = getAccount;
    }

    [HarmonyPatch(typeof(NPC), nameof(NPC.checkAction))]
    [HarmonyPrefix]
    public static void CheckAction_Prefix(NPC __instance, Farmer who)
    {
        if (_service == null || _config == null || _getAccount == null || who == null) return;

        try
        {
            var account = _getAccount();
            string npcName = __instance.Name;
            bool hasDebtDialog = _service.ShouldInjectDialogue(npcName, account);
            bool hasCongrats = account.DebtClearedCongratsShown
                && !account.CongratsNpcSpoken.Contains(npcName)
                && _service.GetCongratsLine(npcName, account) != null;

            if (hasDebtDialog || hasCongrats)
            {
                Game1.afterDialogues = () =>
                {
                    var freshAccount = _getAccount();
                    if (hasCongrats)
                    {
                        var npc = Game1.getCharacterFromName(npcName, mustBeVillager: false);
                        string? text = _service.GetCongratsLine(npcName, freshAccount);
                        if (npc != null && text != null)
                        {
                            _service.MarkCongratsSpoken(npcName, freshAccount);
                            npc.setNewDialogue(new Dialogue(npc, null, text));
                            Game1.drawDialogue(npc);
                        }
                    }
                    else if (hasDebtDialog)
                    {
                        _service.TryInjectDialogue(npcName, freshAccount, _config);
                    }
                };
            }
        }
        catch { /* let original dialogue play if anything fails */ }
    }
}
