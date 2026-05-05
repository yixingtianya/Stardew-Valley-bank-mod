using StardewModdingAPI;

namespace BankMod.Data;

/// <summary>Handles loading/saving bank account data to the save file.</summary>
internal static class BankAccountManager
{
    private const string SaveKey = "bankmod_account_data";

    public static BankAccountData Load(IModHelper helper)
    {
        return helper.Data.ReadSaveData<BankAccountData>(SaveKey) ?? new BankAccountData();
    }

    public static void Save(IModHelper helper, BankAccountData data)
    {
        helper.Data.WriteSaveData(SaveKey, data);
    }
}
